using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>How FreeRDP treats a server certificate it cannot validate (Extras key <see cref="RdpProtocol.CertPolicyKey"/>).</summary>
public enum RdpCertificatePolicy
{
    /// <summary>
    /// Trust on first use (default): an unknown certificate is accepted and pinned in FreeRDP's known-hosts
    /// store (~/.config/freerdp/server/); a later change is rejected. Protects every connection after the
    /// first one against man-in-the-middle attacks, at the cost of trusting the very first connection blindly.
    /// </summary>
    Tofu,

    /// <summary>
    /// Accept any certificate without checking. No man-in-the-middle protection at all — only for labs and
    /// throw-away hosts.
    /// </summary>
    Ignore,

    /// <summary>
    /// Reject any certificate that is neither CA-signed for the host name nor already pinned. Safest, but a
    /// self-signed server (the default for Windows and xrdp) must be pinned out-of-band first.
    /// </summary>
    Deny,
}

/// <summary>Raised by <see cref="RdpProtocol.ConnectAsync"/> when the RDP session could not be established.</summary>
public sealed class RdpConnectionException : Exception
{
    public RdpConnectionException(string message) : base(message) { }
}

/// <summary>Options for <see cref="RdpProtocol.BuildFreeRdpArgs"/>.</summary>
internal sealed record FreeRdpLaunchOptions
{
    /// <summary>Installed FreeRDP major version (2 or 3); decides the syntax of a few options.</summary>
    public int MajorVersion { get; init; } = 3;

    /// <summary>Native window (XID / HWND) FreeRDP should create its desktop window in, or null for a top-level window.</summary>
    public nint? ParentWindow { get; init; }

    /// <summary>Initial desktop size in device pixels.</summary>
    public PixelSize? Size { get; init; }

    /// <summary>Window title (only meaningful for a top-level window).</summary>
    public string? Title { get; init; }
}

/// <summary>
/// RDP via FreeRDP (xfreerdp3 / xfreerdp / wfreerdp) running as a child process.
///
/// Embedding (Linux/X11 and Windows): <see cref="CreateView"/> returns an <see cref="RdpSessionView"/> whose
/// <see cref="RdpNativeHost"/> (an Avalonia NativeControlHost) owns a native child window. ConnectAsync waits until
/// that window exists and has a size, then starts FreeRDP with <c>/parent-window:&lt;XID|HWND&gt;</c> and
/// <c>/size:WxH</c> (device pixels, i.e. scaled by the window's render scaling) plus <c>/dynamic-resolution</c>.
/// FreeRDP creates its desktop window inside ours; <see cref="IEmbeddedWindowSupport"/> keeps it sized to the
/// tab (which, through dynamic resolution, resizes the remote desktop) and moves keyboard focus into it when the
/// tab is selected or clicked.
///
/// Separate window (macOS, Linux without an X11 handle, or no view): FreeRDP opens its own window and the tab
/// says so, offering to bring that window to the front (where supported) or to end the session.
///
/// Connection state: Connected is reported only when FreeRDP's core logs the transition to
/// CONNECTION_STATE_ACTIVE (FreeRDP 3, enabled with WLOG_FILTER — see <see cref="FreeRdpOutputParser"/>).
/// FreeRDP 2 does not log state transitions; for it the session counts as connected when the framebuffer was
/// initialised and the process is still running two seconds later (a heuristic). Errors come from the exit code
/// refined by the ERRCONNECT_* / ERRINFO_* / certificate messages FreeRDP printed.
///
/// Arguments and certificates: FreeRDP 3 receives its arguments through an environment variable
/// (<c>/args-from:env:NAME</c>), so the password is not in the world-readable process list (a process's
/// environment is readable only by its owner). <c>/args-from:stdin</c> is deliberately not used: FreeRDP 3.32 then
/// blocks forever on any later stdin prompt. FreeRDP asks on stdin whether to accept an unknown or changed
/// certificate; stdin is a pipe that is closed right after start, so such a prompt is answered "no" and the
/// connection fails with a certificate error. <c>/cert:&lt;policy&gt;</c> decides beforehand (see
/// <see cref="RdpCertificatePolicy"/>).
/// </summary>
public sealed partial class RdpProtocol : ProtocolBase, IVisualProtocol
{
    /// <summary>Extras key selecting the certificate policy: "tofu" (default), "ignore" or "deny".</summary>
    public const string CertPolicyKey = "rdp.certPolicy";

    private const string ArgsEnvironmentVariable = "MRNG_FREERDP_ARGS";

    private static readonly TimeSpan ViewWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FreeRdp2ConnectedGrace = TimeSpan.FromSeconds(2);
    private const int RecentOutputLines = 25;

    private readonly ILogger<RdpProtocol> _logger;
    private readonly object _sync = new();
    private RdpSessionView? _view;
    private Process? _process;
    private IEmbeddedWindowSupport? _embed;
    private bool _embedded;
    private readonly Queue<string> _recentOutput = new();
    private FreeRdpOutputParser _parser = new();
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConnectionParameters? _parameters;
    private int _majorVersion;
    private volatile bool _disconnectRequested;
    private bool _remoteFocusWanted;
    private bool _disposed;

    public RdpProtocol(ILogger<RdpProtocol> logger) => _logger = logger;

    /// <summary>True when the current/last FreeRDP session was started inside the session tab (not in its own window).</summary>
    public bool IsEmbedded => _embedded;

    // ── IVisualProtocol ────────────────────────────────────────────────────

    public Control CreateView()
    {
        if (_view is not null)
            return _view;

        // macOS has no X11/HWND parent FreeRDP could draw into: FreeRDP always gets its own window there.
        _view = new RdpSessionView(embeddingCandidate: !OperatingSystem.IsMacOS());
        _view.Shown += (_, _) => OnViewShown();
        _view.TabHeaderPressed += (_, _) => RequestRemoteFocus();
        _view.AvaloniaPointerPressed += (_, _) => OnAvaloniaPointerPressed();
        _view.WindowActivated += (_, _) => OnWindowActivated();
        _view.PixelSizeChanged += (_, size) => _embed?.ResizeRemote(size.Width, size.Height);
        _view.BringToFrontRequested += (_, _) => BringFloatingWindowToFront();
        _view.DisconnectRequested += (_, _) => _ = DisconnectAsync();
        return _view;
    }

    // ── IProtocol ──────────────────────────────────────────────────────────

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false })
            throw new InvalidOperationException("The RDP session is already running.");

        _parameters = parameters;
        _disconnectRequested = false;
        _parser = new FreeRdpOutputParser();
        _embedded = false;
        lock (_recentOutput)
            _recentOutput.Clear();
        _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        State = ConnectionState.Connecting;
        RaiseStatus($"Connecting to {parameters.Hostname}:{parameters.Port}…");
        OnUi(v => v.ShowMessage($"Connecting to {parameters.Hostname}…"));

        string? freerdp = FindFreeRdpExecutable();
        if (freerdp is null)
            throw Fail("FreeRDP was not found. Install xfreerdp3 / xfreerdp (Linux, e.g. the freerdp3-x11 package), " +
                       "wfreerdp (Windows) or FreeRDP via Homebrew (macOS).");

        _majorVersion = await GetFreeRdpMajorVersionAsync(freerdp, ct);
        bool argsFromEnvironment = _majorVersion >= 3;

        var (parent, size) = await WaitForEmbedParentAsync(ct);
        var args = BuildFreeRdpArgs(parameters, new FreeRdpLaunchOptions
        {
            MajorVersion = _majorVersion == 0 ? 3 : _majorVersion,
            ParentWindow = parent?.Handle,
            Size = size,
            Title = parent is null ? $"{parameters.Hostname} - mRemoteNG" : null,
        });

        // One argument per line when passed via /args-from: a line break inside a value (e.g. from an
        // untrusted connection file) would smuggle in extra options.
        if (args.Any(a => a.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0))
            throw Fail("A connection setting (host, user name, password, domain, gateway…) contains a line break, which FreeRDP cannot accept.");

        // FreeRDP writes INFO/DEBUG log lines to stdout, which C stdio fully buffers when it is a pipe: the
        // "connected" line could sit in the buffer until 4 KiB of further output arrive. coreutils' stdbuf
        // switches the child to line buffering (it execs FreeRDP, so the PID stays FreeRDP's). Without it
        // (macOS, Windows) the line still arrives, typically with the burst of output following activation.
        string? stdbuf = OperatingSystem.IsLinux() ? FindOnPath("stdbuf") : null;
        var startInfo = new ProcessStartInfo(stdbuf ?? freerdp)
        {
            UseShellExecute = false,
            // Always redirect stdin and close it right away: FreeRDP prompts there (certificate, missing
            // credentials); EOF answers "no" instead of the prompt blocking or reading our terminal.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Make the connection state machine visible (see FreeRdpOutputParser) and keep logs on the console.
        startInfo.Environment["WLOG_APPENDER"] = "CONSOLE";
        startInfo.Environment["WLOG_LEVEL"] = "INFO";
        startInfo.Environment["WLOG_FILTER"] = "com.freerdp.core.rdp:DEBUG";
        if (stdbuf is not null)
        {
            startInfo.ArgumentList.Add("-oL");
            startInfo.ArgumentList.Add("-eL");
            startInfo.ArgumentList.Add(freerdp);
        }

        if (argsFromEnvironment)
        {
            startInfo.Environment[ArgsEnvironmentVariable] = string.Join('\n', args);
            startInfo.ArgumentList.Add($"/args-from:env:{ArgsEnvironmentVariable}");
        }
        else
        {
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(parameters.Password))
                _logger.LogWarning("FreeRDP 2.x does not support /args-from; the password is visible to local users in the process list. Upgrade to FreeRDP 3.");
        }

        _logger.LogDebug("FreeRDP {Version}: {Binary} {Args}", _majorVersion, freerdp, string.Join(' ', args.Select(RedactPassword)));

        _embedded = parent is not null;
        if (parent is not null)
        {
            // Create the glue before FreeRDP starts so it observes FreeRDP mapping its window.
            _embed = EmbeddedWindowSupport.Create(parent, _logger);
            if (_embed is not null)
                _embed.RemoteWindowMapped += (_, _) => _logger.LogDebug("FreeRDP mapped its window inside the session tab");
            // FreeRDP blocks until its window is viewable, so the host must be shown before FreeRDP starts.
            await OnUiAsync(v => v.ShowEmbedded());
        }
        else
        {
            OnUi(v => v.ShowMessage(
                $"Connecting to {parameters.Hostname}…\nFreeRDP will open in a separate window.",
                showDisconnect: true));
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutputLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutputLine(e.Data);
        process.Exited += (_, _) => OnProcessExited(process);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw Fail($"FreeRDP could not be started: {ex.Message}");
        }

        lock (_sync)
        {
            _process?.Dispose(); // an earlier, finished session
            _process = process;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { process.StandardInput.Close(); } catch (IOException) { }

        var cancelled = Task.Delay(Timeout.Infinite, ct);
        var finished = await Task.WhenAny(_connected.Task, _exited.Task, cancelled);

        if (finished == cancelled)
        {
            await DisconnectAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
        }

        if (finished == _exited.Task && !_connected.Task.IsCompleted)
        {
            int exitCode = await _exited.Task;
            if (_disconnectRequested)
                return;
            LogRecentOutput(exitCode);
            throw Fail(_parser.DescribeFailure(exitCode));
        }
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _disconnectRequested = true;
        Process? process;
        lock (_sync)
            process = _process;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    // SIGTERM lets FreeRDP send a proper disconnect to the server; kill it if it does not exit.
                    if (!OperatingSystem.IsWindows())
                        SendSigTerm(process.Id);
                    else
                        process.Kill();

                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    grace.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(grace.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(ct);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // The process was never started or already reaped.
            }
        }

        if (State != ConnectionState.Disconnected)
        {
            State = ConnectionState.Disconnected;
            RaiseStatus("RDP session closed.");
        }
        OnUi(v => v.ShowMessage("Disconnected."));
    }

    // ── FreeRDP process events ─────────────────────────────────────────────

    private void OnOutputLine(string? line)
    {
        if (line is null) return;
        // The core.rdp DEBUG filter (needed for the state transitions) also logs every PDU; keep those out of
        // the application log.
        if (!line.Contains("[DEBUG]", StringComparison.Ordinal))
        {
            _logger.LogDebug("FreeRDP: {Line}", line);
            lock (_recentOutput)
            {
                _recentOutput.Enqueue(line);
                while (_recentOutput.Count > RecentOutputLines)
                    _recentOutput.Dequeue();
            }
        }

        switch (_parser.Process(line))
        {
            case FreeRdpSignal.Connected:
                MarkConnected();
                break;
            case FreeRdpSignal.FramebufferReady when _majorVersion is > 0 and < 3:
                _ = MarkConnectedIfStillRunningAsync();
                break;
            case FreeRdpSignal.Reconnecting when State == ConnectionState.Connected && !_disconnectRequested:
                State = ConnectionState.Reconnecting;
                RaiseStatus("Connection lost; FreeRDP is reconnecting…");
                break;
        }
    }

    private async Task MarkConnectedIfStillRunningAsync()
    {
        await Task.Delay(FreeRdp2ConnectedGrace);
        if (!_exited.Task.IsCompleted)
            MarkConnected();
    }

    private void MarkConnected()
    {
        if (_disconnectRequested || _exited.Task.IsCompleted) return;
        if (State is not (ConnectionState.Connecting or ConnectionState.Reconnecting)) return;

        _parser.ResetDiagnostics();
        State = ConnectionState.Connected;
        string host = _parameters?.Hostname ?? "host";
        RaiseStatus($"Connected to {host}");
        _connected.TrySetResult();

        if (_embedded)
        {
            OnUi(v =>
            {
                v.ShowEmbedded();
                if (v.IsShownInWindow)
                    RequestRemoteFocus();
            });
        }
        else
        {
            OnUi(v => v.ShowMessage(
                $"Connected to {host}.\nFreeRDP is running in a separate window.",
                showBringToFront: CanBringFloatingWindowToFront,
                showDisconnect: true));
        }
    }

    private void OnProcessExited(Process process)
    {
        try { process.WaitForExit(); } catch (InvalidOperationException) { } // drain redirected output
        int exitCode;
        try { exitCode = process.ExitCode; } catch (InvalidOperationException) { exitCode = -1; }
        _logger.LogInformation("FreeRDP exited with code {Code}", exitCode);

        bool wasConnected = _connected.Task.IsCompleted;
        DisposeEmbedSupport();

        if (_disconnectRequested)
        {
            // DisconnectAsync reports the state.
        }
        else if (wasConnected)
        {
            string message = _parser.DescribeFailure(exitCode);
            if (_parser.IsNormalSessionEnd(exitCode))
            {
                State = ConnectionState.Disconnected;
                RaiseStatus($"RDP session ended: {message}");
                OnUi(v => v.ShowMessage($"The RDP session ended.\n{message}"));
            }
            else
            {
                LogRecentOutput(exitCode);
                State = ConnectionState.Error;
                RaiseStatus($"RDP session failed: {message}");
                OnUi(v => v.ShowMessage($"The RDP session failed.\n{message}"));
            }
        }
        // Not connected yet: ConnectAsync turns the exit into an error.

        _exited.TrySetResult(exitCode);
    }

    /// <summary>FreeRDP's own output is only logged at Debug level; on a failure its last lines go to the log as a warning.</summary>
    private void LogRecentOutput(int exitCode)
    {
        string output;
        lock (_recentOutput)
            output = string.Join(Environment.NewLine, _recentOutput);
        _logger.LogWarning("FreeRDP exited with code {Code}. Its last output lines:{NewLine}{Output}", exitCode, Environment.NewLine, output);
    }

    private RdpConnectionException Fail(string message)
    {
        State = ConnectionState.Error;
        RaiseStatus(message);
        OnUi(v => v.ShowMessage($"Could not connect to {_parameters?.Hostname}.\n{message}"));
        DisposeEmbedSupport();
        return new RdpConnectionException(message);
    }

    // ── Embedding ──────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the session view's native window (created when the tab is first shown) and its pixel size.
    /// Returns (null, null) when FreeRDP has to run in its own window.
    /// </summary>
    private async Task<(IPlatformHandle? Parent, PixelSize? Size)> WaitForEmbedParentAsync(CancellationToken ct)
    {
        var host = _view?.Host;
        if (_view is null || host is null)
            return (null, null);

        IPlatformHandle handle;
        try
        {
            handle = await host.HandleReady.WaitAsync(ViewWaitTimeout, ct);
            await _view.Sized.WaitAsync(ViewWaitTimeout, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The RDP tab was not shown within {Timeout}; FreeRDP will use a separate window.", ViewWaitTimeout);
            return (null, null);
        }

        if (!EmbeddedWindowSupport.CanEmbedInto(handle))
        {
            _logger.LogInformation("Native handle type {Descriptor} cannot host FreeRDP; using a separate window.", handle.HandleDescriptor);
            return (null, null);
        }

        var view = _view;
        PixelSize size = await Dispatcher.UIThread.InvokeAsync(() => view.PixelSize);
        return (handle, size);
    }

    private void OnViewShown()
    {
        if (_embed is null) return;
        if (State == ConnectionState.Connected)
            RequestRemoteFocus();
    }

    private void RequestRemoteFocus()
    {
        if (_embed is null) return;
        _remoteFocusWanted = true;
        _embed.FocusRemote();
    }

    private void OnAvaloniaPointerPressed()
    {
        // A click on Avalonia UI (outside the remote desktop): keyboard input belongs to Avalonia again.
        if (_embed is null || _view is null) return;
        _remoteFocusWanted = false;
        _embed.ReturnFocusTo(_view.TopLevelHandle);
    }

    private void OnWindowActivated()
    {
        // The window manager focused our top-level (e.g. Alt+Tab back): restore focus to the session if it had it.
        if (_embed is not null && _remoteFocusWanted && State == ConnectionState.Connected)
            _embed.FocusRemote();
    }

    private void DisposeEmbedSupport()
    {
        IEmbeddedWindowSupport? embed;
        lock (_sync)
        {
            embed = _embed;
            _embed = null;
        }
        embed?.Dispose();
    }

    // ── Separate-window helpers ────────────────────────────────────────────

    private static bool CanBringFloatingWindowToFront => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    private void BringFloatingWindowToFront()
    {
        Process? process;
        lock (_sync)
            process = _process;
        if (process is null || process.HasExited) return;

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
            _logger.LogDebug(ex, "Could not bring the FreeRDP window to the front");
        }
    }

    // ── FreeRDP helpers ────────────────────────────────────────────────────

    private static string? FindFreeRdpExecutable()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["wfreerdp.exe", "wfreerdp3.exe", @"C:\Program Files\FreeRDP\wfreerdp.exe"]
            : OperatingSystem.IsMacOS()
            ? ["xfreerdp", "sdl-freerdp", "/opt/homebrew/bin/xfreerdp", "/opt/homebrew/bin/sdl-freerdp", "/usr/local/bin/xfreerdp", "/usr/local/bin/sdl-freerdp"]
            : ["xfreerdp3", "xfreerdp", "/usr/bin/xfreerdp3", "/usr/bin/xfreerdp"];

        foreach (string candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return candidate;
            }
            else if (FindOnPath(candidate) is { } onPath)
            {
                return onPath;
            }
        }
        return null;
    }

    private static string? FindOnPath(string exe)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>Reads the certificate policy from <see cref="CertPolicyKey"/>; unknown values fall back to TOFU.</summary>
    internal static RdpCertificatePolicy GetCertificatePolicy(ConnectionParameters p) =>
        p.Extras.TryGetValue(CertPolicyKey, out string? value) && Enum.TryParse(value, ignoreCase: true, out RdpCertificatePolicy policy)
            && Enum.IsDefined(policy)
            ? policy
            : RdpCertificatePolicy.Tofu;

    /// <summary>
    /// Builds the FreeRDP 2.x/3.x argument list. Each element is one argument, so values are
    /// never re-parsed by a shell and cannot inject additional options.
    /// </summary>
    internal static IReadOnlyList<string> BuildFreeRdpArgs(ConnectionParameters p, FreeRdpLaunchOptions? options = null)
    {
        options ??= new FreeRdpLaunchOptions();
        bool v3 = options.MajorVersion >= 3;
        var args = new List<string> { $"/v:{p.Hostname}:{p.Port}" };
        var extras = p.Extras;
        bool Flag(string key, bool defaultValue) =>
            extras.TryGetValue(key, out string? v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : defaultValue;

        // Credentials
        if (!string.IsNullOrEmpty(p.Username)) args.Add($"/u:{p.Username}");
        if (!string.IsNullOrEmpty(p.Domain)) args.Add($"/d:{p.Domain}");
        // Without a password FreeRDP would ask for credentials on its terminal (there is none, so it gives up with
        // "cancelled"). An explicit empty password makes it connect anyway: servers without NLA (xrdp, Windows with
        // NLA disabled) then show their own login screen in the session; NLA servers report an authentication error.
        args.Add(string.IsNullOrEmpty(p.Password) ? "/p:" : $"/p:{p.Password}");

        // Window: embedded into our native window, sized to the tab in device pixels.
        if (options.ParentWindow is { } parent)
            args.Add(string.Create(CultureInfo.InvariantCulture, $"/parent-window:{(ulong)parent}"));
        if (options.Size is { } size)
            args.Add(string.Create(CultureInfo.InvariantCulture, $"/size:{size.Width}x{size.Height}"));
        if (!string.IsNullOrEmpty(options.Title))
            args.Add($"/title:{options.Title}");

        // Display & session. Dynamic resolution makes the server follow window resizes.
        args.Add("/dynamic-resolution");
        args.Add("+auto-reconnect");
        args.Add("/network:auto");
        args.Add(extras.TryGetValue(ConnectionParametersFactory.Keys.RdpColorDepth, out string? depth) ? $"/bpp:{depth}" : "/bpp:32");
        if (Flag(ConnectionParametersFactory.Keys.RdpConsole, false)) args.Add("/admin");
        if (extras.TryGetValue(ConnectionParametersFactory.Keys.RdpLoadBalanceInfo, out string? lb)) args.Add($"/load-balance-info:{lb}");

        // Redirection — opt-in per connection, never the whole filesystem.
        // Audio backends are auto-detected: forcing one (e.g. sys:pulse) makes FreeRDP abort in post-connect
        // when that sound server is not running.
        if (Flag(ConnectionParametersFactory.Keys.RdpClipboard, true)) args.Add("/clipboard");
        if (Flag(ConnectionParametersFactory.Keys.RdpHomeDrive, false)) args.Add("+home-drive");
        if (Flag(ConnectionParametersFactory.Keys.RdpMicrophone, false)) args.Add("/microphone");
        switch (extras.GetValueOrDefault(ConnectionParametersFactory.Keys.RdpSound, "local"))
        {
            case "local": args.Add("/sound"); break;
            case "remote": args.Add("/audio-mode:1"); break;
            default: args.Add("/audio-mode:2"); break;
        }

        // Gateway
        if (extras.TryGetValue(ConnectionParametersFactory.Keys.RdpGateway, out string? gw))
        {
            var gateway = $"/gateway:g:{gw}";
            if (extras.TryGetValue(ConnectionParametersFactory.Keys.RdpGatewayUsername, out string? gu)) gateway += $",u:{gu}";
            if (extras.TryGetValue(ConnectionParametersFactory.Keys.RdpGatewayDomain, out string? gd)) gateway += $",d:{gd}";
            args.Add(gateway);
        }

        // Security. "Use CredSSP" (mstsc semantics) allows NLA rather than forcing it: FreeRDP negotiates
        // NLA > TLS > RDP by default. When disabled, NLA is switched off and TLS/RDP security remain.
        if (!Flag(ConnectionParametersFactory.Keys.RdpNla, true))
            args.Add(v3 ? "/sec:nla:off" : "-sec-nla");

        // Certificate policy. FreeRDP 2 only understands the older /cert-* spellings reliably.
        string policy = GetCertificatePolicy(p) switch
        {
            RdpCertificatePolicy.Ignore => "ignore",
            RdpCertificatePolicy.Deny => "deny",
            _ => "tofu",
        };
        args.Add(v3 ? $"/cert:{policy}" : $"/cert-{policy}");

        return args;
    }

    private static string RedactPassword(string arg) =>
        arg.StartsWith("/p:", StringComparison.Ordinal) && arg.Length > 3 ? "/p:********" : arg;

    private async Task<int> GetFreeRdpMajorVersionAsync(string freerdpBin, CancellationToken ct)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(freerdpBin, "/version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (probe is null) return 0;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string output = await probe.StandardOutput.ReadToEndAsync(timeout.Token);
            await probe.WaitForExitAsync(timeout.Token);
            return ParseFreeRdpMajorVersion(output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not determine FreeRDP version");
            return 0;
        }
    }

    /// <summary>Parses output such as "This is FreeRDP version 3.5.1 (...)".</summary>
    internal static int ParseFreeRdpMajorVersion(string versionOutput)
    {
        var match = VersionRegex().Match(versionOutput);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"version\s+(\d+)\.")]
    private static partial Regex VersionRegex();

    // ── UI marshalling ─────────────────────────────────────────────────────

    private void OnUi(Action<RdpSessionView> action)
    {
        var view = _view;
        if (view is null) return;
        if (Dispatcher.UIThread.CheckAccess())
            action(view);
        else
            Dispatcher.UIThread.Post(() => action(view));
    }

    private async Task OnUiAsync(Action<RdpSessionView> action)
    {
        var view = _view;
        if (view is null) return;
        await Dispatcher.UIThread.InvokeAsync(() => action(view));
    }

    // ── Native helpers ─────────────────────────────────────────────────────

    private const int SigTerm = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);

    private static void SendSigTerm(int pid) => SysKill(pid, SigTerm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed) return;
        _disposed = true;
        _disconnectRequested = true;

        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        process?.Dispose();
        DisposeEmbedSupport();

        // The native window must be destroyed on the UI thread, after FreeRDP's child window is gone.
        if (_view?.Host is { } host)
        {
            if (Dispatcher.UIThread.CheckAccess())
                host.Release();
            else
                Dispatcher.UIThread.Post(host.Release);
        }
    }
}
