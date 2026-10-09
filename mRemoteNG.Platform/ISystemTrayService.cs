namespace mRemoteNG.Platform;

/// <summary>
/// Abstracts system tray / status-bar icon across platforms.
/// On Windows: uses Shell_NotifyIcon (Win32) or Avalonia's TrayIcon.
/// On Linux: uses StatusNotifierItem (D-Bus / AppIndicator).
/// On macOS: uses NSStatusItem.
/// All implementations delegate to Avalonia.Controls.TrayIcon where available.
/// </summary>
public interface ISystemTrayService : IDisposable
{
    /// <summary>Creates the tray icon and makes it visible.</summary>
    void Initialize(string tooltip, string iconPath);

    /// <summary>Updates the tooltip text shown on hover.</summary>
    void SetTooltip(string tooltip);

    /// <summary>Fired when the user left-clicks or activates the tray icon.</summary>
    event EventHandler? Activated;

    /// <summary>Shows or hides the tray icon.</summary>
    void SetVisible(bool visible);
}
