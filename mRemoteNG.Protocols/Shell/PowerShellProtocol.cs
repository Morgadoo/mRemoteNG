using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Shell;

/// <summary>
/// Cross-platform PowerShell protocol: runs <c>pwsh</c> (PowerShell 7+; <c>powershell.exe</c> on Windows
/// when pwsh is missing) and shows it in the shared <see cref="TerminalView"/>.
///
/// When <see cref="ConnectionParameters.Hostname"/> is not blank, "localhost", "127.0.0.1" or "::1",
/// the session runs <c>Enter-PSSession -ComputerName {host} [-Credential {user}]</c>.
///
/// How pwsh is hosted (<see cref="PowerShellHostMode"/>):
///   • Linux          → on a real pseudo-terminal (<see cref="UnixPseudoTerminal"/>), TERM=xterm-256color;
///                      Enter, PSReadLine, colours and window-size changes work as in any terminal.
///   • macOS (or Linux if the PTY cannot be created) → under <c>script(1)</c>, which gives pwsh a
///                      pseudo-terminal sized like the view when the session starts (later size changes are
///                      not forwarded).
///   • Windows, or no PTY available → plain pipes. Enter (<c>\r</c>) is translated to <c>\n</c> and
///                      formatted output is rendered at the view's width with <c>Out-String</c> (pwsh reports
///                      a window width of -1 on pipes and would otherwise print blank lines). There is no
///                      PSReadLine: typed characters are not echoed until Enter, when pwsh prints the line.
/// </summary>
public sealed class PowerShellProtocol : ProtocolBase, IVisualProtocol, ITerminalProtocol
{
    private readonly ILogger<PowerShellProtocol> _logger;
    private readonly object _inputLock = new();
    private UnixPseudoTerminal? _pty;
    private Process? _process;
    private PowerShellHostMode _mode;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;
    private Decoder? _ptyDecoder;
    private bool _lastInputWasCr;

    public PowerShellProtocol(ILogger<PowerShellProtocol> logger) => _logger = logger;

    /// <summary>How the running session is hosted (diagnostics and tests).</summary>
    internal PowerShellHostMode Mode => _mode;

    /// <summary>Tests only: host pwsh this way instead of the platform's best option.</summary>
    internal PowerShellHostMode? ForcedHostMode { get; set; }

    public Control CreateView()
    {
        _view = new TerminalView();
        _view.DataToSend += OnDataToSend;
        _view.TerminalResized += OnTerminalResized;
        return _view;
    }

    public override Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        RaiseStatus("Launching PowerShell…");

        string pwshBin = FindPowerShell();
        bool isRemote = !IsLocalhost(parameters.Hostname);
        int cols = _view?.TerminalCols ?? 80;
        int rows = _view?.TerminalRows ?? 24;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            if (ForcedHostMode is null or PowerShellHostMode.PseudoTerminal
                && UnixPseudoTerminal.IsSupported
                && TryStartOnPseudoTerminal(pwshBin, parameters, cols, rows))
                _mode = PowerShellHostMode.PseudoTerminal;
            else
                StartProcess(pwshBin, parameters, cols, rows);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            RaiseStatus($"Could not start {pwshBin}: {ex.Message}");
            _view?.Write($"Could not start {pwshBin}: {ex.Message}\r\n");
            _logger.LogError(ex, "PowerShell start failed");
            throw;
        }

        State = ConnectionState.Connected;
        RaiseStatus(isRemote ? $"PowerShell → {parameters.Hostname}" : "PowerShell (local)");
        return Task.CompletedTask;
    }

    private bool TryStartOnPseudoTerminal(string pwshBin, ConnectionParameters parameters, int cols, int rows)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value ?? "";
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        environment.Remove("COLUMNS"); // the terminal size comes from the PTY
        environment.Remove("LINES");

        try
        {
            _pty = UnixPseudoTerminal.Start(pwshBin, BuildArguments(parameters, PowerShellHostMode.PseudoTerminal, cols), environment, cols, rows);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not start PowerShell on a pseudo-terminal; falling back to script(1) or pipes");
            return false;
        }

        _ptyDecoder = new UTF8Encoding(false).GetDecoder();
        _pty.StartReading(OnPtyData, OnPtyExited);
        return true;
    }

    private void OnPtyData(byte[] buffer, int count)
    {
        var chars = new char[_ptyDecoder!.GetCharCount(buffer, 0, count)];
        int decoded = _ptyDecoder.GetChars(buffer, 0, count, chars, 0);
        if (decoded > 0)
            _view?.Write(new string(chars, 0, decoded));
    }

    private void OnPtyExited(int? exitCode)
    {
        State = ConnectionState.Disconnected;
        RaiseStatus("PowerShell session ended.");
    }

    private void StartProcess(string pwshBin, ConnectionParameters parameters, int cols, int rows)
    {
        ProcessStartInfo psi;
        var script = OperatingSystem.IsWindows() || ForcedHostMode == PowerShellHostMode.Pipes
            ? null
            : LocalShellProtocol.FindOnPath("script");
        if (script is not null)
        {
            _mode = PowerShellHostMode.Script;
            psi = LocalShellProtocol.CreateStartInfo([pwshBin, .. BuildArguments(parameters, _mode, cols)], cols, rows);
        }
        else
        {
            _mode = PowerShellHostMode.Pipes;
            psi = new ProcessStartInfo(pwshBin)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in BuildArguments(parameters, _mode, cols))
                psi.ArgumentList.Add(argument);
        }
        psi.Environment["TERM"] = "xterm-256color";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) =>
        {
            State = ConnectionState.Disconnected;
            RaiseStatus("PowerShell session ended.");
        };
        _process.Start();

        _ = ReadLoopAsync(_process.StandardOutput, _cts!.Token);
        _ = ReadLoopAsync(_process.StandardError, _cts.Token);
    }

    /// <summary>pwsh command-line arguments (after the program name) for a session hosted in <paramref name="mode"/>.</summary>
    internal static IReadOnlyList<string> BuildArguments(ConnectionParameters parameters, PowerShellHostMode mode, int columns)
    {
        var args = new List<string> { "-NoLogo", "-NoProfile" };
        var commands = new List<string>();

        if (mode == PowerShellHostMode.Pipes)
            commands.Add(PipeOutputSetup(columns));

        if (!IsLocalhost(parameters.Hostname))
        {
            var enter = $"Enter-PSSession -ComputerName {QuoteLiteral(parameters.Hostname.Trim())}";
            if (!string.IsNullOrEmpty(parameters.Username))
                enter += $" -Credential {QuoteLiteral(parameters.Username)}";
            commands.Add(enter);
        }

        if (commands.Count > 0)
        {
            // -NoExit keeps the interactive session (and the remote session entered by the command) open.
            args.AddRange(["-NoExit", "-Command", string.Join("; ", commands)]);
        }
        else
        {
            args.Add("-Interactive");
        }
        return args;
    }

    /// <summary>
    /// Without a console pwsh reports a window width of -1, so the formatter truncates every table/list line
    /// to nothing. Route the host's Out-Default through Out-String with an explicit width instead.
    /// </summary>
    internal static string PipeOutputSetup(int columns)
    {
        int width = Math.Max(columns - 1, 40);
        return "function global:Out-Default { [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)] $InputObject, [switch] $Transcript) " +
               "begin { $p = { Microsoft.PowerShell.Utility\\Out-String -Stream -Width " + width.ToString(CultureInfo.InvariantCulture) +
               " | Microsoft.PowerShell.Core\\Out-Default }.GetSteppablePipeline(); $p.Begin($PSCmdlet) } " +
               "process { $p.Process($InputObject) } end { $p.End() } }";
    }

    /// <summary>A PowerShell single-quoted string literal.</summary>
    internal static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        if (_pty is not null)
        {
            var pty = _pty;
            await Task.Run(() => pty.Terminate(TimeSpan.FromSeconds(3)), ct);
        }
        else if (IsProcessRunning())
        {
            try
            {
                await _process!.StandardInput.WriteAsync("exit\n");
                await _process.StandardInput.FlushAsync(ct);
                await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                try { _process!.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
        State = ConnectionState.Disconnected;
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        char[] buf = new char[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buf, ct);
                if (read == 0) break;
                _view?.Write(new string(buf, 0, read));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell read error");
        }
    }

    private void OnTerminalResized(object? sender, TerminalSizeEventArgs e) =>
        ResizeTerminal(e.Columns, e.Rows, e.PixelWidth, e.PixelHeight);

    /// <summary>Tells pwsh the terminal size (pseudo-terminal sessions only; pipes have no window size).</summary>
    internal void ResizeTerminal(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        try
        {
            _pty?.Resize(columns, rows, pixelWidth, pixelHeight);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "PowerShell resize failed");
        }
    }

    private void OnDataToSend(object? sender, byte[] data) => WriteInput(data);

    /// <summary>Writes input to PowerShell as if typed (Enter is "\r"); ignored once it has exited.</summary>
    public Task SendInputAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        WriteInput(data);
        return Task.CompletedTask;
    }

    private void WriteInput(byte[] data)
    {
        try
        {
            if (_pty is not null)
            {
                if (!_pty.HasExited)
                    _pty.Write(data);
                return;
            }

            if (!IsProcessRunning()) return;
            lock (_inputLock)
            {
                // Under script(1) the terminal driver turns CR into NL; on plain pipes pwsh waits for "\n".
                var bytes = _mode == PowerShellHostMode.Pipes ? TranslateEnter(data, ref _lastInputWasCr) : data;
                _process!.StandardInput.BaseStream.Write(bytes);
                _process.StandardInput.BaseStream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "PowerShell write error");
        }
    }

    /// <summary>Maps the terminal's Enter (CR, or CR LF) to the LF a line-reading pwsh expects.</summary>
    internal static byte[] TranslateEnter(byte[] data, ref bool lastWasCr)
    {
        var output = new List<byte>(data.Length);
        foreach (byte b in data)
        {
            if (b == (byte)'\r')
            {
                output.Add((byte)'\n');
                lastWasCr = true;
                continue;
            }
            if (b == (byte)'\n' && lastWasCr)
            {
                lastWasCr = false;
                continue;
            }
            lastWasCr = false;
            output.Add(b);
        }
        return [.. output];
    }

    private bool IsProcessRunning()
    {
        try
        {
            return _process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string FindPowerShell()
    {
        // Try pwsh (PowerShell 7+) first, fall back to powershell.exe on Windows
        string[] candidates = OperatingSystem.IsWindows()
            ? ["pwsh.exe", "powershell.exe"]
            : ["pwsh"];

        foreach (string exe in candidates)
        {
            string? path = LocalShellProtocol.FindOnPath(exe);
            if (path is not null) return path;
        }
        return candidates[0]; // Let the OS resolve it (and report it missing)
    }

    private static bool IsLocalhost(string? host) =>
        string.IsNullOrWhiteSpace(host) || host.Trim() is "localhost" or "127.0.0.1" or "::1";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            if (_view is not null)
            {
                _view.DataToSend -= OnDataToSend;
                _view.TerminalResized -= OnTerminalResized;
            }
            _pty?.Dispose();
            if (IsProcessRunning())
            {
                try { _process!.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            _process?.Dispose();
        }
    }
}

/// <summary>How <see cref="PowerShellProtocol"/> connects pwsh to the terminal view.</summary>
internal enum PowerShellHostMode
{
    /// <summary>A pseudo-terminal owned by mRemoteNG (Linux): full terminal behaviour, resizable.</summary>
    PseudoTerminal,

    /// <summary>A pseudo-terminal created by <c>script(1)</c> (macOS): fixed size.</summary>
    Script,

    /// <summary>Redirected standard streams (Windows, or when no pseudo-terminal is available).</summary>
    Pipes,
}
