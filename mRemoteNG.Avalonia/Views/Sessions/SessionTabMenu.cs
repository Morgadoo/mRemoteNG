using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// The session tab context menu (legacy ConnectionWindow cmenTab). Items the session's protocol
/// cannot do (no <see cref="ISpecialKeysProtocol"/>, <see cref="IDisplayOptionsProtocol"/>, not SSH…)
/// are shown disabled.
/// </summary>
public static class SessionTabMenu
{
    public const string Reconnect = "Reconnect";
    public const string Duplicate = "Duplicate Tab";
    public const string Rename = "Rename Tab...";
    public const string SpecialKeys = "Send Special Keys";
    public const string CtrlAltDel = "Ctrl+Alt+Del";
    public const string CtrlEsc = "Ctrl+Esc";
    public const string SmartSize = "Smart Size";
    public const string ViewOnly = "View Only";
    public const string FullScreen = "Full Screen";
    public const string RefreshScreen = "Refresh Screen";
    public const string TransferFile = "Transfer File (SFTP)...";
    public const string CopyHostname = "Copy Hostname";
    public const string MultiSshTarget = "Include in Multi-SSH";
    public const string MoveToPanel = "Move to Panel";
    public const string NewPanel = "New Panel...";
    public const string Close = "Close";
    public const string CloseOthers = "Close Other Tabs";
    public const string CloseRight = "Close Tabs to the Right";

    /// <summary>Builds the menu for <paramref name="session"/>; <paramref name="owner"/> parents dialogs.</summary>
    public static ContextMenu Build(SessionTabViewModel session, Window? owner)
    {
        var dock = session.Panel?.Owner;
        var protocol = session.Protocol;
        var connected = session.IsConnected;
        var specialKeys = protocol as ISpecialKeysProtocol;
        var display = protocol as IDisplayOptionsProtocol;
        var panel = session.Panel;
        var index = panel?.Sessions.IndexOf(session) ?? -1;

        var items = new List<Control>
        {
            Item(Reconnect, dock is not null && SessionsDockable.CanReconnect(session),
                async () => await dock!.ReconnectSessionAsync(session)),
            Item(Duplicate, dock is not null && session.Factory is not null,
                async () => await dock!.DuplicateSessionAsync(session)),
            Item(Rename, true, () => RenameAsync(session, owner)),
            new Separator(),
            SubMenu(SpecialKeys, specialKeys is not null && connected,
                Item(CtrlAltDel, specialKeys?.SupportedSpecialKeys.Contains(SpecialKey.CtrlAltDel) == true && connected,
                    () => SendKeyAsync(session, SpecialKey.CtrlAltDel)),
                Item(CtrlEsc, specialKeys?.SupportedSpecialKeys.Contains(SpecialKey.CtrlEsc) == true && connected,
                    () => SendKeyAsync(session, SpecialKey.CtrlEsc))),
            Check(SmartSize, display?.SupportsSmartSize == true, display?.SupportsSmartSize == true && display.SmartSize,
                () => display!.SmartSize = !display.SmartSize),
            Check(ViewOnly, display?.SupportsViewOnly == true, display?.SupportsViewOnly == true && display.ViewOnly,
                () => display!.ViewOnly = !display.ViewOnly),
            Check(FullScreen, session.ContentView is not null, SessionFullScreenWindow.IsFullScreen(session),
                () => SessionFullScreenWindow.Toggle(session, owner)),
            Item(RefreshScreen, session.ContentView is not null, () => RefreshView(session)),
            new Separator(),
            Item(TransferFile, IsSsh(session) && owner is not null,
                () => SshFileTransferDialog.ShowFor(owner!, session.Parameters)),
            Item(CopyHostname, !string.IsNullOrEmpty(session.DisplayHostname), () => CopyHostnameAsync(session, owner)),
            Check(MultiSshTarget, session.IsTerminal, session.IsTerminal && session.IsMultiSshTarget,
                () => session.IsMultiSshTarget = !session.IsMultiSshTarget),
            MoveMenu(session, dock, owner),
            new Separator(),
            Item(Close, true, () => session.CloseCommand.Execute().Subscribe()),
            Item(CloseOthers, dock is not null && panel is { Sessions.Count: > 1 },
                async () => await dock!.CloseOtherSessionsAsync(session)),
            Item(CloseRight, dock is not null && panel is not null && index >= 0 && index < panel.Sessions.Count - 1,
                async () => await dock!.CloseSessionsToTheRightAsync(session)),
        };

        return new ContextMenu { ItemsSource = items };
    }

    public static bool IsSsh(SessionTabViewModel session) =>
        session.Parameters.Protocol is ProtocolType.Ssh or ProtocolType.SshSftp;

    /// <summary>Finds a menu item by header, searching submenus (for tests and keyboard access).</summary>
    public static MenuItem? Find(ContextMenu menu, string header) => Find(menu.ItemsSource?.OfType<Control>() ?? [], header);

    private static MenuItem? Find(IEnumerable<Control> items, string header)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (Equals(item.Header, header))
                return item;
            if (Find(item.Items.OfType<Control>(), header) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Runs a menu item's action as a click would.</summary>
    public static void Invoke(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static MenuItem Item(string header, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Item(string header, bool enabled, Func<Task> action)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Report($"{header.TrimEnd('.')} failed: {ex.Message}");
            }
        };
        return item;
    }

    private static MenuItem Check(string header, bool enabled, bool isChecked, Action toggle)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isChecked,
        };
        item.Click += (_, _) =>
        {
            try
            {
                toggle();
            }
            catch (Exception ex)
            {
                Report($"{header} failed: {ex.Message}");
            }
        };
        return item;
    }

    private static MenuItem SubMenu(string header, bool enabled, params MenuItem[] children)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        foreach (var child in children)
            item.Items.Add(child);
        return item;
    }

    private static MenuItem MoveMenu(SessionTabViewModel session, SessionsDockable? dock, Window? owner)
    {
        var item = new MenuItem { Header = MoveToPanel, IsEnabled = dock is not null };
        if (dock is null) return item;
        foreach (var panel in dock.Panels.Where(p => !ReferenceEquals(p, session.Panel)))
            item.Items.Add(Item(panel.Name, true, () => dock.MoveSession(session, panel.Name)));
        if (item.Items.Count > 0)
            item.Items.Add(new Separator());
        item.Items.Add(Item(NewPanel, true, async () =>
        {
            var name = owner is null
                ? dock.UniquePanelName(SessionsDockable.NewPanelBaseName)
                : await new TextPromptDialog("New Panel", "Panel name:", false, null,
                    dock.UniquePanelName(SessionsDockable.NewPanelBaseName)).ShowDialog<string?>(owner);
            if (!string.IsNullOrWhiteSpace(name))
                dock.MoveSession(session, name);
        }));
        return item;
    }

    private static async Task RenameAsync(SessionTabViewModel session, Window? owner)
    {
        if (owner is null) return;
        var name = await new TextPromptDialog("Rename Tab", "New tab name (leave empty for the default):", false,
            session.BaseTitle, session.DisplayTitle).ShowDialog<string?>(owner);
        if (name is not null)
            session.CustomTitle = name;
    }

    private static async Task SendKeyAsync(SessionTabViewModel session, SpecialKey key)
    {
        if (session.Protocol is ISpecialKeysProtocol special)
            await special.SendSpecialKeyAsync(key);
    }

    /// <summary>
    /// Legacy "Refresh screen": asks the server for a full update when the protocol supports it (VNC),
    /// and repaints the session view.
    /// </summary>
    private static void RefreshView(SessionTabViewModel session)
    {
        if (session.Protocol is IRefreshableProtocol refreshable)
            _ = RefreshRemoteAsync(refreshable);
        if (session.ContentView is not { } view) return;
        view.InvalidateMeasure();
        view.InvalidateArrange();
        view.InvalidateVisual();
    }

    private static async Task RefreshRemoteAsync(IRefreshableProtocol protocol)
    {
        try
        {
            await protocol.RefreshScreenAsync();
        }
        catch (Exception ex)
        {
            Report($"Could not refresh the screen: {ex.Message}");
        }
    }

    private static async Task CopyHostnameAsync(SessionTabViewModel session, Window? owner)
    {
        var clipboard = (owner ?? MainWindow())?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(session.DisplayHostname);
    }

    private static void Report(string message)
    {
        if (AppServices.Provider.GetService(typeof(LogPanelDockable)) is LogPanelDockable log)
            log.Log(message, LogLevel.Error);
    }

    private static Window? MainWindow() =>
        (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
