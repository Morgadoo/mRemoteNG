using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform;
using LogLevel = mRemoteNG.Avalonia.ViewModels.Docking.LogLevel;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Makes <see cref="AppSettings"/> take effect in the running app:
/// theme/fonts/toolbars, tray icon, minimise-to-tray, exit confirmation, remembering the
/// last connection file, save-on-exit, desktop notifications and the startup update check.
/// Re-applies whenever <see cref="AppSettingsService.Changed"/> fires.
/// </summary>
public sealed class AppSettingsRuntime : IDisposable
{
    private readonly AppSettingsService _settings;
    private readonly StartupService _startup;
    private readonly ConnectionsService _connections;
    private readonly SessionsDockable _sessions;
    private readonly LogPanelDockable _log;
    private readonly INotificationService? _notifications;
    private readonly UpdateCheckService _updates;
    private readonly CloseConfirmationService _closeConfirmation;
    private readonly ILogger _logger;
    private readonly HashSet<SessionTabViewModel> _watchedSessions = [];
    private readonly Dictionary<SessionTabViewModel, bool> _wasConnected = [];

    private Window? _mainWindow;
    private TrayIconService? _tray;
    private bool _exitConfirmed;
    private bool _confirmingExit;
    private int _exitPersisted;

    public AppSettingsRuntime(
        AppSettingsService settings,
        StartupService startup,
        ConnectionsService connections,
        SessionsDockable sessions,
        LogPanelDockable log,
        UpdateCheckService updates,
        CloseConfirmationService closeConfirmation,
        INotificationService? notifications = null,
        ILogger<AppSettingsRuntime>? logger = null)
    {
        _settings = settings;
        _startup = startup;
        _connections = connections;
        _sessions = sessions;
        _log = log;
        _updates = updates;
        _closeConfirmation = closeConfirmation;
        _notifications = notifications;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Applies the theme before any window is shown.</summary>
    public void ApplyTheme() => ThemeService.Instance.ApplySettings(_settings.Current);

    /// <summary>Hooks the main window, tray and lifetime. Call once from App startup.</summary>
    public void Attach(IClassicDesktopStyleApplicationLifetime desktop, Window mainWindow, TrayIconService tray)
    {
        _mainWindow = mainWindow;
        _tray = tray;

        ApplyAll();
        _settings.Changed += OnSettingsChanged;

        mainWindow.Closing += OnMainWindowClosing;
        mainWindow.PropertyChanged += OnMainWindowPropertyChanged;
        desktop.Exit += (_, _) => PersistOnExit();
        // Covers paths that bypass the window (e.g. Environment.Exit from the File > Exit menu).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => PersistOnExit();

        _sessions.Sessions.CollectionChanged += OnSessionsChanged;
        foreach (var session in _sessions.Sessions)
            Watch(session);
        _log.Entries.CollectionChanged += OnLogEntriesChanged;

        _ = RunStartupUpdateCheckAsync();
    }

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyAll();
        else
            Dispatcher.UIThread.Post(ApplyAll);
    }

    private void ApplyAll()
    {
        var current = _settings.Current;
        ThemeService.Instance.ApplySettings(current);
        _tray?.SetVisible(current.ShowTrayIcon);
    }

    // ── Exit ──────────────────────────────────────────────────────────────

    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitConfirmed || _mainWindow is null)
            return;

        var open = _sessions.Sessions.Count;
        if (!_closeConfirmation.ShouldConfirmExit(open))
            return;

        // Cancel now, ask, and close again if confirmed (Closing cannot be awaited).
        e.Cancel = true;
        if (_confirmingExit)
            return; // the question is already on screen

        _confirmingExit = true;
        try
        {
            if (await _closeConfirmation.ConfirmExitAsync(_mainWindow, open))
            {
                _exitConfirmed = true;
                _mainWindow.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exit confirmation failed");
            _exitConfirmed = true;
            _mainWindow.Close();
        }
        finally
        {
            _confirmingExit = false;
        }
    }

    /// <summary>Remembers the open connection file and saves it if "save on exit" is on. Runs once.</summary>
    public void PersistOnExit()
    {
        if (Interlocked.Exchange(ref _exitPersisted, 1) == 1)
            return;

        try
        {
            _startup.RecordOpenFile(_connections.CurrentFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remember the last connection file");
        }

        if (!_settings.Current.SaveConnectionsOnExit
            || _connections.ConnectionTreeModel is null
            || string.IsNullOrEmpty(_connections.CurrentFilePath))
        {
            return;
        }

        try
        {
            _connections.SaveToFile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save connections on exit");
            Console.Error.WriteLine($"mRemoteNG: could not save connections on exit: {ex.Message}");
        }
    }

    // ── Tray ──────────────────────────────────────────────────────────────

    private void OnMainWindowPropertyChanged(object? sender, global::Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty || _mainWindow is null)
            return;

        if (e.NewValue is WindowState.Minimized
            && _settings.Current.MinimizeToTray
            && _settings.Current.ShowTrayIcon
            && _tray?.IsVisible == true)
        {
            _mainWindow.Hide();
        }
    }

    // ── Notifications ─────────────────────────────────────────────────────

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var session in _watchedSessions.ToList())
                Unwatch(session, notify: true);
            return;
        }

        foreach (SessionTabViewModel session in e.OldItems ?? Array.Empty<SessionTabViewModel>())
            Unwatch(session, notify: true);
        foreach (SessionTabViewModel session in e.NewItems ?? Array.Empty<SessionTabViewModel>())
            Watch(session);
    }

    private void Watch(SessionTabViewModel session)
    {
        if (!_watchedSessions.Add(session))
            return;
        _wasConnected[session] = session.IsConnected;
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void Unwatch(SessionTabViewModel session, bool notify)
    {
        if (!_watchedSessions.Remove(session))
            return;
        session.PropertyChanged -= OnSessionPropertyChanged;
        if (_wasConnected.Remove(session, out var wasConnected) && wasConnected && notify)
            NotifyDisconnected(session);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionTabViewModel.IsConnected) || sender is not SessionTabViewModel session)
            return;

        var was = _wasConnected.GetValueOrDefault(session);
        var now = session.IsConnected;
        _wasConnected[session] = now;

        if (!was && now && _settings.Current.NotifyOnConnect)
            Notify("Connected", $"{session.ProtocolName}: {session.Hostname}", NotificationLevel.Info);
        else if (was && !now)
            NotifyDisconnected(session);
    }

    private void NotifyDisconnected(SessionTabViewModel session)
    {
        if (_settings.Current.NotifyOnDisconnect)
            Notify("Disconnected", $"{session.ProtocolName}: {session.Hostname}", NotificationLevel.Info);
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_settings.Current.NotifyOnError || e.NewItems is null)
            return;

        foreach (LogEntry entry in e.NewItems)
        {
            if (entry.Level == LogLevel.Error)
                Notify("mRemoteNG error", entry.Message, NotificationLevel.Error);
        }
    }

    private void Notify(string title, string body, NotificationLevel level)
    {
        if (_notifications is null)
            return;

        // Platform notifiers may block (notify-send); keep the UI thread free.
        _ = Task.Run(() =>
        {
            try
            {
                _notifications.ShowNotification(title, body, level);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Desktop notification failed");
            }
        });
    }

    // ── Updates ───────────────────────────────────────────────────────────

    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            var result = await _updates.RunStartupCheckAsync().ConfigureAwait(false);
            if (result is null)
                return;

            if (result.IsUpdateAvailable)
                _log.Log($"{result.Message} Download: {result.ReleaseUrl}", LogLevel.Info);
            else if (!result.Succeeded)
                _log.Log($"Update check: {result.Message}", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup update check failed");
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _sessions.Sessions.CollectionChanged -= OnSessionsChanged;
        _log.Entries.CollectionChanged -= OnLogEntriesChanged;
        foreach (var session in _watchedSessions.ToList())
            Unwatch(session, notify: false);
        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.PropertyChanged -= OnMainWindowPropertyChanged;
        }
    }
}
