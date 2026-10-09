using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class LoggingSettingsPage : UserControl
{
    public LoggingSettingsPage() => InitializeComponent();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggingSettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Get("LogFileTitle"),
            SuggestedFileName = "mRemoteNG.log",
            ShowOverwritePrompt = false,
        });

        if (file?.TryGetLocalPath() is { } path)
            vm.LogFilePath = path;
    }

    private async void OnOpenLogFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoggingSettingsViewModel vm)
            await FileLauncher.OpenFileAsync(TopLevel.GetTopLevel(this), vm.EffectiveLogFilePath);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoggingSettingsViewModel vm)
            await FileLauncher.RevealAsync(TopLevel.GetTopLevel(this), vm.EffectiveLogFilePath);
    }
}
