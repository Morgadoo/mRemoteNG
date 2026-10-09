using System.Globalization;
using System.Reactive;
using System.Runtime.CompilerServices;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.App.Info;
using mRemoteNG.Core.Config.DataProviders;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Security;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>Options ▸ Saving &amp; Backups (legacy ConnectionsPage autosave/save-on-edit + BackupPage).</summary>
public sealed class SavingSettingsViewModel(AppSettings working) : SettingsPageViewModel(working)
{
    public IReadOnlyList<Choice<BackupFrequency>> BackupFrequencies { get; } =
    [
        new(BackupFrequency.Never, "Never"),
        new(BackupFrequency.OnSave, "Every time the connections are saved"),
        new(BackupFrequency.OnExit, "When mRemoteNG exits"),
    ];

    public decimal MaxAutoSaveMinutes => AppSettings.MaxAutoSaveMinutes;

    public decimal MaxBackups => FileBackupOptions.MaxKeepCount;

    public decimal? AutoSaveEveryMinutes
    {
        get => Working.AutoSaveEveryMinutes;
        set => SetInt(Working.AutoSaveEveryMinutes, value, v => Working.AutoSaveEveryMinutes = v);
    }

    public bool SaveConnectionsOnEdit
    {
        get => Working.SaveConnectionsOnEdit;
        set => Set(Working.SaveConnectionsOnEdit, value, v => Working.SaveConnectionsOnEdit = v);
    }

    public Choice<BackupFrequency> SelectedBackupFrequency
    {
        get => BackupFrequencies.FirstOrDefault(c => c.Value == Working.BackupFrequency) ?? BackupFrequencies[1];
        set
        {
            if (value is null) return;
            Set(Working.BackupFrequency, value.Value, v => Working.BackupFrequency = v);
            this.RaisePropertyChanged(nameof(BackupsEnabled));
            this.RaisePropertyChanged(nameof(ExampleBackupName));
        }
    }

    public bool BackupsEnabled => Working.BackupFrequency != BackupFrequency.Never;

    public decimal? BackupKeepCount
    {
        get => Working.BackupKeepCount;
        set
        {
            SetInt(Working.BackupKeepCount, value, v => Working.BackupKeepCount = v);
            this.RaisePropertyChanged(nameof(ExampleBackupName));
        }
    }

    public string BackupDirectory
    {
        get => Working.BackupDirectory;
        set
        {
            Set(Working.BackupDirectory, value?.Trim() ?? string.Empty, v => Working.BackupDirectory = v);
            this.RaisePropertyChanged(nameof(ExampleBackupName));
        }
    }

    public string BackupNameFormat
    {
        get => Working.BackupNameFormat;
        set
        {
            Set(Working.BackupNameFormat, value ?? string.Empty, v => Working.BackupNameFormat = v);
            this.RaisePropertyChanged(nameof(ExampleBackupName));
        }
    }

    /// <summary>What a backup of confCons.xml would be called now.</summary>
    public string ExampleBackupName
    {
        get
        {
            if (!BackupsEnabled || Working.BackupKeepCount == 0)
                return "Backups are off.";
            if (!AppSettings.IsValidBackupNameFormat(Working.BackupNameFormat))
                return "The name format must contain {0} (file) and {1} (time).";
            var options = Working.GetBackupOptions();
            var name = string.Format(CultureInfo.InvariantCulture, options.NameFormat, "confCons.xml", DateTime.Now);
            var folder = string.IsNullOrWhiteSpace(Working.BackupDirectory) ? "next to the connection file" : "in the backup folder";
            return $"Example: {name} {folder}; the newest {Working.BackupKeepCount} are kept.";
        }
    }

    public void ResetNameFormat() => BackupNameFormat = FileBackupOptions.DefaultNameFormat;

    private void SetInt(int current, decimal? value, Action<int> assign, [CallerMemberName] string? propertyName = null)
    {
        if (value is null)
            return;
        Set(current, (int)Math.Clamp(Math.Round(value.Value), int.MinValue, int.MaxValue), assign, propertyName);
    }
}

/// <summary>Options ▸ SQL Server (legacy SqlServerPage).</summary>
public sealed class SqlServerSettingsViewModel : SettingsPageViewModel
{
    private readonly ICryptoProvider? _crypto;
    private readonly DatabaseConnectionTester _tester;
    private string _password;
    private string _testStatus = string.Empty;
    private bool _testSucceeded;

    public SqlServerSettingsViewModel(AppSettings working, ICryptoProvider? crypto, DatabaseConnectionTester? tester = null)
        : base(working)
    {
        _crypto = crypto;
        _tester = tester ?? new DatabaseConnectionTester();
        _password = StorageRuntime.UnprotectPassword(crypto, working.SqlPasswordProtected);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        TestConnectionCommand.ThrownExceptions.Subscribe(ex => TestStatus = $"Test failed: {ex.Message}");
    }

    public IReadOnlyList<Choice<DatabaseServerType>> ServerTypes { get; } =
    [
        new(DatabaseServerType.MsSql, "Microsoft SQL Server"),
        new(DatabaseServerType.MySql, "MySQL / MariaDB"),
    ];

    public decimal MinInterval => AppSettings.MinSqlUpdateCheckSeconds;

    public decimal MaxInterval => AppSettings.MaxSqlUpdateCheckSeconds;

    public bool UseSqlServer
    {
        get => Working.UseSqlServer;
        set => Set(Working.UseSqlServer, value, v => Working.UseSqlServer = v);
    }

    public Choice<DatabaseServerType> SelectedServerType
    {
        get => ServerTypes.FirstOrDefault(c => c.Value == Working.SqlServerType) ?? ServerTypes[0];
        set
        {
            if (value is null) return;
            Set(Working.SqlServerType, value.Value, v => Working.SqlServerType = v);
            this.RaisePropertyChanged(nameof(HostWatermark));
        }
    }

    public string HostWatermark => Working.SqlServerType == DatabaseServerType.MySql
        ? "host or host:port (default port 3306)"
        : @"host, host:port or host\instance (default port 1433)";

    public string Host
    {
        get => Working.SqlHost;
        set => Set(Working.SqlHost, value?.Trim() ?? string.Empty, v => Working.SqlHost = v);
    }

    public string DatabaseName
    {
        get => Working.SqlDatabaseName;
        set => Set(Working.SqlDatabaseName, value?.Trim() ?? string.Empty, v => Working.SqlDatabaseName = v);
    }

    public string Username
    {
        get => Working.SqlUsername;
        set => Set(Working.SqlUsername, value ?? string.Empty, v => Working.SqlUsername = v);
    }

    /// <summary>Plain-text password; stored encrypted with the platform crypto provider.</summary>
    public string Password
    {
        get => _password;
        set
        {
            value ??= string.Empty;
            if (value == _password)
                return;
            _password = value;
            Working.SqlPasswordProtected = value.Length == 0 || _crypto is null ? string.Empty : _crypto.Protect(value);
            this.RaisePropertyChanged();
        }
    }

    public bool ReadOnly
    {
        get => Working.SqlReadOnly;
        set => Set(Working.SqlReadOnly, value, v => Working.SqlReadOnly = v);
    }

    public decimal? UpdateCheckInterval
    {
        get => Working.SqlUpdateCheckIntervalSeconds;
        set
        {
            if (value is null) return;
            Set(Working.SqlUpdateCheckIntervalSeconds, (int)Math.Round(value.Value), v => Working.SqlUpdateCheckIntervalSeconds = v);
        }
    }

    public bool AutoReload
    {
        get => Working.SqlAutoReload;
        set => Set(Working.SqlAutoReload, value, v => Working.SqlAutoReload = v);
    }

    public string TestStatus
    {
        get => _testStatus;
        private set => this.RaiseAndSetIfChanged(ref _testStatus, value);
    }

    public bool TestSucceeded
    {
        get => _testSucceeded;
        private set => this.RaiseAndSetIfChanged(ref _testSucceeded, value);
    }

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }

    public DatabaseConnectionSettings CurrentConnectionSettings =>
        new(Working.SqlServerType, Working.SqlHost, Working.SqlDatabaseName, Working.SqlUsername, _password, Working.SqlReadOnly);

    private async Task TestConnectionAsync()
    {
        TestStatus = "Connecting…";
        TestSucceeded = false;
        var result = await _tester.TestAsync(CurrentConnectionSettings);
        TestSucceeded = result.Succeeded;
        TestStatus = result.Message;
    }
}

/// <summary>Options ▸ Logging (legacy NotificationsPage log file settings).</summary>
public sealed class LoggingSettingsViewModel(AppSettings working) : SettingsPageViewModel(working)
{
    public IReadOnlyList<Choice<LogFileLevel>> Levels { get; } =
    [
        new(LogFileLevel.Debug, "Debug (everything)"),
        new(LogFileLevel.Information, "Information"),
        new(LogFileLevel.Warning, "Warnings and errors"),
        new(LogFileLevel.Error, "Errors only"),
    ];

    public bool LogToFile
    {
        get => Working.LogToFile;
        set => Set(Working.LogToFile, value, v => Working.LogToFile = v);
    }

    public Choice<LogFileLevel> SelectedLevel
    {
        get => Levels.FirstOrDefault(c => c.Value == Working.LogLevel) ?? Levels[1];
        set
        {
            if (value is null) return;
            Set(Working.LogLevel, value.Value, v => Working.LogLevel = v);
        }
    }

    public string LogFilePath
    {
        get => Working.LogFilePath;
        set
        {
            Set(Working.LogFilePath, value?.Trim() ?? string.Empty, v => Working.LogFilePath = v);
            this.RaisePropertyChanged(nameof(EffectiveLogFilePath));
        }
    }

    public string DefaultLogFilePath => ApplicationPaths.DefaultLogFilePath;

    /// <summary>The file that is written with the values on this page.</summary>
    public string EffectiveLogFilePath => StorageRuntime.ResolveLogFilePath(Working);

    public string DataDirectoryInfo => ApplicationPaths.IsPortable
        ? $"Portable mode: settings, credentials, known_hosts, themes and logs are kept in {ApplicationPaths.SettingsDirectory}."
        : $"Settings, credentials, known_hosts, themes and logs are kept in {ApplicationPaths.SettingsDirectory}.";
}
