namespace mRemoteNG.Platform.Mac;

/// <summary>
/// macOS menu bar (status item) service — stub.
/// Full implementation will use Avalonia.Controls.TrayIcon in Phase 2.
/// macOS equivalent is NSStatusItem, accessible via ObjC interop.
/// </summary>
public sealed class MacSystemTrayService : ISystemTrayService
{
    public event EventHandler? Activated;
    public void Initialize(string tooltip, string iconPath) { }
    public void SetTooltip(string tooltip) { }
    public void SetVisible(bool visible) { }
    public void Dispose() { }
}
