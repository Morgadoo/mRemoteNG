using Avalonia.Controls;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class PluginsSettingsPage : UserControl
{
    public PluginsSettingsPage() => InitializeComponent();

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginsSettingsViewModel vm)
            return;
        try
        {
            Directory.CreateDirectory(vm.UserPluginFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        await FileLauncher.RevealAsync(TopLevel.GetTopLevel(this), vm.UserPluginFolder);
    }
}
