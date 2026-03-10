using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog(ConnectionDialogViewModel? viewModel = null)
    {
        InitializeComponent();
        DataContext = viewModel ?? new ConnectionDialogViewModel();
    }
}
