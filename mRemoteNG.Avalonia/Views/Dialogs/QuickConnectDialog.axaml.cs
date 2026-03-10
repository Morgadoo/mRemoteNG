using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class QuickConnectDialog : Window
{
    public QuickConnectDialog()
    {
        InitializeComponent();
        DataContext = new QuickConnectViewModel(this);
        CancelBtn.Click += (_, _) => Close(null);
        HostnameBox.AttachedToVisualTree += (_, _) => HostnameBox.Focus();
    }
}
