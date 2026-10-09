using Avalonia.Controls;
using Material.Icons;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>A button shown by <see cref="MessageDialog"/>.</summary>
/// <param name="IsDestructive">
/// The button performs a destructive action (delete, discard…): it is drawn as the filled danger button and the
/// dialog shows a warning icon (docs/design-system.md §8).
/// </param>
public sealed record MessageDialogButton(string Label, string Result, bool IsDefault = false, bool IsCancel = false,
    bool IsDestructive = false);

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
        Header.Title = title;
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrWhiteSpace(message);

        var destructive = buttons.Any(b => b.IsDestructive);
        if (destructive)
        {
            Header.Icon = MaterialIconKind.AlertOutline;
            Header.Classes.Add("danger");
        }
        else if (buttons.Length > 1)
        {
            Header.Icon = MaterialIconKind.HelpCircleOutline;
        }

        foreach (var button in buttons)
        {
            var control = new Button
            {
                Content = button.Label,
                IsDefault = button.IsDefault,
                IsCancel = button.IsCancel,
            };
            if (button.IsDestructive)
            {
                control.Classes.Add("accent");
                control.Classes.Add("danger");
            }
            else if (button.IsDefault && !destructive)
            {
                control.Classes.Add("accent");
            }
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
    /// False makes the "no" button the default (Enter), for destructive actions such as deletes; the confirming
    /// button is then drawn as the danger button and should name the action ("Delete").
    /// </param>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string? yesLabel = null,
        string? noLabel = null, bool confirmIsDefault = true)
    {
        var dialog = new MessageDialog(title, message,
            new MessageDialogButton(noLabel ?? Localizer.Get("No"), "no", IsDefault: !confirmIsDefault, IsCancel: true),
            new MessageDialogButton(yesLabel ?? Localizer.Get("Yes"), "yes", IsDefault: confirmIsDefault,
                IsDestructive: !confirmIsDefault));
        return await dialog.ShowDialog<string?>(owner) == "yes";
    }
}
