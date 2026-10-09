using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Shows SSH prompts (host keys, username, password, keyboard-interactive) as modal dialogs over
/// the active window.
/// </summary>
public sealed class AvaloniaSshUserPrompt : ISshUserPrompt
{
    /// <summary>
    /// Called on SSH.NET's background thread while it waits for <c>HostKeyReceived</c> to return:
    /// shows the dialog on the UI thread and blocks this thread until the user answers.
    /// </summary>
    public HostKeyDecision ConfirmHostKey(HostKeyPromptRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("Host key confirmation blocks and must not run on the UI thread.");

        return Dispatcher.UIThread
            .InvokeAsync(() => ShowAsync(() => new HostKeyDialog(request), HostKeyDecision.Reject))
            .GetAwaiter()
            .GetResult();
    }

    public Task<string?> PromptTextAsync(SshTextPrompt prompt, CancellationToken ct = default) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowAsync<string?>(
            () => new TextPromptDialog(prompt.Title, prompt.Message, prompt.IsSecret, prompt.Watermark, null),
            null));

    private static async Task<T> ShowAsync<T>(Func<Window> createDialog, T fallback)
    {
        var owner = FindOwner();
        if (owner is null)
            return fallback; // no window to attach a modal dialog to (e.g. during shutdown)

        var result = await createDialog().ShowDialog<T?>(owner);
        return result ?? fallback;
    }

    /// <summary>The active window (e.g. the SFTP dialog), otherwise the main window.</summary>
    private static Window? FindOwner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? desktop.MainWindow;
    }
}
