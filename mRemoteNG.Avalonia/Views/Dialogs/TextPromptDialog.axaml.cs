using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Asks for one line of text (username, password, folder name…).
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns the text, or null when cancelled.
/// </summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog() : this("Input Required", "Enter a value.", false, null, null)
    {
    }

    public TextPromptDialog(string title, string message, bool isSecret, string? watermark, string? initialText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputBox.Watermark = watermark;
        InputBox.Text = initialText;
        if (isSecret)
            InputBox.PasswordChar = '●';

        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click += (_, _) => Close(InputBox.Text ?? string.Empty);
        Opened += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }
}
