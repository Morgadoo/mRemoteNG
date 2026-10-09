using Avalonia.Controls;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class UpdatesSettingsPage : UserControl
{
    public UpdatesSettingsPage() => InitializeComponent();

    private async void OnOpenDownload(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UpdatesSettingsViewModel { DownloadedFile: { } file })
            return;

        // An AppImage is run by the user, not opened by a handler: show it in its folder instead.
        if (file.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
            || !await FileLauncher.OpenFileAsync(TopLevel.GetTopLevel(this), file))
        {
            await FileLauncher.RevealAsync(TopLevel.GetTopLevel(this), file);
        }
    }

    private async void OnRevealDownload(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpdatesSettingsViewModel { DownloadedFile: { } file })
            await FileLauncher.RevealAsync(TopLevel.GetTopLevel(this), file);
    }
}
