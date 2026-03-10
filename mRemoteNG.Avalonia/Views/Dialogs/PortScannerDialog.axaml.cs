using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class PortScannerDialog : Window
{
    public PortScannerDialog(PortScannerViewModel? viewModel = null)
    {
        InitializeComponent();
        DataContext = viewModel ?? new PortScannerViewModel();
    }
}
