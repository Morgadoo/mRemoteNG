using System.Diagnostics;

namespace mRemoteNG.Platform.Mac.Notifications;

/// <summary>
/// macOS notification service using osascript to display native notifications.
/// For sandboxed .app bundles, UNUserNotificationCenter via ObjC interop is preferred.
/// </summary>
public sealed class MacNotificationService : INotificationService
{
    public void ShowNotification(string title, string body, NotificationLevel level = NotificationLevel.Info)
    {
        try
        {
            // Use osascript to display a native macOS notification.
            var script = $"display notification \"{EscapeAppleScript(body)}\" with title \"{EscapeAppleScript(title)}\"";
            using var proc = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            proc?.WaitForExit();
        }
        catch
        {
            Console.Error.WriteLine($"[{level}] {title}: {body}");
        }
    }

    public void ShowNotification(string title, string body, string actionLabel, Action onAction, NotificationLevel level = NotificationLevel.Info) =>
        ShowNotification(title, body, level);

    private static string EscapeAppleScript(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
