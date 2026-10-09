using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class ConnectionSettingsPage : UserControl
{
    public ConnectionSettingsPage() => InitializeComponent();

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionSettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Default SSH private key",
            AllowMultiple = false,
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            vm.SshKeyPath = path;
    }
}
