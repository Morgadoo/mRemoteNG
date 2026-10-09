using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Manages the system tray icon using Avalonia's built-in TrayIcon.
/// Works on Windows, Linux (StatusNotifierItem), and macOS (NSStatusItem).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private TrayIcon? _trayIcon;

    public void Initialize(string tooltip)
    {
        _trayIcon = new TrayIcon
        {
            Icon = IconService.GetAppIcon(),
            ToolTipText = tooltip,
            IsVisible = true,
            Menu = BuildMenu(),
        };

        _trayIcon.Clicked += OnTrayIconClicked;
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem(Localizer.Get("ShowMRemoteNG"));
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Add(showItem);

        var quickConnectItem = new NativeMenuItem(Localizer.Get("QuickConnect") + "...");
        quickConnectItem.Click += async (_, _) =>
        {
            // The owner must be visible; it may be hidden in the tray.
            ShowMainWindow();
            var owner = GetMainWindow();
            if (owner is null) return;
            var dialog = new Views.Dialogs.QuickConnectDialog();
            await dialog.ShowDialog(owner);
        };
        menu.Add(quickConnectItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem(Localizer.Get("Exit"));
        exitItem.Click += (_, _) => RequestExit();
        menu.Add(exitItem);

        return menu;
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    /// <summary>
    /// Exits through the main window's normal close path, so exit confirmation and
    /// save-on-exit still run. The window is shown first because a confirmation dialog needs a visible owner.
    /// </summary>
    private static void RequestExit()
    {
        var window = GetMainWindow();
        if (window is null)
        {
            Environment.Exit(0);
            return;
        }

        ShowMainWindow();
        window.Close();
    }

    /// <summary>Restores the main window (e.g. after it was minimised to the tray).</summary>
    public static void ShowMainWindow()
    {
        var window = GetMainWindow();
        if (window is null) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static Window? GetMainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public void SetTooltip(string tooltip)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = tooltip;
    }

    public bool IsVisible => _trayIcon?.IsVisible ?? false;

    public void SetVisible(bool visible)
    {
        if (_trayIcon is not null)
            _trayIcon.IsVisible = visible;
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
