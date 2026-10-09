using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>Quick connect prompt; returns a <see cref="QuickConnectResult"/> or null when cancelled.</summary>
public partial class QuickConnectDialog : Window
{
    public QuickConnectDialog()
    {
        InitializeComponent();
        var vm = new QuickConnectViewModel(this);

        // Set the command before binding: ConnectCommand raises no change notification.
        vm.ConnectCommand = ReactiveUI.ReactiveCommand.Create(() =>
        {
            var result = vm.BuildResult();
            if (result is not null)
                Close(result);
        });
        DataContext = vm;

        CancelBtn.Click += (_, _) => Close(null);
        Opened += (_, _) => HostnameBox.Focus();
    }
}
