using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Yes/no confirmation. <see cref="Window.ShowDialog{TResult}(Window)"/> returns true when confirmed.
/// Cancel is the default button.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("Confirm", "Are you sure?", "OK")
    {
    }

    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        OkButton.Content = confirmText;

        CancelButton.Click += (_, _) => Close(false);
        OkButton.Click += (_, _) => Close(true);
    }
}
