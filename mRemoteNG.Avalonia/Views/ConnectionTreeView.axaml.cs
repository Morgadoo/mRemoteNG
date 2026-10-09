using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views;

public partial class ConnectionTreeView : UserControl
{
    public ConnectionTreeView()
    {
        InitializeComponent();
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Folders toggle expansion on double-click; only connections open a session.
        if (DataContext is ConnectionTreeViewModel { SelectedNode: { IsFolder: false } } vm)
        {
            ICommand command = vm.ConnectSelectedCommand;
            if (command.CanExecute(null))
                command.Execute(null);
        }
    }
}
