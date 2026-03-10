using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class CredentialManagerDialog : Window
{
    public CredentialManagerDialog(CredentialManagerViewModel? viewModel = null)
    {
        InitializeComponent();
        DataContext = viewModel ?? new CredentialManagerViewModel();
    }
}
