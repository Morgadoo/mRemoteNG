using System.Diagnostics;

namespace mRemoteNG.Platform.Linux.Notifications;

/// <summary>
/// Linux desktop notification service using libnotify's notify-send command.
/// Falls back to stderr logging if notify-send is not installed.
/// </summary>
public sealed class LinuxNotificationService : INotificationService
{
    public void ShowNotification(string title, string body, NotificationLevel level = NotificationLevel.Info)
    {
        var urgency = level switch
        {
            NotificationLevel.Warning => "normal",
            NotificationLevel.Error => "critical",
            _ => "low",
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList = { $"--urgency={urgency}", title, body },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            proc?.WaitForExit();
        }
        catch
        {
            // notify-send not available; log to stderr.
            Console.Error.WriteLine($"[{level}] {title}: {body}");
        }
    }

    public void ShowNotification(string title, string body, string actionLabel, Action onAction, NotificationLevel level = NotificationLevel.Info)
    {
        // Action buttons require libnotify's action support or a custom D-Bus service.
        // For now, show the notification without the action button.
        ShowNotification(title, body, level);
    }
}
