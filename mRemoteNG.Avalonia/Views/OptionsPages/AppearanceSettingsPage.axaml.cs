using Avalonia.Controls;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class AppearanceSettingsPage : UserControl
{
    public AppearanceSettingsPage() => InitializeComponent();

    private async void OnOpenThemeEditor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppearanceSettingsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var editor = new ThemeEditorDialog(new ThemeEditorViewModel(
            ThemeService.Instance, AppServices.GetRequired<AppSettingsService>(), vm.SelectedTheme.ThemeName is { Length: > 0 } name ? name : null));
        var saved = await editor.ShowDialog<string?>(owner);

        if (saved is not null)
            vm.SelectThemeByName(saved);
        else
            vm.RefreshThemes();
    }
}
