using System.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.Http;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Xml
{
    /// <summary>
    /// Deserializes mRemoteNG XML connection files (versions 2.5–2.8) into a ConnectionTreeModel.
    /// Backward-compatible with all known file format versions.
    /// </summary>
    public class XmlConnectionsDeserializer : IDeserializer<string, ConnectionTreeModel>
    {
        private readonly ICryptographyProvider _cryptoProvider;
        private readonly string _decryptionKey;

        public XmlConnectionsDeserializer(
            ICryptographyProvider cryptoProvider,
            string decryptionKey)
        {
            _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
            _decryptionKey = decryptionKey ?? "";
        }

        public ConnectionTreeModel Deserialize(string xml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var rootElement = doc.DocumentElement
                ?? throw new InvalidOperationException("XML document has no root element.");

            var confVersion = rootElement.GetAttribute("ConfVersion");
            var fullFileEncryption = bool.TryParse(rootElement.GetAttribute("FullFileEncryption"), out var ffe) && ffe;

            if (fullFileEncryption)
            {
                var decrypted = _cryptoProvider.Decrypt(rootElement.InnerText, _decryptionKey);
                rootElement.InnerXml = decrypted;
            }

            var rootNodeInfo = new RootNodeInfo(RootNodeType.Connection)
            {
                Name = rootElement.GetAttribute("Name")
            };

            DeserializeChildren(rootElement, rootNodeInfo, confVersion);

            return new ConnectionTreeModel(rootNodeInfo);
        }

        private void DeserializeChildren(XmlElement parentElement, ContainerInfo parentContainer, string confVersion)
        {
            foreach (XmlNode childNode in parentElement.ChildNodes)
            {
                if (childNode is not XmlElement childElement || childElement.Name != "Node")
                    continue;

                var nodeType = childElement.GetAttribute("Type");

                if (nodeType.Equals("Container", StringComparison.OrdinalIgnoreCase))
                {
                    var container = DeserializeContainer(childElement, confVersion);
                    parentContainer.AddChild(container);
                    DeserializeChildren(childElement, container, confVersion);
                }
                else
                {
                    var connection = DeserializeConnection(childElement, confVersion);
                    parentContainer.AddChild(connection);
                }
            }
        }

        private ContainerInfo DeserializeContainer(XmlElement element, string confVersion)
        {
            var id = GetAttr(element, "Id", Guid.NewGuid().ToString());
            var container = new ContainerInfo(id);
            PopulateConnectionInfo(container, element, confVersion);
            return container;
        }

        private ConnectionInfo DeserializeConnection(XmlElement element, string confVersion)
        {
            var id = GetAttr(element, "Id", Guid.NewGuid().ToString());
            var connection = new ConnectionInfo(id);
            PopulateConnectionInfo(connection, element, confVersion);
            return connection;
        }

        private void PopulateConnectionInfo(ConnectionInfo node, XmlElement e, string confVersion)
        {
            node.Name = GetAttr(e, "Name", "");
            node.Description = GetAttr(e, "Descr", "");
            node.Icon = GetAttr(e, "Icon", "mRemoteNG");
            node.Panel = GetAttr(e, "Panel", "General");
            node.TabColor = GetAttr(e, "TabColor", "");
            node.ConnectionFrameColor = GetEnum(e, "ConnectionFrameColor", ConnectionFrameColor.None);

            // Connection
            node.Hostname = GetAttr(e, "Hostname", "");
            node.Protocol = GetEnum(e, "Protocol", ProtocolType.RDP);
            node.Port = GetInt(e, "Port", ConnectionInfo.GetDefaultPort(node.Protocol));
            node.PuttySession = GetAttr(e, "PuttySession", "Default Settings");
            node.SSHTunnelConnectionName = GetAttr(e, "SSHTunnelConnectionName", "");
            node.OpeningCommand = GetAttr(e, "OpeningCommand", "");
            node.SSHOptions = GetAttr(e, "SSHOptions", "");

            // Credentials
            node.Username = GetAttr(e, "Username", "");
            node.Domain = GetAttr(e, "Domain", "");
            node.Password = DecryptAttribute(e, "Password");

            // External Credentials
            node.ExternalCredentialProvider = GetEnum(e, "ExternalCredentialProvider", ExternalCredentialProvider.None);
            node.UserViaAPI = GetAttr(e, "UserViaAPI", "");
            node.ExternalAddressProvider = GetEnum(e, "ExternalAddressProvider", ExternalAddressProvider.None);
            node.EC2InstanceId = GetAttr(e, "EC2InstanceId", "");
            node.EC2Region = GetAttr(e, "EC2Region", "");

            // Vault
            node.VaultOpenbaoMount = GetAttr(e, "VaultOpenbaoMount", "");
            node.VaultOpenbaoRole = GetAttr(e, "VaultOpenbaoRole", "");
            node.VaultOpenbaoSecretEngine = GetEnum(e, "VaultOpenbaoSecretEngine", VaultOpenbaoSecretEngine.Kv);

            // VM
            node.VmId = GetAttr(e, "VmId", "");
            node.UseVmId = GetBool(e, "UseVmId", false);
            node.UseEnhancedMode = GetBool(e, "UseEnhancedMode", false);

            // RDP
            node.RdpVersion = GetEnum(e, "RdpVersion", RdpVersion.Highest);
            node.Resolution = GetEnum(e, "Resolution", RDPResolutions.FitToWindow);
            node.AutomaticResize = GetBool(e, "AutomaticResize", true);
            node.Colors = GetEnum(e, "Colors", RDPColors.Colors24Bit);
            node.DisplayWallpaper = GetBool(e, "DisplayWallpaper", false);
            node.DisplayThemes = GetBool(e, "DisplayThemes", false);
            node.CacheBitmaps = GetBool(e, "CacheBitmaps", true);
            node.EnableFontSmoothing = GetBool(e, "EnableFontSmoothing", false);
            node.EnableDesktopComposition = GetBool(e, "EnableDesktopComposition", false);
            node.DisableFullWindowDrag = GetBool(e, "DisableFullWindowDrag", false);
            node.DisableMenuAnimations = GetBool(e, "DisableMenuAnimations", false);
            node.DisableCursorShadow = GetBool(e, "DisableCursorShadow", false);
            node.DisableCursorBlinking = GetBool(e, "DisableCursorBlinking", false);

            // RDP Redirects
            node.RedirectKeys = GetBool(e, "RedirectKeys", false);
            node.RedirectDiskDrives = GetEnum(e, "RedirectDiskDrives", RDPDiskDrives.None);
            node.RedirectDiskDrivesCustom = GetAttr(e, "RedirectDiskDrivesCustom", "");
            node.RedirectPorts = GetBool(e, "RedirectPorts", false);
            node.RedirectPrinters = GetBool(e, "RedirectPrinters", false);
            node.RedirectClipboard = GetBool(e, "RedirectClipboard", true);
            node.RedirectSmartCards = GetBool(e, "RedirectSmartCards", false);
            node.RedirectSound = GetEnum(e, "RedirectSound", RDPSounds.DoNotPlay);
            node.SoundQuality = GetEnum(e, "SoundQuality", RDPSoundQuality.Dynamic);
            node.RedirectAudioCapture = GetBool(e, "RedirectAudioCapture", false);

            // RDP Auth
            node.UseConsoleSession = GetBool(e, "UseConsoleSession", false);
            node.UseCredSsp = GetBool(e, "UseCredSsp", true);
            node.UseRestrictedAdmin = GetBool(e, "UseRestrictedAdmin", false);
            node.UseRCG = GetBool(e, "UseRCG", false);
            node.RDPAuthenticationLevel = GetEnum(e, "RDPAuthenticationLevel", AuthenticationLevel.NoAuth);
            node.RDPMinutesToIdleTimeout = GetInt(e, "RDPMinutesToIdleTimeout", 0);
            node.RDPAlertIdleTimeout = GetBool(e, "RDPAlertIdleTimeout", false);
            node.LoadBalanceInfo = GetAttr(e, "LoadBalanceInfo", "");
            node.RenderingEngine = GetEnum(e, "RenderingEngine", RenderingEngine.EdgeChromium);

            // RDP Start Program
            node.RDPStartProgram = GetAttr(e, "StartProgram", "");
            node.RDPStartProgramWorkDir = GetAttr(e, "StartProgramWorkDir", "");

            // RD Gateway
            node.RDGatewayUsageMethod = GetEnum(e, "RDGatewayUsageMethod", RDGatewayUsageMethod.Never);
            node.RDGatewayHostname = GetAttr(e, "RDGatewayHostname", "");
            node.RDGatewayUseConnectionCredentials = GetEnum(e, "RDGatewayUseConnectionCredentials", RDGatewayUseConnectionCredentials.Yes);
            node.RDGatewayUsername = GetAttr(e, "RDGatewayUsername", "");
            node.RDGatewayPassword = DecryptAttribute(e, "RDGatewayPassword");
            node.RDGatewayDomain = GetAttr(e, "RDGatewayDomain", "");
            node.RDGatewayExternalCredentialProvider = GetEnum(e, "RDGatewayExternalCredentialProvider", ExternalCredentialProvider.None);
            node.RDGatewayUserViaAPI = GetAttr(e, "RDGatewayUserViaAPI", "");
            node.RDGatewayAccessToken = GetAttr(e, "RDGatewayAccessToken", "");

            // VNC
            node.VNCCompression = GetEnum(e, "VNCCompression", VncCompression.CompNone);
            node.VNCEncoding = GetEnum(e, "VNCEncoding", VncEncoding.EncTight);
            node.VNCAuthMode = GetEnum(e, "VNCAuthMode", VncAuthMode.AuthVNC);
            node.VNCProxyType = GetEnum(e, "VNCProxyType", VncProxyType.ProxyNone);
            node.VNCProxyIP = GetAttr(e, "VNCProxyIP", "");
            node.VNCProxyPort = GetInt(e, "VNCProxyPort", 0);
            node.VNCProxyUsername = GetAttr(e, "VNCProxyUsername", "");
            node.VNCProxyPassword = DecryptAttribute(e, "VNCProxyPassword");
            node.VNCColors = GetEnum(e, "VNCColors", VncColors.ColNormal);
            node.VNCSmartSizeMode = GetEnum(e, "VNCSmartSizeMode", VncSmartSizeMode.SmartSAspect);
            node.VNCViewOnly = GetBool(e, "VNCViewOnly", false);

            // Misc
            node.ExtApp = GetAttr(e, "ExtApp", "");
            node.PreExtApp = GetAttr(e, "PreExtApp", "");
            node.PostExtApp = GetAttr(e, "PostExtApp", "");
            node.MacAddress = GetAttr(e, "MacAddress", "");
            node.UserField = GetAttr(e, "UserField", "");
            node.Favorite = GetBool(e, "Favorite", false);
            node.EnvironmentTags = GetAttr(e, "EnvironmentTags", "");

            // Inheritance
            DeserializeInheritance(e, node.Inheritance);
        }

        private static void DeserializeInheritance(XmlElement e, ConnectionInfoInheritance inh)
        {
            inh.CacheBitmaps = GetBool(e, "InheritCacheBitmaps", false);
            inh.Colors = GetBool(e, "InheritColors", false);
            inh.Description = GetBool(e, "InheritDescription", false);
            inh.DisplayThemes = GetBool(e, "InheritDisplayThemes", false);
            inh.DisplayWallpaper = GetBool(e, "InheritDisplayWallpaper", false);
            inh.EnableFontSmoothing = GetBool(e, "InheritEnableFontSmoothing", false);
            inh.EnableDesktopComposition = GetBool(e, "InheritEnableDesktopComposition", false);
            inh.DisableFullWindowDrag = GetBool(e, "InheritDisableFullWindowDrag", false);
            inh.DisableMenuAnimations = GetBool(e, "InheritDisableMenuAnimations", false);
            inh.DisableCursorShadow = GetBool(e, "InheritDisableCursorShadow", false);
            inh.DisableCursorBlinking = GetBool(e, "InheritDisableCursorBlinking", false);
            inh.Domain = GetBool(e, "InheritDomain", false);
            inh.Icon = GetBool(e, "InheritIcon", false);
            inh.Panel = GetBool(e, "InheritPanel", false);
            inh.TabColor = GetBool(e, "InheritTabColor", false);
            inh.ConnectionFrameColor = GetBool(e, "InheritConnectionFrameColor", false);
            inh.Password = GetBool(e, "InheritPassword", false);
            inh.Port = GetBool(e, "InheritPort", false);
            inh.Protocol = GetBool(e, "InheritProtocol", false);
            inh.RdpVersion = GetBool(e, "InheritRdpVersion", false);
            inh.Username = GetBool(e, "InheritUsername", false);
            inh.Resolution = GetBool(e, "InheritResolution", false);
            inh.AutomaticResize = GetBool(e, "InheritAutomaticResize", false);
            inh.RedirectKeys = GetBool(e, "InheritRedirectKeys", false);
            inh.RedirectDiskDrives = GetBool(e, "InheritRedirectDiskDrives", false);
            inh.RedirectDiskDrivesCustom = GetBool(e, "InheritRedirectDiskDrivesCustom", false);
            inh.RedirectPorts = GetBool(e, "InheritRedirectPorts", false);
            inh.RedirectPrinters = GetBool(e, "InheritRedirectPrinters", false);
            inh.RedirectClipboard = GetBool(e, "InheritRedirectClipboard", false);
            inh.RedirectSmartCards = GetBool(e, "InheritRedirectSmartCards", false);
            inh.RedirectSound = GetBool(e, "InheritRedirectSound", false);
            inh.SoundQuality = GetBool(e, "InheritSoundQuality", false);
            inh.RedirectAudioCapture = GetBool(e, "InheritRedirectAudioCapture", false);
            inh.UseConsoleSession = GetBool(e, "InheritUseConsoleSession", false);
            inh.UseCredSsp = GetBool(e, "InheritUseCredSsp", false);
            inh.UseRestrictedAdmin = GetBool(e, "InheritUseRestrictedAdmin", false);
            inh.UseRCG = GetBool(e, "InheritUseRCG", false);
            inh.RenderingEngine = GetBool(e, "InheritRenderingEngine", false);
            inh.RDPAuthenticationLevel = GetBool(e, "InheritRDPAuthenticationLevel", false);
            inh.RDPMinutesToIdleTimeout = GetBool(e, "InheritRDPMinutesToIdleTimeout", false);
            inh.RDPAlertIdleTimeout = GetBool(e, "InheritRDPAlertIdleTimeout", false);
            inh.LoadBalanceInfo = GetBool(e, "InheritLoadBalanceInfo", false);
            inh.SSHTunnelConnectionName = GetBool(e, "InheritSSHTunnelConnectionName", false);
            inh.OpeningCommand = GetBool(e, "InheritOpeningCommand", false);
            inh.SSHOptions = GetBool(e, "InheritSSHOptions", false);
            inh.PuttySession = GetBool(e, "InheritPuttySession", false);
            inh.PreExtApp = GetBool(e, "InheritPreExtApp", false);
            inh.PostExtApp = GetBool(e, "InheritPostExtApp", false);
            inh.MacAddress = GetBool(e, "InheritMacAddress", false);
            inh.UserField = GetBool(e, "InheritUserField", false);
            inh.ExtApp = GetBool(e, "InheritExtApp", false);
            inh.Favorite = GetBool(e, "InheritFavorite", false);
            inh.EnvironmentTags = GetBool(e, "InheritEnvironmentTags", false);
            inh.VNCCompression = GetBool(e, "InheritVNCCompression", false);
            inh.VNCEncoding = GetBool(e, "InheritVNCEncoding", false);
            inh.VNCAuthMode = GetBool(e, "InheritVNCAuthMode", false);
            inh.VNCProxyType = GetBool(e, "InheritVNCProxyType", false);
            inh.VNCProxyIP = GetBool(e, "InheritVNCProxyIP", false);
            inh.VNCProxyPort = GetBool(e, "InheritVNCProxyPort", false);
            inh.VNCProxyUsername = GetBool(e, "InheritVNCProxyUsername", false);
            inh.VNCProxyPassword = GetBool(e, "InheritVNCProxyPassword", false);
            inh.VNCColors = GetBool(e, "InheritVNCColors", false);
            inh.VNCSmartSizeMode = GetBool(e, "InheritVNCSmartSizeMode", false);
            inh.VNCViewOnly = GetBool(e, "InheritVNCViewOnly", false);
            inh.RDGatewayUsageMethod = GetBool(e, "InheritRDGatewayUsageMethod", false);
            inh.RDGatewayHostname = GetBool(e, "InheritRDGatewayHostname", false);
            inh.RDGatewayUseConnectionCredentials = GetBool(e, "InheritRDGatewayUseConnectionCredentials", false);
            inh.RDGatewayUsername = GetBool(e, "InheritRDGatewayUsername", false);
            inh.RDGatewayPassword = GetBool(e, "InheritRDGatewayPassword", false);
            inh.RDGatewayDomain = GetBool(e, "InheritRDGatewayDomain", false);
            inh.RDGatewayExternalCredentialProvider = GetBool(e, "InheritRDGatewayExternalCredentialProvider", false);
            inh.RDGatewayUserViaAPI = GetBool(e, "InheritRDGatewayUserViaAPI", false);
            inh.VmId = GetBool(e, "InheritVmId", false);
            inh.UseVmId = GetBool(e, "InheritUseVmId", false);
            inh.UseEnhancedMode = GetBool(e, "InheritUseEnhancedMode", false);
            inh.ExternalCredentialProvider = GetBool(e, "InheritExternalCredentialProvider", false);
            inh.UserViaAPI = GetBool(e, "InheritUserViaAPI", false);
            inh.RDPStartProgram = GetBool(e, "InheritStartProgram", false);
            inh.RDPStartProgramWorkDir = GetBool(e, "InheritStartProgramWorkDir", false);
        }

        private string DecryptAttribute(XmlElement element, string attributeName)
        {
            var value = element.GetAttribute(attributeName);
            if (string.IsNullOrEmpty(value)) return "";

            try
            {
                return _cryptoProvider.Decrypt(value, _decryptionKey);
            }
            catch
            {
                return "";
            }
        }

        private static string GetAttr(XmlElement e, string name, string defaultValue)
        {
            var val = e.GetAttribute(name);
            return string.IsNullOrEmpty(val) ? defaultValue : val;
        }

        private static bool GetBool(XmlElement e, string name, bool defaultValue)
        {
            var val = e.GetAttribute(name);
            return string.IsNullOrEmpty(val) ? defaultValue : bool.TryParse(val, out var result) ? result : defaultValue;
        }

        private static int GetInt(XmlElement e, string name, int defaultValue)
        {
            var val = e.GetAttribute(name);
            return string.IsNullOrEmpty(val) ? defaultValue : int.TryParse(val, out var result) ? result : defaultValue;
        }

        private static T GetEnum<T>(XmlElement e, string name, T defaultValue) where T : struct, Enum
        {
            var val = e.GetAttribute(name);
            return string.IsNullOrEmpty(val) ? defaultValue : Enum.TryParse<T>(val, ignoreCase: true, out var result) ? result : defaultValue;
        }
    }
}
