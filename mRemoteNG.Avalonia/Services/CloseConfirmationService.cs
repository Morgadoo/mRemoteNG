using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Avalonia.Views.Dialogs;
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
        return await MessageDialog.ConfirmAsync(owner, "Exit mRemoteNG",
            $"{openConnections} {noun} still open. Exit mRemoteNG and close them?", "Exit", "Cancel");
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

        return await MessageDialog.ConfirmAsync(owner, "Close connection",
            $"Close the connection \"{connectionName}\"?", "Close", "Cancel");
    }
}
