using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class QuickConnectDialog : Window
{
    public QuickConnectDialog()
    {
        InitializeComponent();
        var vm = new QuickConnectViewModel(this);
        DataContext = vm;

        CancelBtn.Click += (_, _) => Close(null);
        HostnameBox.AttachedToVisualTree += (_, _) => HostnameBox.Focus();

        // Wire Connect button to close dialog and return result
        vm.ConnectCommand = ReactiveUI.ReactiveCommand.Create(() =>
        {
            var result = vm.BuildResult();
            if (result is not null)
                Close(result);
        });
    }
}
