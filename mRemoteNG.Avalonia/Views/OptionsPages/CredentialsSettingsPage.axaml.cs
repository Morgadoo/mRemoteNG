using Avalonia.Controls;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.Dialogs;

namespace mRemoteNG.Avalonia.Views.OptionsPages;

public partial class CredentialsSettingsPage : UserControl
{
    public CredentialsSettingsPage() => InitializeComponent();

    private async void OnManageCredentials(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var dialog = new CredentialManagerDialog(AppServices.GetRequired<CredentialManagerViewModel>());
        await dialog.ShowDialog(owner);

        if (DataContext is CredentialsSettingsViewModel vm)
            vm.RefreshSummary();
    }
}
