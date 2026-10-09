using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class ExternalProvidersSettingsPage : UserControl
{
    public ExternalProvidersSettingsPage() => InitializeComponent();

    private async void OnBrowseOnePasswordCli(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExternalProvidersSettingsViewModel vm && await PickFileAsync("1Password CLI (op)") is { } path)
            vm.OnePasswordCliPath = path;
    }

    private async void OnBrowseVaultCa(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExternalProvidersSettingsViewModel vm && await PickFileAsync(Localizer.Get("CACertificatePEM")) is { } path)
            vm.VaultCaCertificatePath = path;
    }

    private async Task<string?> PickFileAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
