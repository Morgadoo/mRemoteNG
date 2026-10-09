using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Data.Converters;
using Avalonia.Platform;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// One editable connection property with an optional "inherit from folder" checkbox.
/// While <see cref="Inherit"/> is on, <see cref="Value"/> shows the parent folder's value read-only;
/// the node's own value is kept and restored when inheritance is switched off.
/// </summary>
public sealed class InheritableField<T> : ReactiveObject
{
    private readonly T _parentValue;
    private readonly T _originalValue;
    private readonly bool _originalInherit;
    private T _own;
    private bool _inherit;

    internal InheritableField(string propertyName, ConnectionInfo target, ContainerInfo? parent, bool canInherit)
    {
        PropertyName = propertyName;
        CanInherit = canInherit && ConnectionInheritanceAccessor.SupportsInheritance(propertyName);
        _own = ConnectionInheritanceAccessor.GetOwnValue<T>(target, propertyName);
        _inherit = CanInherit && ConnectionInheritanceAccessor.GetInheritFlag(target, propertyName);
        _parentValue = CanInherit && parent is not null
            ? ConnectionInheritanceAccessor.GetValue<T>(parent, propertyName)
            : _own;
        _originalValue = _own;
        _originalInherit = _inherit;
    }

    public string PropertyName { get; }

    /// <summary>True when the node sits in a folder it can inherit this property from.</summary>
    public bool CanInherit { get; }

    public bool Inherit
    {
        get => _inherit;
        set
        {
            if (!CanInherit || _inherit == value) return;
            this.RaiseAndSetIfChanged(ref _inherit, value);
            this.RaisePropertyChanged(nameof(Value));
            this.RaisePropertyChanged(nameof(IsEditable));
        }
    }

    public bool IsEditable => !_inherit;

    /// <summary>The effective value: the folder's while inheriting, otherwise the node's own.</summary>
    public T Value
    {
        get => _inherit ? _parentValue : _own;
        set
        {
            if (_inherit) return;
            this.RaiseAndSetIfChanged(ref _own, value);
        }
    }

    /// <summary>Writes the value and inheritance flag to <paramref name="target"/>; true when anything changed.</summary>
    internal bool ApplyTo(ConnectionInfo target)
    {
        var changed = !EqualityComparer<T>.Default.Equals(_own, _originalValue) || _inherit != _originalInherit;
        ConnectionInheritanceAccessor.SetOwnValue(target, PropertyName, _own);
        // Flags of nodes that cannot inherit (directly under the root) are inactive; keep them as loaded.
        if (CanInherit)
            ConnectionInheritanceAccessor.SetInheritFlag(target, PropertyName, _inherit);
        return changed;
    }
}

/// <summary>
/// Property editor for a connection or folder. Edits are buffered in the dialog's fields and written
/// to the Core <see cref="ConnectionInfo"/> only by <see cref="Apply"/> (OK); Cancel discards them.
/// </summary>
public sealed class ConnectionDialogViewModel : ReactiveObject
{
    private readonly ConnectionInfo _target;
    private string _name;
    private string _hostname;
    private string _testStatus = string.Empty;
    private CoreProtocolType _lastProtocol;

    /// <param name="target">The node to edit (for a new node: a detached, not-yet-added instance).</param>
    /// <param name="parent">The folder the node is (or will be) in; decides what can be inherited.</param>
    public ConnectionDialogViewModel(ConnectionInfo target, ContainerInfo? parent, bool isNew)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        IsNew = isNew;
        IsFolder = target is ContainerInfo;
        IsRoot = target is RootNodeInfo;
        CanInherit = !IsRoot && ConnectionInheritanceAccessor.CanInheritFrom(parent);
        ParentName = parent?.Name ?? string.Empty;

        _name = target.Name;
        _hostname = target.Hostname;

        InheritableField<TValue> Field<TValue>(string name) => new(name, target, parent, CanInherit);

        Description = Field<string>(nameof(ConnectionInfo.Description));
        Icon = Field<string>(nameof(ConnectionInfo.Icon));
        Panel = Field<string>(nameof(ConnectionInfo.Panel));
        Protocol = Field<CoreProtocolType>(nameof(ConnectionInfo.Protocol));
        Port = Field<int>(nameof(ConnectionInfo.Port));
        Username = Field<string>(nameof(ConnectionInfo.Username));
        Password = Field<string>(nameof(ConnectionInfo.Password));
        Domain = Field<string>(nameof(ConnectionInfo.Domain));

        Resolution = Field<RDPResolutions>(nameof(ConnectionInfo.Resolution));
        Colors = Field<RDPColors>(nameof(ConnectionInfo.Colors));
        UseCredSsp = Field<bool>(nameof(ConnectionInfo.UseCredSsp));
        UseConsoleSession = Field<bool>(nameof(ConnectionInfo.UseConsoleSession));
        RedirectClipboard = Field<bool>(nameof(ConnectionInfo.RedirectClipboard));
        RedirectDiskDrives = Field<RDPDiskDrives>(nameof(ConnectionInfo.RedirectDiskDrives));
        RedirectSound = Field<RDPSounds>(nameof(ConnectionInfo.RedirectSound));
        RedirectAudioCapture = Field<bool>(nameof(ConnectionInfo.RedirectAudioCapture));
        RDGatewayUsageMethod = Field<RDGatewayUsageMethod>(nameof(ConnectionInfo.RDGatewayUsageMethod));
        RDGatewayHostname = Field<string>(nameof(ConnectionInfo.RDGatewayHostname));
        RDGatewayUseConnectionCredentials = Field<RDGatewayUseConnectionCredentials>(nameof(ConnectionInfo.RDGatewayUseConnectionCredentials));
        RDGatewayUsername = Field<string>(nameof(ConnectionInfo.RDGatewayUsername));
        RDGatewayPassword = Field<string>(nameof(ConnectionInfo.RDGatewayPassword));
        RDGatewayDomain = Field<string>(nameof(ConnectionInfo.RDGatewayDomain));

        OpeningCommand = Field<string>(nameof(ConnectionInfo.OpeningCommand));

        VNCViewOnly = Field<bool>(nameof(ConnectionInfo.VNCViewOnly));
        VNCSmartSizeMode = Field<VncSmartSizeMode>(nameof(ConnectionInfo.VNCSmartSizeMode));

        _lastProtocol = Protocol.Value;

        // Protocol drives which sections show and (unless customised) the port.
        Protocol.WhenAnyValue(p => p.Value).Subscribe(OnProtocolChanged);
        RDGatewayUsageMethod.WhenAnyValue(f => f.Value).Subscribe(_ => this.RaisePropertyChanged(nameof(IsGatewayEnabled)));
        RDGatewayUseConnectionCredentials.WhenAnyValue(f => f.Value).Subscribe(_ => this.RaisePropertyChanged(nameof(ShowGatewayCredentials)));

        var validation = this.WhenAnyValue(
                x => x.Name, x => x.Hostname, x => x.Protocol.Value, x => x.Port.Value,
                (_, _, _, _) => Unit.Default)
            .Do(_ => RaiseValidation());
        var canSave = validation.Select(_ => IsValid).ObserveOn(RxApp.MainThreadScheduler);

        OkCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(true), canSave);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(false));
        TestCommand = ReactiveCommand.CreateFromTask(TestAsync,
            this.WhenAnyValue(x => x.HostnameError, x => x.Hostname,
                    (error, host) => !IsFolder && error is null && !string.IsNullOrWhiteSpace(host))
                .ObserveOn(RxApp.MainThreadScheduler));
    }

    public bool IsNew { get; }
    public bool IsFolder { get; }
    public bool IsRoot { get; }
    public bool IsConnection => !IsFolder;

    /// <summary>True when the node is inside a folder (not directly under the root), so values can be inherited.</summary>
    public bool CanInherit { get; }

    public string ParentName { get; }

    public string InheritHint => CanInherit
        ? $"Checked \"Inherit\" boxes take the value from the folder \"{ParentName}\"."
        : string.Empty;

    public string WindowTitle => (IsNew, IsFolder) switch
    {
        (true, true) => "New Folder",
        (true, false) => "New Connection",
        (false, true) => $"Edit Folder — {_target.Name}",
        _ => $"Edit Connection — {_target.Name}",
    };

    // ── General ───────────────────────────────────────────────────────────

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string Hostname
    {
        get => _hostname;
        set => this.RaiseAndSetIfChanged(ref _hostname, value);
    }

    public InheritableField<string> Description { get; }
    public InheritableField<string> Icon { get; }
    public InheritableField<string> Panel { get; }
    public InheritableField<CoreProtocolType> Protocol { get; }
    public InheritableField<int> Port { get; }

    // ── Credentials ───────────────────────────────────────────────────────

    public InheritableField<string> Username { get; }
    public InheritableField<string> Password { get; }
    public InheritableField<string> Domain { get; }

    // ── RDP ───────────────────────────────────────────────────────────────

    public InheritableField<RDPResolutions> Resolution { get; }
    public InheritableField<RDPColors> Colors { get; }
    public InheritableField<bool> UseCredSsp { get; }
    public InheritableField<bool> UseConsoleSession { get; }
    public InheritableField<bool> RedirectClipboard { get; }
    public InheritableField<RDPDiskDrives> RedirectDiskDrives { get; }
    public InheritableField<RDPSounds> RedirectSound { get; }
    public InheritableField<bool> RedirectAudioCapture { get; }
    public InheritableField<RDGatewayUsageMethod> RDGatewayUsageMethod { get; }
    public InheritableField<string> RDGatewayHostname { get; }
    public InheritableField<RDGatewayUseConnectionCredentials> RDGatewayUseConnectionCredentials { get; }
    public InheritableField<string> RDGatewayUsername { get; }
    public InheritableField<string> RDGatewayPassword { get; }
    public InheritableField<string> RDGatewayDomain { get; }

    public bool IsGatewayEnabled => RDGatewayUsageMethod.Value != Core.Connection.Protocol.RDP.RDGatewayUsageMethod.Never;

    public bool ShowGatewayCredentials => IsGatewayEnabled
        && RDGatewayUseConnectionCredentials.Value == Core.Connection.Protocol.RDP.RDGatewayUseConnectionCredentials.No;

    // ── SSH / VNC ─────────────────────────────────────────────────────────

    public InheritableField<string> OpeningCommand { get; }
    public InheritableField<bool> VNCViewOnly { get; }
    public InheritableField<VncSmartSizeMode> VNCSmartSizeMode { get; }

    // ── Option lists ──────────────────────────────────────────────────────

    public CoreProtocolType[] Protocols { get; } = Enum.GetValues<CoreProtocolType>();
    public RDPResolutions[] Resolutions { get; } = Enum.GetValues<RDPResolutions>();
    public RDPColors[] ColorDepths { get; } = Enum.GetValues<RDPColors>();
    public RDPDiskDrives[] DiskDriveOptions { get; } = Enum.GetValues<RDPDiskDrives>();
    public RDPSounds[] SoundOptions { get; } = Enum.GetValues<RDPSounds>();
    public RDGatewayUsageMethod[] GatewayUsageOptions { get; } = Enum.GetValues<RDGatewayUsageMethod>();
    public RDGatewayUseConnectionCredentials[] GatewayCredentialOptions { get; } = Enum.GetValues<RDGatewayUseConnectionCredentials>();
    public VncSmartSizeMode[] SmartSizeOptions { get; } = Enum.GetValues<VncSmartSizeMode>();
    public string[] IconNames { get; } = LoadIconNames();

    // ── Protocol sections ─────────────────────────────────────────────────

    public bool IsRdp => IsConnection && Protocol.Value == CoreProtocolType.RDP;
    public bool IsSsh => IsConnection && Protocol.Value is CoreProtocolType.SSH1 or CoreProtocolType.SSH2;
    public bool IsVnc => IsConnection && Protocol.Value is CoreProtocolType.VNC or CoreProtocolType.ARD;
    public bool IsHttp => IsConnection && Protocol.Value is CoreProtocolType.HTTP or CoreProtocolType.HTTPS;
    public bool UsesPort => IsConnection && ConnectionDefaults.UsesPort(Protocol.Value);

    /// <summary>Explains how the chosen protocol behaves on this platform (empty when nothing to add).</summary>
    public string ProtocolNote => !IsConnection ? string.Empty : Protocol.Value switch
    {
        CoreProtocolType.HTTP or CoreProtocolType.HTTPS =>
            "Web pages open in your default browser. Rendering engine settings do not apply.",
        CoreProtocolType.ARD => "Apple Remote Desktop connects with the built-in VNC client.",
        CoreProtocolType.AnyDesk => "Launches the installed AnyDesk application; put the AnyDesk ID in Hostname.",
        CoreProtocolType.Terminal => "Opens a local shell; with a hostname it runs ssh to that host.",
        CoreProtocolType.WSL => "Opens a WSL shell (Windows only); Hostname selects the distribution.",
        CoreProtocolType.RAW => "Plain TCP socket with local line editing.",
        CoreProtocolType.SSH1 => "SSH1 is obsolete; the connection uses SSH2.",
        _ when ConnectionParametersFactory.MapProtocol(Protocol.Value) is null =>
            $"{Protocol.Value} is not supported on this platform yet (needs the legacy External Tools feature).",
        _ => string.Empty,
    };

    // ── Validation ────────────────────────────────────────────────────────

    public string? NameError => string.IsNullOrWhiteSpace(Name) ? "Name is required." : null;

    public string? HostnameError => IsConnection ? ConnectionDefaults.ValidateHostname(Protocol.Value, Hostname) : null;

    public string? PortError => IsConnection ? ConnectionDefaults.ValidatePort(Protocol.Value, Port.Value) : null;

    public bool IsValid => NameError is null && HostnameError is null && PortError is null;

    public string TestStatus
    {
        get => _testStatus;
        set => this.RaiseAndSetIfChanged(ref _testStatus, value);
    }

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestCommand { get; }

    /// <summary>Raised when the dialog should close: true for OK, false for Cancel.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Writes all edits to the target node. Returns true when anything changed.</summary>
    public bool Apply()
    {
        if (!IsValid)
            throw new InvalidOperationException("The connection settings are not valid.");

        var changed = false;
        if (_target.Name != Name.Trim())
        {
            _target.Name = Name.Trim();
            changed = true;
        }

        IEnumerable<object> fields = IsFolder
            ? [Description, Icon, Panel, Username, Password, Domain]
            :
            [
                Description, Icon, Panel, Protocol, Port, Username, Password, Domain,
                Resolution, Colors, UseCredSsp, UseConsoleSession, RedirectClipboard, RedirectDiskDrives,
                RedirectSound, RedirectAudioCapture, RDGatewayUsageMethod, RDGatewayHostname,
                RDGatewayUseConnectionCredentials, RDGatewayUsername, RDGatewayPassword, RDGatewayDomain,
                OpeningCommand, VNCViewOnly, VNCSmartSizeMode,
            ];

        if (!IsFolder && _target.Hostname != Hostname.Trim())
        {
            _target.Hostname = Hostname.Trim();
            changed = true;
        }

        foreach (var field in fields)
        {
            changed |= field switch
            {
                InheritableField<string> f => f.ApplyTo(_target),
                InheritableField<int> f => f.ApplyTo(_target),
                InheritableField<bool> f => f.ApplyTo(_target),
                InheritableField<CoreProtocolType> f => f.ApplyTo(_target),
                InheritableField<RDPResolutions> f => f.ApplyTo(_target),
                InheritableField<RDPColors> f => f.ApplyTo(_target),
                InheritableField<RDPDiskDrives> f => f.ApplyTo(_target),
                InheritableField<RDPSounds> f => f.ApplyTo(_target),
                InheritableField<RDGatewayUsageMethod> f => f.ApplyTo(_target),
                InheritableField<RDGatewayUseConnectionCredentials> f => f.ApplyTo(_target),
                InheritableField<VncSmartSizeMode> f => f.ApplyTo(_target),
                _ => throw new InvalidOperationException($"Unhandled field type {field.GetType()}"),
            };
        }

        return changed;
    }

    private void OnProtocolChanged(CoreProtocolType protocol)
    {
        if (!Port.Inherit)
            Port.Value = ConnectionDefaults.PortAfterProtocolChange(_lastProtocol, protocol, Port.Value);
        _lastProtocol = protocol;

        foreach (var name in new[] { nameof(IsRdp), nameof(IsSsh), nameof(IsVnc), nameof(IsHttp), nameof(UsesPort), nameof(ProtocolNote) })
            this.RaisePropertyChanged(name);
    }

    private void RaiseValidation()
    {
        this.RaisePropertyChanged(nameof(NameError));
        this.RaisePropertyChanged(nameof(HostnameError));
        this.RaisePropertyChanged(nameof(PortError));
        this.RaisePropertyChanged(nameof(IsValid));
    }

    private async Task TestAsync()
    {
        var host = Hostname.Trim();
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && host.Contains("://"))
            host = uri.Host;
        var port = Port.Value > 0 ? Port.Value : ConnectionInfo.GetDefaultPort(Protocol.Value);
        if (port <= 0)
        {
            TestStatus = $"{Protocol.Value} does not use a network port; nothing to test.";
            return;
        }

        TestStatus = $"Testing {host}:{port}...";
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
            TestStatus = $"Success — {host}:{port} is reachable";
        }
        catch (OperationCanceledException)
        {
            TestStatus = $"Timed out — {host}:{port} did not respond within 5s";
        }
        catch (Exception ex)
        {
            TestStatus = $"Failed — {ex.Message}";
        }
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

/// <summary>Turns enum values into readable labels (e.g. Colors16Bit → "16-bit", FitToWindow → "Fit To Window").</summary>
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
        [VncSmartSizeMode.SmartSNo] = "No smart size",
        [VncSmartSizeMode.SmartSFree] = "Free",
        [VncSmartSizeMode.SmartSAspect] = "Keep aspect ratio",
        [Core.Connection.Protocol.RDP.RDGatewayUseConnectionCredentials.No] = "Use separate gateway credentials",
        [Core.Connection.Protocol.RDP.RDGatewayUseConnectionCredentials.Yes] = "Use connection credentials",
        [Core.Connection.Protocol.RDP.RDGatewayUseConnectionCredentials.SmartCard] = "Smart card",
        [Core.Connection.Protocol.RDP.RDGatewayUseConnectionCredentials.ExternalCredentialProvider] = "External credential provider",
        [Core.Connection.Protocol.RDP.RDGatewayUseConnectionCredentials.AccessToken] = "Access token",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        if (Labels.TryGetValue(value, out var label)) return label;
        if (value is RDPResolutions res && res.ToString().StartsWith("Res", StringComparison.Ordinal))
            return res.ToString()[3..];
        if (value is not Enum) return value.ToString();

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
