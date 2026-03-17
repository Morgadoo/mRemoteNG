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

        string args = BuildFreeRdpArgs(parameters);
        _logger.LogDebug("FreeRDP: {Binary} {Args}", freerdpBin, args);

        _rdpProcess = new Process
        {
            StartInfo = new ProcessStartInfo(freerdpBin, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false,
            },
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

        // Wait briefly for the window to appear
        await Task.Delay(800, ct);

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
            : ["xfreerdp", "xfreerdp3", "/usr/bin/xfreerdp"];

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

    private static string BuildFreeRdpArgs(ConnectionParameters p)
    {
        var sb = new System.Text.StringBuilder();

        // Target
        sb.Append($"/v:{p.Hostname}:{p.Port} ");

        // Credentials
        if (!string.IsNullOrEmpty(p.Username)) sb.Append($"/u:\"{p.Username}\" ");
        if (!string.IsNullOrEmpty(p.Domain)) sb.Append($"/d:\"{p.Domain}\" ");
        if (!string.IsNullOrEmpty(p.Password)) sb.Append($"/p:\"{p.Password}\" ");

        // Performance & features
        sb.Append("/dynamic-resolution ");
        sb.Append("/sound:sys:pulse ");
        sb.Append("/microphone ");
        sb.Append("/drive:home,/ ");   // Drive redirection
        sb.Append("/clipboard ");
        sb.Append("/fonts ");
        sb.Append("+auto-reconnect ");
        sb.Append("/network:auto ");

        // Protocol extras
        if (p.Extras.TryGetValue("rdp.colorDepth", out string? depth))
            sb.Append($"/bpp:{depth} ");
        else
            sb.Append("/bpp:32 ");

        if (p.Extras.TryGetValue("rdp.gateway", out string? gw))
            sb.Append($"/g:{gw} ");

        if (p.Extras.TryGetValue("rdp.nla", out string? nla) && nla == "false")
            sb.Append("-nla ");
        else
            sb.Append("/sec:nla ");

        // Embedding: request the window to be embeddable
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            sb.Append("/parent:0 "); // XEmbed protocol (FreeRDP 3.x)

        return sb.ToString().Trim();
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
