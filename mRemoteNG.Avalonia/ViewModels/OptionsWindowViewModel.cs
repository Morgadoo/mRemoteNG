using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Config;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Security;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

// ── Settings page ViewModels ──────────────────────────────────────────────
// Each page edits the Options window's working copy of AppSettings.
// Nothing reaches the live settings until OK/Apply.

public abstract class SettingsPageViewModel(AppSettings working) : ReactiveObject
{
    protected AppSettings Working { get; } = working;

    protected void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        assign(value);
        this.RaisePropertyChanged(propertyName);
    }
}

public sealed class Choice<T>(T value, string displayName)
{
    public T Value { get; } = value;
    public string DisplayName { get; } = displayName;
    public override string ToString() => DisplayName;
}

public sealed class GeneralSettingsViewModel(AppSettings working) : SettingsPageViewModel(working)
{
    public IReadOnlyList<Choice<StartupFileBehavior>> StartupChoices { get; } =
    [
        new(StartupFileBehavior.ReopenLastFile, "Reopen the last connection file"),
        new(StartupFileBehavior.OpenSpecificFile, "Open a specific connection file"),
        new(StartupFileBehavior.None, "Start with an empty connection tree"),
    ];

    public IReadOnlyList<Choice<ConfirmCloseEnum>> ConfirmCloseChoices { get; } =
    [
        new(ConfirmCloseEnum.Never, "Never"),
        new(ConfirmCloseEnum.Exit, "When exiting with open connections"),
        new(ConfirmCloseEnum.All, "When exiting and when closing a connection"),
    ];

    public Choice<StartupFileBehavior> SelectedStartupChoice
    {
        get => StartupChoices.First(c => c.Value == Working.StartupBehavior);
        set
        {
            if (value is null) return;
            Set(Working.StartupBehavior, value.Value, v => Working.StartupBehavior = v);
            this.RaisePropertyChanged(nameof(IsStartupFileEnabled));
        }
    }

    public bool IsStartupFileEnabled => Working.StartupBehavior == StartupFileBehavior.OpenSpecificFile;

    public string StartupFilePath
    {
        get => Working.StartupFilePath;
        set => Set(Working.StartupFilePath, value ?? string.Empty, v => Working.StartupFilePath = v);
    }

    public bool SingleInstance
    {
        get => Working.SingleInstance;
        set => Set(Working.SingleInstance, value, v => Working.SingleInstance = v);
    }

    public bool SaveConnectionsOnExit
    {
        get => Working.SaveConnectionsOnExit;
        set => Set(Working.SaveConnectionsOnExit, value, v => Working.SaveConnectionsOnExit = v);
    }

    public Choice<ConfirmCloseEnum> SelectedConfirmCloseChoice
    {
        get => ConfirmCloseChoices.FirstOrDefault(c => c.Value == Working.ConfirmCloseConnection) ?? ConfirmCloseChoices[1];
        set
        {
            if (value is null) return;
            Set(Working.ConfirmCloseConnection, value.Value, v => Working.ConfirmCloseConnection = v);
        }
    }

    public bool ShowTrayIcon
    {
        get => Working.ShowTrayIcon;
        set
        {
            Set(Working.ShowTrayIcon, value, v => Working.ShowTrayIcon = v);
            // Minimising to a tray icon that does not exist would strand the window.
            if (!value)
                MinimizeToTray = false;
        }
    }

    public bool MinimizeToTray
    {
        get => Working.MinimizeToTray;
        set => Set(Working.MinimizeToTray, value, v => Working.MinimizeToTray = v);
    }

    public bool StartMinimized
    {
        get => Working.StartMinimized;
        set => Set(Working.StartMinimized, value, v => Working.StartMinimized = v);
    }

    public string DataDirectoryInfo => mRemoteNG.Core.App.Info.ApplicationPaths.IsPortable
        ? $"Portable mode: all data is kept in {mRemoteNG.Core.App.Info.ApplicationPaths.SettingsDirectory}"
        : $"Data folder: {mRemoteNG.Core.App.Info.ApplicationPaths.SettingsDirectory}";
}

/// <summary>A theme in the Appearance list: a plain mode (Dark/Light/System) or a named theme.</summary>
public sealed class ThemeChoice(string displayName, ThemeMode mode, string themeName)
{
    public string DisplayName { get; } = displayName;
    public ThemeMode Mode { get; } = mode;

    /// <summary>Empty for the plain modes.</summary>
    public string ThemeName { get; } = themeName;

    public override string ToString() => DisplayName;
}

public sealed class AppearanceSettingsViewModel : SettingsPageViewModel
{
    private readonly ThemeCatalog _catalog;
    private IReadOnlyList<ThemeChoice> _themes = [];

    public AppearanceSettingsViewModel(AppSettings working, ThemeCatalog? catalog = null) : base(working)
    {
        _catalog = catalog ?? ThemeService.Instance.Catalog;
        RefreshThemes();
    }

    /// <summary>Dark, Light, Follow system, then the legacy themes (VS2015 Blue, Darcula) and user themes.</summary>
    public IReadOnlyList<ThemeChoice> Themes
    {
        get => _themes;
        private set => this.RaiseAndSetIfChanged(ref _themes, value);
    }

    public ThemeChoice SelectedTheme
    {
        get => Themes.FirstOrDefault(t => t.ThemeName.Length > 0 && string.Equals(t.ThemeName, Working.ThemeName, StringComparison.OrdinalIgnoreCase))
               ?? Themes.First(t => t.ThemeName.Length == 0 && t.Mode == Working.Theme);
        set
        {
            if (value is null) return;
            if (value.ThemeName.Length == 0)
                Working.Theme = value.Mode;
            Set(Working.ThemeName, value.ThemeName, v => Working.ThemeName = v);
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Re-reads the user themes (after the theme editor saved or deleted one).</summary>
    public void RefreshThemes()
    {
        var list = new List<ThemeChoice>
        {
            new("Dark (VS2015 Dark)", ThemeMode.Dark, string.Empty),
            new("Light (VS2015 Light)", ThemeMode.Light, string.Empty),
            new("Follow system setting", ThemeMode.System, string.Empty),
        };
        foreach (var theme in _catalog.GetAll().Where(t => t.Name is not ThemeCatalog.DarkName and not ThemeCatalog.LightName))
            list.Add(new ThemeChoice(theme.IsBuiltIn ? theme.Name : $"{theme.Name} (user theme)", theme.IsDark ? ThemeMode.Dark : ThemeMode.Light, theme.Name));
        Themes = list;
        this.RaisePropertyChanged(nameof(SelectedTheme));
    }

    /// <summary>Selects a theme saved by the theme editor.</summary>
    public void SelectThemeByName(string name)
    {
        RefreshThemes();
        var choice = Themes.FirstOrDefault(t => string.Equals(t.ThemeName, name, StringComparison.OrdinalIgnoreCase));
        if (choice is not null)
            SelectedTheme = choice;
    }

    public string FontFamily
    {
        get => Working.FontFamily;
        set => Set(Working.FontFamily, value?.Trim() ?? string.Empty, v => Working.FontFamily = v);
    }

    public decimal? FontSize
    {
        get => (decimal)Working.FontSize;
        set
        {
            if (value is null) return;
            Set(Working.FontSize, (double)value.Value, v => Working.FontSize = v);
        }
    }


    public bool ShowToolbar
    {
        get => Working.ShowToolbar;
        set => Set(Working.ShowToolbar, value, v => Working.ShowToolbar = v);
    }

    public bool ShowStatusBar
    {
        get => Working.ShowStatusBar;
        set => Set(Working.ShowStatusBar, value, v => Working.ShowStatusBar = v);
    }
}

public sealed class ConnectionSettingsViewModel(AppSettings working) : SettingsPageViewModel(working)
{
    public IReadOnlyList<string> Protocols => AppSettings.DefaultProtocolChoices;

    public decimal MinPort => AppSettings.MinPort;
    public decimal MaxPort => AppSettings.MaxPort;
    public decimal MinTimeout => AppSettings.MinConnectTimeoutSeconds;
    public decimal MaxTimeout => AppSettings.MaxConnectTimeoutSeconds;
    public decimal MinKeepAlive => AppSettings.MinKeepAliveSeconds;
    public decimal MaxKeepAlive => AppSettings.MaxKeepAliveSeconds;

    public string DefaultProtocol
    {
        get => Working.DefaultProtocol;
        set => Set(Working.DefaultProtocol, value ?? Working.DefaultProtocol, v => Working.DefaultProtocol = v);
    }

    public decimal? ConnectTimeout
    {
        get => Working.ConnectTimeoutSeconds;
        set => SetInt(Working.ConnectTimeoutSeconds, value, v => Working.ConnectTimeoutSeconds = v);
    }

    public string DefaultUsername
    {
        get => Working.DefaultUsername;
        set => Set(Working.DefaultUsername, value ?? string.Empty, v => Working.DefaultUsername = v);
    }

    public bool KeepAlive
    {
        get => Working.SshKeepAliveEnabled;
        set => Set(Working.SshKeepAliveEnabled, value, v => Working.SshKeepAliveEnabled = v);
    }

    public decimal? KeepAliveInterval
    {
        get => Working.SshKeepAliveIntervalSeconds;
        set => SetInt(Working.SshKeepAliveIntervalSeconds, value, v => Working.SshKeepAliveIntervalSeconds = v);
    }

    public string SshKeyPath
    {
        get => Working.SshPrivateKeyPath;
        set => Set(Working.SshPrivateKeyPath, value?.Trim() ?? string.Empty, v => Working.SshPrivateKeyPath = v);
    }

    public decimal? SshPort { get => Working.SshPort; set => SetInt(Working.SshPort, value, v => Working.SshPort = v); }
    public decimal? TelnetPort { get => Working.TelnetPort; set => SetInt(Working.TelnetPort, value, v => Working.TelnetPort = v); }
    public decimal? RloginPort { get => Working.RloginPort; set => SetInt(Working.RloginPort, value, v => Working.RloginPort = v); }
    public decimal? RdpPort { get => Working.RdpPort; set => SetInt(Working.RdpPort, value, v => Working.RdpPort = v); }
    public decimal? VncPort { get => Working.VncPort; set => SetInt(Working.VncPort, value, v => Working.VncPort = v); }
    public decimal? HttpPort { get => Working.HttpPort; set => SetInt(Working.HttpPort, value, v => Working.HttpPort = v); }
    public decimal? HttpsPort { get => Working.HttpsPort; set => SetInt(Working.HttpsPort, value, v => Working.HttpsPort = v); }

    private void SetInt(int current, decimal? value, Action<int> assign, [CallerMemberName] string? propertyName = null)
    {
        // An emptied NumericUpDown yields null: keep the previous value (validation still runs on Apply).
        if (value is null)
            return;
        var rounded = (int)Math.Clamp(Math.Round(value.Value), int.MinValue, int.MaxValue);
        Set(current, rounded, assign, propertyName);
    }
}

public sealed class CredentialsSettingsViewModel(AppSettings working, Func<int> credentialCount) : SettingsPageViewModel(working)
{
    public string Summary
    {
        get
        {
            var count = credentialCount();
            return count == 1 ? "1 saved credential." : $"{count} saved credentials.";
        }
    }

    public void RefreshSummary() => this.RaisePropertyChanged(nameof(Summary));
}

public sealed class NotificationsSettingsViewModel(AppSettings working) : SettingsPageViewModel(working)
{
    public bool ShowConnectNotify
    {
        get => Working.NotifyOnConnect;
        set => Set(Working.NotifyOnConnect, value, v => Working.NotifyOnConnect = v);
    }

    public bool ShowDisconnectNotify
    {
        get => Working.NotifyOnDisconnect;
        set => Set(Working.NotifyOnDisconnect, value, v => Working.NotifyOnDisconnect = v);
    }

    public bool ShowErrorNotify
    {
        get => Working.NotifyOnError;
        set => Set(Working.NotifyOnError, value, v => Working.NotifyOnError = v);
    }
}

public sealed class UpdatesSettingsViewModel : SettingsPageViewModel
{
    private readonly UpdateCheckService _updates;
    private readonly ICryptoProvider? _crypto;
    private string _status = string.Empty;
    private string? _releaseUrl;
    private UpdateCheckResult? _lastCheck;
    private string _proxyPassword;
    private double _downloadProgress;
    private bool _isDownloading;
    private string? _downloadedFile;

    public UpdatesSettingsViewModel(AppSettings working, UpdateCheckService updates, ICryptoProvider? crypto = null) : base(working)
    {
        _updates = updates;
        _crypto = crypto;
        _proxyPassword = StorageRuntime.UnprotectPassword(crypto, working.UpdateProxyPasswordProtected);
        CheckNowCommand = ReactiveCommand.CreateFromTask(CheckNowAsync);
        CheckNowCommand.ThrownExceptions.Subscribe(ex => Status = $"Update check failed: {ex.Message}");
        var canDownload = this.WhenAnyValue(x => x.CanDownload);
        DownloadCommand = ReactiveCommand.CreateFromTask(DownloadAsync, canDownload);
        DownloadCommand.ThrownExceptions.Subscribe(ex =>
        {
            IsDownloading = false;
            Status = $"Download failed: {ex.Message}";
        });
    }

    public bool UseProxy
    {
        get => Working.UpdateUseProxy;
        set => Set(Working.UpdateUseProxy, value, v => Working.UpdateUseProxy = v);
    }

    public string ProxyAddress
    {
        get => Working.UpdateProxyAddress;
        set => Set(Working.UpdateProxyAddress, value?.Trim() ?? string.Empty, v => Working.UpdateProxyAddress = v);
    }

    public decimal? ProxyPort
    {
        get => Working.UpdateProxyPort;
        set
        {
            if (value is null) return;
            Set(Working.UpdateProxyPort, (int)Math.Round(value.Value), v => Working.UpdateProxyPort = v);
        }
    }

    public bool ProxyUseAuthentication
    {
        get => Working.UpdateProxyUseAuthentication;
        set => Set(Working.UpdateProxyUseAuthentication, value, v => Working.UpdateProxyUseAuthentication = v);
    }

    public string ProxyUsername
    {
        get => Working.UpdateProxyUsername;
        set => Set(Working.UpdateProxyUsername, value ?? string.Empty, v => Working.UpdateProxyUsername = v);
    }

    /// <summary>Plain-text proxy password; stored encrypted with the platform crypto provider.</summary>
    public string ProxyPassword
    {
        get => _proxyPassword;
        set
        {
            value ??= string.Empty;
            if (value == _proxyPassword) return;
            _proxyPassword = value;
            Working.UpdateProxyPasswordProtected = value.Length == 0 || _crypto is null ? string.Empty : _crypto.Protect(value);
            this.RaisePropertyChanged();
        }
    }

    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }

    /// <summary>True when the last check found an update with a package for this platform.</summary>
    public bool CanDownload => _lastCheck is { IsUpdateAvailable: true } check
                               && mRemoteNG.Core.Settings.UpdateDownloader.SelectAsset(check.Assets, _updates.Platform) is not null
                               && !IsDownloading;

    public string? PackageName => _lastCheck is null ? null : mRemoteNG.Core.Settings.UpdateDownloader.SelectAsset(_lastCheck.Assets, _updates.Platform)?.Name;

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDownloading, value);
            this.RaisePropertyChanged(nameof(CanDownload));
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    /// <summary>The downloaded package (for "Open" / "Show in folder").</summary>
    public string? DownloadedFile
    {
        get => _downloadedFile;
        private set
        {
            this.RaiseAndSetIfChanged(ref _downloadedFile, value);
            this.RaisePropertyChanged(nameof(HasDownloadedFile));
        }
    }

    public bool HasDownloadedFile => !string.IsNullOrEmpty(DownloadedFile);

    private async Task DownloadAsync()
    {
        if (_lastCheck is null)
            return;
        IsDownloading = true;
        DownloadedFile = null;
        DownloadProgress = 0;
        Status = $"Downloading {PackageName}…";
        var progress = new Progress<double>(p => DownloadProgress = p * 100);
        var result = await _updates.DownloadAsync(_lastCheck, progress, Working);
        IsDownloading = false;
        Status = result.Message;
        DownloadedFile = result.Succeeded ? result.FilePath : null;
    }

    public IReadOnlyList<Choice<UpdateChannel>> Channels { get; } =
    [
        new(UpdateChannel.Stable, "Stable releases"),
        new(UpdateChannel.PreRelease, "Stable and pre-releases"),
    ];

    public bool AutoCheck
    {
        get => Working.CheckForUpdatesOnStartup;
        set => Set(Working.CheckForUpdatesOnStartup, value, v => Working.CheckForUpdatesOnStartup = v);
    }

    public Choice<UpdateChannel> SelectedChannel
    {
        get => Channels.First(c => c.Value == Working.UpdateChannel);
        set
        {
            if (value is null) return;
            Set(Working.UpdateChannel, value.Value, v => Working.UpdateChannel = v);
        }
    }

    public string CurrentVersion => _updates.CurrentVersionText;

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public string? ReleaseUrl
    {
        get => _releaseUrl;
        private set
        {
            this.RaiseAndSetIfChanged(ref _releaseUrl, value);
            this.RaisePropertyChanged(nameof(HasReleaseUrl));
        }
    }

    public bool HasReleaseUrl => !string.IsNullOrEmpty(ReleaseUrl);

    public ReactiveCommand<Unit, Unit> CheckNowCommand { get; }

    private async Task CheckNowAsync()
    {
        Status = "Checking…";
        ReleaseUrl = null;
        DownloadedFile = null;
        _lastCheck = null;
        // Uses the proxy entered on this page, even before OK/Apply.
        var result = await _updates.CheckAsync(Working.UpdateChannel, Working);
        _lastCheck = result;
        Status = result.Message;
        ReleaseUrl = result.IsUpdateAvailable ? result.ReleaseUrl : null;
        this.RaisePropertyChanged(nameof(CanDownload));
        this.RaisePropertyChanged(nameof(PackageName));
    }
}

// ── Main OptionsWindowViewModel ───────────────────────────────────────────
public sealed class SettingsCategoryViewModel(string displayName, string key) : ReactiveObject
{
    public string DisplayName { get; } = displayName;
    public string Key { get; } = key;
}

/// <summary>
/// Options dialog. Edits a private copy of <see cref="AppSettings"/>:
/// OK/Apply validate and commit it through <see cref="AppSettingsService"/>, Cancel discards it,
/// "Reset to defaults" resets the copy (still needs OK/Apply to take effect).
/// </summary>
public sealed class OptionsWindowViewModel : ReactiveObject
{
    private readonly AppSettingsService _settings;
    private readonly UpdateCheckService _updates;
    private readonly ICryptoProvider? _crypto;
    private readonly Func<int> _credentialCount;
    private readonly AppSettings _working;
    private SettingsCategoryViewModel? _selectedCategory;
    private object? _currentPage;
    private string _validationMessage = string.Empty;

    public OptionsWindowViewModel(AppSettingsService settings, UpdateCheckService updates,
        mRemoteNG.Core.Credential.FileCredentialRepository credentials, ICryptoProvider? crypto = null)
        : this(settings, updates, () => credentials.CredentialRecords.Count, crypto)
    {
    }

    private OptionsWindowViewModel(AppSettingsService settings, UpdateCheckService updates, Func<int> credentialCount, ICryptoProvider? crypto)
    {
        _settings = settings;
        _updates = updates;
        _crypto = crypto;
        _credentialCount = credentialCount;
        _working = settings.CreateEditableCopy();
        CreatePages();

        OkCommand = ReactiveCommand.Create(OnOk);
        CancelCommand = ReactiveCommand.Create(OnCancel);
        ApplyCommand = ReactiveCommand.Create(() => { OnApply(); });
        ResetCommand = ReactiveCommand.Create(OnReset);
        SelectedCategory = Categories.FirstOrDefault();
    }

    // Page ViewModels (recreated by Reset)
    public GeneralSettingsViewModel General { get; private set; } = null!;
    public AppearanceSettingsViewModel Appearance { get; private set; } = null!;
    public ConnectionSettingsViewModel Connections { get; private set; } = null!;
    public CredentialsSettingsViewModel Credentials { get; private set; } = null!;
    public NotificationsSettingsViewModel Notifications { get; private set; } = null!;
    public UpdatesSettingsViewModel Updates { get; private set; } = null!;
    public SavingSettingsViewModel Saving { get; private set; } = null!;
    public SqlServerSettingsViewModel SqlServer { get; private set; } = null!;
    public LoggingSettingsViewModel Logging { get; private set; } = null!;

    public List<SettingsCategoryViewModel> Categories { get; } =
    [
        new("Startup & Exit", "general"),
        new("Appearance", "appearance"),
        new("Connections", "connections"),
        new("Saving & Backups", "saving"),
        new("SQL Server", "sql"),
        new("Credentials", "credentials"),
        new("Notifications", "notifications"),
        new("Logging", "logging"),
        new("Updates", "updates"),
    ];

    /// <summary>Selects a page by key ("general", "sql", …).</summary>
    public void SelectCategory(string key) =>
        SelectedCategory = Categories.FirstOrDefault(c => c.Key == key) ?? SelectedCategory;

    public SettingsCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCategory, value);
            CurrentPage = ResolvePageViewModel(value?.Key);
        }
    }

    public object? CurrentPage
    {
        get => _currentPage;
        set => this.RaiseAndSetIfChanged(ref _currentPage, value);
    }

    /// <summary>Validation or save errors from the last OK/Apply; empty when none.</summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _validationMessage, value);
            this.RaisePropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    /// <summary>Raised when the window should close.</summary>
    public event Action? CloseRequested;

    /// <summary>The working copy edited by the pages (exposed for tests and the view).</summary>
    public AppSettings WorkingCopy => _working;

    /// <summary>Validates and commits the working copy. Returns true on success.</summary>
    public bool OnApply()
    {
        IReadOnlyList<string> errors;
        try
        {
            errors = _settings.Apply(_working);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ValidationMessage = $"Could not save settings: {ex.Message}";
            return false;
        }

        ValidationMessage = string.Join(Environment.NewLine, errors);
        return errors.Count == 0;
    }

    /// <summary>Called by the view after the credential manager closed.</summary>
    public void RefreshCredentialSummary() => Credentials.RefreshSummary();

    private void CreatePages()
    {
        General = new GeneralSettingsViewModel(_working);
        Appearance = new AppearanceSettingsViewModel(_working);
        Connections = new ConnectionSettingsViewModel(_working);
        Credentials = new CredentialsSettingsViewModel(_working, _credentialCount);
        Notifications = new NotificationsSettingsViewModel(_working);
        Updates = new UpdatesSettingsViewModel(_working, _updates, _crypto);
        Saving = new SavingSettingsViewModel(_working);
        SqlServer = new SqlServerSettingsViewModel(_working, _crypto);
        Logging = new LoggingSettingsViewModel(_working);
    }

    private object? ResolvePageViewModel(string? key) => key switch
    {
        "general" => General,
        "appearance" => Appearance,
        "connections" => Connections,
        "credentials" => Credentials,
        "notifications" => Notifications,
        "updates" => Updates,
        "saving" => Saving,
        "sql" => SqlServer,
        "logging" => Logging,
        _ => null,
    };

    private void OnOk()
    {
        if (OnApply())
            CloseRequested?.Invoke();
    }

    private void OnCancel() => CloseRequested?.Invoke();

    private void OnReset()
    {
        // Keep state that is not an option (last file, last update check).
        var defaults = new AppSettings
        {
            LastConnectionFilePath = _working.LastConnectionFilePath,
            LastUpdateCheckUtc = _working.LastUpdateCheckUtc,
        };
        _working.CopyFrom(defaults);
        ValidationMessage = string.Empty;

        CreatePages();
        this.RaisePropertyChanged(nameof(General));
        this.RaisePropertyChanged(nameof(Appearance));
        this.RaisePropertyChanged(nameof(Connections));
        this.RaisePropertyChanged(nameof(Credentials));
        this.RaisePropertyChanged(nameof(Notifications));
        this.RaisePropertyChanged(nameof(Updates));
        this.RaisePropertyChanged(nameof(Saving));
        this.RaisePropertyChanged(nameof(SqlServer));
        this.RaisePropertyChanged(nameof(Logging));
        CurrentPage = ResolvePageViewModel(SelectedCategory?.Key);
    }
}
