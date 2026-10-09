using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Asks for a password. <see cref="Window.ShowDialog{TResult}(Window)"/> returns the password,
/// or null when cancelled.
/// </summary>
public partial class PasswordPromptDialog : Window
{
    public PasswordPromptDialog() : this("Enter the password.", null)
    {
    }

    public PasswordPromptDialog(string message, string? error)
    {
        InitializeComponent();
        MessageText.Text = message;
        ErrorText.Text = error;
        ErrorText.IsVisible = !string.IsNullOrEmpty(error);

        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click += (_, _) => Close(PasswordBox.Text ?? "");
        Opened += (_, _) => PasswordBox.Focus();
    }
}
