using System.Globalization;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Protocols.Rdp;
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
        /// <summary>RD Gateway password (set by the external credential provider step).</summary>
        public const string RdpGatewayPassword = "rdp.gatewayPassword";
        public const string RdpNla = "rdp.nla";
        public const string RdpConsole = "rdp.console";
        public const string RdpClipboard = "rdp.clipboard";
        /// <summary>RDP: redirect the user's home folder. Only read when <see cref="RdpDrives"/> is absent.</summary>
        public const string RdpHomeDrive = "rdp.homeDrive";
        public const string RdpSound = "rdp.sound";
        public const string RdpMicrophone = "rdp.microphone";
        public const string RdpLoadBalanceInfo = "rdp.loadBalanceInfo";
        /// <summary>RDP desktop size: "fit" (default), "smartsize", "fullscreen" or a fixed "WIDTHxHEIGHT".</summary>
        public const string RdpResolution = "rdp.resolution";
        /// <summary>RDP: resize the remote desktop when the tab is resized ("fit"/"fullscreen" only; default true).</summary>
        public const string RdpAutoResize = "rdp.autoResize";
        public const string RdpWallpaper = "rdp.wallpaper";
        public const string RdpThemes = "rdp.themes";
        public const string RdpFontSmoothing = "rdp.fontSmoothing";
        public const string RdpDesktopComposition = "rdp.desktopComposition";
        /// <summary>RDP: show window contents while dragging (the inverse of the legacy DisableFullWindowDrag).</summary>
        public const string RdpFullWindowDrag = "rdp.fullWindowDrag";
        /// <summary>RDP: menu and window animations (the inverse of the legacy DisableMenuAnimations).</summary>
        public const string RdpMenuAnimations = "rdp.menuAnimations";
        /// <summary>RDP: keep the bitmap cache on disk between sessions (legacy CacheBitmaps).</summary>
        public const string RdpPersistentBitmapCache = "rdp.persistentBitmapCache";
        /// <summary>RDP: send Windows key combinations (Alt+Tab, Win…) to the remote session (legacy RedirectKeys).</summary>
        public const string RdpRedirectKeys = "rdp.redirectKeys";
        public const string RdpRedirectPorts = "rdp.redirectPorts";
        public const string RdpRedirectPrinters = "rdp.redirectPrinters";
        public const string RdpRedirectSmartCards = "rdp.redirectSmartCards";
        /// <summary>RDP drive redirection: "none", "local", "all" or "custom" (see <see cref="RdpDrivesCustom"/>).</summary>
        public const string RdpDrives = "rdp.drives";
        /// <summary>Drives for "custom" redirection, separated by ',' or ';': drive letters (C,D) or paths, optionally "Name=path".</summary>
        public const string RdpDrivesCustom = "rdp.drivesCustom";
        /// <summary>RDP audio quality: "dynamic", "medium" or "high".</summary>
        public const string RdpSoundQuality = "rdp.soundQuality";
        public const string RdpRestrictedAdmin = "rdp.restrictedAdmin";
        public const string RdpRemoteCredentialGuard = "rdp.remoteCredentialGuard";
        /// <summary>RDP: disconnect after this many minutes without user input (0 = never).</summary>
        public const string RdpIdleTimeoutMinutes = "rdp.idleTimeoutMinutes";
        /// <summary>RDP: tell the user when the session was disconnected for inactivity.</summary>
        public const string RdpIdleTimeoutAlert = "rdp.idleTimeoutAlert";
        /// <summary>RDP: program started instead of the desktop (alternate shell).</summary>
        public const string RdpStartProgram = "rdp.startProgram";
        public const string RdpStartProgramWorkDir = "rdp.startProgramWorkDir";
        /// <summary>RD Gateway usage: "always" (default when a gateway is set) or "detect".</summary>
        public const string RdpGatewayUsage = "rdp.gatewayUsage";
        /// <summary>RD Gateway credentials: "connection" (default), "explicit", "smartcard" or "token".</summary>
        public const string RdpGatewayCredentials = "rdp.gatewayCredentials";
        public const string RdpGatewayAccessToken = "rdp.gatewayAccessToken";
        /// <summary>Hyper-V VM id: connect to the VM console through the Hyper-V host (port 2179).</summary>
        public const string RdpVmId = "rdp.vmId";
        public const string RdpVmEnhancedMode = "rdp.vmEnhancedMode";
        public const string OpeningCommand = "shell.openingCommand";
        /// <summary>VNC scaling: "none", "fit" (keep aspect ratio) or "stretch".</summary>
        public const string VncScaling = "vnc.scaling";
        public const string VncViewOnly = "vnc.viewOnly";
        /// <summary>Preferred VNC encoding: raw, rre, corre, hextile, zlib, tight or zrle.</summary>
        public const string VncEncoding = "vnc.encoding";
        /// <summary>VNC compression level 0-9; absent leaves it to the server.</summary>
        public const string VncCompression = "vnc.compression";
        /// <summary>VNC JPEG quality 0-9 (lossy Tight JPEG); absent keeps the session lossless.</summary>
        public const string VncJpegQuality = "vnc.jpegQuality";
        /// <summary>VNC colour depth: "full", "16" or "8".</summary>
        public const string VncColors = "vnc.colors";
        /// <summary>VNC authentication: "vnc", "windows" (UltraVNC MS-Logon) or "ard" (Apple Remote Desktop).</summary>
        public const string VncAuthMode = "vnc.authMode";
        /// <summary>VNC proxy: "none", "http", "socks5" or "ultravnc" (repeater).</summary>
        public const string VncProxyType = "vnc.proxy.type";
        public const string VncProxyHost = "vnc.proxy.host";
        public const string VncProxyPort = "vnc.proxy.port";
        public const string VncProxyUsername = "vnc.proxy.username";
        public const string VncProxyPassword = "vnc.proxy.password";
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
            AddVncExtras(info, extras, isAppleRemoteDesktop: info.Protocol == CoreProtocol.ARD);
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

        // Display
        extras[Keys.RdpResolution] = info.Resolution switch
        {
            RDPResolutions.FitToWindow => "fit",
            RDPResolutions.Fullscreen => "fullscreen",
            RDPResolutions.SmartSize => "smartsize",
            // Res1024x768 → "1024x768"
            var fixedSize => fixedSize.ToString()["Res".Length..],
        };
        extras[Keys.RdpAutoResize] = Bool(info.AutomaticResize);
        extras[Keys.RdpWallpaper] = Bool(info.DisplayWallpaper);
        extras[Keys.RdpThemes] = Bool(info.DisplayThemes);
        extras[Keys.RdpFontSmoothing] = Bool(info.EnableFontSmoothing);
        extras[Keys.RdpDesktopComposition] = Bool(info.EnableDesktopComposition);
        extras[Keys.RdpFullWindowDrag] = Bool(!info.DisableFullWindowDrag);
        extras[Keys.RdpMenuAnimations] = Bool(!info.DisableMenuAnimations);
        extras[Keys.RdpPersistentBitmapCache] = Bool(info.CacheBitmaps);

        // Redirection
        extras[Keys.RdpRedirectKeys] = Bool(info.RedirectKeys);
        extras[Keys.RdpClipboard] = Bool(info.RedirectClipboard);
        extras[Keys.RdpRedirectPorts] = Bool(info.RedirectPorts);
        extras[Keys.RdpRedirectPrinters] = Bool(info.RedirectPrinters);
        extras[Keys.RdpRedirectSmartCards] = Bool(info.RedirectSmartCards);
        extras[Keys.RdpDrives] = info.RedirectDiskDrives switch
        {
            RDPDiskDrives.Local => "local",
            RDPDiskDrives.All => "all",
            RDPDiskDrives.Custom => "custom",
            _ => "none",
        };
        if (info.RedirectDiskDrives == RDPDiskDrives.Custom && !string.IsNullOrWhiteSpace(info.RedirectDiskDrivesCustom))
            extras[Keys.RdpDrivesCustom] = info.RedirectDiskDrivesCustom;
        extras[Keys.RdpMicrophone] = Bool(info.RedirectAudioCapture);
        extras[Keys.RdpSound] = info.RedirectSound switch
        {
            RDPSounds.BringToThisComputer => "local",
            RDPSounds.LeaveAtRemoteComputer => "remote",
            _ => "off",
        };
        extras[Keys.RdpSoundQuality] = info.SoundQuality switch
        {
            RDPSoundQuality.Medium => "medium",
            RDPSoundQuality.High => "high",
            _ => "dynamic",
        };

        // Security. The legacy "server authentication" level decides what happens to a certificate that
        // cannot be validated, exactly like mstsc's AuthenticationLevel.
        extras[RdpProtocol.CertPolicyKey] = info.RDPAuthenticationLevel switch
        {
            AuthenticationLevel.AuthRequired => "deny",
            AuthenticationLevel.WarnOnFailedAuth => "tofu",
            _ => "ignore",
        };
        extras[Keys.RdpRestrictedAdmin] = Bool(info.UseRestrictedAdmin);
        extras[Keys.RdpRemoteCredentialGuard] = Bool(info.UseRCG);
        if (info.RDPMinutesToIdleTimeout > 0)
        {
            extras[Keys.RdpIdleTimeoutMinutes] = info.RDPMinutesToIdleTimeout.ToString(CultureInfo.InvariantCulture);
            extras[Keys.RdpIdleTimeoutAlert] = Bool(info.RDPAlertIdleTimeout);
        }

        if (!string.IsNullOrEmpty(info.LoadBalanceInfo))
            extras[Keys.RdpLoadBalanceInfo] = info.LoadBalanceInfo;
        if (!string.IsNullOrWhiteSpace(info.RDPStartProgram))
        {
            extras[Keys.RdpStartProgram] = info.RDPStartProgram;
            if (!string.IsNullOrWhiteSpace(info.RDPStartProgramWorkDir))
                extras[Keys.RdpStartProgramWorkDir] = info.RDPStartProgramWorkDir;
        }

        if (info.UseVmId && !string.IsNullOrEmpty(info.VmId))
        {
            extras[Keys.RdpVmId] = info.VmId;
            extras[Keys.RdpVmEnhancedMode] = Bool(info.UseEnhancedMode);
        }

        if (info.RDGatewayUsageMethod != RDGatewayUsageMethod.Never && !string.IsNullOrEmpty(info.RDGatewayHostname))
        {
            extras[Keys.RdpGateway] = info.RDGatewayHostname;
            extras[Keys.RdpGatewayUsage] = info.RDGatewayUsageMethod == RDGatewayUsageMethod.Detect ? "detect" : "always";
            switch (info.RDGatewayUseConnectionCredentials)
            {
                case RDGatewayUseConnectionCredentials.Yes:
                    extras[Keys.RdpGatewayCredentials] = "connection";
                    break;
                case RDGatewayUseConnectionCredentials.SmartCard:
                    extras[Keys.RdpGatewayCredentials] = "smartcard";
                    break;
                case RDGatewayUseConnectionCredentials.AccessToken:
                    extras[Keys.RdpGatewayCredentials] = "token";
                    if (!string.IsNullOrEmpty(info.RDGatewayAccessToken))
                        extras[Keys.RdpGatewayAccessToken] = info.RDGatewayAccessToken;
                    break;
                default:
                    // "No" (separate gateway credentials) and external credential providers, whose
                    // preparation step fills the gateway user name, domain and password.
                    extras[Keys.RdpGatewayCredentials] = "explicit";
                    if (!string.IsNullOrEmpty(info.RDGatewayUsername))
                        extras[Keys.RdpGatewayUsername] = info.RDGatewayUsername;
                    if (!string.IsNullOrEmpty(info.RDGatewayDomain))
                        extras[Keys.RdpGatewayDomain] = info.RDGatewayDomain;
                    if (!string.IsNullOrEmpty(info.RDGatewayPassword))
                        extras[Keys.RdpGatewayPassword] = info.RDGatewayPassword;
                    break;
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

    private static void AddVncExtras(ConnectionInfo info, Dictionary<string, string> extras, bool isAppleRemoteDesktop)
    {
        extras[Keys.VncScaling] = info.VNCSmartSizeMode switch
        {
            VncSmartSizeMode.SmartSNo => "none",
            VncSmartSizeMode.SmartSFree => "stretch",
            _ => "fit",
        };
        extras[Keys.VncViewOnly] = Bool(info.VNCViewOnly);
        extras[Keys.VncEncoding] = info.VNCEncoding switch
        {
            VncEncoding.EncRaw => "raw",
            VncEncoding.EncRRE => "rre",
            VncEncoding.EncCorre => "corre",
            VncEncoding.EncHextile => "hextile",
            VncEncoding.EncZlib => "zlib",
            VncEncoding.EncZLibHex => "zlibhex",
            VncEncoding.EncZRLE => "zrle",
            _ => "tight",
        };
        if (info.VNCCompression is >= VncCompression.Comp0 and <= VncCompression.Comp9)
            extras[Keys.VncCompression] = ((int)info.VNCCompression).ToString(CultureInfo.InvariantCulture);
        extras[Keys.VncColors] = info.VNCColors == VncColors.Col8Bit ? "8" : "full";
        extras[Keys.VncAuthMode] = isAppleRemoteDesktop ? "ard" : info.VNCAuthMode == VncAuthMode.AuthWin ? "windows" : "vnc";

        if (info.VNCProxyType != VncProxyType.ProxyNone)
        {
            extras[Keys.VncProxyType] = info.VNCProxyType switch
            {
                VncProxyType.ProxyHTTP => "http",
                VncProxyType.ProxySocks5 => "socks5",
                _ => "ultravnc",
            };
            extras[Keys.VncProxyHost] = info.VNCProxyIP ?? "";
            if (info.VNCProxyPort > 0)
                extras[Keys.VncProxyPort] = info.VNCProxyPort.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(info.VNCProxyUsername))
                extras[Keys.VncProxyUsername] = info.VNCProxyUsername;
            if (!string.IsNullOrEmpty(info.VNCProxyPassword))
                extras[Keys.VncProxyPassword] = info.VNCProxyPassword;
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
