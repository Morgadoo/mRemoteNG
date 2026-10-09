using System.Globalization;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Protocols.Abstractions;

/// <summary>
/// Builds <see cref="ConnectionParameters"/> from a connection-tree node, resolving inherited values.
/// </summary>
public static class ConnectionParametersFactory
{
    /// <summary>Extras keys understood by the protocol implementations.</summary>
    public static class Keys
    {
        /// <summary>Connect/handshake timeout in seconds (global setting).</summary>
        public const string ConnectTimeoutSeconds = "connect.timeoutSeconds";
        /// <summary>SSH keep-alive interval in seconds; 0 disables it (global setting).</summary>
        public const string SshKeepAliveSeconds = "ssh.keepAliveSeconds";
        public const string RdpColorDepth = "rdp.colorDepth";
        public const string RdpGateway = "rdp.gateway";
        public const string RdpGatewayUsername = "rdp.gatewayUsername";
        public const string RdpGatewayDomain = "rdp.gatewayDomain";
        public const string RdpNla = "rdp.nla";
        public const string RdpConsole = "rdp.console";
        public const string RdpClipboard = "rdp.clipboard";
        public const string RdpHomeDrive = "rdp.homeDrive";
        public const string RdpSound = "rdp.sound";
        public const string RdpMicrophone = "rdp.microphone";
        public const string RdpLoadBalanceInfo = "rdp.loadBalanceInfo";
        public const string OpeningCommand = "shell.openingCommand";
        /// <summary>VNC scaling: "none", "fit" (keep aspect ratio) or "stretch".</summary>
        public const string VncScaling = "vnc.scaling";
        public const string VncViewOnly = "vnc.viewOnly";
        /// <summary>Command template for <see cref="ProtocolType.ExternalApp"/> (see ExternalAppProtocol).</summary>
        public const string ExternalCommand = "external.command";
        /// <summary>Local shell flavour for <see cref="ProtocolType.LocalShell"/>: "terminal" or "wsl".</summary>
        public const string LocalShellMode = "shell.mode";
        /// <summary>Display name of the external tool an <see cref="ProtocolType.IntApp"/> session runs (ConnectionInfo.ExtApp).</summary>
        public const string IntAppTool = "intapp.tool";
        /// <summary>Connection values for the external tool variables (%NAME%, %DESCRIPTION%, …) of an IntApp session.</summary>
        public const string ToolName = "tool.name";
        public const string ToolDescription = "tool.description";
        public const string ToolMacAddress = "tool.macAddress";
        public const string ToolUserField = "tool.userField";
        public const string ToolProtocol = "tool.protocol";
    }

    /// <summary>Command used for AnyDesk connections; the hostname holds the AnyDesk ID or alias.</summary>
    public const string AnyDeskCommand = "anydesk {hostname}";

    /// <summary>
    /// Maps a legacy/Core protocol to a cross-platform protocol implementation,
    /// or null when no cross-platform implementation exists yet.
    /// </summary>
    public static ProtocolType? MapProtocol(CoreProtocol protocol) => protocol switch
    {
        CoreProtocol.RDP => ProtocolType.Rdp,
        CoreProtocol.VNC => ProtocolType.Vnc,
        // SSH1 is obsolete and unsupported by SSH.NET; servers still offering it also speak SSH2.
        CoreProtocol.SSH1 or CoreProtocol.SSH2 => ProtocolType.Ssh,
        CoreProtocol.Telnet => ProtocolType.Telnet,
        CoreProtocol.Rlogin => ProtocolType.Rlogin,
        CoreProtocol.HTTP => ProtocolType.Http,
        CoreProtocol.HTTPS => ProtocolType.Https,
        CoreProtocol.PowerShell => ProtocolType.PowerShell,
        CoreProtocol.RAW => ProtocolType.Raw,
        // Apple Remote Desktop speaks RFB, so the VNC client handles it (default port 5900).
        CoreProtocol.ARD => ProtocolType.Vnc,
        // AnyDesk has no embeddable client: launch the installed AnyDesk app with the ID.
        CoreProtocol.AnyDesk => ProtocolType.ExternalApp,
        CoreProtocol.Terminal or CoreProtocol.WSL => ProtocolType.LocalShell,
        // An external tool whose window is embedded in the session tab (see IntegratedProgramProtocol).
        CoreProtocol.IntApp => ProtocolType.IntApp,
        _ => null,
    };

    /// <exception cref="NotSupportedException">The node's protocol has no cross-platform implementation.</exception>
    public static ConnectionParameters FromConnectionInfo(ConnectionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var protocol = MapProtocol(info.Protocol)
            ?? throw new NotSupportedException($"The {info.Protocol} protocol is not supported on this platform yet.");

        var extras = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(info.OpeningCommand))
            extras[Keys.OpeningCommand] = info.OpeningCommand;
        if (protocol == ProtocolType.Rdp)
            AddRdpExtras(info, extras);
        else if (protocol == ProtocolType.Vnc)
            AddVncExtras(info, extras);
        if (info.Protocol == CoreProtocol.AnyDesk)
            extras[Keys.ExternalCommand] = AnyDeskCommand;
        if (protocol == ProtocolType.LocalShell)
            extras[Keys.LocalShellMode] = info.Protocol == CoreProtocol.WSL ? "wsl" : "terminal";
        if (protocol == ProtocolType.IntApp)
            AddIntAppExtras(info, extras);

        return new ConnectionParameters
        {
            Hostname = info.Hostname,
            Port = info.Port > 0 ? info.Port : ConnectionInfo.GetDefaultPort(info.Protocol),
            Protocol = protocol,
            Username = NullIfEmpty(info.Username),
            Password = NullIfEmpty(info.Password),
            Domain = NullIfEmpty(info.Domain),
            Extras = extras,
        };
    }

    private static void AddRdpExtras(ConnectionInfo info, Dictionary<string, string> extras)
    {
        extras[Keys.RdpColorDepth] = ((int)info.Colors).ToString(CultureInfo.InvariantCulture);
        extras[Keys.RdpNla] = Bool(info.UseCredSsp);
        extras[Keys.RdpConsole] = Bool(info.UseConsoleSession);
        extras[Keys.RdpClipboard] = Bool(info.RedirectClipboard);
        extras[Keys.RdpHomeDrive] = Bool(info.RedirectDiskDrives != RDPDiskDrives.None);
        extras[Keys.RdpMicrophone] = Bool(info.RedirectAudioCapture);
        extras[Keys.RdpSound] = info.RedirectSound switch
        {
            RDPSounds.BringToThisComputer => "local",
            RDPSounds.LeaveAtRemoteComputer => "remote",
            _ => "off",
        };

        if (!string.IsNullOrEmpty(info.LoadBalanceInfo))
            extras[Keys.RdpLoadBalanceInfo] = info.LoadBalanceInfo;

        if (info.RDGatewayUsageMethod != RDGatewayUsageMethod.Never && !string.IsNullOrEmpty(info.RDGatewayHostname))
        {
            extras[Keys.RdpGateway] = info.RDGatewayHostname;
            if (info.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.No)
            {
                if (!string.IsNullOrEmpty(info.RDGatewayUsername))
                    extras[Keys.RdpGatewayUsername] = info.RDGatewayUsername;
                if (!string.IsNullOrEmpty(info.RDGatewayDomain))
                    extras[Keys.RdpGatewayDomain] = info.RDGatewayDomain;
            }
        }
    }

    private static void AddIntAppExtras(ConnectionInfo info, Dictionary<string, string> extras)
    {
        extras[Keys.IntAppTool] = info.ExtApp ?? string.Empty;
        extras[Keys.ToolName] = info.Name ?? string.Empty;
        extras[Keys.ToolDescription] = info.Description ?? string.Empty;
        extras[Keys.ToolMacAddress] = info.MacAddress ?? string.Empty;
        extras[Keys.ToolUserField] = info.UserField ?? string.Empty;
        extras[Keys.ToolProtocol] = info.Protocol.ToString();
    }

    private static void AddVncExtras(ConnectionInfo info, Dictionary<string, string> extras)
    {
        extras[Keys.VncScaling] = info.VNCSmartSizeMode switch
        {
            VncSmartSizeMode.SmartSNo => "none",
            VncSmartSizeMode.SmartSFree => "stretch",
            _ => "fit",
        };
        extras[Keys.VncViewOnly] = Bool(info.VNCViewOnly);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
