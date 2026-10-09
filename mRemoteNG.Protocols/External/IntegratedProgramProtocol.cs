using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Embedding;
using mRemoteNG.Protocols.Rdp;

namespace mRemoteNG.Protocols.External;

/// <summary>
/// The IntApp protocol (legacy <c>IntegratedProgram</c>): starts the external tool named by the connection's ExtApp
/// setting with the connection's variables and embeds the tool's top-level window in the session tab.
/// <list type="bullet">
/// <item>Linux/X11: the window is found by <c>_NET_WM_PID</c> (the process or its children) and reparented into
/// the tab's native window, which then keeps it sized and gives it the keyboard focus on click / tab activation;</item>
/// <item>Windows: the process's main window becomes a borderless child of the tab (as in the legacy app; untested);</item>
/// <item>macOS, Wayland-only sessions, tools with "Try to integrate" off, and programs that show no window of their
/// own process within the timeout: the program runs in its own window and the tab says so.</item>
/// </list>
/// Closing the tab ends the program (the legacy app kills it as well); when the program exits the session ends.
/// Integrated tools are never elevated (as in the legacy app).
/// </summary>
public sealed class IntegratedProgramProtocol : ProtocolBase, IVisualProtocol
{
    internal static readonly TimeSpan WindowWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ViewWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly ExternalToolsService _tools;
    private readonly ILogger<IntegratedProgramProtocol> _logger;
    private readonly object _sync = new();
    private RdpSessionView? _view;
    private Process? _process;
    private EmbeddedForeignWindow? _embedded;
    private string _toolName = "the program";
    private volatile bool _disconnectRequested;
    private bool _remoteFocusWanted;
    private bool _disposed;

    public IntegratedProgramProtocol(ExternalToolsService tools, ILogger<IntegratedProgramProtocol> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    /// <summary>True while the program's window is inside the session tab.</summary>
    public bool IsEmbedded => _embedded is not null;

    /// <summary>The embedded window (XID/HWND), or 0.</summary>
    public nint EmbeddedWindow => _embedded?.Window ?? 0;

    /// <summary>The program's process id while it runs, or null.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_sync)
                return _process?.Id;
        }
    }

    public Control CreateView()
    {
        if (_view is not null)
            return _view;

        _view = new RdpSessionView(embeddingCandidate: !OperatingSystem.IsMacOS(), iconClass: "app");
        _view.ShowMessage("Starting the external tool…");
        _view.Shown += (_, _) => OnViewShown();
        _view.TabHeaderPressed += (_, _) => RequestRemoteFocus();
        _view.AvaloniaPointerPressed += (_, _) => OnAvaloniaPointerPressed();
        _view.WindowActivated += (_, _) => OnWindowActivated();
        _view.KeyboardReleaseRequested += (_, _) => OnKeyboardReleaseRequested();
        _view.KeyboardReleaseEnded += (_, _) => OnWindowActivated();
        _view.PixelSizeChanged += (_, size) => _embedded?.Support?.ResizeRemote(size.Width, size.Height);
        _view.BringToFrontRequested += (_, _) => BringToFront();
        _view.DisconnectRequested += (_, _) => _ = DisconnectAsync();
        return _view;
    }

    /// <summary>The variables an IntApp session passes to its tool (see <see cref="ConnectionParametersFactory.Keys"/>).</summary>
    public static ExternalToolVariables BuildVariables(ConnectionParameters parameters)
    {
        string Extra(string key) => parameters.Extras.TryGetValue(key, out string? value) ? value : string.Empty;
        return new ExternalToolVariables
        {
            Name = Extra(ConnectionParametersFactory.Keys.ToolName),
            Hostname = parameters.Hostname,
            Port = parameters.Port.ToString(CultureInfo.InvariantCulture),
            Username = parameters.Username ?? string.Empty,
            Password = parameters.Password ?? string.Empty,
            Domain = parameters.Domain ?? string.Empty,
            Description = Extra(ConnectionParametersFactory.Keys.ToolDescription),
            MacAddress = Extra(ConnectionParametersFactory.Keys.ToolMacAddress),
            UserField = Extra(ConnectionParametersFactory.Keys.ToolUserField),
            Protocol = Extra(ConnectionParametersFactory.Keys.ToolProtocol),
        };
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("The external tool is already running.");
        }
        _disconnectRequested = false;
        State = ConnectionState.Connecting;

        parameters.Extras.TryGetValue(ConnectionParametersFactory.Keys.IntAppTool, out string? toolName);
        if (string.IsNullOrEmpty(toolName))
            throw Fail("No external tool is selected for this connection (setting \"External Application\").");
        _toolName = toolName;

        var tool = _tools.Find(toolName)
            ?? throw Fail($"Could not find the external tool \"{toolName}\".");
        if (!tool.IsAvailableOnCurrentPlatform)
            throw Fail($"The external tool \"{toolName}\" is meant for {tool.Platform}.");

        var variables = _tools.VariablesFilter is { } filter ? filter(BuildVariables(parameters)) : BuildVariables(parameters);
        RaiseStatus($"Starting {toolName}…");
        OnUi(v => v.ShowMessage($"Starting {toolName}…"));

        // Find the tab's native window before the program starts, so its window can be taken as soon as it appears.
        var (parent, size) = tool.TryIntegrate ? await WaitForEmbedParentAsync(ct) : (null, null);

        Process process;
        try
        {
            var psi = _tools.Launcher.BuildStartInfo(tool, variables, allowElevation: !tool.TryIntegrate);
            _logger.LogInformation("Starting integrated tool \"{Tool}\": {Command}", toolName, ExternalToolLauncher.Describe(psi));
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnProcessExited(process);
            if (!process.Start())
                throw new ExternalToolException($"\"{toolName}\" was handed to the desktop and has no process to embed.");
        }
        catch (Exception ex) when (ex is ExternalToolException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw Fail($"Could not start \"{toolName}\": {ex.Message}");
        }

        lock (_sync)
            _process = process;

        if (parent is not null && size is { } pixelSize)
        {
            OnUi(v => v.ShowMessage($"Waiting for the window of {toolName}…", showDisconnect: true));
            EmbeddedForeignWindow? embedded = null;
            try
            {
                embedded = await ForeignWindowEmbedder.EmbedAsync(parent, process, pixelSize, WindowWaitTimeout, _logger,
                    () => OnUiAsync(v => v.ShowEmbedded()), ct);
            }
            catch (OperationCanceledException)
            {
                KillProcess();
                throw;
            }

            if (embedded is not null)
            {
                bool exited;
                lock (_sync)
                {
                    exited = _process is null || HasExited(process);
                    if (!exited)
                        _embedded = embedded;
                }
                if (!exited && embedded.Support is { } clickSupport)
                    clickSupport.RemoteClicked += (_, _) => Dispatcher.UIThread.Post(() => _remoteFocusWanted = true);
                if (exited)
                {
                    embedded.Dispose();
                }
                else
                {
                    State = ConnectionState.Connected;
                    RaiseStatus($"{toolName} is running in this tab");
                    await OnUiAsync(v =>
                    {
                        v.ShowEmbedded();
                        if (v.IsShownInWindow)
                            RequestRemoteFocus();
                    });
                    return;
                }
            }

            if (HasExited(process))
                return; // OnProcessExited reported it.

            _logger.LogInformation("No embeddable window of \"{Tool}\" (pid {Pid}) appeared within {Timeout}", toolName, process.Id, WindowWaitTimeout);
            ShowRunningSeparately($"{toolName} did not show a window of its own process within {WindowWaitTimeout.TotalSeconds:0} s, " +
                                  "so it could not be embedded; it keeps running in its own window.");
            return;
        }

        ShowRunningSeparately(tool.TryIntegrate
            ? $"{toolName} is running in its own window (embedding is not available on this platform)."
            : $"{toolName} is running in its own window (\"Try to integrate\" is off for this tool).");
    }

    public override Task DisconnectAsync(CancellationToken ct = default)
    {
        _disconnectRequested = true;
        KillProcess();
        DisposeEmbedded();
        State = ConnectionState.Disconnected;
        RaiseStatus($"{_toolName} was closed");
        OnUi(v => v.ShowMessage($"{_toolName} was closed."));
        return Task.CompletedTask;
    }

    private void ShowRunningSeparately(string message)
    {
        if (HasExitedOrMissing())
            return;
        State = ConnectionState.Connected;
        RaiseStatus(message);
        OnUi(v => v.ShowMessage(message, showBringToFront: CanBringToFront, showDisconnect: true));
    }

    private void OnProcessExited(Process process)
    {
        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }
        _logger.LogInformation("Integrated tool \"{Tool}\" exited with code {Code}", _toolName, exitCode);
        DisposeEmbedded();
        if (_disconnectRequested || _disposed)
            return;

        bool failed = State == ConnectionState.Connecting && exitCode != 0;
        State = failed ? ConnectionState.Error : ConnectionState.Disconnected;
        string message = State == ConnectionState.Error
            ? $"{_toolName} exited with code {exitCode} before showing a window."
            : $"{_toolName} exited (code {exitCode}).";
        RaiseStatus(message);
        OnUi(v => v.ShowMessage(message));
    }

    private ExternalToolException Fail(string message)
    {
        State = ConnectionState.Error;
        RaiseStatus(message);
        OnUi(v => v.ShowMessage(message));
        return new ExternalToolException(message);
    }

    // ── Embedding ──────────────────────────────────────────────────────────

    private async Task<(IPlatformHandle? Parent, PixelSize? Size)> WaitForEmbedParentAsync(CancellationToken ct)
    {
        var view = _view;
        var host = view?.Host;
        if (view is null || host is null)
            return (null, null);

        IPlatformHandle handle;
        try
        {
            handle = await host.HandleReady.WaitAsync(ViewWaitTimeout, ct);
            await view.Sized.WaitAsync(ViewWaitTimeout, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The session tab was not shown within {Timeout}; the tool will use its own window.", ViewWaitTimeout);
            return (null, null);
        }

        if (!ForeignWindowEmbedder.CanEmbedInto(handle))
        {
            _logger.LogInformation("Native handle type {Descriptor} cannot host other programs' windows", handle.HandleDescriptor);
            return (null, null);
        }

        // The host stays hidden behind the message panel until the program's window is inside it.
        PixelSize size = await Dispatcher.UIThread.InvokeAsync(() => view.PixelSize);
        return (handle, size);
    }

    private void OnViewShown()
    {
        if (_embedded is not null && State == ConnectionState.Connected)
            RequestRemoteFocus();
    }

    private void RequestRemoteFocus()
    {
        if (_embedded?.Support is not { } support)
            return;
        _remoteFocusWanted = true;
        support.FocusRemote();
    }

    private void OnAvaloniaPointerPressed()
    {
        if (_embedded?.Support is not { } support || _view is null)
            return;
        _remoteFocusWanted = false;
        support.ReturnFocusTo(_view.TopLevelHandle);
    }

    private void OnWindowActivated()
    {
        // Not while one of our dialogs is open over the session (see OnKeyboardReleaseRequested).
        if (_embedded?.Support is { } support && _view is { IsCoveredByWindow: false, IsShownInWindow: true }
            && _remoteFocusWanted && State == ConnectionState.Connected)
            support.FocusRemote();
    }

    private void OnKeyboardReleaseRequested()
    {
        // A dialog opened over the session: the keyboard leaves the program's window until the dialog closes.
        if (_embedded?.Support is { } support && _view is not null)
            support.ReturnFocusTo(_view.TopLevelHandle);
    }

    private void DisposeEmbedded()
    {
        EmbeddedForeignWindow? embedded;
        lock (_sync)
        {
            embedded = _embedded;
            _embedded = null;
        }
        embedded?.Dispose();
    }

    // ── Process helpers ────────────────────────────────────────────────────

    private static bool CanBringToFront => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    private void BringToFront()
    {
        Process? process;
        lock (_sync)
            process = _process;
        if (process is null || HasExited(process))
            return;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("osascript") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture,
                    $"tell application \"System Events\" to set frontmost of (first process whose unix id is {process.Id}) to true"));
                using var _ = Process.Start(psi);
            }
            else if (OperatingSystem.IsWindows())
            {
                process.Refresh();
                if (process.MainWindowHandle != 0)
                    SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not bring {Tool} to the front", _toolName);
        }
    }

    private void KillProcess()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not end {Tool}", _toolName);
        }
        process.Dispose();
    }

    private bool HasExitedOrMissing()
    {
        lock (_sync)
            return _process is null || HasExited(_process);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    // ── UI marshalling ─────────────────────────────────────────────────────

    private void OnUi(Action<RdpSessionView> action)
    {
        var view = _view;
        if (view is null)
            return;
        if (Dispatcher.UIThread.CheckAccess())
            action(view);
        else
            Dispatcher.UIThread.Post(() => action(view));
    }

    private async Task OnUiAsync(Action<RdpSessionView> action)
    {
        var view = _view;
        if (view is null)
            return;
        await Dispatcher.UIThread.InvokeAsync(() => action(view));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
            return;
        _disposed = true;
        _disconnectRequested = true;
        KillProcess();
        DisposeEmbedded();

        // Destroy the native window on the UI thread, after the program's window inside it is gone.
        if (_view?.Host is { } host)
        {
            if (Dispatcher.UIThread.CheckAccess())
                host.Release();
            else
                Dispatcher.UIThread.Post(host.Release);
        }
    }
}
