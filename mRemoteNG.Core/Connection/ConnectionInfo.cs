using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.Http;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;

namespace mRemoteNG.Core.Connection
{
    /// <summary>
    /// Cross-platform connection information model.
    /// Mirror of the legacy mRemoteNG.Connection.ConnectionInfo, without WinForms attributes.
    /// </summary>
    public class ConnectionInfo : INotifyPropertyChanged, IHasParent, IInheritable
    {
        #region Fields

        private string _name = "New Connection";
        private string _description = "";
        private string _icon = "";
        private string _panel = "General";
        private string _color = "";
        private string _tabColor = "";
        private ConnectionFrameColor _connectionFrameColor;

        private string _hostname = "";
        private ExternalAddressProvider _externalAddressProvider;
        private string _ec2InstanceId = "";
        private string _ec2Region = "";
        private ExternalCredentialProvider _externalCredentialProvider;
        private string _userViaAPI = "";
        private string _username = "";
        private string _password = "";
        private string _vaultRole = "";
        private string _vaultMount = "";
        private VaultOpenbaoSecretEngine _vaultSecretEngine = VaultOpenbaoSecretEngine.Kv;
        private string _domain = "";
        private string _vmId = "";
        private bool _useEnhancedMode;

        private string _sshTunnelConnectionName = "";
        private ProtocolType _protocol;
        private RdpVersion _rdpProtocolVersion = RdpVersion.Rdc10;
        private string _extApp = "";
        private int _port;
        private string _sshOptions = "";
        private string _puttySession = "";
        private bool _useConsoleSession;
        private AuthenticationLevel _rdpAuthenticationLevel;
        private int _rdpMinutesToIdleTimeout;
        private bool _rdpAlertIdleTimeout;
        private string _loadBalanceInfo = "";
        private RenderingEngine _renderingEngine;
        private bool _useCredSsp;
        private bool _useRestrictedAdmin;
        private bool _useRCG;
        private bool _useVmId;

        private RDGatewayUsageMethod _rdGatewayUsageMethod;
        private string _rdGatewayHostname = "";
        private RDGatewayUseConnectionCredentials _rdGatewayUseConnectionCredentials;
        private string _rdGatewayUsername = "";
        private string _rdGatewayPassword = "";
        private string _rdGatewayDomain = "";
        private string _rdGatewayAccessToken = "";
        private ExternalCredentialProvider _rdGatewayExternalCredentialProvider;
        private string _rdGatewayUserViaAPI = "";

        private RDPResolutions _resolution;
        private bool _automaticResize;
        private RDPColors _colors;
        private bool _cacheBitmaps;
        private bool _displayWallpaper;
        private bool _displayThemes;
        private bool _enableFontSmoothing;
        private bool _enableDesktopComposition;
        private bool _disableFullWindowDrag;
        private bool _disableMenuAnimations;
        private bool _disableCursorShadow;
        private bool _disableCursorBlinking;

        private bool _redirectKeys;
        private RDPDiskDrives _redirectDiskDrives;
        private string _redirectDiskDrivesCustom = "";
        private bool _redirectPrinters;
        private bool _redirectClipboard;
        private bool _redirectPorts;
        private bool _redirectSmartCards;
        private RDPSounds _redirectSound;
        private RDPSoundQuality _soundQuality;
        private bool _redirectAudioCapture;

        private string _preExtApp = "";
        private string _postExtApp = "";
        private string _macAddress = "";
        private string _openingCommand = "";
        private string _userField = "";
        private string _environmentTags = "";
        private string _rdpStartProgram = "";
        private string _rdpStartProgramWorkDir = "";
        private bool _favorite;

        private VncCompression _vncCompression;
        private VncEncoding _vncEncoding;
        private VncAuthMode _vncAuthMode;
        private VncProxyType _vncProxyType;
        private string _vncProxyIp = "";
        private int _vncProxyPort;
        private string _vncProxyUsername = "";
        private string _vncProxyPassword = "";
        private VncColors _vncColors;
        private VncSmartSizeMode _vncSmartSizeMode;
        private bool _vncViewOnly;

        private ConnectionInfoInheritance _inheritance;

        #endregion

        #region Properties

        public string ConstantID { get; }

        // Display
        public virtual string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public virtual string Description
        {
            get => GetPropertyValue(nameof(Description), _description);
            set => SetField(ref _description, value);
        }

        public virtual string Icon
        {
            get => GetPropertyValue(nameof(Icon), _icon);
            set => SetField(ref _icon, value);
        }

        public virtual string Panel
        {
            get => GetPropertyValue(nameof(Panel), _panel);
            set => SetField(ref _panel, value);
        }

        public virtual string Color
        {
            get => GetPropertyValue(nameof(Color), _color);
            set => SetField(ref _color, value);
        }

        public virtual string TabColor
        {
            get => GetPropertyValue(nameof(TabColor), _tabColor);
            set => SetField(ref _tabColor, value);
        }

        public virtual ConnectionFrameColor ConnectionFrameColor
        {
            get => GetPropertyValue(nameof(ConnectionFrameColor), _connectionFrameColor);
            set => SetField(ref _connectionFrameColor, value);
        }

        // Connection
        public virtual string Hostname
        {
            get => _hostname?.Trim() ?? "";
            set => SetField(ref _hostname, value?.Trim() ?? "");
        }

        public virtual int Port
        {
            get => GetPropertyValue(nameof(Port), _port);
            set => SetField(ref _port, value);
        }

        public ExternalAddressProvider ExternalAddressProvider
        {
            get => GetPropertyValue(nameof(ExternalAddressProvider), _externalAddressProvider);
            set => SetField(ref _externalAddressProvider, value);
        }

        public string EC2InstanceId
        {
            get => GetPropertyValue(nameof(EC2InstanceId), _ec2InstanceId)?.Trim() ?? "";
            set => SetField(ref _ec2InstanceId, value?.Trim() ?? "");
        }

        public string EC2Region
        {
            get => GetPropertyValue(nameof(EC2Region), _ec2Region)?.Trim() ?? "";
            set => SetField(ref _ec2Region, value?.Trim() ?? "");
        }

        public ExternalCredentialProvider ExternalCredentialProvider
        {
            get => GetPropertyValue(nameof(ExternalCredentialProvider), _externalCredentialProvider);
            set => SetField(ref _externalCredentialProvider, value);
        }

        public virtual string UserViaAPI
        {
            get => GetPropertyValue(nameof(UserViaAPI), _userViaAPI);
            set => SetField(ref _userViaAPI, value);
        }

        public virtual string Username
        {
            get => GetPropertyValue(nameof(Username), _username);
            set => SetField(ref _username, value?.Trim() ?? "");
        }

        public virtual string Password
        {
            get => GetPropertyValue(nameof(Password), _password);
            set => SetField(ref _password, value);
        }

        public virtual string VaultOpenbaoMount
        {
            get => GetPropertyValue(nameof(VaultOpenbaoMount), _vaultMount);
            set => SetField(ref _vaultMount, value);
        }

        public virtual string VaultOpenbaoRole
        {
            get => GetPropertyValue(nameof(VaultOpenbaoRole), _vaultRole);
            set => SetField(ref _vaultRole, value);
        }

        public VaultOpenbaoSecretEngine VaultOpenbaoSecretEngine
        {
            get => GetPropertyValue(nameof(VaultOpenbaoSecretEngine), _vaultSecretEngine);
            set => SetField(ref _vaultSecretEngine, value);
        }

        public string Domain
        {
            get => GetPropertyValue(nameof(Domain), _domain)?.Trim() ?? "";
            set => SetField(ref _domain, value?.Trim() ?? "");
        }

        public string VmId
        {
            get => GetPropertyValue(nameof(VmId), _vmId)?.Trim() ?? "";
            set => SetField(ref _vmId, value?.Trim() ?? "");
        }

        public bool UseEnhancedMode
        {
            get => GetPropertyValue(nameof(UseEnhancedMode), _useEnhancedMode);
            set => SetField(ref _useEnhancedMode, value);
        }

        public string SSHTunnelConnectionName
        {
            get => GetPropertyValue(nameof(SSHTunnelConnectionName), _sshTunnelConnectionName)?.Trim() ?? "";
            set => SetField(ref _sshTunnelConnectionName, value?.Trim() ?? "");
        }

        public virtual string OpeningCommand
        {
            get => GetPropertyValue(nameof(OpeningCommand), _openingCommand);
            set => SetField(ref _openingCommand, value);
        }

        // Protocol
        public virtual ProtocolType Protocol
        {
            get => GetPropertyValue(nameof(Protocol), _protocol);
            set => SetField(ref _protocol, value);
        }

        public virtual RdpVersion RdpVersion
        {
            get => GetPropertyValue(nameof(RdpVersion), _rdpProtocolVersion);
            set => SetField(ref _rdpProtocolVersion, value);
        }

        public string ExtApp
        {
            get => GetPropertyValue(nameof(ExtApp), _extApp);
            set => SetField(ref _extApp, value);
        }

        public virtual string PuttySession
        {
            get => GetPropertyValue(nameof(PuttySession), _puttySession);
            set => SetField(ref _puttySession, value);
        }

        public virtual string SSHOptions
        {
            get => GetPropertyValue(nameof(SSHOptions), _sshOptions);
            set => SetField(ref _sshOptions, value);
        }

        public bool UseConsoleSession
        {
            get => GetPropertyValue(nameof(UseConsoleSession), _useConsoleSession);
            set => SetField(ref _useConsoleSession, value);
        }

        public AuthenticationLevel RDPAuthenticationLevel
        {
            get => GetPropertyValue(nameof(RDPAuthenticationLevel), _rdpAuthenticationLevel);
            set => SetField(ref _rdpAuthenticationLevel, value);
        }

        public virtual int RDPMinutesToIdleTimeout
        {
            get => GetPropertyValue(nameof(RDPMinutesToIdleTimeout), _rdpMinutesToIdleTimeout);
            set => SetField(ref _rdpMinutesToIdleTimeout, Math.Clamp(value, 0, 240));
        }

        public bool RDPAlertIdleTimeout
        {
            get => GetPropertyValue(nameof(RDPAlertIdleTimeout), _rdpAlertIdleTimeout);
            set => SetField(ref _rdpAlertIdleTimeout, value);
        }

        public string LoadBalanceInfo
        {
            get => GetPropertyValue(nameof(LoadBalanceInfo), _loadBalanceInfo)?.Trim() ?? "";
            set => SetField(ref _loadBalanceInfo, value?.Trim() ?? "");
        }

        public RenderingEngine RenderingEngine
        {
            get => GetPropertyValue(nameof(RenderingEngine), _renderingEngine);
            set => SetField(ref _renderingEngine, value);
        }

        public bool UseCredSsp
        {
            get => GetPropertyValue(nameof(UseCredSsp), _useCredSsp);
            set => SetField(ref _useCredSsp, value);
        }

        public bool UseRestrictedAdmin
        {
            get => GetPropertyValue(nameof(UseRestrictedAdmin), _useRestrictedAdmin);
            set => SetField(ref _useRestrictedAdmin, value);
        }

        public bool UseRCG
        {
            get => GetPropertyValue(nameof(UseRCG), _useRCG);
            set => SetField(ref _useRCG, value);
        }

        public bool UseVmId
        {
            get => GetPropertyValue(nameof(UseVmId), _useVmId);
            set => SetField(ref _useVmId, value);
        }

        // RD Gateway
        public RDGatewayUsageMethod RDGatewayUsageMethod
        {
            get => GetPropertyValue(nameof(RDGatewayUsageMethod), _rdGatewayUsageMethod);
            set => SetField(ref _rdGatewayUsageMethod, value);
        }

        public string RDGatewayHostname
        {
            get => GetPropertyValue(nameof(RDGatewayHostname), _rdGatewayHostname)?.Trim() ?? "";
            set => SetField(ref _rdGatewayHostname, value?.Trim() ?? "");
        }

        public RDGatewayUseConnectionCredentials RDGatewayUseConnectionCredentials
        {
            get => GetPropertyValue(nameof(RDGatewayUseConnectionCredentials), _rdGatewayUseConnectionCredentials);
            set => SetField(ref _rdGatewayUseConnectionCredentials, value);
        }

        public string RDGatewayUsername
        {
            get => GetPropertyValue(nameof(RDGatewayUsername), _rdGatewayUsername)?.Trim() ?? "";
            set => SetField(ref _rdGatewayUsername, value?.Trim() ?? "");
        }

        public string RDGatewayPassword
        {
            get => GetPropertyValue(nameof(RDGatewayPassword), _rdGatewayPassword);
            set => SetField(ref _rdGatewayPassword, value);
        }

        public string RDGatewayAccessToken
        {
            get => GetPropertyValue(nameof(RDGatewayAccessToken), _rdGatewayAccessToken);
            set => SetField(ref _rdGatewayAccessToken, value);
        }

        public string RDGatewayDomain
        {
            get => GetPropertyValue(nameof(RDGatewayDomain), _rdGatewayDomain)?.Trim() ?? "";
            set => SetField(ref _rdGatewayDomain, value?.Trim() ?? "");
        }

        public ExternalCredentialProvider RDGatewayExternalCredentialProvider
        {
            get => GetPropertyValue(nameof(RDGatewayExternalCredentialProvider), _rdGatewayExternalCredentialProvider);
            set => SetField(ref _rdGatewayExternalCredentialProvider, value);
        }

        public virtual string RDGatewayUserViaAPI
        {
            get => GetPropertyValue(nameof(RDGatewayUserViaAPI), _rdGatewayUserViaAPI);
            set => SetField(ref _rdGatewayUserViaAPI, value);
        }

        // Appearance
        public RDPResolutions Resolution
        {
            get => GetPropertyValue(nameof(Resolution), _resolution);
            set => SetField(ref _resolution, value);
        }

        public bool AutomaticResize
        {
            get => GetPropertyValue(nameof(AutomaticResize), _automaticResize);
            set => SetField(ref _automaticResize, value);
        }

        public RDPColors Colors
        {
            get => GetPropertyValue(nameof(Colors), _colors);
            set => SetField(ref _colors, value);
        }

        public bool CacheBitmaps
        {
            get => GetPropertyValue(nameof(CacheBitmaps), _cacheBitmaps);
            set => SetField(ref _cacheBitmaps, value);
        }

        public bool DisplayWallpaper
        {
            get => GetPropertyValue(nameof(DisplayWallpaper), _displayWallpaper);
            set => SetField(ref _displayWallpaper, value);
        }

        public bool DisplayThemes
        {
            get => GetPropertyValue(nameof(DisplayThemes), _displayThemes);
            set => SetField(ref _displayThemes, value);
        }

        public bool EnableFontSmoothing
        {
            get => GetPropertyValue(nameof(EnableFontSmoothing), _enableFontSmoothing);
            set => SetField(ref _enableFontSmoothing, value);
        }

        public bool EnableDesktopComposition
        {
            get => GetPropertyValue(nameof(EnableDesktopComposition), _enableDesktopComposition);
            set => SetField(ref _enableDesktopComposition, value);
        }

        public bool DisableFullWindowDrag
        {
            get => GetPropertyValue(nameof(DisableFullWindowDrag), _disableFullWindowDrag);
            set => SetField(ref _disableFullWindowDrag, value);
        }

        public bool DisableMenuAnimations
        {
            get => GetPropertyValue(nameof(DisableMenuAnimations), _disableMenuAnimations);
            set => SetField(ref _disableMenuAnimations, value);
        }

        public bool DisableCursorShadow
        {
            get => GetPropertyValue(nameof(DisableCursorShadow), _disableCursorShadow);
            set => SetField(ref _disableCursorShadow, value);
        }

        public bool DisableCursorBlinking
        {
            get => GetPropertyValue(nameof(DisableCursorBlinking), _disableCursorBlinking);
            set => SetField(ref _disableCursorBlinking, value);
        }

        // Redirect
        public bool RedirectKeys
        {
            get => GetPropertyValue(nameof(RedirectKeys), _redirectKeys);
            set => SetField(ref _redirectKeys, value);
        }

        public RDPDiskDrives RedirectDiskDrives
        {
            get => GetPropertyValue(nameof(RedirectDiskDrives), _redirectDiskDrives);
            set => SetField(ref _redirectDiskDrives, value);
        }

        public string RedirectDiskDrivesCustom
        {
            get => GetPropertyValue(nameof(RedirectDiskDrivesCustom), _redirectDiskDrivesCustom);
            set => SetField(ref _redirectDiskDrivesCustom, value);
        }

        public bool RedirectPrinters
        {
            get => GetPropertyValue(nameof(RedirectPrinters), _redirectPrinters);
            set => SetField(ref _redirectPrinters, value);
        }

        public bool RedirectClipboard
        {
            get => GetPropertyValue(nameof(RedirectClipboard), _redirectClipboard);
            set => SetField(ref _redirectClipboard, value);
        }

        public bool RedirectPorts
        {
            get => GetPropertyValue(nameof(RedirectPorts), _redirectPorts);
            set => SetField(ref _redirectPorts, value);
        }

        public bool RedirectSmartCards
        {
            get => GetPropertyValue(nameof(RedirectSmartCards), _redirectSmartCards);
            set => SetField(ref _redirectSmartCards, value);
        }

        public RDPSounds RedirectSound
        {
            get => GetPropertyValue(nameof(RedirectSound), _redirectSound);
            set => SetField(ref _redirectSound, value);
        }

        public RDPSoundQuality SoundQuality
        {
            get => GetPropertyValue(nameof(SoundQuality), _soundQuality);
            set => SetField(ref _soundQuality, value);
        }

        public bool RedirectAudioCapture
        {
            get => GetPropertyValue(nameof(RedirectAudioCapture), _redirectAudioCapture);
            set => SetField(ref _redirectAudioCapture, value);
        }

        // Misc
        public virtual string PreExtApp
        {
            get => GetPropertyValue(nameof(PreExtApp), _preExtApp);
            set => SetField(ref _preExtApp, value);
        }

        public virtual string PostExtApp
        {
            get => GetPropertyValue(nameof(PostExtApp), _postExtApp);
            set => SetField(ref _postExtApp, value);
        }

        public virtual string MacAddress
        {
            get => GetPropertyValue(nameof(MacAddress), _macAddress);
            set => SetField(ref _macAddress, value);
        }

        public virtual string UserField
        {
            get => GetPropertyValue(nameof(UserField), _userField);
            set => SetField(ref _userField, value);
        }

        public virtual string EnvironmentTags
        {
            get => GetPropertyValue(nameof(EnvironmentTags), _environmentTags);
            set => SetField(ref _environmentTags, value);
        }

        public virtual bool Favorite
        {
            get => GetPropertyValue(nameof(Favorite), _favorite);
            set => SetField(ref _favorite, value);
        }

        public virtual string RDPStartProgram
        {
            get => GetPropertyValue(nameof(RDPStartProgram), _rdpStartProgram);
            set => SetField(ref _rdpStartProgram, value);
        }

        public virtual string RDPStartProgramWorkDir
        {
            get => GetPropertyValue(nameof(RDPStartProgramWorkDir), _rdpStartProgramWorkDir);
            set => SetField(ref _rdpStartProgramWorkDir, value);
        }

        // VNC
        public VncCompression VNCCompression
        {
            get => GetPropertyValue(nameof(VNCCompression), _vncCompression);
            set => SetField(ref _vncCompression, value);
        }

        public VncEncoding VNCEncoding
        {
            get => GetPropertyValue(nameof(VNCEncoding), _vncEncoding);
            set => SetField(ref _vncEncoding, value);
        }

        public VncAuthMode VNCAuthMode
        {
            get => GetPropertyValue(nameof(VNCAuthMode), _vncAuthMode);
            set => SetField(ref _vncAuthMode, value);
        }

        public VncProxyType VNCProxyType
        {
            get => GetPropertyValue(nameof(VNCProxyType), _vncProxyType);
            set => SetField(ref _vncProxyType, value);
        }

        public string VNCProxyIP
        {
            get => GetPropertyValue(nameof(VNCProxyIP), _vncProxyIp);
            set => SetField(ref _vncProxyIp, value);
        }

        public int VNCProxyPort
        {
            get => GetPropertyValue(nameof(VNCProxyPort), _vncProxyPort);
            set => SetField(ref _vncProxyPort, value);
        }

        public string VNCProxyUsername
        {
            get => GetPropertyValue(nameof(VNCProxyUsername), _vncProxyUsername);
            set => SetField(ref _vncProxyUsername, value);
        }

        public string VNCProxyPassword
        {
            get => GetPropertyValue(nameof(VNCProxyPassword), _vncProxyPassword);
            set => SetField(ref _vncProxyPassword, value);
        }

        public VncColors VNCColors
        {
            get => GetPropertyValue(nameof(VNCColors), _vncColors);
            set => SetField(ref _vncColors, value);
        }

        public VncSmartSizeMode VNCSmartSizeMode
        {
            get => GetPropertyValue(nameof(VNCSmartSizeMode), _vncSmartSizeMode);
            set => SetField(ref _vncSmartSizeMode, value);
        }

        public bool VNCViewOnly
        {
            get => GetPropertyValue(nameof(VNCViewOnly), _vncViewOnly);
            set => SetField(ref _vncViewOnly, value);
        }

        // Non-browsable
        public ConnectionInfoInheritance Inheritance
        {
            get => _inheritance;
            set => _inheritance = _inheritance?.Parent != this
                ? _inheritance?.Clone(this) ?? value
                : value;
        }

        public virtual bool IsContainer { get; set; }

        public bool IsDefault { get; set; }

        public ContainerInfo? Parent { get; internal set; }

        public bool IsQuickConnect { get; set; }

        public bool PleaseConnect { get; set; }

        #endregion

        #region Constructors

        public ConnectionInfo()
            : this(Guid.NewGuid().ToString())
        {
        }

        public ConnectionInfo(string uniqueId)
        {
            ConstantID = uniqueId ?? throw new ArgumentNullException(nameof(uniqueId));
            _inheritance = new ConnectionInfoInheritance(this);
            SetDefaultPort();
        }

        #endregion

        #region Public Methods

        public virtual ConnectionInfo Clone()
        {
            ConnectionInfo newConnectionInfo = new();
            newConnectionInfo.CopyFrom(this);
            return newConnectionInfo;
        }

        public void CopyFrom(ConnectionInfo source)
        {
            var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite);

            foreach (var property in properties)
            {
                if (property.Name is nameof(Parent) or nameof(ConstantID))
                    continue;
                try
                {
                    object? value = property.GetValue(source);
                    property.SetValue(this, value);
                }
                catch
                {
                    // Skip properties that can't be copied
                }
            }

            Inheritance = source.Inheritance.Clone(this);
        }

        public virtual TreeNodeType GetTreeNodeType()
        {
            return TreeNodeType.Connection;
        }

        public int GetDefaultPort()
        {
            return GetDefaultPort(Protocol);
        }

        public void SetDefaultPort()
        {
            if (_port == 0)
                Port = GetDefaultPort();
        }

        public virtual void SetParent(ContainerInfo newParent)
        {
            RemoveParent();
            newParent?.AddChild(this);
        }

        public void RemoveParent()
        {
            Parent?.RemoveChild(this);
        }

        public ConnectionInfo GetRootParent()
        {
            return Parent != null ? Parent.GetRootParent() : this;
        }

        public virtual IEnumerable<PropertyInfo> GetSerializableProperties()
        {
            string[] excluded =
            {
                "Parent", "Name", "Hostname", "Port", "Inheritance",
                "IsContainer", "IsDefault", "ConstantID", "IsQuickConnect", "PleaseConnect"
            };
            return typeof(ConnectionInfo).GetProperties()
                .Where(p => !excluded.Contains(p.Name));
        }

        #endregion

        #region Enumerations

        [Flags]
        public enum Force
        {
            None = 0,
            UseConsoleSession = 1,
            Fullscreen = 2,
            DoNotJump = 4,
            OverridePanel = 8,
            DontUseConsoleSession = 16,
            NoCredentials = 32,
            ViewOnly = 64
        }

        #endregion

        #region Property Inheritance

        protected virtual TPropertyType GetPropertyValue<TPropertyType>(string propertyName, TPropertyType value)
        {
            if (!ShouldThisPropertyBeInherited(propertyName))
                return value;

            if (TryGetInheritedPropertyValue<TPropertyType>(propertyName, out var inheritedValue))
                return inheritedValue;

            return value;
        }

        private bool ShouldThisPropertyBeInherited(string propertyName)
        {
            return Inheritance != null &&
                   Inheritance.InheritanceActive &&
                   Parent != null &&
                   IsInheritanceTurnedOnForThisProperty(propertyName);
        }

        private bool IsInheritanceTurnedOnForThisProperty(string propertyName)
        {
            var inheritProperty = typeof(ConnectionInfoInheritance).GetProperty(propertyName);
            return inheritProperty != null && Convert.ToBoolean(inheritProperty.GetValue(Inheritance));
        }

        private bool TryGetInheritedPropertyValue<TPropertyType>(string propertyName, out TPropertyType inheritedValue)
        {
            try
            {
                var parentProperty = Parent?.GetType().GetProperty(propertyName);
                if (parentProperty == null)
                {
                    inheritedValue = default!;
                    return false;
                }

                inheritedValue = (TPropertyType)parentProperty.GetValue(Parent)!;
                return true;
            }
            catch
            {
                inheritedValue = default!;
                return false;
            }
        }

        #endregion

        #region Default Ports

        private static readonly Dictionary<ProtocolType, int> DefaultPorts = new()
        {
            { ProtocolType.RDP, 3389 },
            { ProtocolType.VNC, 5900 },
            { ProtocolType.SSH1, 22 },
            { ProtocolType.SSH2, 22 },
            { ProtocolType.Telnet, 23 },
            { ProtocolType.Rlogin, 513 },
            { ProtocolType.RAW, 23 },
            { ProtocolType.HTTP, 80 },
            { ProtocolType.HTTPS, 443 },
            { ProtocolType.PowerShell, 5985 },
            { ProtocolType.ARD, 5900 },
            { ProtocolType.Terminal, 0 },
            { ProtocolType.WSL, 0 },
            { ProtocolType.AnyDesk, 0 },
            { ProtocolType.IntApp, 0 },
        };

        public static int GetDefaultPort(ProtocolType protocol)
        {
            return DefaultPorts.TryGetValue(protocol, out int port) ? port : 0;
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void RaisePropertyChangedEvent(object? sender, PropertyChangedEventArgs args)
        {
            PropertyChanged?.Invoke(sender, args);
        }

        protected void SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            RaisePropertyChangedEvent(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
