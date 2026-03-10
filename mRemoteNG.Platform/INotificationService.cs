namespace mRemoteNG.Platform;

/// <summary>
/// Abstracts desktop notification / toast popups across platforms.
/// On Windows: uses WinRT toast notifications (Windows 10+).
/// On Linux: uses libnotify via D-Bus.
/// On macOS: uses NSUserNotification / UNUserNotificationCenter.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows a desktop notification with the given title and body text.</summary>
    void ShowNotification(string title, string body, NotificationLevel level = NotificationLevel.Info);

    /// <summary>Shows a notification with an action button.</summary>
    void ShowNotification(string title, string body, string actionLabel, Action onAction, NotificationLevel level = NotificationLevel.Info);
}

public enum NotificationLevel
{
    Info,
    Warning,
    Error,
}
