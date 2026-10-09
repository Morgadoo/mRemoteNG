using System.Reflection;
using mRemoteNG.Core.Config;
using mRemoteNG.Core.Connection.Protocol;

namespace mRemoteNG.Core.Settings;

public enum ThemeMode
{
    Dark,
    Light,
    /// <summary>Follow the operating system's light/dark preference.</summary>
    System,
}

public enum StartupFileBehavior
{
    /// <summary>Start with an empty connection tree.</summary>
    None,
    /// <summary>Reopen the connection file that was open when the app last exited.</summary>
    ReopenLastFile,
    /// <summary>Always open <see cref="AppSettings.StartupFilePath"/>.</summary>
    OpenSpecificFile,
}

public enum UpdateChannel
{
    /// <summary>Only full releases.</summary>
    Stable,
    /// <summary>Full releases and pre-releases (betas, nightlies).</summary>
    PreRelease,
}

/// <summary>Marks an <see cref="AppSettings"/> property as persisted, under the given settings section.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersistedSettingAttribute(string section) : Attribute
{
    public string Section { get; } = section;
}

/// <summary>
/// Strongly typed application settings with their defaults.
/// Persisted by <see cref="AppSettingsService"/> through <see cref="Platform.ISettingsProvider"/>:
/// each property marked with <see cref="PersistedSettingAttribute"/> is stored under its section,
/// keyed by property name. Every property is a value type or string, so <see cref="Clone"/> is a full copy.
/// </summary>
public sealed class AppSettings
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;
    public const int MinConnectTimeoutSeconds = 1;
    public const int MaxConnectTimeoutSeconds = 300;
    public const int MinKeepAliveSeconds = 5;
    public const int MaxKeepAliveSeconds = 3600;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 32;
    public const int MinReconnectAttempts = 1;
    public const int MaxReconnectAttempts = 50;
    public const int DefaultReconnectAttempts = 5;

    /// <summary>Protocol names offered as the default protocol (same strings as the quick-connect box).</summary>
    public static IReadOnlyList<string> DefaultProtocolChoices { get; } = ["SSH", "RDP", "VNC", "Telnet", "HTTP", "HTTPS"];

    // ── Startup / exit ───────────────────────────────────────────────────

    [PersistedSetting("Startup")] public StartupFileBehavior StartupBehavior { get; set; } = StartupFileBehavior.ReopenLastFile;

    /// <summary>File opened at startup when <see cref="StartupBehavior"/> is <see cref="StartupFileBehavior.OpenSpecificFile"/>.</summary>
    [PersistedSetting("Startup")] public string StartupFilePath { get; set; } = string.Empty;

    /// <summary>The connection file that was open when the app last exited (not shown in the UI).</summary>
    [PersistedSetting("Startup")] public string LastConnectionFilePath { get; set; } = string.Empty;

    /// <summary>Only one running instance; a second launch exits immediately.</summary>
    [PersistedSetting("Startup")] public bool SingleInstance { get; set; }

    /// <summary>Save the open connection file automatically when the app exits.</summary>
    [PersistedSetting("Exit")] public bool SaveConnectionsOnExit { get; set; }

    /// <summary>
    /// <see cref="ConfirmCloseEnum.Never"/>: never ask.
    /// <see cref="ConfirmCloseEnum.Exit"/>: ask before exiting while connections are open.
    /// <see cref="ConfirmCloseEnum.All"/>: also ask before closing each connection tab.
    /// </summary>
    [PersistedSetting("Exit")] public ConfirmCloseEnum ConfirmCloseConnection { get; set; } = ConfirmCloseEnum.Exit;

    [PersistedSetting("Tray")] public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Hide the main window when it is minimised; restore it from the tray icon.</summary>
    [PersistedSetting("Tray")] public bool MinimizeToTray { get; set; }

    // ── Appearance ───────────────────────────────────────────────────────

    [PersistedSetting("Appearance")] public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    /// <summary>UI font family; empty means the theme's default font.</summary>
    [PersistedSetting("Appearance")] public string FontFamily { get; set; } = string.Empty;

    [PersistedSetting("Appearance")] public double FontSize { get; set; } = 13;

    [PersistedSetting("Appearance")] public bool ShowToolbar { get; set; } = true;

    [PersistedSetting("Appearance")] public bool ShowStatusBar { get; set; } = true;

    /// <summary>Show the tools' names (not only their icons) on the External Tools toolbar (legacy ExtAppsTBShowText).</summary>
    [PersistedSetting("Appearance")] public bool ShowExternalToolsText { get; set; } = true;

    // ── Connections ──────────────────────────────────────────────────────

    /// <summary>Protocol preselected for new / quick connections; one of <see cref="DefaultProtocolChoices"/>.</summary>
    [PersistedSetting("Connections")] public string DefaultProtocol { get; set; } = "SSH";

    [PersistedSetting("Connections")] public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Username used when a connection does not specify one.</summary>
    [PersistedSetting("Connections")] public string DefaultUsername { get; set; } = string.Empty;

    [PersistedSetting("Connections")] public bool SshKeepAliveEnabled { get; set; } = true;

    [PersistedSetting("Connections")] public int SshKeepAliveIntervalSeconds { get; set; } = 60;

    /// <summary>Private key used for SSH connections that do not specify one; empty for none.</summary>
    [PersistedSetting("Connections")] public string SshPrivateKeyPath { get; set; } = string.Empty;

    [PersistedSetting("DefaultPorts")] public int SshPort { get; set; } = 22;
    [PersistedSetting("DefaultPorts")] public int TelnetPort { get; set; } = 23;
    [PersistedSetting("DefaultPorts")] public int RloginPort { get; set; } = 513;
    [PersistedSetting("DefaultPorts")] public int RdpPort { get; set; } = 3389;
    [PersistedSetting("DefaultPorts")] public int VncPort { get; set; } = 5900;
    [PersistedSetting("DefaultPorts")] public int HttpPort { get; set; } = 80;
    [PersistedSetting("DefaultPorts")] public int HttpsPort { get; set; } = 443;

    // ── Default connection ───────────────────────────────────────────────

    /// <summary>
    /// Values given to new connections and folders (legacy ConDefault*), as written by
    /// <see cref="Connection.DefaultConnectionSettings.Save"/>; empty for the built-in defaults.
    /// </summary>
    [PersistedSetting("DefaultConnection")] public string DefaultConnectionValues { get; set; } = string.Empty;

    /// <summary>Properties new connections inherit from their folder (legacy InhDefault*), comma-separated.</summary>
    [PersistedSetting("DefaultConnection")] public string DefaultConnectionInheritance { get; set; } = string.Empty;

    // ── Notifications ────────────────────────────────────────────────────

    [PersistedSetting("Notifications")] public bool NotifyOnConnect { get; set; }

    [PersistedSetting("Notifications")] public bool NotifyOnDisconnect { get; set; }

    [PersistedSetting("Notifications")] public bool NotifyOnError { get; set; } = true;

    // ── Updates ──────────────────────────────────────────────────────────

    [PersistedSetting("Updates")] public bool CheckForUpdatesOnStartup { get; set; } = true;

    [PersistedSetting("Updates")] public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>When the last automatic update check ran (not shown in the UI).</summary>
    [PersistedSetting("Updates")] public DateTime? LastUpdateCheckUtc { get; set; }

    // ── External credential / address providers ──────────────────────────
    // Properties ending in "Protected" hold ICryptoProvider ciphertext; empty means
    // "not saved — ask once per session" (the legacy app's behaviour).

    /// <summary>Provider used for connections without a username (legacy "UserViaAPIDefault").</summary>
    [PersistedSetting("ExternalProviders")] public Connection.ExternalCredentialProvider DefaultExternalCredentialProvider { get; set; }

    /// <summary>Secret reference used with <see cref="DefaultExternalCredentialProvider"/>.</summary>
    [PersistedSetting("ExternalProviders")] public string DefaultUserViaApi { get; set; } = string.Empty;

    /// <summary>Delinea (Thycotic) Secret Server base URL, e.g. https://cred.domain.local/SecretServer.</summary>
    [PersistedSetting("DelineaSecretServer")] public string DelineaUrl { get; set; } = string.Empty;
    [PersistedSetting("DelineaSecretServer")] public string DelineaUsername { get; set; } = string.Empty;
    /// <summary>Optional login domain sent with the OAuth2 password grant.</summary>
    [PersistedSetting("DelineaSecretServer")] public string DelineaDomain { get; set; } = string.Empty;
    /// <summary>Use integrated Windows / Kerberos authentication (winauthwebservices).</summary>
    [PersistedSetting("DelineaSecretServer")] public bool DelineaUseSso { get; set; }
    /// <summary>Ask for a one-time password when logging in.</summary>
    [PersistedSetting("DelineaSecretServer")] public bool DelineaRequireOtp { get; set; }
    [PersistedSetting("DelineaSecretServer")] public string DelineaPasswordProtected { get; set; } = string.Empty;

    /// <summary>Clickstudios Passwordstate base URL, e.g. https://passwordstate.domain.local.</summary>
    [PersistedSetting("Passwordstate")] public string PasswordstateUrl { get; set; } = string.Empty;
    /// <summary>Use integrated Windows / Kerberos authentication (/winapi) instead of an API key.</summary>
    [PersistedSetting("Passwordstate")] public bool PasswordstateUseSso { get; set; }
    [PersistedSetting("Passwordstate")] public bool PasswordstateRequireOtp { get; set; }
    [PersistedSetting("Passwordstate")] public string PasswordstateApiKeyProtected { get; set; } = string.Empty;

    /// <summary>Path of the 1Password CLI; empty runs "op" from PATH.</summary>
    [PersistedSetting("OnePassword")] public string OnePasswordCliPath { get; set; } = string.Empty;
    /// <summary>Default --account for references that do not name one.</summary>
    [PersistedSetting("OnePassword")] public string OnePasswordAccount { get; set; } = string.Empty;

    /// <summary>Vault/OpenBao address, e.g. https://vault.domain.local:8200.</summary>
    [PersistedSetting("VaultOpenbao")] public string VaultUrl { get; set; } = string.Empty;
    /// <summary>Optional namespace (X-Vault-Namespace).</summary>
    [PersistedSetting("VaultOpenbao")] public string VaultNamespace { get; set; } = string.Empty;
    [PersistedSetting("VaultOpenbao")] public VaultAuthMethod VaultAuthMethod { get; set; } = VaultAuthMethod.Token;
    /// <summary>Mount path of the auth method; empty uses the method's default ("userpass", "ldap", "approle").</summary>
    [PersistedSetting("VaultOpenbao")] public string VaultAuthMount { get; set; } = string.Empty;
    /// <summary>Username (userpass/LDAP) or role ID (AppRole).</summary>
    [PersistedSetting("VaultOpenbao")] public string VaultUsername { get; set; } = string.Empty;
    /// <summary>Token, password or secret ID, depending on <see cref="VaultAuthMethod"/>.</summary>
    [PersistedSetting("VaultOpenbao")] public string VaultSecretProtected { get; set; } = string.Empty;
    /// <summary>Optional PEM file with the CA that signed the server certificate.</summary>
    [PersistedSetting("VaultOpenbao")] public string VaultCaCertificatePath { get; set; } = string.Empty;

    [PersistedSetting("AwsEc2")] public AwsCredentialSource AwsCredentialSource { get; set; } = AwsCredentialSource.DefaultChain;
    [PersistedSetting("AwsEc2")] public string AwsProfile { get; set; } = string.Empty;
    [PersistedSetting("AwsEc2")] public string AwsAccessKeyId { get; set; } = string.Empty;
    [PersistedSetting("AwsEc2")] public string AwsSecretAccessKeyProtected { get; set; } = string.Empty;
    /// <summary>Region used when a connection's EC2Region is empty.</summary>
    [PersistedSetting("AwsEc2")] public string AwsDefaultRegion { get; set; } = string.Empty;
    [PersistedSetting("AwsEc2")] public AwsAddressKind AwsAddressKind { get; set; } = AwsAddressKind.PublicIp;
    /// <summary>Optional EC2 endpoint override (VPC endpoint, LocalStack…).</summary>
    [PersistedSetting("AwsEc2")] public string AwsServiceUrl { get; set; } = string.Empty;

    // ── Tabs & panels ────────────────────────────────────────────────────

    /// <summary>Prefix tab titles with the protocol ("SSH2: name").</summary>
    [PersistedSetting("TabsPanels")] public bool ShowProtocolOnTabs { get; set; }

    /// <summary>Append the logon (DOMAIN\user) to tab titles.</summary>
    [PersistedSetting("TabsPanels")] public bool ShowLogonInfoOnTabs { get; set; }

    /// <summary>Title quick-connect tabs "Quick: host".</summary>
    [PersistedSetting("TabsPanels")] public bool IdentifyQuickConnectTabs { get; set; }

    /// <summary>Double-clicking a session tab closes it.</summary>
    [PersistedSetting("TabsPanels")] public bool DoubleClickOnTabClosesIt { get; set; } = true;

    /// <summary>Show the panel tab strip even when only one panel exists.</summary>
    [PersistedSetting("TabsPanels")] public bool AlwaysShowPanelTabs { get; set; }

    /// <summary>Ask which panel to open a connection in, even when the connection names one.</summary>
    [PersistedSetting("TabsPanels")] public bool AlwaysShowPanelSelectionDlg { get; set; }

    /// <summary>Create the panel named <see cref="StartUpPanelName"/> at startup.</summary>
    [PersistedSetting("TabsPanels")] public bool CreateEmptyPanelOnStartUp { get; set; }

    [PersistedSetting("TabsPanels")] public string StartUpPanelName { get; set; } = "General";

    // ── Sessions ─────────────────────────────────────────────────────────

    /// <summary>Reopen the sessions that were open when the connection file was last saved ("Connected" attribute).</summary>
    [PersistedSetting("Sessions")] public bool OpenConnectionsFromLastSession { get; set; }

    /// <summary>Reconnect automatically when a session drops without the user closing it.</summary>
    [PersistedSetting("Sessions")] public bool ReconnectOnDisconnect { get; set; }

    /// <summary>How many automatic reconnect attempts to make (with exponential back-off).</summary>
    [PersistedSetting("Sessions")] public int ReconnectAttempts { get; set; } = DefaultReconnectAttempts;

    // ── Layout ───────────────────────────────────────────────────────────

    /// <summary>Main window and session panel layout as JSON (not shown in the UI); empty for the default layout.</summary>
    [PersistedSetting("Layout")] public string WindowLayout { get; set; } = string.Empty;

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>All persisted properties with their section.</summary>
    public static IReadOnlyList<(PropertyInfo Property, string Section)> PersistedProperties { get; } =
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<PersistedSettingAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Property, x.Attribute!.Section))
            .ToList();

    /// <summary>Returns an independent copy.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Copies every persisted value from <paramref name="other"/> into this instance.</summary>
    public void CopyFrom(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (property, _) in PersistedProperties)
            property.SetValue(this, property.GetValue(other));
    }

    /// <summary>True when every persisted value equals the one in <paramref name="other"/>.</summary>
    public bool ValueEquals(AppSettings other) =>
        PersistedProperties.All(p => Equals(p.Property.GetValue(this), p.Property.GetValue(other)));

    /// <summary>The default port for a protocol, or null when the protocol has no network port.</summary>
    public int? GetDefaultPort(ProtocolType protocol) => protocol switch
    {
        ProtocolType.SSH1 or ProtocolType.SSH2 => SshPort,
        ProtocolType.Telnet => TelnetPort,
        ProtocolType.Rlogin => RloginPort,
        ProtocolType.RDP => RdpPort,
        ProtocolType.VNC or ProtocolType.ARD => VncPort,
        ProtocolType.HTTP => HttpPort,
        ProtocolType.HTTPS => HttpsPort,
        _ => null,
    };

    /// <summary>The default port for a protocol display name ("SSH", "RDP", …), or null if unknown.</summary>
    public int? GetDefaultPort(string protocolName) => protocolName?.Trim().ToUpperInvariant() switch
    {
        "SSH" or "SSH2" or "SSH1" or "SFTP" => SshPort,
        "TELNET" => TelnetPort,
        "RLOGIN" => RloginPort,
        "RDP" => RdpPort,
        "VNC" => VncPort,
        "HTTP" => HttpPort,
        "HTTPS" => HttpsPort,
        _ => null,
    };

    /// <summary>Returns a human-readable message for every invalid value; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        void Port(string name, int value)
        {
            if (value is < MinPort or > MaxPort)
                errors.Add($"{name} default port must be between {MinPort} and {MaxPort}.");
        }

        Port("SSH", SshPort);
        Port("Telnet", TelnetPort);
        Port("rlogin", RloginPort);
        Port("RDP", RdpPort);
        Port("VNC", VncPort);
        Port("HTTP", HttpPort);
        Port("HTTPS", HttpsPort);

        if (ConnectTimeoutSeconds is < MinConnectTimeoutSeconds or > MaxConnectTimeoutSeconds)
            errors.Add($"Connect timeout must be between {MinConnectTimeoutSeconds} and {MaxConnectTimeoutSeconds} seconds.");

        if (SshKeepAliveIntervalSeconds is < MinKeepAliveSeconds or > MaxKeepAliveSeconds)
            errors.Add($"Keep-alive interval must be between {MinKeepAliveSeconds} and {MaxKeepAliveSeconds} seconds.");

        if (double.IsNaN(FontSize) || FontSize is < MinFontSize or > MaxFontSize)
            errors.Add($"Font size must be between {MinFontSize} and {MaxFontSize}.");

        if (ReconnectAttempts is < MinReconnectAttempts or > MaxReconnectAttempts)
            errors.Add($"Reconnect attempts must be between {MinReconnectAttempts} and {MaxReconnectAttempts}.");

        if (!DefaultProtocolChoices.Contains(DefaultProtocol))
            errors.Add($"Default protocol must be one of: {string.Join(", ", DefaultProtocolChoices)}.");

        if (StartupBehavior == StartupFileBehavior.OpenSpecificFile && string.IsNullOrWhiteSpace(StartupFilePath))
            errors.Add("Choose the connection file to open at startup.");

        if (!Enum.IsDefined(StartupBehavior) || !Enum.IsDefined(ConfirmCloseConnection)
            || !Enum.IsDefined(Theme) || !Enum.IsDefined(UpdateChannel))
            errors.Add("An option has an unknown value.");

        ValidateExternalProviders(errors);

        return errors;
    }

    /// <summary>
    /// Replaces every invalid value with its default (used after loading a hand-edited or old file).
    /// Returns the names of the properties that were reset.
    /// </summary>
    public IReadOnlyList<string> Normalize()
    {
        var defaults = new AppSettings();
        var reset = new List<string>();

        void Fix<T>(string name, bool invalid, Action<T> set, T value)
        {
            if (!invalid) return;
            set(value);
            reset.Add(name);
        }

        Fix<int>(nameof(SshPort), SshPort is < MinPort or > MaxPort, v => SshPort = v, defaults.SshPort);
        Fix<int>(nameof(TelnetPort), TelnetPort is < MinPort or > MaxPort, v => TelnetPort = v, defaults.TelnetPort);
        Fix<int>(nameof(RloginPort), RloginPort is < MinPort or > MaxPort, v => RloginPort = v, defaults.RloginPort);
        Fix<int>(nameof(RdpPort), RdpPort is < MinPort or > MaxPort, v => RdpPort = v, defaults.RdpPort);
        Fix<int>(nameof(VncPort), VncPort is < MinPort or > MaxPort, v => VncPort = v, defaults.VncPort);
        Fix<int>(nameof(HttpPort), HttpPort is < MinPort or > MaxPort, v => HttpPort = v, defaults.HttpPort);
        Fix<int>(nameof(HttpsPort), HttpsPort is < MinPort or > MaxPort, v => HttpsPort = v, defaults.HttpsPort);
        Fix<int>(nameof(ConnectTimeoutSeconds),
            ConnectTimeoutSeconds is < MinConnectTimeoutSeconds or > MaxConnectTimeoutSeconds,
            v => ConnectTimeoutSeconds = v, defaults.ConnectTimeoutSeconds);
        Fix<int>(nameof(SshKeepAliveIntervalSeconds),
            SshKeepAliveIntervalSeconds is < MinKeepAliveSeconds or > MaxKeepAliveSeconds,
            v => SshKeepAliveIntervalSeconds = v, defaults.SshKeepAliveIntervalSeconds);
        Fix<double>(nameof(FontSize), double.IsNaN(FontSize) || FontSize is < MinFontSize or > MaxFontSize,
            v => FontSize = v, defaults.FontSize);
        Fix<string>(nameof(DefaultProtocol), !DefaultProtocolChoices.Contains(DefaultProtocol),
            v => DefaultProtocol = v, defaults.DefaultProtocol);
        Fix<string>(nameof(StartupFilePath), StartupFilePath is null, v => StartupFilePath = v, string.Empty);
        Fix<string>(nameof(LastConnectionFilePath), LastConnectionFilePath is null, v => LastConnectionFilePath = v, string.Empty);
        Fix<string>(nameof(FontFamily), FontFamily is null, v => FontFamily = v, string.Empty);
        Fix<string>(nameof(DefaultUsername), DefaultUsername is null, v => DefaultUsername = v, string.Empty);
        Fix<int>(nameof(ReconnectAttempts), ReconnectAttempts is < MinReconnectAttempts or > MaxReconnectAttempts,
            v => ReconnectAttempts = v, defaults.ReconnectAttempts);
        Fix<string>(nameof(StartUpPanelName), StartUpPanelName is null, v => StartUpPanelName = v, defaults.StartUpPanelName);
        Fix<string>(nameof(WindowLayout), WindowLayout is null, v => WindowLayout = v, string.Empty);
        Fix<string>(nameof(SshPrivateKeyPath), SshPrivateKeyPath is null, v => SshPrivateKeyPath = v, string.Empty);
        Fix<string>(nameof(DefaultConnectionValues), DefaultConnectionValues is null, v => DefaultConnectionValues = v, string.Empty);
        Fix<string>(nameof(DefaultConnectionInheritance), DefaultConnectionInheritance is null, v => DefaultConnectionInheritance = v, string.Empty);
        Fix<StartupFileBehavior>(nameof(StartupBehavior),
            StartupBehavior == StartupFileBehavior.OpenSpecificFile && string.IsNullOrWhiteSpace(StartupFilePath),
            v => StartupBehavior = v, defaults.StartupBehavior);

        foreach (var property in ExternalProviderStringProperties)
            Fix<string>(property.Name, property.GetValue(this) is null, v => property.SetValue(this, v), string.Empty);
        Fix<Connection.ExternalCredentialProvider>(nameof(DefaultExternalCredentialProvider),
            !Enum.IsDefined(DefaultExternalCredentialProvider), v => DefaultExternalCredentialProvider = v, defaults.DefaultExternalCredentialProvider);
        Fix<VaultAuthMethod>(nameof(VaultAuthMethod), !Enum.IsDefined(VaultAuthMethod), v => VaultAuthMethod = v, defaults.VaultAuthMethod);
        Fix<AwsCredentialSource>(nameof(AwsCredentialSource), !Enum.IsDefined(AwsCredentialSource), v => AwsCredentialSource = v, defaults.AwsCredentialSource);
        Fix<AwsAddressKind>(nameof(AwsAddressKind), !Enum.IsDefined(AwsAddressKind), v => AwsAddressKind = v, defaults.AwsAddressKind);

        return reset;
    }

    private static readonly IReadOnlyList<PropertyInfo> ExternalProviderStringProperties =
        PersistedProperties
            .Where(p => p.Property.PropertyType == typeof(string)
                && p.Section is "ExternalProviders" or "DelineaSecretServer" or "Passwordstate" or "OnePassword" or "VaultOpenbao" or "AwsEc2")
            .Select(p => p.Property)
            .ToList();

    private void ValidateExternalProviders(List<string> errors)
    {
        void Url(string name, string value)
        {
            if (value.Length == 0)
                return;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                errors.Add($"{name} must be an http:// or https:// address.");
        }

        Url("Delinea Secret Server URL", DelineaUrl ?? string.Empty);
        Url("Passwordstate URL", PasswordstateUrl ?? string.Empty);
        Url("Vault/OpenBao URL", VaultUrl ?? string.Empty);
        Url("AWS EC2 endpoint URL", AwsServiceUrl ?? string.Empty);

        if (AwsCredentialSource == AwsCredentialSource.Profile && string.IsNullOrWhiteSpace(AwsProfile))
            errors.Add("Enter the AWS profile name, or choose another AWS credential source.");

        if (!Enum.IsDefined(DefaultExternalCredentialProvider) || !Enum.IsDefined(VaultAuthMethod)
            || !Enum.IsDefined(AwsCredentialSource) || !Enum.IsDefined(AwsAddressKind))
            errors.Add("An external provider option has an unknown value.");
    }
}
