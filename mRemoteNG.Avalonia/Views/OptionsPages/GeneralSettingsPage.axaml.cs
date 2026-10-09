using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class GeneralSettingsPage : UserControl
{
    public GeneralSettingsPage() => InitializeComponent();

    private async void OnBrowseStartupFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Connection file to open at startup",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("mRemoteNG connection files") { Patterns = ["*.xml"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            vm.StartupFilePath = path;
    }
}
