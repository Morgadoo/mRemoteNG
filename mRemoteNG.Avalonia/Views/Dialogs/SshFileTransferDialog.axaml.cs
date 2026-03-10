using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class SshFileTransferDialog : Window
{
    public SshFileTransferDialog(SshFileTransferViewModel? viewModel = null)
    {
        InitializeComponent();
        DataContext = viewModel ?? new SshFileTransferViewModel();
    }
}
