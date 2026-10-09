using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Data.Converters;
using Avalonia.Platform;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.Http;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Net;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>Context for the connection editor: suggestion lists and the editing mode.</summary>
public sealed class ConnectionDialogOptions
{
    public static ConnectionDialogOptions Default { get; } = new();

    /// <summary>
    /// Editing the "default connection" (values and inheritance given to new nodes): no name or host name,
    /// and the Inherit boxes set default flags instead of replacing values.
    /// </summary>
    public bool IsDefaultConnection { get; init; }

    public IReadOnlyList<string> Panels { get; init; } = ["General"];
    public IReadOnlyList<string> PuttySessions { get; init; } = [];
    public IReadOnlyList<string> SshTunnels { get; init; } = [];

    /// <summary>External tool names (filled once External Tools are available; free text until then).</summary>
    public IReadOnlyList<string> ExternalTools { get; init; } = [];
}

/// <summary>
/// Property editor for a connection, folder or the default connection. Every editable, persisted
/// <see cref="ConnectionInfo"/> property (<see cref="ConnectionPropertyCatalog"/>) gets an editor, grouped
/// into the legacy categories; properties that do not apply to the chosen protocol or settings are hidden.
/// Edits are buffered and written to the Core node only by <see cref="Apply"/> (OK); Cancel discards them.
/// </summary>
public sealed class ConnectionDialogViewModel : ReactiveObject
{
    private static readonly string[] ColorSuggestions =
        ["Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Pink", "Brown", "Gray", "Black", "White"];

    private readonly ConnectionInfo _target;
    private readonly Dictionary<string, PropertyFieldViewModel> _fields = new(StringComparer.Ordinal);
    private string _statusText = string.Empty;
    private bool _updatingInheritEverything;
    private bool? _inheritEverythingChoice;
    private CoreProtocolType _lastProtocol;

    /// <param name="target">The node to edit (for a new node: a detached, not-yet-added instance).</param>
    /// <param name="parent">The folder the node is (or will be) in; decides what can be inherited.</param>
    public ConnectionDialogViewModel(ConnectionInfo target, ContainerInfo? parent, bool isNew, ConnectionDialogOptions? options = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        options ??= ConnectionDialogOptions.Default;
        IsNew = isNew;
        IsDefaultConnection = options.IsDefaultConnection;
        IsFolder = target is ContainerInfo;
        IsRoot = target is RootNodeInfo;
        CanInherit = IsDefaultConnection || (!IsRoot && ConnectionInheritanceAccessor.CanInheritFrom(parent));
        ParentName = parent?.Name ?? string.Empty;

        IReadOnlyList<string> Suggestions(ConnectionPropertySuggestions kind) => kind switch
        {
            ConnectionPropertySuggestions.Icons => IconNames,
            ConnectionPropertySuggestions.Panels => options.Panels,
            ConnectionPropertySuggestions.PuttySessions => options.PuttySessions,
            ConnectionPropertySuggestions.SshTunnels => options.SshTunnels.Where(n => n != target.Name).ToList(),
            ConnectionPropertySuggestions.ExternalTools => options.ExternalTools,
            ConnectionPropertySuggestions.Colors => ColorSuggestions,
            _ => [],
        };

        foreach (var descriptor in ConnectionPropertyCatalog.All)
        {
            var field = PropertyFieldViewModel.Create(descriptor, target, IsDefaultConnection ? null : parent,
                CanInherit, IsDefaultConnection, ParentName, Suggestions);
            _fields.Add(descriptor.Name, field);
            field.ValueChanged += OnFieldValueChanged;
            if (field.SupportsInheritance)
                field.WhenAnyValue(f => f.Inherit).Skip(1).Subscribe(_ => RaiseInheritEverything());
        }

        Fields = ConnectionPropertyCatalog.All.Select(d => _fields[d.Name]).ToList();
        Tabs = ConnectionPropertyCategories.All.Select(BuildTab).ToList();
        InheritanceSections = Fields.Where(f => f.SupportsInheritance)
            .GroupBy(f => f.Descriptor.Category == ConnectionPropertyCategories.Protocol ? f.Descriptor.Section : f.Descriptor.Category)
            .Select(g => new PropertySectionViewModel(ConnectionPropertyCategories.GetDisplayName(g.Key), g.ToList()))
            .ToList();

        _lastProtocol = Protocol.Value is CoreProtocolType p ? p : CoreProtocolType.RDP;
        UpdateVisibility();
        Validate();

        var canSave = this.WhenAnyValue(x => x.IsValid).ObserveOn(RxApp.MainThreadScheduler);
        OkCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(true), canSave);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(false));
        CheckStatusCommand = ReactiveCommand.CreateFromTask(CheckStatusAsync,
            this.WhenAnyValue(x => x.CanCheckStatus).ObserveOn(RxApp.MainThreadScheduler));
    }

    public bool IsNew { get; }
    public bool IsFolder { get; }
    public bool IsRoot { get; }
    public bool IsConnection => !IsFolder && !IsDefaultConnection;
    public bool IsDefaultConnection { get; }

    /// <summary>True when Inherit boxes can be changed (the node is inside a folder, or the default connection).</summary>
    public bool CanInherit { get; }

    public string ParentName { get; }

    public string InheritHint => IsDefaultConnection
        ? Localizer.Get("InheritHintDefaultConnection")
        : CanInherit
            ? Localizer.Format("InheritHintFolderFormat", ParentName)
            : Localizer.Get("InheritHintRoot");

    public string WindowTitle => IsDefaultConnection
        ? Localizer.Get("DefaultConnectionProperties")
        : (IsNew, IsFolder) switch
        {
            (true, true) => Localizer.Get("NewFolder"),
            (true, false) => Localizer.Get("NewConnection"),
            (false, true) => Localizer.Format("EditFolderTitleFormat", _target.Name),
            _ => Localizer.Format("EditConnectionTitleFormat", _target.Name),
        };

    // ── Fields ────────────────────────────────────────────────────────────

    /// <summary>Every property editor, in catalog order.</summary>
    public IReadOnlyList<PropertyFieldViewModel> Fields { get; }

    /// <summary>Display, Connection, Credentials, Protocol and Miscellaneous tabs.</summary>
    public IReadOnlyList<PropertyTabViewModel> Tabs { get; }

    public PropertyTabViewModel DisplayTab => Tabs[0];
    public PropertyTabViewModel ConnectionTab => Tabs[1];
    public PropertyTabViewModel CredentialsTab => Tabs[2];
    public PropertyTabViewModel ProtocolTab => Tabs[3];
    public PropertyTabViewModel MiscellaneousTab => Tabs[4];

    /// <summary>The Inheritance tab: every property that has an Inherit flag, grouped.</summary>
    public IReadOnlyList<PropertySectionViewModel> InheritanceSections { get; }

    public PropertyFieldViewModel Field(string propertyName) =>
        _fields.TryGetValue(propertyName, out var field)
            ? field
            : throw new ArgumentException($"No editor for {propertyName}.", nameof(propertyName));

    public TField Field<TField>(string propertyName) where TField : PropertyFieldViewModel => (TField)Field(propertyName);

    public TextFieldViewModel NameField => Field<TextFieldViewModel>(nameof(ConnectionInfo.Name));
    public TextFieldViewModel HostnameField => Field<TextFieldViewModel>(nameof(ConnectionInfo.Hostname));
    public ChoiceFieldViewModel Protocol => Field<ChoiceFieldViewModel>(nameof(ConnectionInfo.Protocol));
    public NumberFieldViewModel Port => Field<NumberFieldViewModel>(nameof(ConnectionInfo.Port));

    public string Name
    {
        get => NameField.Value;
        set => NameField.Value = value;
    }

    public string Hostname
    {
        get => HostnameField.Value;
        set => HostnameField.Value = value;
    }

    public CoreProtocolType SelectedProtocol => Protocol.Value is CoreProtocolType p ? p : CoreProtocolType.RDP;

    /// <summary>Legacy "Inherit everything": all Inherit boxes on (null when some are).</summary>
    public bool? InheritEverything
    {
        get
        {
            var flags = Fields.Where(f => f.CanInherit).Select(f => f.Inherit).Distinct().ToList();
            return flags.Count switch
            {
                0 => false,
                1 => flags[0],
                _ => null,
            };
        }
        set
        {
            if (value is null) return;
            _inheritEverythingChoice = value;
            _updatingInheritEverything = true;
            try
            {
                foreach (var editor in Fields.Where(f => f.CanInherit))
                    editor.Inherit = value.Value;
            }
            finally
            {
                _updatingInheritEverything = false;
            }
            RaiseInheritEverything();
        }
    }

    public string[] IconNames { get; } = LoadIconNames();

    /// <summary>Explains how the chosen protocol behaves on this platform (empty when nothing to add).</summary>
    public string ProtocolNote => !IsConnection ? string.Empty : SelectedProtocol switch
    {
        CoreProtocolType.HTTP or CoreProtocolType.HTTPS =>
            Localizer.Get("ProtocolNoteHttp"),
        CoreProtocolType.ARD => Localizer.Get("ProtocolNoteArd"),
        CoreProtocolType.AnyDesk => Localizer.Get("ProtocolNoteAnyDesk"),
        CoreProtocolType.Terminal => Localizer.Get("ProtocolNoteTerminal"),
        CoreProtocolType.WSL => Localizer.Get("ProtocolNoteWsl"),
        CoreProtocolType.RAW => Localizer.Get("ProtocolNoteRaw"),
        CoreProtocolType.SSH1 => Localizer.Get("ProtocolNoteSsh1"),
        _ when ConnectionParametersFactory.MapProtocol(SelectedProtocol) is null =>
            Localizer.Format("ProtocolNotSupportedFormat", SelectedProtocol),
        _ => string.Empty,
    };

    // ── Validation ────────────────────────────────────────────────────────

    public string? NameError => NameField.Error;
    public string? HostnameError => HostnameField.Error;
    public string? PortError => Port.Error;

    public bool IsValid => Fields.All(f => f.Error is null);

    // ── Host status ───────────────────────────────────────────────────────

    public bool CanCheckStatus => IsConnection && HostnameField.Error is null && !string.IsNullOrWhiteSpace(Hostname);

    /// <summary>Result of the last host status check (legacy "Status" in the config window).</summary>
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>Kept for callers of the former "Test Connection" button.</summary>
    public string TestStatus => StatusText;

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckStatusCommand { get; }

    /// <summary>Raised when the dialog should close: true for OK, false for Cancel.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Writes all edits to the target node. Returns true when anything changed.</summary>
    public bool Apply()
    {
        Validate();
        if (!IsValid)
            throw new InvalidOperationException("The connection settings are not valid: " +
                                                string.Join(" ", Fields.Select(f => f.Error).OfType<string>()));

        var changed = false;
        foreach (var field in Fields)
        {
            if (field.Name is nameof(ConnectionInfo.Name) or nameof(ConnectionInfo.Hostname))
                continue;
            changed |= field.ApplyTo(_target);
        }

        // "Inherit everything" also covers flags without an editor (e.g. Color, which files do not store).
        if (CanInherit && _inheritEverythingChoice is { } all && InheritEverything == all)
        {
            foreach (var flag in _target.Inheritance.GetProperties()
                         .Where(p => p.PropertyType == typeof(bool) && p.CanWrite && !_fields.ContainsKey(p.Name)))
            {
                changed |= (bool)flag.GetValue(_target.Inheritance)! != all;
                flag.SetValue(_target.Inheritance, all);
            }
        }

        if (!IsDefaultConnection)
        {
            var name = Name.Trim();
            if (_target.Name != name)
            {
                _target.Name = name;
                changed = true;
            }

            if (!IsFolder && _target.Hostname != Hostname.Trim())
            {
                _target.Hostname = Hostname.Trim();
                changed = true;
            }
        }

        return changed;
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private PropertyTabViewModel BuildTab(string category)
    {
        var sections = ConnectionPropertyCatalog.All
            .Where(d => d.Category == category)
            .GroupBy(d => d.Section)
            .Select(g => new PropertySectionViewModel(ConnectionPropertyCategories.GetDisplayName(g.Key),
                g.Select(d => _fields[d.Name]).ToList()))
            .ToList();
        return new PropertyTabViewModel(category, sections);
    }

    private void OnFieldValueChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, Protocol))
            OnProtocolChanged();
        UpdateVisibility();
        Validate();
    }

    private void OnProtocolChanged()
    {
        var protocol = SelectedProtocol;
        if (Port.IsEditable && !Port.Inherit)
            Port.IntValue = ConnectionDefaults.PortAfterProtocolChange(_lastProtocol, protocol, Port.IntValue);
        _lastProtocol = protocol;
        this.RaisePropertyChanged(nameof(SelectedProtocol));
        this.RaisePropertyChanged(nameof(ProtocolNote));
    }

    private void UpdateVisibility()
    {
        object? Lookup(string name) => _fields.TryGetValue(name, out var f) ? f.BoxedValue : null;

        foreach (var field in Fields)
        {
            field.IsVisible = field.Name switch
            {
                nameof(ConnectionInfo.Name) => !IsDefaultConnection,
                nameof(ConnectionInfo.Hostname) => IsConnection,
                _ => !IsRoot && field.Descriptor.IsRelevant(Lookup, IsFolder),
            };
        }

        foreach (var tab in Tabs)
        {
            foreach (var section in tab.Sections)
                section.IsVisible = section.Fields.Any(f => f.IsVisible);
            tab.IsVisible = tab.Sections.Any(s => s.IsVisible);
        }
    }

    private void Validate()
    {
        var protocol = SelectedProtocol;
        NameField.Error = !IsDefaultConnection && string.IsNullOrWhiteSpace(Name) ? Localizer.Get("NameIsRequired") : null;
        HostnameField.Error = IsConnection ? ConnectionDefaults.ValidateHostname(protocol, Hostname) : null;
        Port.Error = Port.IsVisible && !IsFolder ? ConnectionDefaults.ValidatePort(protocol, Port.IntValue) : null;

        var mac = Field(nameof(ConnectionInfo.MacAddress));
        var macText = (mac.BoxedValue as string)?.Trim();
        mac.Error = string.IsNullOrEmpty(macText) || WakeOnLan.TryParseMacAddress(macText, out _)
            ? null
            : Localizer.Get("InvalidMacAddress");

        this.RaisePropertyChanged(nameof(NameError));
        this.RaisePropertyChanged(nameof(HostnameError));
        this.RaisePropertyChanged(nameof(PortError));
        this.RaisePropertyChanged(nameof(IsValid));
        this.RaisePropertyChanged(nameof(CanCheckStatus));
    }

    private void RaiseInheritEverything()
    {
        if (!_updatingInheritEverything)
            this.RaisePropertyChanged(nameof(InheritEverything));
    }

    private async Task CheckStatusAsync()
    {
        var host = Hostname.Trim();
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && host.Contains("://"))
            host = uri.Host;
        var port = ConnectionDefaults.UsesPort(SelectedProtocol)
            ? (Port.IntValue > 0 ? Port.IntValue : Core.Connection.ConnectionInfo.GetDefaultPort(SelectedProtocol))
            : 0;

        StatusText = port > 0 ? Localizer.Format("CheckingHostAndPortFormat", host, port) : Localizer.Format("PingingHostFormat", host);
        var status = await HostStatusProbe.ProbeAsync(host, port, TimeSpan.FromSeconds(4));
        StatusText = status.Summary;
        this.RaisePropertyChanged(nameof(TestStatus));
    }

    private static string[] LoadIconNames()
    {
        try
        {
            return AssetLoader.GetAssets(new Uri("avares://mRemoteNG.Avalonia/Assets/Icons/"), null)
                .Select(u => Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(u.AbsolutePath)))
                .Where(n => !string.IsNullOrEmpty(n))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return ["mRemoteNG"];
        }
    }
}

/// <summary>Turns enum values into readable labels (e.g. Colors16Bit → "High colour (16-bit)", FitToWindow → "Fit To Window").</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public static readonly EnumLabelConverter Instance = new();

    private static readonly Dictionary<object, string> Labels = new()
    {
        [RDPColors.Colors256] = "256 colours",
        [RDPColors.Colors15Bit] = "High colour (15-bit)",
        [RDPColors.Colors16Bit] = "High colour (16-bit)",
        [RDPColors.Colors24Bit] = "True colour (24-bit)",
        [RDPColors.Colors32Bit] = "Highest quality (32-bit)",
        [RDPSounds.BringToThisComputer] = "Bring to this computer",
        [RDPSounds.LeaveAtRemoteComputer] = "Leave at remote computer",
        [RDPSounds.DoNotPlay] = "Do not play",
        [RdpVersion.Highest] = "Highest available",
        [AuthenticationLevel.NoAuth] = "Connect, don't warn me",
        [AuthenticationLevel.AuthRequired] = "Don't connect",
        [AuthenticationLevel.WarnOnFailedAuth] = "Warn me",
        [RDGatewayUsageMethod.Detect] = "Detect automatically",
        [VncSmartSizeMode.SmartSNo] = "No smart size",
        [VncSmartSizeMode.SmartSFree] = "Free",
        [VncSmartSizeMode.SmartSAspect] = "Keep aspect ratio",
        [VncCompression.CompNone] = "None",
        [VncEncoding.EncRaw] = "Raw",
        [VncEncoding.EncRRE] = "RRE",
        [VncEncoding.EncCorre] = "CoRRE",
        [VncEncoding.EncHextile] = "Hextile",
        [VncEncoding.EncZlib] = "Zlib",
        [VncEncoding.EncTight] = "Tight",
        [VncEncoding.EncZLibHex] = "ZlibHex",
        [VncEncoding.EncZRLE] = "ZRLE",
        [VncAuthMode.AuthVNC] = "VNC password",
        [VncAuthMode.AuthWin] = "Windows (username and password)",
        [VncProxyType.ProxyNone] = "None",
        [VncProxyType.ProxyHTTP] = "HTTP",
        [VncProxyType.ProxySocks5] = "SOCKS 5",
        [VncProxyType.ProxyUltra] = "UltraVNC repeater",
        [VncColors.ColNormal] = "Normal",
        [VncColors.Col8Bit] = "8-bit",
        [RenderingEngine.IE] = "Internet Explorer",
        [RenderingEngine.EdgeChromium] = "Edge (Chromium)",
        [ExternalCredentialProvider.DelineaSecretServer] = "Delinea Secret Server",
        [ExternalCredentialProvider.ClickstudiosPasswordState] = "Clickstudios PasswordState",
        [ExternalCredentialProvider.OnePassword] = "1Password",
        [ExternalCredentialProvider.VaultOpenbao] = "HashiCorp Vault / OpenBao",
        [ExternalAddressProvider.AmazonWebServices] = "Amazon Web Services (EC2)",
        [VaultOpenbaoSecretEngine.Kv] = "Key/value",
        [VaultOpenbaoSecretEngine.LdapDynamic] = "LDAP (dynamic)",
        [VaultOpenbaoSecretEngine.LdapStatic] = "LDAP (static)",
        [VaultOpenbaoSecretEngine.SSHOTP] = "SSH one-time password",
        [RDGatewayUseConnectionCredentials.No] = "Use separate gateway credentials",
        [RDGatewayUseConnectionCredentials.Yes] = "Use connection credentials",
        [RDGatewayUseConnectionCredentials.SmartCard] = "Smart card",
        [RDGatewayUseConnectionCredentials.ExternalCredentialProvider] = "External credential provider",
        [RDGatewayUseConnectionCredentials.AccessToken] = "Access token",
        [ConsoleSessionChoice.AsConfigured] = "As configured",
        [ConsoleSessionChoice.Console] = "Connect to the console session",
        [ConsoleSessionChoice.NoConsole] = "Don't connect to the console session",
    };

    /// <summary>
    /// Resource keys whose translations are shown for enum values (the WinForms app's enum descriptions,
    /// plus the cross-platform app's own); the English label stays the one above.
    /// </summary>
    public static IReadOnlyDictionary<object, string> ResourceKeys { get; } = new Dictionary<object, string>
    {
        [RDPColors.Colors256] = "Rdp256Colors",
        [RDPColors.Colors15Bit] = "Rdp32768Colors",
        [RDPColors.Colors16Bit] = "Rdp65536Colors",
        [RDPColors.Colors24Bit] = "Rdp16777216Colors",
        [RDPSounds.BringToThisComputer] = "RdpSoundBringToThisComputer",
        [RDPSounds.LeaveAtRemoteComputer] = "RdpSoundLeaveAtRemoteComputer",
        [RDPSounds.DoNotPlay] = "DoNotPlay",
        [RDPSoundQuality.Dynamic] = "Dynamic",
        [RDPSoundQuality.Medium] = "Medium",
        [RDPSoundQuality.High] = "High",
        [RDPDiskDrives.None] = "RdpDrivesNone",
        [RDPDiskDrives.Local] = "RdpDrivesLocal",
        [RDPDiskDrives.All] = "RdpDrivesAll",
        [RDPDiskDrives.Custom] = "RdpDrivesCustom",
        [RDPResolutions.FitToWindow] = "FitToPanel",
        [RDPResolutions.Fullscreen] = "Fullscreen",
        [RDPResolutions.SmartSize] = "SmartSize",
        [RdpVersion.Highest] = "RdpVersionHighest",
        [AuthenticationLevel.NoAuth] = "AlwaysConnectEvenIfAuthFails",
        [AuthenticationLevel.AuthRequired] = "DontConnectWhenAuthFails",
        [AuthenticationLevel.WarnOnFailedAuth] = "WarnIfAuthFails",
        [RDGatewayUsageMethod.Never] = "Never",
        [RDGatewayUsageMethod.Always] = "Always",
        [RDGatewayUsageMethod.Detect] = "Detect",
        [RDGatewayUseConnectionCredentials.No] = "UseDifferentUsernameAndPassword",
        [RDGatewayUseConnectionCredentials.Yes] = "UseSameUsernameAndPassword",
        [RDGatewayUseConnectionCredentials.SmartCard] = "UseSmartCard",
        [RDGatewayUseConnectionCredentials.ExternalCredentialProvider] = "UseExternalCredentialProvider",
        [RDGatewayUseConnectionCredentials.AccessToken] = "UseAccessToken",
        [VncSmartSizeMode.SmartSNo] = "NoSmartSize",
        [VncSmartSizeMode.SmartSFree] = "Free",
        [VncSmartSizeMode.SmartSAspect] = "Aspect",
        [VncCompression.CompNone] = "None",
        [VncAuthMode.AuthWin] = "Windows",
        [VncProxyType.ProxyNone] = "None",
        [VncProxyType.ProxyUltra] = "UltraVncRepeater",
        [VncColors.ColNormal] = "Normal",
        [ConnectionFrameColor.None] = "FrameColorNone",
        [ConnectionFrameColor.Red] = "FrameColorRed",
        [ConnectionFrameColor.Yellow] = "FrameColorYellow",
        [ConnectionFrameColor.Green] = "FrameColorGreen",
        [ConnectionFrameColor.Blue] = "FrameColorBlue",
        [ConnectionFrameColor.Purple] = "FrameColorPurple",
        [ExternalCredentialProvider.None] = "ECPNone",
        [ExternalAddressProvider.None] = "EAPNone",
        [VaultOpenbaoSecretEngine.LdapDynamic] = "VaultOpenbaoSecretEngineLDAPDynamic",
        [VaultOpenbaoSecretEngine.LdapStatic] = "VaultOpenbaoSecretEngineLDAPStatic",
        [VaultOpenbaoSecretEngine.SSHOTP] = "VaultOpenbaoSecretEngineSSHOTP",
        [CoreProtocolType.SSH1] = "SshV1",
        [CoreProtocolType.SSH2] = "SshV2",
        [CoreProtocolType.Terminal] = "Terminal",
        [CoreProtocolType.IntApp] = "ExternalTool",
        [ConsoleSessionChoice.AsConfigured] = "ConsoleSessionAsConfigured",
        [ConsoleSessionChoice.Console] = "ConnectToConsoleSession",
        [ConsoleSessionChoice.NoConsole] = "DontConnectToConsoleSession",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        var english = EnglishLabel(value);
        return value is Enum && ResourceKeys.TryGetValue(value, out var key) ? Localizer.Get(key, english) : english;
    }

    /// <summary>The English label of <paramref name="value"/>.</summary>
    public static string EnglishLabel(object value)
    {
        if (Labels.TryGetValue(value, out var label)) return label;
        if (value is Enum && !Enum.IsDefined(value.GetType(), value)) return Localizer.Get("ValueNotSet");
        if (value is RDPResolutions res && res.ToString().StartsWith("Res", StringComparison.Ordinal))
            return res.ToString()[3..];
        if (value is RdpVersion version) return "RDC " + version.ToString()[3..];
        if (value is VncCompression compression) return Localizer.Format("CompressionLevelFormat", compression.ToString()[4..]);
        if (value is not Enum) return value.ToString() ?? string.Empty;

        // Split PascalCase, keeping acronyms together: "FitToWindow" → "Fit To Window", "SSH2" → "SSH2".
        var text = value.ToString()!;
        var builder = new System.Text.StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && char.IsLower(text[i - 1]))
                builder.Append(' ');
            builder.Append(text[i]);
        }
        return builder.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
