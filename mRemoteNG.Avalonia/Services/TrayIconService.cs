using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

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

        var showItem = new NativeMenuItem("Show mRemoteNG");
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Add(showItem);

        var quickConnectItem = new NativeMenuItem("Quick Connect...");
        quickConnectItem.Click += async (_, _) =>
        {
            var dialog = new Views.Dialogs.QuickConnectDialog();
            await dialog.ShowDialog(GetMainWindow());
        };
        menu.Add(quickConnectItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => Environment.Exit(0);
        menu.Add(exitItem);

        return menu;
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private static void ShowMainWindow()
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
