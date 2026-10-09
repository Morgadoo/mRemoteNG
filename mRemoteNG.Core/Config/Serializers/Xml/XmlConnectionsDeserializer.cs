using System.Globalization;
using System.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.Http;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Xml
{
    /// <summary>
    /// Deserializes mRemoteNG XML connection files (versions up to 2.8) into a ConnectionTreeModel.
    /// Mirrors the legacy WinForms loader so existing files, passwords and master passwords keep working.
    /// </summary>
    public class XmlConnectionsDeserializer : IDeserializer<string, ConnectionTreeModel>
    {
        public const double MaxSupportedConfVersion = 2.8;
        private const string NotProtectedMarker = "ThisIsNotProtected";
        private const string ProtectedMarker = "ThisIsProtected";

        private readonly ICryptoProviderFactory _cryptoProviderFactory;
        private readonly string? _password;
        private ICryptographyProvider _cryptoProvider = null!;
        private string _decryptionKey = "";
        private bool _wasPre26FullFileEncrypted;

        /// <summary>The encryption settings found in the last file deserialized.</summary>
        public ConnectionFileEncryption Encryption { get; private set; } = new();

        /// <param name="cryptoProviderFactory">Builds the cipher described by the file header.</param>
        /// <param name="password">
        /// The master password, or null to try the built-in default key. When the file needs a
        /// different password a <see cref="ConnectionFilePasswordException"/> is thrown.
        /// </param>
        public XmlConnectionsDeserializer(ICryptoProviderFactory cryptoProviderFactory, string? password = null)
        {
            _cryptoProviderFactory = cryptoProviderFactory ?? throw new ArgumentNullException(nameof(cryptoProviderFactory));
            _password = string.IsNullOrEmpty(password) ? null : password;
        }

        public ConnectionTreeModel Deserialize(string xml)
        {
            var defaultKey = new RootNodeInfo(RootNodeType.Connection).DefaultPassword;
            _wasPre26FullFileEncrypted = false;
            var doc = LoadDocument(DecryptPre26FullFile(xml, defaultKey));

            var rootElement = doc.DocumentElement
                ?? throw new InvalidOperationException("XML document has no root element.");

            var confVersion = ParseConfVersion(rootElement);
            if (confVersion > MaxSupportedConfVersion)
                throw new ConnectionFileVersionException(confVersion, MaxSupportedConfVersion);

            if (confVersion >= 2.6)
            {
                var iterations = GetInt(rootElement, "KdfIterations", 1000);
                Encryption = new ConnectionFileEncryption(
                    GetEnum(rootElement, "EncryptionEngine", BlockCipherEngines.AES),
                    GetEnum(rootElement, "BlockCipherMode", BlockCipherModes.GCM),
                    iterations < 1000 ? 1000 : iterations,
                    GetBool(rootElement, "FullFileEncryption", false));
                _cryptoProvider = _cryptoProviderFactory.Build(Encryption.Engine, Encryption.Mode, Encryption.KeyDerivationIterations);
            }
            else
            {
                // Saved back with the default AEAD cipher, keeping whole-file encryption if the user had it.
                Encryption = new ConnectionFileEncryption(FullFileEncryption: _wasPre26FullFileEncrypted);
                _cryptoProvider = _cryptoProviderFactory.BuildLegacy();
            }

            _decryptionKey = Authenticate(rootElement.GetAttribute("Protected"), defaultKey);

            // Pre-2.6 whole-file encryption was already undone before parsing.
            if (confVersion >= 2.6 && Encryption.FullFileEncryption)
                rootElement.InnerXml = _cryptoProvider.Decrypt(rootElement.InnerText, _decryptionKey);

            var rootNodeInfo = new RootNodeInfo(RootNodeType.Connection)
            {
                Name = GetAttr(rootElement, "Name", "Connections")
            };
            if (_decryptionKey != defaultKey)
                rootNodeInfo.PasswordString = _decryptionKey;

            DeserializeChildren(rootElement, rootNodeInfo, confVersion);

            return new ConnectionTreeModel(rootNodeInfo);
        }

        /// <summary>
        /// Files older than 2.6 with full-file encryption are a single base64 blob, not XML.
        /// </summary>
        private string DecryptPre26FullFile(string content, string defaultKey)
        {
            var trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (trimmed.StartsWith('<')) return content;

            var legacy = _cryptoProviderFactory.BuildLegacy();
            foreach (var key in CandidateKeys(defaultKey))
            {
                try
                {
                    var decrypted = legacy.Decrypt(trimmed, key);
                    if (decrypted.TrimStart('\uFEFF').StartsWith('<'))
                    {
                        _wasPre26FullFileEncrypted = true;
                        return decrypted;
                    }
                }
                catch (EncryptionException)
                {
                }
            }

            throw new ConnectionFilePasswordException(_password is not null);
        }

        /// <summary>
        /// Verifies the key against the file's "Protected" marker and returns the key to use.
        /// </summary>
        private string Authenticate(string protectedValue, string defaultKey)
        {
            if (string.IsNullOrEmpty(protectedValue))
                return _password ?? defaultKey;

            foreach (var key in CandidateKeys(defaultKey))
            {
                try
                {
                    var marker = _cryptoProvider.Decrypt(protectedValue, key);
                    if (marker is NotProtectedMarker or ProtectedMarker)
                        return key;
                }
                catch (EncryptionException)
                {
                }
            }

            throw new ConnectionFilePasswordException(_password is not null);
        }

        private IEnumerable<string> CandidateKeys(string defaultKey)
        {
            if (_password is not null)
                yield return _password;
            if (_password != defaultKey)
                yield return defaultKey;
        }

        private static XmlDocument LoadDocument(string xml)
        {
            // Connection files are untrusted input: never process DTDs or resolve external entities.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            var doc = new XmlDocument { XmlResolver = null };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            doc.Load(reader);
            return doc;
        }

        private static double ParseConfVersion(XmlElement root)
        {
            var value = root.GetAttribute("ConfVersion").Replace(",", ".");
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var version) ? version : 0;
        }

        private void DeserializeChildren(XmlElement parentElement, ContainerInfo parentContainer, double confVersion)
        {
            foreach (XmlNode childNode in parentElement.ChildNodes)
            {
                if (childNode is not XmlElement childElement || childElement.LocalName != "Node")
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

        private ContainerInfo DeserializeContainer(XmlElement element, double confVersion)
        {
            var id = GetAttr(element, "Id", Guid.NewGuid().ToString());
            var container = new ContainerInfo(id);
            PopulateConnectionInfo(container, element, confVersion);
            container.IsExpanded = GetBool(element, "Expanded", true);
            return container;
        }

        private ConnectionInfo DeserializeConnection(XmlElement element, double confVersion)
        {
            var id = GetAttr(element, "Id", Guid.NewGuid().ToString());
            var connection = new ConnectionInfo(id);
            PopulateConnectionInfo(connection, element, confVersion);
            return connection;
        }

        private void PopulateConnectionInfo(ConnectionInfo node, XmlElement e, double confVersion)
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
            node.RedirectDiskDrives = GetRedirectDiskDrives(e);
            node.RedirectDiskDrivesCustom = GetAttr(e, "RedirectDiskDrivesCustom", "");
            node.RedirectPorts = GetBool(e, "RedirectPorts", false);
            node.RedirectPrinters = GetBool(e, "RedirectPrinters", false);
            node.RedirectClipboard = GetBool(e, "RedirectClipboard", true);
            node.RedirectSmartCards = GetBool(e, "RedirectSmartCards", false);
            node.RedirectSound = GetEnum(e, "RedirectSound", RDPSounds.DoNotPlay);
            node.SoundQuality = GetEnum(e, "SoundQuality", RDPSoundQuality.Dynamic);
            node.RedirectAudioCapture = GetBool(e, "RedirectAudioCapture", false);

            // RDP Auth
            // Legacy name is ConnectToConsole; UseConsoleSession was written by early builds of this port.
            node.UseConsoleSession = GetBool(e, "ConnectToConsole", GetBool(e, "UseConsoleSession", false));
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
            // "Connected" marks sessions the legacy app reopens at startup; kept so a round trip
            // through this app doesn't change that behaviour.
            node.PleaseConnect = GetBool(e, "Connected", false);

            // Inheritance
            DeserializeInheritance(e, node.Inheritance);
        }

        /// <summary>
        /// Files before 2.8 stored a boolean ("True" = local drives); 2.8 stores the enum name.
        /// </summary>
        private static RDPDiskDrives GetRedirectDiskDrives(XmlElement e)
        {
            var value = e.GetAttribute("RedirectDiskDrives");
            if (bool.TryParse(value, out var redirect))
                return redirect ? RDPDiskDrives.Local : RDPDiskDrives.None;
            return GetEnum(e, "RedirectDiskDrives", RDPDiskDrives.None);
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
            catch (EncryptionException)
            {
                // The file key was verified above, so this is a single corrupt field.
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
