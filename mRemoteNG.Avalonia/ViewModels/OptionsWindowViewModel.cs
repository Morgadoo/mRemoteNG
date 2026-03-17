using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

// ── Settings page ViewModels ──────────────────────────────────────────────
public sealed class AppearanceSettingsViewModel : ReactiveObject
{
    private double _fontSize = 13;
    private string _fontFamily = "Segoe UI, SF Pro Display, Ubuntu";
    private bool _showStatusBar = true;
    private bool _showToolbar = true;

    public double FontSize { get => _fontSize; set => this.RaiseAndSetIfChanged(ref _fontSize, value); }
    public string FontFamily { get => _fontFamily; set => this.RaiseAndSetIfChanged(ref _fontFamily, value); }
    public bool ShowStatusBar { get => _showStatusBar; set => this.RaiseAndSetIfChanged(ref _showStatusBar, value); }
    public bool ShowToolbar { get => _showToolbar; set => this.RaiseAndSetIfChanged(ref _showToolbar, value); }
}

public sealed class ConnectionSettingsViewModel : ReactiveObject
{
    private int _defaultPort = 22;
    private int _connectTimeout = 10;
    private bool _autoReconnect = true;
    private bool _keepAlive = true;
    private int _keepAliveInterval = 60;
    private string _defaultProtocol = "SSH";

    public int DefaultPort { get => _defaultPort; set => this.RaiseAndSetIfChanged(ref _defaultPort, value); }
    public int ConnectTimeout { get => _connectTimeout; set => this.RaiseAndSetIfChanged(ref _connectTimeout, value); }
    public bool AutoReconnect { get => _autoReconnect; set => this.RaiseAndSetIfChanged(ref _autoReconnect, value); }
    public bool KeepAlive { get => _keepAlive; set => this.RaiseAndSetIfChanged(ref _keepAlive, value); }
    public int KeepAliveInterval { get => _keepAliveInterval; set => this.RaiseAndSetIfChanged(ref _keepAliveInterval, value); }
    public string DefaultProtocol { get => _defaultProtocol; set => this.RaiseAndSetIfChanged(ref _defaultProtocol, value); }

    public string[] Protocols { get; } = ["SSH", "RDP", "VNC", "Telnet", "HTTP", "HTTPS"];
}

public sealed class SecuritySettingsViewModel : ReactiveObject
{
    private bool _useMasterPassword;
    private bool _lockOnIdle;
    private int _lockIdleMinutes = 15;
    private bool _encryptConnections = true;
    private bool _verifyServerCerts = true;

    public bool UseMasterPassword { get => _useMasterPassword; set => this.RaiseAndSetIfChanged(ref _useMasterPassword, value); }
    public bool LockOnIdle { get => _lockOnIdle; set => this.RaiseAndSetIfChanged(ref _lockOnIdle, value); }
    public int LockIdleMinutes { get => _lockIdleMinutes; set => this.RaiseAndSetIfChanged(ref _lockIdleMinutes, value); }
    public bool EncryptConnections { get => _encryptConnections; set => this.RaiseAndSetIfChanged(ref _encryptConnections, value); }
    public bool VerifyServerCerts { get => _verifyServerCerts; set => this.RaiseAndSetIfChanged(ref _verifyServerCerts, value); }
}

public sealed class AdvancedSettingsViewModel : ReactiveObject
{
    private bool _enableLogging = true;
    private bool _debugMode;
    private bool _checkUpdatesOnStart = true;
    private int _maxLogEntries = 1000;
    private bool _singleInstance;

    public bool EnableLogging { get => _enableLogging; set => this.RaiseAndSetIfChanged(ref _enableLogging, value); }
    public bool DebugMode { get => _debugMode; set => this.RaiseAndSetIfChanged(ref _debugMode, value); }
    public bool CheckUpdatesOnStart { get => _checkUpdatesOnStart; set => this.RaiseAndSetIfChanged(ref _checkUpdatesOnStart, value); }
    public int MaxLogEntries { get => _maxLogEntries; set => this.RaiseAndSetIfChanged(ref _maxLogEntries, value); }
    public bool SingleInstance { get => _singleInstance; set => this.RaiseAndSetIfChanged(ref _singleInstance, value); }
}

public sealed class UpdatesSettingsViewModel : ReactiveObject
{
    private bool _autoCheck = true;
    private string _updateChannel = "Stable";
    public bool AutoCheck { get => _autoCheck; set => this.RaiseAndSetIfChanged(ref _autoCheck, value); }
    public string UpdateChannel { get => _updateChannel; set => this.RaiseAndSetIfChanged(ref _updateChannel, value); }
    public string[] Channels { get; } = ["Stable", "Beta", "Nightly"];
}

public sealed class NotificationsSettingsViewModel : ReactiveObject
{
    private bool _showConnectNotify = true;
    private bool _showDisconnectNotify = true;
    private bool _showErrorNotify = true;
    private bool _playSounds;

    public bool ShowConnectNotify { get => _showConnectNotify; set => this.RaiseAndSetIfChanged(ref _showConnectNotify, value); }
    public bool ShowDisconnectNotify { get => _showDisconnectNotify; set => this.RaiseAndSetIfChanged(ref _showDisconnectNotify, value); }
    public bool ShowErrorNotify { get => _showErrorNotify; set => this.RaiseAndSetIfChanged(ref _showErrorNotify, value); }
    public bool PlaySounds { get => _playSounds; set => this.RaiseAndSetIfChanged(ref _playSounds, value); }
}

public sealed class ThemeSettingsViewModel : ReactiveObject
{
    private string _selectedTheme = "Dark";
    public string SelectedTheme { get => _selectedTheme; set => this.RaiseAndSetIfChanged(ref _selectedTheme, value); }
    public string[] Themes { get; } = ["Dark", "Light", "System"];
}

public sealed class TabsSettingsViewModel : ReactiveObject
{
    private bool _confirmOnClose = true;
    private bool _closeOnDoubleClick;
    private string _tabPosition = "Top";
    private bool _showCloseButton = true;

    public bool ConfirmOnClose { get => _confirmOnClose; set => this.RaiseAndSetIfChanged(ref _confirmOnClose, value); }
    public bool CloseOnDoubleClick { get => _closeOnDoubleClick; set => this.RaiseAndSetIfChanged(ref _closeOnDoubleClick, value); }
    public string TabPosition { get => _tabPosition; set => this.RaiseAndSetIfChanged(ref _tabPosition, value); }
    public bool ShowCloseButton { get => _showCloseButton; set => this.RaiseAndSetIfChanged(ref _showCloseButton, value); }
    public string[] Positions { get; } = ["Top", "Bottom"];
}

public sealed class CredentialsSettingsViewModel : ReactiveObject
{
    private string _defaultUsername = string.Empty;
    private bool _savePasswords = true;
    private bool _askBeforeConnect;

    public string DefaultUsername { get => _defaultUsername; set => this.RaiseAndSetIfChanged(ref _defaultUsername, value); }
    public bool SavePasswords { get => _savePasswords; set => this.RaiseAndSetIfChanged(ref _savePasswords, value); }
    public bool AskBeforeConnect { get => _askBeforeConnect; set => this.RaiseAndSetIfChanged(ref _askBeforeConnect, value); }
}

public sealed class ProtocolsSettingsViewModel : ReactiveObject
{
    private string _sshKeyPath = string.Empty;
    private int _rdpColorDepth = 32;
    private bool _rdpSmartSize = true;
    private string _vncEncoding = "Tight";
    private bool _puttyCompatMode;

    public string SshKeyPath { get => _sshKeyPath; set => this.RaiseAndSetIfChanged(ref _sshKeyPath, value); }
    public int RdpColorDepth { get => _rdpColorDepth; set => this.RaiseAndSetIfChanged(ref _rdpColorDepth, value); }
    public bool RdpSmartSize { get => _rdpSmartSize; set => this.RaiseAndSetIfChanged(ref _rdpSmartSize, value); }
    public string VncEncoding { get => _vncEncoding; set => this.RaiseAndSetIfChanged(ref _vncEncoding, value); }
    public bool PuttyCompatMode { get => _puttyCompatMode; set => this.RaiseAndSetIfChanged(ref _puttyCompatMode, value); }
    public int[] ColorDepths { get; } = [8, 15, 16, 24, 32];
    public string[] VncEncodings { get; } = ["Tight", "ZRLE", "Hextile", "Raw"];
}

// ── Main OptionsWindowViewModel ───────────────────────────────────────────
public sealed class SettingsCategoryViewModel(string displayName, string key) : ReactiveObject
{
    public string DisplayName { get; } = displayName;
    public string Key { get; } = key;
}

public sealed class OptionsWindowViewModel : ReactiveObject
{
    private SettingsCategoryViewModel? _selectedCategory;
    private object? _currentPage;

    // Page ViewModels
    public AppearanceSettingsViewModel Appearance { get; } = new();
    public ConnectionSettingsViewModel Connections { get; } = new();
    public SecuritySettingsViewModel Security { get; } = new();
    public AdvancedSettingsViewModel Advanced { get; } = new();
    public UpdatesSettingsViewModel Updates { get; } = new();
    public NotificationsSettingsViewModel Notifications { get; } = new();
    public ThemeSettingsViewModel Theme { get; } = new();
    public TabsSettingsViewModel Tabs { get; } = new();
    public CredentialsSettingsViewModel Credentials { get; } = new();
    public ProtocolsSettingsViewModel Protocols { get; } = new();

    public List<SettingsCategoryViewModel> Categories { get; } =
    [
        new("Appearance", "appearance"),
        new("Connections", "connections"),
        new("Security", "security"),
        new("Advanced", "advanced"),
        new("Updates", "updates"),
        new("Notifications", "notifications"),
        new("Theme", "theme"),
        new("Tabs & Panels", "tabs"),
        new("Credentials", "credentials"),
        new("Protocols", "protocols"),
    ];

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

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    public OptionsWindowViewModel()
    {
        OkCommand = ReactiveCommand.Create(OnOk);
        CancelCommand = ReactiveCommand.Create(OnCancel);
        ApplyCommand = ReactiveCommand.Create(OnApply);
        ResetCommand = ReactiveCommand.Create(OnReset);
        SelectedCategory = Categories.FirstOrDefault();
    }

    private object? ResolvePageViewModel(string? key) => key switch
    {
        "appearance" => Appearance,
        "connections" => Connections,
        "security" => Security,
        "advanced" => Advanced,
        "updates" => Updates,
        "notifications" => Notifications,
        "theme" => Theme,
        "tabs" => Tabs,
        "credentials" => Credentials,
        "protocols" => Protocols,
        _ => null,
    };

    /// <summary>Raised when the window should close.</summary>
    public event Action? CloseRequested;

    private void OnOk() { OnApply(); CloseRequested?.Invoke(); }
    private void OnCancel() { CloseRequested?.Invoke(); }
    private void OnApply()
    {
        // Settings are already bound to page VMs via two-way binding.
        // Future: persist to ISettingsProvider here.
    }
    private void OnReset()
    {
        // Reset all pages to defaults
        Appearance.FontSize = 13;
        Appearance.ShowStatusBar = true;
        Appearance.ShowToolbar = true;
        Connections.DefaultPort = 22;
        Connections.ConnectTimeout = 10;
        Connections.AutoReconnect = true;
        Security.EncryptConnections = true;
        Advanced.EnableLogging = true;
    }
}
