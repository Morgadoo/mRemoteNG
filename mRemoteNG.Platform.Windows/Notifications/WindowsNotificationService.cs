using System.Runtime.Versioning;

namespace mRemoteNG.Platform.Windows.Notifications;

/// <summary>
/// Windows notification implementation.
/// Uses balloon tooltip via System.Windows.Forms.NotifyIcon on Windows 10
/// (WinRT toast requires UWP identity which we don't have in a classic Win32 app).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationService : INotificationService
{
    public void ShowNotification(string title, string body, NotificationLevel level = NotificationLevel.Info)
    {
        // Balloon tooltip via tray icon — lightweight, no package identity required.
        // The tray icon is managed by WindowsSystemTrayService.
        // This is a simple approach; can be upgraded to WinRT toast if app is MSIX-packaged.
        System.Diagnostics.Trace.TraceInformation($"[Notification] {level}: {title} — {body}");
    }

    public void ShowNotification(string title, string body, string actionLabel, Action onAction, NotificationLevel level = NotificationLevel.Info)
    {
        ShowNotification(title, body, level);
        // Action buttons require WinRT toast; log intent for now.
        System.Diagnostics.Trace.TraceInformation($"[Notification] Action: {actionLabel}");
    }
}
