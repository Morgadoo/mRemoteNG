using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using mRemoteNG.Core.Config;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Implements the "Confirm closing connections" option
/// (<see cref="AppSettings.ConfirmCloseConnection"/>).
/// </summary>
public sealed class CloseConfirmationService(AppSettingsService settings)
{
    /// <summary>True when exiting with <paramref name="openConnections"/> open connections needs confirmation.</summary>
    public bool ShouldConfirmExit(int openConnections) =>
        openConnections > 0 && settings.Current.ConfirmCloseConnection is ConfirmCloseEnum.Exit or ConfirmCloseEnum.All;

    /// <summary>True when closing a single connection tab needs confirmation.</summary>
    public bool ShouldConfirmCloseConnection => settings.Current.ConfirmCloseConnection == ConfirmCloseEnum.All;

    /// <summary>Asks before exiting when required; returns true when the app may exit.</summary>
    public async Task<bool> ConfirmExitAsync(Window owner, int openConnections)
    {
        if (!ShouldConfirmExit(openConnections))
            return true;

        var noun = openConnections == 1 ? "connection is" : "connections are";
        return await ConfirmationDialog.ShowAsync(owner, "Exit mRemoteNG",
            $"{openConnections} {noun} still open. Exit mRemoteNG and close them?", "Exit");
    }

    /// <summary>
    /// Asks before closing one connection when required; returns true when it may be closed.
    /// Uses the main window as owner when <paramref name="owner"/> is null.
    /// </summary>
    public async Task<bool> ConfirmCloseConnectionAsync(string connectionName, Window? owner = null)
    {
        if (!ShouldConfirmCloseConnection)
            return true;

        owner ??= (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null || !owner.IsVisible)
            return true;

        return await ConfirmationDialog.ShowAsync(owner, "Close connection",
            $"Close the connection \"{connectionName}\"?", "Close");
    }
}

/// <summary>Minimal modal yes/no dialog.</summary>
public static class ConfirmationDialog
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText = "OK")
    {
        var confirmed = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            MaxWidth = 520,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirm = new Button { Content = confirmText, MinWidth = 80, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        confirm.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { confirm, cancel },
                },
            },
        };

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
