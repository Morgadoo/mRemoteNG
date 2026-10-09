using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// File ▸ Properties (master password and encryption of the open connection file).
/// <see cref="Window.ShowDialog{TResult}(Window)"/> with <c>bool</c> returns true when the user pressed OK
/// with valid values; the caller then calls <see cref="FilePropertiesViewModel.Apply"/>.
/// </summary>
public partial class FilePropertiesDialog : Window
{
    public FilePropertiesDialog()
    {
        InitializeComponent();
    }

    public FilePropertiesDialog(FilePropertiesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += ok => Close(ok);
        Opened += (_, _) => RootNameBox.Focus();
    }
}
