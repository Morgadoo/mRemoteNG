using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// RDP protocol implementation using FreeRDP (xfreerdp / wfreerdp) as an
/// external subprocess whose window is embedded into the Avalonia session surface.
///
/// Architecture Decision Record — ADR-006:
///   Approach chosen: subprocess + native window embedding.
///   Rationale:
///     • FreeRDP is the most mature, actively maintained cross-platform RDP client
///     • Embedding via HWND (Windows) / XID (X11) / NSView (macOS) lets Avalonia
///       host the remote desktop inside a regular Control
///     • Avoids reimplementing the RDP stack in managed code
///     • FreeRDP-Sharp bindings exist but are unmaintained; subprocess approach
///       is more stable across FreeRDP versions
///   Trade-offs:
///     • Native embedding is platform-specific (but abstracted here per OS)
///     • Window resize requires sending DISPLAY_UPDATE_REQUEST to xfreerdp
///     • Audio redirection handled by FreeRDP natively
///
/// On Windows: embed using SetParent(hWnd) + Win32 child window positioning.
/// On Linux: embed using XReparentWindow into the Avalonia X11 surface XID.
/// On macOS: embed using NSWindow addChildWindow (AppKit via objc_msgSend).
///
/// Subprocess arguments follow the xfreerdp 3.x command-line interface.
/// </summary>
public sealed class RdpProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<RdpProtocol> _logger;
    private Process? _rdpProcess;
    private RdpEmbedControl? _embedControl;
    private ConnectionParameters? _parameters;

    public RdpProtocol(ILogger<RdpProtocol> logger) => _logger = logger;

    // ── IVisualProtocol ────────────────────────────────────────────────────

    public Control CreateView()
    {
        _embedControl = new RdpEmbedControl(this);
        return _embedControl;
    }

    // ── IProtocol ──────────────────────────────────────────────────────────

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        _parameters = parameters;
        State = ConnectionState.Connecting;
        RaiseStatus($"Launching FreeRDP → {parameters.Hostname}:{parameters.Port}…");

        string? freerdpBin = FindFreeRdpExecutable();
        if (freerdpBin is null)
        {
            State = ConnectionState.Error;
            RaiseStatus("FreeRDP not found. Install xfreerdp (Linux), wfreerdp (Windows), or FreeRDP (macOS via brew).");
            return;
        }

        var args = BuildFreeRdpArgs(parameters,
            OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "winmm" : "pulse");
        bool argsFromStdin = await SupportsArgsFromStdinAsync(freerdpBin, ct);

        var startInfo = new ProcessStartInfo(freerdpBin)
        {
            UseShellExecute = false,
            RedirectStandardInput = argsFromStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };

        if (argsFromStdin)
        {
            // FreeRDP 3: pass everything (including the password) over stdin so it never
            // appears in the process list.
            startInfo.ArgumentList.Add("/args-from:stdin");
        }
        else
        {
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(parameters.Password))
                _logger.LogWarning("FreeRDP 2.x does not support /args-from:stdin; the password is visible to local users in the process list. Upgrade to FreeRDP 3.");
        }

        _logger.LogDebug("FreeRDP: {Binary} {Args}", freerdpBin, string.Join(' ', args.Select(RedactPassword)));

        _rdpProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        _rdpProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) RaiseStatus(e.Data);
        };
        _rdpProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _logger.LogDebug("FreeRDP stderr: {Line}", e.Data);
        };
        _rdpProcess.Exited += (_, _) =>
        {
            int exitCode = _rdpProcess?.ExitCode ?? -1;
            _logger.LogInformation("FreeRDP exited with code {Code}", exitCode);
            if (State == ConnectionState.Connected)
            {
                State = exitCode == 0 ? ConnectionState.Disconnected : ConnectionState.Error;
                RaiseStatus(exitCode == 0 ? "RDP session ended." : $"FreeRDP exited with code {exitCode}.");
            }
        };

        _rdpProcess.Start();
        _rdpProcess.BeginOutputReadLine();
        _rdpProcess.BeginErrorReadLine();

        if (argsFromStdin)
        {
            foreach (string arg in args)
                await _rdpProcess.StandardInput.WriteLineAsync(arg.AsMemory(), ct);
            _rdpProcess.StandardInput.Close();
        }

        // Wait briefly for the window to appear
        await Task.Delay(800, ct);

        if (_rdpProcess.HasExited)
        {
            State = ConnectionState.Error;
            RaiseStatus($"FreeRDP exited immediately with code {_rdpProcess.ExitCode}. Check the host, credentials and certificate.");
            return;
        }

        // Attempt to embed the FreeRDP window into our control
        bool embedded = await TryEmbedWindowAsync(ct);
        if (!embedded)
        {
            // Floating window fallback — still functional, just not embedded
            _logger.LogWarning("FreeRDP window embedding not yet available on this platform. " +
                               "FreeRDP will run in a separate window.");
        }

        State = ConnectionState.Connected;
        RaiseStatus($"RDP connected to {parameters.Hostname}");
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_rdpProcess is { HasExited: false })
        {
            _rdpProcess.Kill();
            await _rdpProcess.WaitForExitAsync(ct);
        }
        State = ConnectionState.Disconnected;
    }

    // ── FreeRDP helpers ────────────────────────────────────────────────────

    private static string? FindFreeRdpExecutable()
    {
        string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ["wfreerdp.exe", "xfreerdp.exe", @"C:\Program Files\FreeRDP\wfreerdp.exe"]
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? ["xfreerdp", "/usr/local/bin/xfreerdp", "/opt/homebrew/bin/xfreerdp"]
            : ["xfreerdp3", "xfreerdp", "/usr/bin/xfreerdp"];

        foreach (string candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return candidate;
            }
            else
            {
                // Search PATH
                string? onPath = FindOnPath(candidate);
                if (onPath is not null) return onPath;
            }
        }
        return null;
    }

    private static string? FindOnPath(string exe)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            string full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// Builds the FreeRDP 2.x/3.x argument list. Each element is one argument, so values are
    /// never re-parsed by a shell and cannot inject additional options.
    /// </summary>
    internal static IReadOnlyList<string> BuildFreeRdpArgs(ConnectionParameters p, string audioBackend = "pulse")
    {
        var args = new List<string> { $"/v:{p.Hostname}:{p.Port}" };
        var extras = p.Extras;
        bool Flag(string key, bool defaultValue) =>
            extras.TryGetValue(key, out string? v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : defaultValue;

        // Credentials
        if (!string.IsNullOrEmpty(p.Username)) args.Add($"/u:{p.Username}");
        if (!string.IsNullOrEmpty(p.Domain)) args.Add($"/d:{p.Domain}");
        if (!string.IsNullOrEmpty(p.Password)) args.Add($"/p:{p.Password}");

        // Display & session
        args.Add("/dynamic-resolution");
        args.Add("+auto-reconnect");
        args.Add("/network:auto");
        args.Add(extras.TryGetValue(ConnectionParametersFactory.Keys.RdpColorDepth, out string? depth) ? $"/bpp:{depth}" : "/bpp:32");
        if (Flag(ConnectionParametersFactory.Keys.RdpConsole, false)) args.Add("/admin");
        if (extras.TryGetValue(ConnectionParametersFactory.Keys.RdpLoadBalanceInfo, out string? lb)) args.Add($"/load-balance-info:{lb}");

        // Redirection — opt-in per connection, never the whole filesystem.
        if (Flag(ConnectionParametersFactory.Keys.RdpClipboard, true)) args.Add("/clipboard");
        if (Flag(ConnectionParametersFactory.Keys.RdpHomeDrive, false)) args.Add("+home-drive");
        if (Flag(ConnectionParametersFactory.Keys.RdpMicrophone, false)) args.Add($"/microphone:sys:{audioBackend}");
        switch (extras.GetValueOrDefault(ConnectionParametersFactory.Keys.RdpSound, "local"))
        {
            case "local": args.Add($"/sound:sys:{audioBackend}"); break;
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

        // Security: NLA unless explicitly disabled.
        args.Add(Flag(ConnectionParametersFactory.Keys.RdpNla, true) ? "/sec:nla" : "-sec-nla");

        return args;
    }

    private static string RedactPassword(string arg) => arg.StartsWith("/p:", StringComparison.Ordinal) ? "/p:********" : arg;

    /// <summary>FreeRDP 3.x supports <c>/args-from:stdin</c>; 2.x does not.</summary>
    private async Task<bool> SupportsArgsFromStdinAsync(string freerdpBin, CancellationToken ct)
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
            if (probe is null) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string output = await probe.StandardOutput.ReadToEndAsync(timeout.Token);
            await probe.WaitForExitAsync(timeout.Token);
            return ParseFreeRdpMajorVersion(output) >= 3;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not determine FreeRDP version");
            return false;
        }
    }

    /// <summary>Parses output such as "This is FreeRDP version 3.5.1 (...)".</summary>
    internal static int ParseFreeRdpMajorVersion(string versionOutput)
    {
        var match = System.Text.RegularExpressions.Regex.Match(versionOutput, @"version\s+(\d+)\.");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private async Task<bool> TryEmbedWindowAsync(CancellationToken ct)
    {
        // Phase 4: implement platform-specific window embedding
        // - Windows: user32.SetParent + MoveWindow
        // - Linux X11: XReparentWindow
        // - macOS: NSView addSubview
        // For Phase 3, FreeRDP runs as a floating window.
        await Task.CompletedTask;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_rdpProcess is { HasExited: false })
                _rdpProcess.Kill();
            _rdpProcess?.Dispose();
        }
    }
}

/// <summary>
/// Avalonia control placeholder for the embedded FreeRDP window.
/// Displays a status message while embedding is being established
/// or when embedding is not available on this platform.
/// </summary>
internal sealed class RdpEmbedControl : UserControl
{
    private readonly RdpProtocol _protocol;

    public RdpEmbedControl(RdpProtocol protocol)
    {
        _protocol = protocol;
        Background = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
        Content = new Avalonia.Controls.TextBlock
        {
            Text = "RDP session is running in a separate window.\n" +
                   "Window embedding will be available in Phase 4.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        };
    }
}
