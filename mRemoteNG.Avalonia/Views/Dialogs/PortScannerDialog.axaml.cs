using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class PortScannerDialog : Window
{
    public PortScannerDialog() : this(null) { }
    public PortScannerDialog(PortScannerViewModel? viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? new PortScannerViewModel();
    }
}
