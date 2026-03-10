using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class SshFileTransferDialog : Window
{
    public SshFileTransferDialog() : this(null) { }
    public SshFileTransferDialog(SshFileTransferViewModel? viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? new SshFileTransferViewModel();
    }
}
