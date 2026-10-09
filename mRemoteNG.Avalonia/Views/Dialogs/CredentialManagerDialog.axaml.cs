using Avalonia.Controls;
using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class CredentialManagerDialog : Window
{
    public CredentialManagerDialog() : this(null) { }

    public CredentialManagerDialog(CredentialManagerViewModel? viewModel)
    {
        InitializeComponent();
        var vm = viewModel ?? AppServices.GetRequired<CredentialManagerViewModel>();
        DataContext = vm;
        vm.CloseRequested += Close;
        // A new credential is named first.
        var focusNewName = vm.AddCommand.Subscribe(_ => Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, DispatcherPriority.Loaded));
        Closed += (_, _) => focusNewName.Dispose();
    }
}
