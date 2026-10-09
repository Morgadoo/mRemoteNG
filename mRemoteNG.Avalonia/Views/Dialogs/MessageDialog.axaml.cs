using Avalonia.Controls;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>A button shown by <see cref="MessageDialog"/>.</summary>
public sealed record MessageDialogButton(string Label, string Result, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// Simple message box. <see cref="Window.ShowDialog{TResult}(Window)"/> returns the
/// <see cref="MessageDialogButton.Result"/> of the clicked button, or null when the window was closed.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog() : this("mRemoteNG", string.Empty, new MessageDialogButton(Localizer.Get("_Ok"), "ok", IsDefault: true))
    {
    }

    public MessageDialog(string title, string message, params MessageDialogButton[] buttons)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        foreach (var button in buttons)
        {
            var control = new Button
            {
                Content = button.Label,
                MinWidth = 80,
                IsDefault = button.IsDefault,
                IsCancel = button.IsCancel,
            };
            if (button.IsDefault)
                control.Classes.Add("accent");
            control.Click += (_, _) => Close(button.Result);
            ButtonPanel.Children.Add(control);
        }

        Opened += (_, _) =>
        {
            foreach (var child in ButtonPanel.Children)
            {
                if (child is Button { IsDefault: true } defaultButton)
                    defaultButton.Focus();
            }
        };
    }

    /// <summary>Asks a yes/no question; true for Yes.</summary>
    /// <param name="yesLabel">The confirming button; "Yes" (in the UI language) when null.</param>
    /// <param name="noLabel">The cancelling button; "No" (in the UI language) when null.</param>
    /// <param name="confirmIsDefault">
    /// False makes the "no" button the default (Enter), for destructive actions such as deletes.
    /// </param>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string? yesLabel = null,
        string? noLabel = null, bool confirmIsDefault = true)
    {
        var dialog = new MessageDialog(title, message,
            new MessageDialogButton(noLabel ?? Localizer.Get("No"), "no", IsDefault: !confirmIsDefault, IsCancel: true),
            new MessageDialogButton(yesLabel ?? Localizer.Get("Yes"), "yes", IsDefault: confirmIsDefault));
        return await dialog.ShowDialog<string?>(owner) == "yes";
    }
}
