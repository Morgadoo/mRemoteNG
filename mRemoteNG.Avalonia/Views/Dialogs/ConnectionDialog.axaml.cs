using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Connection;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Connection / folder property editor. <see cref="Window.ShowDialog{TResult}(Window)"/> with
/// <c>bool</c> returns true when the user pressed OK; the caller then calls
/// <see cref="ConnectionDialogViewModel.Apply"/>.
/// </summary>
public partial class ConnectionDialog : Window
{
    /// <summary>Designer/XAML loader constructor: edits a throw-away connection.</summary>
    public ConnectionDialog()
        : this(new ConnectionDialogViewModel(ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo()), null, isNew: true))
    {
    }

    public ConnectionDialog(ConnectionDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += ok => Close(ok);
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }
}
