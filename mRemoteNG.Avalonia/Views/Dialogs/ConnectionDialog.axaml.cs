using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog() : this(null) { }

    public ConnectionDialog(ConnectionDialogViewModel? viewModel)
    {
        InitializeComponent();
        var vm = viewModel ?? new ConnectionDialogViewModel();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}
