using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class SavingSettingsPage : UserControl
{
    public SavingSettingsPage() => InitializeComponent();

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SavingSettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Backup folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.BackupDirectory = path;
    }

    private void OnResetFormat(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SavingSettingsViewModel vm)
            vm.ResetNameFormat();
    }
}
