using Avalonia.Media;
using Material.Icons;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using ProtocolKind = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Avalonia.Services;

/// <summary>The protocol groups that share an icon colour (docs/design-system.md §2 and §5).</summary>
public enum ProtocolFamily
{
    Unknown,
    Ssh,
    Telnet,
    Rdp,
    Vnc,
    Http,
    PowerShell,
    Terminal,
    Serial,
    IntApp,
    AnyDesk,
}

/// <summary>
/// Icons, colours and short labels for protocols and connections (Material Design Icons; docs/design-system.md §5).
/// Accepts the Core <see cref="CoreProtocol"/>, the Protocols <see cref="ProtocolKind"/> or a protocol name.
/// The brushes are stable instances whose colours follow the active palette's <c>Proto…</c> keys
/// (<see cref="ThemeService"/> calls <see cref="SyncColors"/>), so bindings that use them stay correct when the
/// theme changes. XAML uses them through <c>Converters.ProtocolConverters</c> and <c>Converters.ConnectionConverters</c>.
/// </summary>
public static class ProtocolVisuals
{
    private static readonly Dictionary<ProtocolFamily, SolidColorBrush> Brushes = CreateBrushes(1.0);
    private static readonly Dictionary<ProtocolFamily, SolidColorBrush> TintBrushes = CreateBrushes(0.18);

    /// <summary>
    /// Legacy connection icon names (the WinForms app's Icons folder, <c>ConnectionInfo.Icon</c>) → Material glyph.
    /// "mRemoteNG" (the default) and unknown names have no entry: the protocol glyph is used.
    /// </summary>
    private static readonly Dictionary<string, MaterialIconKind> LegacyIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Admin"] = MaterialIconKind.ShieldAccountOutline,
        ["Anti Virus"] = MaterialIconKind.ShieldBugOutline,
        ["Apple"] = MaterialIconKind.Apple,
        ["Backup"] = MaterialIconKind.BackupRestore,
        ["Build Server"] = MaterialIconKind.HammerWrench,
        ["Console"] = MaterialIconKind.Console,
        ["Database"] = MaterialIconKind.Database,
        ["Domain Controller"] = MaterialIconKind.AccountNetworkOutline,
        ["ESX"] = MaterialIconKind.ServerNetwork,
        ["Fax"] = MaterialIconKind.Fax,
        ["File Server"] = MaterialIconKind.FolderNetworkOutline,
        ["Finance"] = MaterialIconKind.Finance,
        ["Firewall"] = MaterialIconKind.WallFire,
        ["Infrastructure"] = MaterialIconKind.Lan,
        ["Linux"] = MaterialIconKind.Linux,
        ["Log"] = MaterialIconKind.TextBoxOutline,
        ["Mail Server"] = MaterialIconKind.EmailOutline,
        ["PowerShell"] = MaterialIconKind.Powershell,
        ["Production"] = MaterialIconKind.Factory,
        ["PuTTY"] = MaterialIconKind.ConsoleNetwork,
        ["RaspberryPi"] = MaterialIconKind.RaspberryPi,
        ["Remote Desktop"] = MaterialIconKind.RemoteDesktop,
        ["Router"] = MaterialIconKind.RouterNetwork,
        ["SSH"] = MaterialIconKind.Console,
        ["SharePoint"] = MaterialIconKind.MicrosoftSharepoint,
        ["Staging"] = MaterialIconKind.Flask,
        ["Switch"] = MaterialIconKind.Switch,
        ["Tel"] = MaterialIconKind.Phone,
        ["Telnet"] = MaterialIconKind.ConsoleNetwork,
        ["Terminal Server"] = MaterialIconKind.ServerOutline,
        ["Test Server"] = MaterialIconKind.TestTube,
        ["Virtual Machine"] = MaterialIconKind.Monitor,
        ["WSL"] = MaterialIconKind.Linux,
        ["Web Server"] = MaterialIconKind.Web,
        ["WiFi"] = MaterialIconKind.Wifi,
        ["Windows"] = MaterialIconKind.MicrosoftWindows,
        ["Workstation"] = MaterialIconKind.DesktopTowerMonitor,
    };

    /// <summary>The palette colour key of a protocol family ("ProtoSsh"…); null for <see cref="ProtocolFamily.Unknown"/>.</summary>
    public static string? TokenFor(ProtocolFamily family) => family == ProtocolFamily.Unknown ? null : "Proto" + family;

    /// <summary>The family of a Core/Protocols protocol type or a protocol name ("SSH2", "Rdp", "ssh"…).</summary>
    public static ProtocolFamily FamilyOf(object? protocol) => protocol switch
    {
        CoreProtocol core => core switch
        {
            CoreProtocol.SSH1 or CoreProtocol.SSH2 => ProtocolFamily.Ssh,
            CoreProtocol.Telnet or CoreProtocol.Rlogin or CoreProtocol.RAW => ProtocolFamily.Telnet,
            CoreProtocol.RDP => ProtocolFamily.Rdp,
            CoreProtocol.VNC or CoreProtocol.ARD => ProtocolFamily.Vnc,
            CoreProtocol.HTTP or CoreProtocol.HTTPS => ProtocolFamily.Http,
            CoreProtocol.PowerShell => ProtocolFamily.PowerShell,
            CoreProtocol.Terminal or CoreProtocol.WSL => ProtocolFamily.Terminal,
            CoreProtocol.AnyDesk => ProtocolFamily.AnyDesk,
            CoreProtocol.IntApp => ProtocolFamily.IntApp,
            _ => ProtocolFamily.Unknown,
        },
        ProtocolKind kind => kind switch
        {
            ProtocolKind.Ssh or ProtocolKind.SshSftp => ProtocolFamily.Ssh,
            ProtocolKind.Telnet or ProtocolKind.Rlogin or ProtocolKind.Raw => ProtocolFamily.Telnet,
            ProtocolKind.Rdp => ProtocolFamily.Rdp,
            ProtocolKind.Vnc => ProtocolFamily.Vnc,
            ProtocolKind.Http or ProtocolKind.Https => ProtocolFamily.Http,
            ProtocolKind.PowerShell => ProtocolFamily.PowerShell,
            ProtocolKind.LocalShell => ProtocolFamily.Terminal,
            ProtocolKind.Serial => ProtocolFamily.Serial,
            ProtocolKind.ExternalApp or ProtocolKind.IntApp => ProtocolFamily.IntApp,
            _ => ProtocolFamily.Unknown,
        },
        ProtocolFamily family => family,
        string name => FamilyOf(Parse(name)),
        _ => ProtocolFamily.Unknown,
    };

    /// <summary>Material glyph for a protocol (docs/design-system.md §5).</summary>
    public static MaterialIconKind IconFor(object? protocol) => Parse(protocol) switch
    {
        CoreProtocol.SSH1 or CoreProtocol.SSH2 or ProtocolKind.Ssh => MaterialIconKind.Console,
        ProtocolKind.SshSftp => MaterialIconKind.FolderNetworkOutline,
        CoreProtocol.Telnet or ProtocolKind.Telnet => MaterialIconKind.ConsoleNetwork,
        CoreProtocol.Rlogin or ProtocolKind.Rlogin => MaterialIconKind.ConsoleLine,
        CoreProtocol.RAW or ProtocolKind.Raw => MaterialIconKind.LanConnect,
        CoreProtocol.RDP or ProtocolKind.Rdp => MaterialIconKind.MonitorScreenshot,
        CoreProtocol.VNC or CoreProtocol.ARD or ProtocolKind.Vnc => MaterialIconKind.MonitorEye,
        CoreProtocol.HTTP or CoreProtocol.HTTPS or ProtocolKind.Http or ProtocolKind.Https => MaterialIconKind.Web,
        CoreProtocol.PowerShell or ProtocolKind.PowerShell => MaterialIconKind.Powershell,
        CoreProtocol.Terminal or ProtocolKind.LocalShell => MaterialIconKind.Console,
        CoreProtocol.WSL => MaterialIconKind.Linux,
        ProtocolKind.Serial => MaterialIconKind.SerialPort,
        CoreProtocol.IntApp or ProtocolKind.IntApp or ProtocolKind.ExternalApp => MaterialIconKind.Application,
        CoreProtocol.AnyDesk => MaterialIconKind.Monitor,
        _ => MaterialIconKind.LanConnect,
    };

    /// <summary>Short chip label: "SSH", "RDP", "VNC", "HTTPS"…</summary>
    public static string LabelFor(object? protocol) => Parse(protocol) switch
    {
        CoreProtocol.SSH2 or ProtocolKind.Ssh => "SSH",
        CoreProtocol.SSH1 => "SSH1",
        ProtocolKind.SshSftp => "SFTP",
        CoreProtocol.RAW or ProtocolKind.Raw => "Raw",
        CoreProtocol.HTTP or ProtocolKind.Http => "HTTP",
        CoreProtocol.HTTPS or ProtocolKind.Https => "HTTPS",
        CoreProtocol.PowerShell or ProtocolKind.PowerShell => "PS",
        CoreProtocol.IntApp or ProtocolKind.IntApp or ProtocolKind.ExternalApp => "App",
        ProtocolKind.LocalShell => "Shell",
        ProtocolKind.Rdp => "RDP",
        ProtocolKind.Vnc => "VNC",
        CoreProtocol core => core.ToString(),
        ProtocolKind kind => kind.ToString(),
        _ => protocol as string ?? string.Empty,
    };

    /// <summary>The protocol colour (icons, chip text). Stable instance that follows the palette.</summary>
    public static IBrush BrushFor(object? protocol) => Brushes[FamilyOf(protocol)];

    /// <summary>The protocol colour at 18 % (chip background). Stable instance that follows the palette.</summary>
    public static IBrush TintBrushFor(object? protocol) => TintBrushes[FamilyOf(protocol)];

    /// <summary>Material glyph for a legacy connection icon name, or null when there is none (use the protocol glyph).</summary>
    public static MaterialIconKind? IconForLegacyName(string? iconName) =>
        !string.IsNullOrWhiteSpace(iconName) && LegacyIcons.TryGetValue(iconName.Trim(), out var kind) ? kind : null;

    /// <summary>
    /// Glyph for a tree node / connection: the PuTTY sessions root, the connection root, folders (open when
    /// <paramref name="expanded"/>), otherwise the legacy icon's glyph when it has one, else the protocol glyph.
    /// Accepts a Core <see cref="ConnectionInfo"/>, a <see cref="ConnectionNodeViewModel"/> (uses its expansion
    /// when <paramref name="expanded"/> is null) or a <see cref="SessionTabViewModel"/>.
    /// </summary>
    public static MaterialIconKind IconForConnection(object? connection, bool? expanded = null)
    {
        switch (connection)
        {
            case ConnectionNodeViewModel node:
                return IconForConnection(node.Model, expanded ?? node.IsExpanded);
            case SessionTabViewModel tab:
                return tab.Connection is { } tabConnection ? IconForConnection(tabConnection, false) : IconFor(tab.Parameters.Protocol);
            case RootPuttySessionsNodeInfo:
                return MaterialIconKind.ConsoleNetworkOutline;
            case RootNodeInfo:
                return MaterialIconKind.Database;
            case ContainerInfo container:
                return expanded ?? container.IsExpanded ? MaterialIconKind.FolderOpen : MaterialIconKind.Folder;
            case ConnectionInfo info:
                return IconForLegacyName(info.Icon) ?? IconFor(info.Protocol);
            default:
                return IconFor(connection);
        }
    }

    /// <summary>Protocol colour for a connection; null for folders and roots (their icon uses the text colour).</summary>
    public static IBrush? BrushForConnection(object? connection) => connection switch
    {
        ConnectionNodeViewModel node => BrushForConnection(node.Model),
        SessionTabViewModel tab => tab.Connection is { } info ? BrushFor(info.Protocol) : BrushFor(tab.Parameters.Protocol),
        ContainerInfo => null,
        ConnectionInfo info => BrushFor(info.Protocol),
        _ => BrushFor(connection),
    };

    /// <summary>Updates the protocol brushes from a palette (<paramref name="lookup"/>: colour key → colour).</summary>
    public static void SyncColors(Func<string, Color?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        foreach (var family in Enum.GetValues<ProtocolFamily>())
        {
            var token = TokenFor(family) ?? "ProtoTelnet";
            if (lookup(token) is not { } color)
                continue;
            Brushes[family].Color = color;
            TintBrushes[family].Color = color;
        }
    }

    /// <summary>Normalises a protocol value: enums pass through, names are parsed (Core names first).</summary>
    private static object? Parse(object? protocol)
    {
        if (protocol is not string name)
            return protocol;

        name = name.Trim();
        if (name.Length == 0)
            return null;
        if (name.Equals("SSH", StringComparison.OrdinalIgnoreCase))
            return CoreProtocol.SSH2;
        if (name.Equals("SFTP", StringComparison.OrdinalIgnoreCase))
            return ProtocolKind.SshSftp;
        if (Enum.TryParse<CoreProtocol>(name, ignoreCase: true, out var core) && !int.TryParse(name, out _))
            return core;
        if (Enum.TryParse<ProtocolKind>(name, ignoreCase: true, out var kind) && !int.TryParse(name, out _))
            return kind;
        return name;
    }

    private static Dictionary<ProtocolFamily, SolidColorBrush> CreateBrushes(double opacity) =>
        Enum.GetValues<ProtocolFamily>().ToDictionary(
            f => f,
            f => new SolidColorBrush(Color.Parse(DefaultColorFor(f)), opacity));

    private static string DefaultColorFor(ProtocolFamily family) => family switch
    {
        ProtocolFamily.Ssh => "#3FB950",
        ProtocolFamily.Rdp => "#4C8DFF",
        ProtocolFamily.Vnc => "#A371F7",
        ProtocolFamily.Http => "#F0883E",
        ProtocolFamily.PowerShell => "#56B6F7",
        ProtocolFamily.Terminal => "#C9D1D9",
        ProtocolFamily.Serial => "#D29922",
        ProtocolFamily.IntApp => "#DB61A2",
        ProtocolFamily.AnyDesk => "#EF443B",
        _ => "#8B949E",
    };
}
