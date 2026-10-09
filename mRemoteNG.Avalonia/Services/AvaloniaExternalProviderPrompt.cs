using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.ExternalProviders;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Asks for unsaved external provider secrets (passwords, API keys, OTP codes) with a modal
/// dialog over the active window — the Options window during a test, otherwise the main window.
/// </summary>
public sealed class AvaloniaExternalProviderPrompt : IExternalProviderPrompt
{
    public Task<string?> PromptAsync(ExternalProviderPromptRequest request, CancellationToken ct = default) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = FindOwner();
            if (owner is null)
                return null; // no window to attach a modal dialog to (e.g. during shutdown)

            var dialog = new TextPromptDialog(request.Title, request.Message, request.IsSecret, request.Watermark, null);
            await using var registration = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            return await dialog.ShowDialog<string?>(owner);
        });

    private static Window? FindOwner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? desktop.MainWindow;
    }
}
