using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class CredentialManagerDialog : Window
{
    public CredentialManagerDialog() : this(null) { }
    public CredentialManagerDialog(CredentialManagerViewModel? viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? new CredentialManagerViewModel();
    }
}
