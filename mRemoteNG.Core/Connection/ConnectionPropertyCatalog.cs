using System.Reflection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;

namespace mRemoteNG.Core.Connection
{
    /// <summary>How a connection property is edited.</summary>
    public enum ConnectionPropertyEditor
    {
        /// <summary>Single-line free text.</summary>
        Text,
        /// <summary>Masked text.</summary>
        Password,
        /// <summary>Check box.</summary>
        Boolean,
        /// <summary>Whole number.</summary>
        Number,
        /// <summary>One value of an enum.</summary>
        Choice,
        /// <summary>Free text with suggested values (panels, PuTTY sessions, external tools, …).</summary>
        Suggest,
        /// <summary>A colour name or #RRGGBB value.</summary>
        Color,
    }

    /// <summary>Where suggestions for a <see cref="ConnectionPropertyEditor.Suggest"/> property come from.</summary>
    public enum ConnectionPropertySuggestions
    {
        None,
        Icons,
        Panels,
        PuttySessions,
        SshTunnels,
        ExternalTools,
        Colors,
    }

    /// <summary>Tabs of the connection editor, in the legacy property grid's category order.</summary>
    public static class ConnectionPropertyCategories
    {
        public const string Display = "Display";
        public const string Connection = "Connection";
        public const string Credentials = "Credentials";
        public const string Protocol = "Protocol";
        public const string Miscellaneous = "Miscellaneous";

        public static IReadOnlyList<string> All { get; } = [Display, Connection, Credentials, Protocol, Miscellaneous];
    }

    /// <summary>
    /// Read access to the values being edited (which may not be written to a node yet), used to decide
    /// which properties are relevant.
    /// </summary>
    public delegate object? ConnectionPropertyValueLookup(string propertyName);

    /// <summary>Describes one editable <see cref="ConnectionInfo"/> property.</summary>
    public sealed class ConnectionPropertyDescriptor
    {
        private readonly HashSet<ProtocolType>? _protocols;
        private readonly Func<ConnectionPropertyValueLookup, bool>? _condition;

        internal ConnectionPropertyDescriptor(
            string name,
            string category,
            string section,
            string displayName,
            string description,
            ConnectionPropertyEditor editor,
            IEnumerable<ProtocolType>? protocols,
            Func<ConnectionPropertyValueLookup, bool>? condition,
            ConnectionPropertySuggestions suggestions,
            int minimum,
            int maximum,
            bool connectionOnly)
        {
            Property = typeof(ConnectionInfo).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"ConnectionInfo has no property {name}.", nameof(name));
            Name = name;
            Category = category;
            Section = section;
            DisplayName = displayName;
            Description = description;
            Editor = editor;
            _protocols = protocols is null ? null : [.. protocols];
            _condition = condition;
            Suggestions = suggestions;
            Minimum = minimum;
            Maximum = maximum;
            ConnectionOnly = connectionOnly;
        }

        public string Name { get; }

        /// <summary>One of <see cref="ConnectionPropertyCategories"/>.</summary>
        public string Category { get; }

        /// <summary>Heading inside the category (e.g. "RD Gateway").</summary>
        public string Section { get; }

        public string DisplayName { get; }
        public string Description { get; }
        public ConnectionPropertyEditor Editor { get; }
        public ConnectionPropertySuggestions Suggestions { get; }

        /// <summary>Bounds for <see cref="ConnectionPropertyEditor.Number"/>.</summary>
        public int Minimum { get; }

        public int Maximum { get; }

        /// <summary>True for properties folders do not have (the host name).</summary>
        public bool ConnectionOnly { get; }

        public PropertyInfo Property { get; }
        public Type PropertyType => Property.PropertyType;

        /// <summary>The protocols the property applies to; null for every protocol.</summary>
        public IReadOnlyCollection<ProtocolType>? Protocols => _protocols;

        /// <summary>True when the property has an Inherit flag.</summary>
        public bool SupportsInheritance => ConnectionInheritanceAccessor.SupportsInheritance(Name);

        public bool AppliesTo(ProtocolType protocol) => _protocols is null || _protocols.Contains(protocol);

        /// <summary>
        /// True when the property is relevant for the edited values: it applies to the protocol, to the
        /// node type, and its dependency (e.g. "RD Gateway is used") is met. Mirrors the legacy property grid.
        /// </summary>
        public bool IsRelevant(ConnectionPropertyValueLookup values, bool isFolder)
        {
            if (ConnectionOnly && isFolder)
                return false;
            if (values(nameof(ConnectionInfo.Protocol)) is ProtocolType protocol && !AppliesTo(protocol))
                return false;
            return _condition is null || _condition(values);
        }
    }

    /// <summary>
    /// Every user-editable, persisted <see cref="ConnectionInfo"/> property with its editor, category,
    /// protocol applicability and visibility rules (ported from the legacy ConnectionInfoPropertyGrid).
    /// </summary>
    public static class ConnectionPropertyCatalog
    {
        private static readonly ProtocolType[] Rdp = [ProtocolType.RDP];
        private static readonly ProtocolType[] Vnc = [ProtocolType.VNC, ProtocolType.ARD];
        private static readonly ProtocolType[] Ssh = [ProtocolType.SSH1, ProtocolType.SSH2];
        private static readonly ProtocolType[] RdpSsh = [ProtocolType.RDP, ProtocolType.SSH1, ProtocolType.SSH2];
        private static readonly ProtocolType[] Http = [ProtocolType.HTTP, ProtocolType.HTTPS];

        private static readonly ProtocolType[] PuttyProtocols =
            [ProtocolType.SSH1, ProtocolType.SSH2, ProtocolType.Telnet, ProtocolType.RAW, ProtocolType.Rlogin];

        private static readonly ProtocolType[] UsernameProtocols =
        [
            ProtocolType.RDP, ProtocolType.SSH1, ProtocolType.SSH2, ProtocolType.HTTP, ProtocolType.HTTPS,
            ProtocolType.IntApp, ProtocolType.VNC, ProtocolType.ARD, ProtocolType.PowerShell, ProtocolType.Rlogin,
            ProtocolType.Telnet, ProtocolType.Terminal,
        ];

        private static readonly ProtocolType[] PasswordProtocols =
            Enum.GetValues<ProtocolType>().Except([ProtocolType.Telnet, ProtocolType.Rlogin, ProtocolType.RAW]).ToArray();

        private static readonly ProtocolType[] DomainProtocols =
            [ProtocolType.RDP, ProtocolType.IntApp, ProtocolType.PowerShell, ProtocolType.WSL, ProtocolType.VNC, ProtocolType.ARD];

        private static readonly ProtocolType[] AddressProviderProtocols = [ProtocolType.RDP, ProtocolType.SSH2];

        /// <summary>All descriptors in display order.</summary>
        public static IReadOnlyList<ConnectionPropertyDescriptor> All { get; } = Build();

        private static readonly Dictionary<string, ConnectionPropertyDescriptor> ByName =
            All.ToDictionary(d => d.Name, StringComparer.Ordinal);

        public static ConnectionPropertyDescriptor Get(string name) =>
            ByName.TryGetValue(name, out var descriptor)
                ? descriptor
                : throw new ArgumentException($"{name} is not an editable connection property.", nameof(name));

        public static bool TryGet(string name, out ConnectionPropertyDescriptor descriptor) =>
            ByName.TryGetValue(name, out descriptor!);

        /// <summary>The names of the properties that are relevant for <paramref name="node"/>'s current values.</summary>
        public static IEnumerable<string> RelevantProperties(ConnectionInfo node) =>
            All.Where(d => d.IsRelevant(name => Get(name).Property.GetValue(node), node is Container.ContainerInfo))
                .Select(d => d.Name);

        private static IReadOnlyList<ConnectionPropertyDescriptor> Build()
        {
            var list = new List<ConnectionPropertyDescriptor>();

            void Add(string name, string category, string section, string displayName, string description,
                ConnectionPropertyEditor editor = ConnectionPropertyEditor.Text,
                IEnumerable<ProtocolType>? protocols = null,
                Func<ConnectionPropertyValueLookup, bool>? when = null,
                ConnectionPropertySuggestions suggestions = ConnectionPropertySuggestions.None,
                int min = 0, int max = 0, bool connectionOnly = false) =>
                list.Add(new ConnectionPropertyDescriptor(name, category, section, displayName, description, editor,
                    protocols, when, suggestions, min, max, connectionOnly));

            static T V<T>(ConnectionPropertyValueLookup values, string name) =>
                values(name) is T value ? value : default!;

            static bool CredentialProviderIs(ConnectionPropertyValueLookup v, params ExternalCredentialProvider[] providers) =>
                providers.Contains(V<ExternalCredentialProvider>(v, nameof(ConnectionInfo.ExternalCredentialProvider)));

            static bool IsVncAuth(ConnectionPropertyValueLookup v) =>
                V<ProtocolType>(v, nameof(ConnectionInfo.Protocol)) is ProtocolType.VNC or ProtocolType.ARD
                && V<VncAuthMode>(v, nameof(ConnectionInfo.VNCAuthMode)) == VncAuthMode.AuthVNC;

            static bool GatewayUsed(ConnectionPropertyValueLookup v) =>
                V<RDGatewayUsageMethod>(v, nameof(ConnectionInfo.RDGatewayUsageMethod)) != RDGatewayUsageMethod.Never;

            static bool GatewayCredentials(ConnectionPropertyValueLookup v, params RDGatewayUseConnectionCredentials[] modes) =>
                GatewayUsed(v) && modes.Contains(V<RDGatewayUseConnectionCredentials>(v, nameof(ConnectionInfo.RDGatewayUseConnectionCredentials)));

            static bool VncProxyUsed(ConnectionPropertyValueLookup v) =>
                V<VncProxyType>(v, nameof(ConnectionInfo.VNCProxyType)) != VncProxyType.ProxyNone;

            const string display = ConnectionPropertyCategories.Display;
            const string connection = ConnectionPropertyCategories.Connection;
            const string credentials = ConnectionPropertyCategories.Credentials;
            const string protocol = ConnectionPropertyCategories.Protocol;
            const string misc = ConnectionPropertyCategories.Miscellaneous;

            // ── Display ──────────────────────────────────────────────────────
            Add(nameof(ConnectionInfo.Name), display, "Display", "Name",
                "This is the name that will be displayed in the connections tree.");
            Add(nameof(ConnectionInfo.Description), display, "Display", "Description",
                "Put your notes or a description for the host here.");
            Add(nameof(ConnectionInfo.Icon), display, "Display", "Icon",
                "Choose an icon that will be displayed when connected to the host.",
                ConnectionPropertyEditor.Suggest, suggestions: ConnectionPropertySuggestions.Icons);
            Add(nameof(ConnectionInfo.Panel), display, "Display", "Panel",
                "Sets the panel in which the connection will open.",
                ConnectionPropertyEditor.Suggest, suggestions: ConnectionPropertySuggestions.Panels);
            Add(nameof(ConnectionInfo.TabColor), display, "Display", "Tab colour",
                "Sets the colour of the connection tab (a colour name or #RRGGBB). Leave empty for the default theme colour.",
                ConnectionPropertyEditor.Color, suggestions: ConnectionPropertySuggestions.Colors);
            Add(nameof(ConnectionInfo.ConnectionFrameColor), display, "Display", "Connection frame colour",
                "Sets a coloured border around the connection panel to distinguish environments (e.g. production, test).",
                ConnectionPropertyEditor.Choice);
            Add(nameof(ConnectionInfo.Favorite), display, "Display", "Favourite",
                "Show this connection in the favourites menu.", ConnectionPropertyEditor.Boolean);
            Add(nameof(ConnectionInfo.EnvironmentTags), display, "Display", "Environment tags",
                "Tags to categorise the environment (e.g. #PROD, #UAT, #TEST).");

            // ── Connection ───────────────────────────────────────────────────
            Add(nameof(ConnectionInfo.Hostname), connection, "Connection", "Host name / IP",
                "Enter the host name or IP address you want to connect to.", connectionOnly: true);
            Add(nameof(ConnectionInfo.Protocol), connection, "Connection", "Protocol",
                "Choose the protocol mRemoteNG should use to connect to the host.", ConnectionPropertyEditor.Choice);
            Add(nameof(ConnectionInfo.Port), connection, "Connection", "Port",
                "Enter the port the selected protocol is listening on.", ConnectionPropertyEditor.Number,
                when: v => ConnectionDefaults.UsesPort(V<ProtocolType>(v, nameof(ConnectionInfo.Protocol))),
                min: 0, max: 65535);
            Add(nameof(ConnectionInfo.SSHTunnelConnectionName), connection, "Connection", "SSH tunnel",
                "To connect through an SSH tunnel (jump host), choose the SSH connection used to open the tunnel.",
                ConnectionPropertyEditor.Suggest, suggestions: ConnectionPropertySuggestions.SshTunnels);

            Add(nameof(ConnectionInfo.PuttySession), connection, "SSH / PuTTY", "PuTTY session",
                "Select a PuTTY saved session to be used when connecting.",
                ConnectionPropertyEditor.Suggest, PuttyProtocols, suggestions: ConnectionPropertySuggestions.PuttySessions);
            Add(nameof(ConnectionInfo.SSHOptions), connection, "SSH / PuTTY", "SSH options",
                "Additional options for the SSH connection (PuTTY command-line syntax).", protocols: Ssh);
            Add(nameof(ConnectionInfo.OpeningCommand), connection, "SSH / PuTTY", "Opening command",
                "Command sent to the host after logging in.", protocols: Ssh);

            Add(nameof(ConnectionInfo.RdpVersion), connection, "Remote Desktop", "RDP version",
                "Sets the version of RDP to use when opening connections.", ConnectionPropertyEditor.Choice, Rdp);
            Add(nameof(ConnectionInfo.UseCredSsp), connection, "Remote Desktop", "Use CredSSP (NLA)",
                "Use the Credential Security Support Provider (CredSSP) for authentication if it is available.",
                ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.LoadBalanceInfo), connection, "Remote Desktop", "Load balance info",
                "Load balancing information used by load balancing routers to choose the best server.", protocols: Rdp);
            Add(nameof(ConnectionInfo.UseVmId), connection, "Remote Desktop", "Use VM ID",
                "Use a VM ID to connect to a VM running on Hyper-V.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.VmId), connection, "Remote Desktop", "VM ID",
                "The ID of the Hyper-V virtual machine to connect to.", protocols: Rdp,
                when: v => V<bool>(v, nameof(ConnectionInfo.UseVmId)));
            Add(nameof(ConnectionInfo.UseEnhancedMode), connection, "Remote Desktop", "Use enhanced mode",
                "Connect to a Hyper-V host with enhanced mode enabled.", ConnectionPropertyEditor.Boolean, Rdp,
                when: v => V<bool>(v, nameof(ConnectionInfo.UseVmId)));

            Add(nameof(ConnectionInfo.ExternalAddressProvider), connection, "External address", "External address provider",
                "External provider to retrieve the host address from.", ConnectionPropertyEditor.Choice, AddressProviderProtocols);
            Add(nameof(ConnectionInfo.EC2InstanceId), connection, "External address", "EC2 instance ID",
                "The AWS EC2 instance whose public IP is used as the host address.", protocols: AddressProviderProtocols,
                when: v => V<ExternalAddressProvider>(v, nameof(ConnectionInfo.ExternalAddressProvider)) == ExternalAddressProvider.AmazonWebServices);
            Add(nameof(ConnectionInfo.EC2Region), connection, "External address", "EC2 region",
                "The AWS region of the EC2 instance.", protocols: AddressProviderProtocols,
                when: v => V<ExternalAddressProvider>(v, nameof(ConnectionInfo.ExternalAddressProvider)) == ExternalAddressProvider.AmazonWebServices);

            Add(nameof(ConnectionInfo.MacAddress), connection, "Wake-on-LAN", "MAC address",
                "MAC address of the remote host, used by Wake-on-LAN and external tools.");

            // ── Credentials ──────────────────────────────────────────────────
            Add(nameof(ConnectionInfo.Username), credentials, "Credentials", "Username", "Enter your username.",
                protocols: UsernameProtocols,
                when: v => !CredentialProviderIs(v, ExternalCredentialProvider.DelineaSecretServer, ExternalCredentialProvider.ClickstudiosPasswordState)
                           && !(CredentialProviderIs(v, ExternalCredentialProvider.VaultOpenbao)
                                && V<VaultOpenbaoSecretEngine>(v, nameof(ConnectionInfo.VaultOpenbaoSecretEngine)) is not (VaultOpenbaoSecretEngine.Kv or VaultOpenbaoSecretEngine.SSHOTP))
                           && !IsVncAuth(v));
            Add(nameof(ConnectionInfo.Password), credentials, "Credentials", "Password", "Enter your password.",
                ConnectionPropertyEditor.Password, PasswordProtocols,
                when: v => !CredentialProviderIs(v, ExternalCredentialProvider.DelineaSecretServer,
                    ExternalCredentialProvider.ClickstudiosPasswordState, ExternalCredentialProvider.VaultOpenbao));
            Add(nameof(ConnectionInfo.Domain), credentials, "Credentials", "Domain", "Enter your domain.",
                protocols: DomainProtocols,
                when: v => !CredentialProviderIs(v, ExternalCredentialProvider.DelineaSecretServer, ExternalCredentialProvider.ClickstudiosPasswordState)
                           && !IsVncAuth(v));

            Add(nameof(ConnectionInfo.ExternalCredentialProvider), credentials, "External credential provider", "Credential provider",
                "External credential provider / vault to retrieve the credentials from.", ConnectionPropertyEditor.Choice, RdpSsh);
            Add(nameof(ConnectionInfo.UserViaAPI), credentials, "External credential provider", "User via API",
                "ID of the credential in the external provider (e.g. Secret Server). Leave username/password/domain empty when used.",
                protocols: RdpSsh,
                when: v => !CredentialProviderIs(v, ExternalCredentialProvider.None, ExternalCredentialProvider.VaultOpenbao));
            Add(nameof(ConnectionInfo.VaultOpenbaoSecretEngine), credentials, "External credential provider", "Vault secret engine",
                "Secret engine used in Vault / OpenBao to store the secret.", ConnectionPropertyEditor.Choice, RdpSsh,
                when: v => CredentialProviderIs(v, ExternalCredentialProvider.VaultOpenbao));
            Add(nameof(ConnectionInfo.VaultOpenbaoMount), credentials, "External credential provider", "Vault mount",
                "Mount path of the secret engine in Vault / OpenBao.", protocols: RdpSsh,
                when: v => CredentialProviderIs(v, ExternalCredentialProvider.VaultOpenbao));
            Add(nameof(ConnectionInfo.VaultOpenbaoRole), credentials, "External credential provider", "Vault role",
                "Role (or secret name) in Vault / OpenBao.", protocols: RdpSsh,
                when: v => CredentialProviderIs(v, ExternalCredentialProvider.VaultOpenbao));

            // ── Protocol: RDP ────────────────────────────────────────────────
            const string rdp = "Remote Desktop";
            Add(nameof(ConnectionInfo.RDPAuthenticationLevel), protocol, rdp, "Server authentication",
                "Select which authentication level this connection should use.", ConnectionPropertyEditor.Choice, Rdp);
            Add(nameof(ConnectionInfo.UseConsoleSession), protocol, rdp, "Console session",
                "Connect to the console session of the remote host.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.UseRestrictedAdmin), protocol, rdp, "Restricted admin",
                "Use restricted admin mode on the target host (local system context).", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.UseRCG), protocol, rdp, "Remote Credential Guard",
                "Use Remote Credential Guard to tunnel authentication back to this computer through the RDP channel.",
                ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.RDPMinutesToIdleTimeout), protocol, rdp, "Idle timeout (minutes)",
                "Minutes the session may sit idle before it is disconnected automatically (0 for no limit).",
                ConnectionPropertyEditor.Number, Rdp, min: 0, max: 240);
            Add(nameof(ConnectionInfo.RDPAlertIdleTimeout), protocol, rdp, "Alert on idle disconnect",
                "Show an alert after the session disconnects because of inactivity.", ConnectionPropertyEditor.Boolean, Rdp,
                when: v => V<int>(v, nameof(ConnectionInfo.RDPMinutesToIdleTimeout)) > 0);
            Add(nameof(ConnectionInfo.RDPStartProgram), protocol, rdp, "Start program",
                "The program to start on the remote server upon connection.", protocols: Rdp);
            Add(nameof(ConnectionInfo.RDPStartProgramWorkDir), protocol, rdp, "Start program working dir",
                "The working directory of the start program.", protocols: Rdp);

            const string rdpDisplay = "Remote Desktop: display";
            Add(nameof(ConnectionInfo.Resolution), protocol, rdpDisplay, "Resolution",
                "Choose the resolution or mode this connection will open in.", ConnectionPropertyEditor.Choice, Rdp);
            Add(nameof(ConnectionInfo.AutomaticResize), protocol, rdpDisplay, "Automatic resize",
                "Resize the remote desktop when the window is resized or full screen is toggled.",
                ConnectionPropertyEditor.Boolean, Rdp,
                when: v => V<RDPResolutions>(v, nameof(ConnectionInfo.Resolution)) is RDPResolutions.FitToWindow or RDPResolutions.Fullscreen);
            Add(nameof(ConnectionInfo.Colors), protocol, rdpDisplay, "Colours", "Select the colour quality to be used.",
                ConnectionPropertyEditor.Choice, Rdp);
            Add(nameof(ConnectionInfo.CacheBitmaps), protocol, rdpDisplay, "Cache bitmaps",
                "Use bitmap caching.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.DisplayWallpaper), protocol, rdpDisplay, "Display wallpaper",
                "Show the remote desktop wallpaper.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.DisplayThemes), protocol, rdpDisplay, "Display themes",
                "Show the remote host's visual theme.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.EnableFontSmoothing), protocol, rdpDisplay, "Font smoothing",
                "Use font smoothing.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.EnableDesktopComposition), protocol, rdpDisplay, "Desktop composition",
                "Use desktop composition.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.DisableFullWindowDrag), protocol, rdpDisplay, "Disable full window drag",
                "Do not show window contents while dragging a window.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.DisableMenuAnimations), protocol, rdpDisplay, "Disable menu animations",
                "Do not animate menus and windows in the remote session.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.DisableCursorShadow), protocol, rdpDisplay, "Disable cursor shadow",
                "Do not show a mouse cursor shadow.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.DisableCursorBlinking), protocol, rdpDisplay, "Disable cursor blinking",
                "Do not let the text cursor blink.", ConnectionPropertyEditor.Boolean, Rdp);

            const string redirect = "Remote Desktop: redirection";
            Add(nameof(ConnectionInfo.RedirectKeys), protocol, redirect, "Key combinations",
                "Send key combinations (e.g. Alt+Tab) to the remote host.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.RedirectDiskDrives), protocol, redirect, "Disk drives",
                "Which local disk drives are available on the remote host.", ConnectionPropertyEditor.Choice, Rdp);
            Add(nameof(ConnectionInfo.RedirectDiskDrivesCustom), protocol, redirect, "Custom drives",
                "Drives to redirect, e.g. C,D,X.", protocols: Rdp,
                when: v => V<RDPDiskDrives>(v, nameof(ConnectionInfo.RedirectDiskDrives)) == RDPDiskDrives.Custom);
            Add(nameof(ConnectionInfo.RedirectPorts), protocol, redirect, "Ports",
                "Make local serial and parallel ports available on the remote host.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.RedirectPrinters), protocol, redirect, "Printers",
                "Make local printers available on the remote host.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.RedirectSmartCards), protocol, redirect, "Smart cards",
                "Make local smart cards available on the remote host.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.RedirectClipboard), protocol, redirect, "Clipboard",
                "Share the clipboard with the remote host.", ConnectionPropertyEditor.Boolean, Rdp);
            Add(nameof(ConnectionInfo.RedirectSound), protocol, redirect, "Sound",
                "Select how remote sound is played.", ConnectionPropertyEditor.Choice, Rdp);
            Add(nameof(ConnectionInfo.SoundQuality), protocol, redirect, "Sound quality",
                "Sound quality provided by the protocol.", ConnectionPropertyEditor.Choice, Rdp,
                when: v => V<RDPSounds>(v, nameof(ConnectionInfo.RedirectSound)) == RDPSounds.BringToThisComputer);
            Add(nameof(ConnectionInfo.RedirectAudioCapture), protocol, redirect, "Audio capture",
                "Redirect this computer's default audio input (microphone) to the remote host.",
                ConnectionPropertyEditor.Boolean, Rdp);

            const string gateway = "RD Gateway";
            Add(nameof(ConnectionInfo.RDGatewayUsageMethod), protocol, gateway, "Use gateway",
                "When to use a Remote Desktop Gateway server.", ConnectionPropertyEditor.Choice, Rdp);
            Add(nameof(ConnectionInfo.RDGatewayHostname), protocol, gateway, "Gateway host name",
                "Host name of the Remote Desktop Gateway server.", protocols: Rdp, when: GatewayUsed);
            Add(nameof(ConnectionInfo.RDGatewayUseConnectionCredentials), protocol, gateway, "Gateway credentials",
                "How to log on to the gateway.", ConnectionPropertyEditor.Choice, Rdp, when: GatewayUsed);
            Add(nameof(ConnectionInfo.RDGatewayUsername), protocol, gateway, "Gateway username",
                "User name for the RD Gateway server.", protocols: Rdp,
                when: v => GatewayCredentials(v, RDGatewayUseConnectionCredentials.No));
            Add(nameof(ConnectionInfo.RDGatewayPassword), protocol, gateway, "Gateway password",
                "Password for the RD Gateway server.", ConnectionPropertyEditor.Password, Rdp,
                when: v => GatewayCredentials(v, RDGatewayUseConnectionCredentials.No));
            Add(nameof(ConnectionInfo.RDGatewayDomain), protocol, gateway, "Gateway domain",
                "Domain for the RD Gateway server.", protocols: Rdp,
                when: v => GatewayCredentials(v, RDGatewayUseConnectionCredentials.No));
            Add(nameof(ConnectionInfo.RDGatewayExternalCredentialProvider), protocol, gateway, "Gateway credential provider",
                "External credential provider for the gateway credentials.", ConnectionPropertyEditor.Choice, Rdp,
                when: v => GatewayCredentials(v, RDGatewayUseConnectionCredentials.No, RDGatewayUseConnectionCredentials.ExternalCredentialProvider));
            Add(nameof(ConnectionInfo.RDGatewayUserViaAPI), protocol, gateway, "Gateway user via API",
                "ID of the gateway credential in the external provider.", protocols: Rdp,
                when: v => GatewayCredentials(v, RDGatewayUseConnectionCredentials.No, RDGatewayUseConnectionCredentials.ExternalCredentialProvider));

            // ── Protocol: VNC ────────────────────────────────────────────────
            const string vnc = "VNC";
            Add(nameof(ConnectionInfo.VNCAuthMode), protocol, vnc, "Authentication mode",
                "How to authenticate against the VNC server.", ConnectionPropertyEditor.Choice, Vnc);
            Add(nameof(ConnectionInfo.VNCCompression), protocol, vnc, "Compression",
                "Compression level to be used.", ConnectionPropertyEditor.Choice, Vnc);
            Add(nameof(ConnectionInfo.VNCEncoding), protocol, vnc, "Encoding",
                "Encoding to be used.", ConnectionPropertyEditor.Choice, Vnc);
            Add(nameof(ConnectionInfo.VNCColors), protocol, vnc, "Colours",
                "Colour depth to be used.", ConnectionPropertyEditor.Choice, Vnc);
            Add(nameof(ConnectionInfo.VNCSmartSizeMode), protocol, vnc, "Smart size",
                "How the remote screen is scaled to the window.", ConnectionPropertyEditor.Choice, Vnc);
            Add(nameof(ConnectionInfo.VNCViewOnly), protocol, vnc, "View only",
                "Do not send keyboard or mouse input.", ConnectionPropertyEditor.Boolean, Vnc);

            const string vncProxy = "VNC proxy";
            Add(nameof(ConnectionInfo.VNCProxyType), protocol, vncProxy, "Proxy type",
                "The type of proxy used to tunnel VNC connections.", ConnectionPropertyEditor.Choice, Vnc);
            Add(nameof(ConnectionInfo.VNCProxyIP), protocol, vncProxy, "Proxy address",
                "Address of the proxy.", protocols: Vnc, when: VncProxyUsed);
            Add(nameof(ConnectionInfo.VNCProxyPort), protocol, vncProxy, "Proxy port",
                "Port the proxy listens on.", ConnectionPropertyEditor.Number, Vnc, when: VncProxyUsed, min: 0, max: 65535);
            Add(nameof(ConnectionInfo.VNCProxyUsername), protocol, vncProxy, "Proxy username",
                "Username for the proxy.", protocols: Vnc, when: VncProxyUsed);
            Add(nameof(ConnectionInfo.VNCProxyPassword), protocol, vncProxy, "Proxy password",
                "Password for the proxy.", ConnectionPropertyEditor.Password, Vnc, when: VncProxyUsed);

            // ── Protocol: others ─────────────────────────────────────────────
            Add(nameof(ConnectionInfo.RenderingEngine), protocol, "Web", "Rendering engine",
                "Rendering engine used to display web pages.", ConnectionPropertyEditor.Choice, Http);
            Add(nameof(ConnectionInfo.ExtApp), protocol, "External application", "External tool",
                "The external tool to start for this connection.", ConnectionPropertyEditor.Suggest,
                [ProtocolType.IntApp], suggestions: ConnectionPropertySuggestions.ExternalTools);

            // ── Miscellaneous ────────────────────────────────────────────────
            Add(nameof(ConnectionInfo.PreExtApp), misc, "External tools", "External tool before",
                "External tool started before the connection to the remote host is established.",
                ConnectionPropertyEditor.Suggest, suggestions: ConnectionPropertySuggestions.ExternalTools);
            Add(nameof(ConnectionInfo.PostExtApp), misc, "External tools", "External tool after",
                "External tool started after disconnecting from the remote host.",
                ConnectionPropertyEditor.Suggest, suggestions: ConnectionPropertySuggestions.ExternalTools);
            Add(nameof(ConnectionInfo.UserField), misc, "Miscellaneous", "User field",
                "Free text for any information you need (available to external tools as %USERFIELD%).");

            return list;
        }
    }
}
