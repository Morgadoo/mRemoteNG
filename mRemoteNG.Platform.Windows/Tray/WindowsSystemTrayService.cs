using System.Runtime.Versioning;

namespace mRemoteNG.Platform.Windows;

/// <summary>
/// Windows system tray service — stub implementation.
/// Full implementation will use Avalonia.Controls.TrayIcon in the Avalonia UI project.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemTrayService : ISystemTrayService
{
    public event EventHandler? Activated;

    public void Initialize(string tooltip, string iconPath)
    {
        // Will be wired to Avalonia TrayIcon in Phase 2.
        System.Diagnostics.Trace.TraceInformation($"[Tray] Initialize: {tooltip}");
    }

    public void SetTooltip(string tooltip) =>
        System.Diagnostics.Trace.TraceInformation($"[Tray] SetTooltip: {tooltip}");

    public void SetVisible(bool visible) =>
        System.Diagnostics.Trace.TraceInformation($"[Tray] SetVisible: {visible}");

    public void Dispose() { }
}
