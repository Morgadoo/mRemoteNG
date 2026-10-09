using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Shell;

/// <summary>Shows the <see cref="Services.ToastService"/> toasts; placed over the main window's content, bottom-right.</summary>
public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        InitializeComponent();
    }
}
