using Avalonia.Controls;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.Services;

namespace mRemoteNG.Avalonia.Views.Shell;

/// <summary>Shows the <see cref="Services.ToastService"/> toasts; placed over the main window's content, bottom-right.</summary>
public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        InitializeComponent();
    }

    private async void OnCopyToast(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ToastItem { ErrorToCopy.Length: > 0 } toast })
            await TextClipboard.SetAsync(this, toast.ErrorToCopy);
    }
}
