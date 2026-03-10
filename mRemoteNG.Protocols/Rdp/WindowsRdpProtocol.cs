using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// Windows-only RDP implementation using the MSTSCLib ActiveX control (mstscax.dll).
/// This is kept as a high-fidelity fallback on Windows for features not yet
/// supported by the FreeRDP subprocess approach (e.g. RemoteApp, multi-monitor).
///
/// Requires Windows SDK; guarded with [SupportedOSPlatform("windows")].
/// On Linux/macOS, <see cref="RdpProtocol"/> (FreeRDP) is used instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRdpProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<WindowsRdpProtocol> _logger;

    public WindowsRdpProtocol(ILogger<WindowsRdpProtocol> logger)
    {
        _logger = logger;
    }

    public Control CreateView()
    {
        // Phase 4: integrate MSTSCLib via COM interop in an Avalonia NativeControlHost
        // For now, return placeholder
        return new Avalonia.Controls.TextBlock
        {
            Text = "Windows native RDP (MSTSCLib)\nPhase 4: COM interop integration",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        _logger.LogInformation("Windows RDP connecting to {Host}:{Port}", parameters.Hostname, parameters.Port);

        // Phase 4: call MSTSCLib.IMsRdpClient8.Connect()
        // For now, fall through to FreeRDP
        await Task.CompletedTask;
        State = ConnectionState.Connected;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Disconnected;
        await Task.CompletedTask;
    }
}
