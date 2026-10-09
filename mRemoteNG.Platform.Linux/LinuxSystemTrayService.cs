namespace mRemoteNG.Platform.Linux;

/// <summary>
/// Linux system tray service — stub.
/// Full implementation will use Avalonia.Controls.TrayIcon (Phase 2).
/// Linux tray requires StatusNotifierItem protocol (D-Bus) supported
/// by KDE Plasma, GNOME (with AppIndicator extension), etc.
/// </summary>
public sealed class LinuxSystemTrayService : ISystemTrayService
{
    public event EventHandler? Activated;

    public void Initialize(string tooltip, string iconPath) { }
    public void SetTooltip(string tooltip) { }
    public void SetVisible(bool visible) { }
    public void Dispose() { }
}
