using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// "Connect with options". <see cref="Window.ShowDialog{TResult}(Window)"/> with <c>bool</c> returns true
/// to connect with <see cref="ConnectWithOptionsViewModel.ToOptions"/>.
/// </summary>
public partial class ConnectWithOptionsDialog : Window
{
    public ConnectWithOptionsDialog() : this(new ConnectWithOptionsViewModel("Connection", ["General"]))
    {
    }

    public ConnectWithOptionsDialog(ConnectWithOptionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += ok => Close(ok);
        Opened += (_, _) => NoCredentialsBox.Focus();
    }

    /// <summary>Opens the dialog with the panel box focused (legacy "Choose panel before connecting").</summary>
    public void FocusPanel() => Opened += (_, _) => PanelBox.Focus();
}
