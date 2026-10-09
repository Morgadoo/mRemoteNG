using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.App;
using mRemoteNG.Core.App.Info;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Connections.Multiuser;
using mRemoteNG.Core.Config.Connections.Sql;
using mRemoteNG.Core.Config.DataProviders;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Logging;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Security;
using LogLevel = mRemoteNG.Avalonia.ViewModels.Docking.LogLevel;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace mRemoteNG.Avalonia.Services;

/// <summary>Raised when another client saved the SQL connection database and this tree was not reloaded.</summary>
public sealed class RemoteUpdateEventArgs(DateTime updateTime, bool reloaded) : EventArgs
{
    public DateTime UpdateTime { get; } = updateTime;

    /// <summary>True when the tree was reloaded automatically (no unsaved local changes).</summary>
    public bool Reloaded { get; } = reloaded;
}

/// <summary>
/// Storage features driven by <see cref="AppSettings"/>:
/// <list type="bullet">
///   <item>rolling backups of the connection file (on save / on exit);</item>
///   <item>automatic saving (every N minutes and/or after edits) through the connection tree's dirty flag;</item>
///   <item>the SQL connection database: loading at startup, saving, and multi-user change polling
///         (reloads automatically when there are no unsaved changes, otherwise reports the change);</item>
///   <item>file logging: level, file and on/off; messages of the in-app log panel go to the file too.</item>
/// </list>
/// </summary>
public sealed class StorageRuntime : IDisposable
{
    private readonly AppSettingsService _settings;
    private readonly ConnectionsService _connections;
    private readonly ConnectionTreeViewModel _tree;
    private readonly LogPanelDockable _log;
    private readonly ICryptoProvider? _crypto;
    private readonly RollingFileLoggerProvider? _fileLog;
    private readonly ILogger _logger;
    private readonly ConnectionsAutoSaver _autoSaver;
    private SqlConnectionsUpdateChecker? _updateChecker;
    private bool _attached;

    public StorageRuntime(
        AppSettingsService settings,
        ConnectionsService connections,
        ConnectionTreeViewModel tree,
        LogPanelDockable log,
        ICryptoProvider? crypto = null,
        RollingFileLoggerProvider? fileLog = null,
        ILogger<StorageRuntime>? logger = null)
    {
        _settings = settings;
        _connections = connections;
        _tree = tree;
        _log = log;
        _crypto = crypto;
        _fileLog = fileLog;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _autoSaver = new ConnectionsAutoSaver(action => Dispatcher.UIThread.Post(action), _logger);
        _autoSaver.Attach(new TreeAutoSaveTarget(this));
        _autoSaver.AutoSaved += OnAutoSaved;
    }

    /// <summary>The automatic saver (exposed for status display and tests).</summary>
    public ConnectionsAutoSaver AutoSaver => _autoSaver;

    /// <summary>The log file currently written (also when file logging is off).</summary>
    public string LogFilePath => _fileLog?.FilePath ?? ResolveLogFilePath(_settings.Current);

    /// <summary>Raised on the UI thread when another client changed the SQL database.</summary>
    public event EventHandler<RemoteUpdateEventArgs>? RemoteUpdateDetected;

    /// <summary>Applies settings now and whenever they change; hooks tree edits and storage events.</summary>
    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;

        Apply(_settings.Current);
        _settings.Changed += OnSettingsChanged;
        _tree.PropertyChanged += OnTreePropertyChanged;
        _connections.Loaded += OnStorageChanged;
        _connections.Saved += OnStorageChanged;
        _log.Entries.CollectionChanged += OnLogEntriesChanged;
    }

    // ── Settings ──────────────────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e) => Apply(e.Current);

    /// <summary>Pushes backup, autosave, logging and SQL polling settings to the services.</summary>
    public void Apply(AppSettings settings)
    {
        _connections.BackupFrequency = settings.BackupFrequency;
        _connections.BackupOptions = settings.GetBackupOptions();
        _autoSaver.Configure(settings.AutoSaveEveryMinutes, settings.SaveConnectionsOnEdit);

        if (_fileLog is not null)
        {
            _fileLog.FilePath = ResolveLogFilePath(settings);
            _fileLog.MinimumLevel = settings.LogToFile ? ToMsLevel(settings.LogLevel) : MsLogLevel.None;
        }

        UpdatePolling(settings);
    }

    public static string ResolveLogFilePath(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.LogFilePath)
            ? ApplicationPaths.DefaultLogFilePath
            : ApplicationPaths.FromStoredPath(settings.LogFilePath);

    public static MsLogLevel ToMsLevel(LogFileLevel level) => level switch
    {
        LogFileLevel.Debug => MsLogLevel.Debug,
        LogFileLevel.Warning => MsLogLevel.Warning,
        LogFileLevel.Error => MsLogLevel.Error,
        _ => MsLogLevel.Information,
    };

    // ── SQL database ──────────────────────────────────────────────────────

    /// <summary>The SQL connection settings from <paramref name="settings"/> (password decrypted).</summary>
    public DatabaseConnectionSettings GetDatabaseSettings(AppSettings settings) =>
        new(settings.SqlServerType, settings.SqlHost, settings.SqlDatabaseName, settings.SqlUsername,
            UnprotectPassword(_crypto, settings.SqlPasswordProtected), settings.SqlReadOnly);

    public static string UnprotectPassword(ICryptoProvider? crypto, string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue) || crypto is null)
            return string.Empty;
        try
        {
            return crypto.Unprotect(protectedValue);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Loads the connections from the configured SQL database into the tree, asking for the master password
    /// when the database has one. Returns false when it failed or was cancelled (the reason is logged).
    /// </summary>
    public async Task<bool> OpenDatabaseAsync(Window? owner)
    {
        var settings = _settings.Current;
        var databaseSettings = GetDatabaseSettings(settings);
        var errors = databaseSettings.Validate();
        if (errors.Count > 0)
        {
            _log.Log($"SQL server settings are incomplete: {string.Join(" ", errors)}", LogLevel.Error);
            return false;
        }

        var store = new SqlConnectionsStore(databaseSettings, _logger);
        string? password = null;
        string? error = null;
        while (true)
        {
            try
            {
                var load = Task.Run(() => _connections.LoadFromDatabase(store, password));
                await load.ConfigureAwait(true);
                _tree.RefreshFromModel();
                var count = _connections.ConnectionTreeModel?.GetRecursiveChildList().Count() ?? 0;
                _log.Log($"Loaded {count} connections from SQL database {store.DisplayName}{(store.ReadOnly ? " (read-only)" : string.Empty)}.");
                UpdatePolling(_settings.Current);
                return true;
            }
            catch (ConnectionFilePasswordException ex)
            {
                if (owner is null)
                {
                    _log.Log("The SQL connection database is protected by a password.", LogLevel.Error);
                    return false;
                }

                if (ex.PasswordWasSupplied)
                    error = Localizer.Get("IncorrectPasswordTryAgain");
                password = await new PasswordPromptDialog(Localizer.Format("SqlDatabaseIsPasswordProtectedFormat", store.DisplayName), error)
                    .ShowDialog<string?>(owner);
                if (password is null)
                {
                    _log.Log("Opening the SQL connection database was cancelled.", LogLevel.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.Log($"Could not load connections from SQL database {store.DisplayName}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }
    }

    /// <summary>
    /// At startup: when "use SQL server" is on and no connection file was given on the command line,
    /// load the connections from the database (the legacy app does the same).
    /// </summary>
    public Task<bool> LoadAtStartupAsync(Window owner, StartupArguments arguments)
    {
        if (!_settings.Current.UseSqlServer || arguments.ConnectionFile is not null)
            return Task.FromResult(false);
        return OpenDatabaseAsync(owner);
    }

    private void OnStorageChanged(object? sender, ConnectionsStorageEventArgs e)
    {
        _updateChecker?.Reset();
        Dispatcher.UIThread.Post(() => UpdatePolling(_settings.Current));
    }

    private void UpdatePolling(AppSettings settings)
    {
        if (!_connections.UsingDatabase)
        {
            _updateChecker?.Dispose();
            _updateChecker = null;
            return;
        }

        if (_updateChecker is null)
        {
            var connections = _connections;
            _updateChecker = new SqlConnectionsUpdateChecker(
                () => connections.Database?.GetLastUpdate(),
                () => connections.LastDatabaseUpdate,
                _logger);
            _updateChecker.UpdateAvailable += (_, e) => Dispatcher.UIThread.Post(() => OnRemoteUpdate(e.UpdateTime));
            _updateChecker.CheckFailed += (_, ex) => _log.Log($"Could not check the SQL database for changes: {ex.Message}", LogLevel.Warning);
        }

        _updateChecker.Start(TimeSpan.FromSeconds(settings.SqlUpdateCheckIntervalSeconds));
    }

    /// <summary>Handles another client's save (UI thread): reload when safe, otherwise report it.</summary>
    public void OnRemoteUpdate(DateTime updateTime)
    {
        if (!_connections.UsingDatabase)
            return;

        if (_settings.Current.SqlAutoReload && !_tree.IsDirty)
        {
            try
            {
                _connections.ReloadFromDatabase();
                _tree.RefreshFromModel();
                _log.Log($"Connections reloaded: another user saved the SQL database at {updateTime:G}.");
                RemoteUpdateDetected?.Invoke(this, new RemoteUpdateEventArgs(updateTime, reloaded: true));
                return;
            }
            catch (Exception ex)
            {
                _log.Log($"Could not reload the SQL connection database: {ex.Message}", LogLevel.Error);
            }
        }

        _log.Log($"Another user saved the SQL connection database at {updateTime:G}. " +
                 (_tree.IsDirty
                     ? "You have unsaved changes: saving will overwrite theirs; reload to get their version."
                     : "Reload to see their changes."),
            LogLevel.Warning);
        RemoteUpdateDetected?.Invoke(this, new RemoteUpdateEventArgs(updateTime, reloaded: false));
    }

    /// <summary>Re-reads the SQL database into the tree (e.g. a "Reload" command after <see cref="RemoteUpdateDetected"/>).</summary>
    public bool ReloadDatabase()
    {
        if (!_connections.UsingDatabase)
            return false;
        try
        {
            _connections.ReloadFromDatabase();
            _tree.RefreshFromModel();
            _log.Log("Connections reloaded from the SQL database.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Log($"Could not reload the SQL connection database: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    // ── Autosave / backups ────────────────────────────────────────────────

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionTreeViewModel.IsDirty) && _tree.IsDirty)
            _autoSaver.NotifyEdited();
    }

    private void OnAutoSaved(object? sender, AutoSaveEventArgs e)
    {
        if (e.Error is not null)
            _log.Log($"Automatic save failed: {e.Error.Message}", LogLevel.Error);
        else
            _log.Log($"Connections saved automatically ({(e.Reason == ConnectionsAutoSaver.ReasonEdit ? "after an edit" : "timer")}).", LogLevel.Debug);
    }

    /// <summary>Backs up the connection file when the backup frequency is "on exit". Runs at exit.</summary>
    public void OnExit()
    {
        try
        {
            if (_settings.Current.BackupFrequency == BackupFrequency.OnExit)
            {
                var backup = _connections.BackupCurrentFile();
                if (backup is not null)
                    _logger.LogInformation("Backed up the connection file to {Backup}", backup);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not back up the connection file on exit");
        }

        _updateChecker?.Stop();
        _fileLog?.Flush();
    }

    // ── Logging ───────────────────────────────────────────────────────────

    private void OnLogEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_fileLog is null || e.NewItems is null)
            return;

        foreach (LogEntry entry in e.NewItems)
        {
            var level = entry.Level switch
            {
                LogLevel.Error => MsLogLevel.Error,
                LogLevel.Warning => MsLogLevel.Warning,
                LogLevel.Debug => MsLogLevel.Debug,
                _ => MsLogLevel.Information,
            };
            _fileLog.Write(level, "Log", entry.Message);
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _tree.PropertyChanged -= OnTreePropertyChanged;
        _connections.Loaded -= OnStorageChanged;
        _connections.Saved -= OnStorageChanged;
        _log.Entries.CollectionChanged -= OnLogEntriesChanged;
        _autoSaver.Dispose();
        _updateChecker?.Dispose();
    }

    /// <summary>Lets the autosaver save the tree through the view model, so the dirty flag is cleared.</summary>
    private sealed class TreeAutoSaveTarget(StorageRuntime runtime) : IAutoSaveTarget
    {
        public bool IsDirty => runtime._tree.IsDirty;

        public bool CanSave => runtime._connections.HasStorage;

        public void Save() => runtime._tree.SaveToFile();
    }
}
