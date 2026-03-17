using System.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Xml
{
    /// <summary>
    /// Serializes a ConnectionTreeModel to mRemoteNG XML format (version 2.8).
    /// Compatible with the legacy WinForms app.
    /// </summary>
    public class XmlConnectionsSerializer : ISerializer<ConnectionTreeModel, string>
    {
        private readonly ICryptographyProvider _cryptoProvider;
        private readonly string _encryptionKey;
        private readonly SaveFilter _saveFilter;

        public XmlConnectionsSerializer(
            ICryptographyProvider cryptoProvider,
            string encryptionKey,
            SaveFilter? saveFilter = null)
        {
            _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
            _encryptionKey = encryptionKey ?? "";
            _saveFilter = saveFilter ?? new SaveFilter();
        }

        public string Serialize(ConnectionTreeModel model)
        {
            var doc = new XmlDocument();
            var declaration = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(declaration);

            var root = SerializeRootNode(doc, model.RootNode);
            doc.AppendChild(root);

            SerializeChildren(doc, root, model.RootNode);

            using var sw = new StringWriter();
            using var xw = XmlWriter.Create(sw, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                Encoding = System.Text.Encoding.UTF8
            });
            doc.WriteTo(xw);
            xw.Flush();
            return sw.ToString();
        }

        private XmlElement SerializeRootNode(XmlDocument doc, RootNodeInfo rootNode)
        {
            var element = doc.CreateElement("Connections");
            element.SetAttribute("Name", rootNode.Name);
            element.SetAttribute("Export", "false");
            element.SetAttribute("EncryptionEngine", "AES");
            element.SetAttribute("BlockCipherMode", "GCM");
            element.SetAttribute("KdfIterations", "1000");
            element.SetAttribute("FullFileEncryption", "false");

            var protectedString = _cryptoProvider.Encrypt("ThisIsProtected", _encryptionKey);
            element.SetAttribute("Protected", protectedString);
            element.SetAttribute("ConfVersion", "2.8");

            return element;
        }

        private void SerializeChildren(XmlDocument doc, XmlElement parentElement, ContainerInfo container)
        {
            foreach (var child in container.Children)
            {
                var element = SerializeNode(doc, child);
                parentElement.AppendChild(element);

                if (child is ContainerInfo childContainer)
                    SerializeChildren(doc, element, childContainer);
            }
        }

        private XmlElement SerializeNode(XmlDocument doc, ConnectionInfo node)
        {
            var element = doc.CreateElement("Node");
            var isContainer = node is ContainerInfo;

            element.SetAttribute("Name", node.Name);
            element.SetAttribute("Type", isContainer ? "Container" : "Connection");

            if (isContainer && node is ContainerInfo container)
                element.SetAttribute("Expanded", "true");

            element.SetAttribute("Descr", node.Description);
            element.SetAttribute("Icon", node.Icon);
            element.SetAttribute("Panel", node.Panel);
            element.SetAttribute("Id", node.ConstantID);
            element.SetAttribute("TabColor", node.TabColor);
            element.SetAttribute("ConnectionFrameColor", node.ConnectionFrameColor.ToString());

            // Connection
            element.SetAttribute("Hostname", node.Hostname);
            element.SetAttribute("Port", node.Port.ToString());
            element.SetAttribute("Protocol", node.Protocol.ToString());
            element.SetAttribute("PuttySession", node.PuttySession);
            element.SetAttribute("SSHTunnelConnectionName", node.SSHTunnelConnectionName);
            element.SetAttribute("OpeningCommand", node.OpeningCommand);
            element.SetAttribute("SSHOptions", node.SSHOptions);

            // Credentials
            element.SetAttribute("Username", _saveFilter.SaveUsername ? node.Username : "");
            element.SetAttribute("Domain", _saveFilter.SaveDomain ? node.Domain : "");
            element.SetAttribute("Password",
                _saveFilter.SavePassword
                    ? _cryptoProvider.Encrypt(node.Password, _encryptionKey)
                    : "");

            // External Credentials
            element.SetAttribute("ExternalCredentialProvider", node.ExternalCredentialProvider.ToString());
            element.SetAttribute("UserViaAPI", node.UserViaAPI);
            element.SetAttribute("ExternalAddressProvider", node.ExternalAddressProvider.ToString());
            element.SetAttribute("EC2InstanceId", node.EC2InstanceId);
            element.SetAttribute("EC2Region", node.EC2Region);

            // Vault
            element.SetAttribute("VaultOpenbaoMount", node.VaultOpenbaoMount);
            element.SetAttribute("VaultOpenbaoRole", node.VaultOpenbaoRole);
            element.SetAttribute("VaultOpenbaoSecretEngine", node.VaultOpenbaoSecretEngine.ToString());

            // VM
            element.SetAttribute("VmId", node.VmId);
            element.SetAttribute("UseVmId", node.UseVmId.ToString().ToLowerInvariant());
            element.SetAttribute("UseEnhancedMode", node.UseEnhancedMode.ToString().ToLowerInvariant());

            // RDP Display
            element.SetAttribute("RdpVersion", node.RdpVersion.ToString());
            element.SetAttribute("Resolution", node.Resolution.ToString());
            element.SetAttribute("AutomaticResize", node.AutomaticResize.ToString().ToLowerInvariant());
            element.SetAttribute("Colors", node.Colors.ToString());
            element.SetAttribute("DisplayWallpaper", node.DisplayWallpaper.ToString().ToLowerInvariant());
            element.SetAttribute("DisplayThemes", node.DisplayThemes.ToString().ToLowerInvariant());
            element.SetAttribute("CacheBitmaps", node.CacheBitmaps.ToString().ToLowerInvariant());
            element.SetAttribute("EnableFontSmoothing", node.EnableFontSmoothing.ToString().ToLowerInvariant());
            element.SetAttribute("EnableDesktopComposition", node.EnableDesktopComposition.ToString().ToLowerInvariant());
            element.SetAttribute("DisableFullWindowDrag", node.DisableFullWindowDrag.ToString().ToLowerInvariant());
            element.SetAttribute("DisableMenuAnimations", node.DisableMenuAnimations.ToString().ToLowerInvariant());
            element.SetAttribute("DisableCursorShadow", node.DisableCursorShadow.ToString().ToLowerInvariant());
            element.SetAttribute("DisableCursorBlinking", node.DisableCursorBlinking.ToString().ToLowerInvariant());

            // RDP Redirects
            element.SetAttribute("RedirectKeys", node.RedirectKeys.ToString().ToLowerInvariant());
            element.SetAttribute("RedirectDiskDrives", node.RedirectDiskDrives.ToString());
            element.SetAttribute("RedirectDiskDrivesCustom", node.RedirectDiskDrivesCustom);
            element.SetAttribute("RedirectPorts", node.RedirectPorts.ToString().ToLowerInvariant());
            element.SetAttribute("RedirectPrinters", node.RedirectPrinters.ToString().ToLowerInvariant());
            element.SetAttribute("RedirectClipboard", node.RedirectClipboard.ToString().ToLowerInvariant());
            element.SetAttribute("RedirectSmartCards", node.RedirectSmartCards.ToString().ToLowerInvariant());
            element.SetAttribute("RedirectSound", node.RedirectSound.ToString());
            element.SetAttribute("SoundQuality", node.SoundQuality.ToString());
            element.SetAttribute("RedirectAudioCapture", node.RedirectAudioCapture.ToString().ToLowerInvariant());

            // RDP Auth
            element.SetAttribute("UseConsoleSession", node.UseConsoleSession.ToString().ToLowerInvariant());
            element.SetAttribute("UseCredSsp", node.UseCredSsp.ToString().ToLowerInvariant());
            element.SetAttribute("UseRestrictedAdmin", node.UseRestrictedAdmin.ToString().ToLowerInvariant());
            element.SetAttribute("UseRCG", node.UseRCG.ToString().ToLowerInvariant());
            element.SetAttribute("RDPAuthenticationLevel", node.RDPAuthenticationLevel.ToString());
            element.SetAttribute("RDPMinutesToIdleTimeout", node.RDPMinutesToIdleTimeout.ToString());
            element.SetAttribute("RDPAlertIdleTimeout", node.RDPAlertIdleTimeout.ToString().ToLowerInvariant());
            element.SetAttribute("LoadBalanceInfo", node.LoadBalanceInfo);
            element.SetAttribute("RenderingEngine", node.RenderingEngine.ToString());

            // RDP Start Program
            element.SetAttribute("StartProgram", node.RDPStartProgram);
            element.SetAttribute("StartProgramWorkDir", node.RDPStartProgramWorkDir);

            // RD Gateway
            element.SetAttribute("RDGatewayUsageMethod", node.RDGatewayUsageMethod.ToString());
            element.SetAttribute("RDGatewayHostname", node.RDGatewayHostname);
            element.SetAttribute("RDGatewayUseConnectionCredentials", node.RDGatewayUseConnectionCredentials.ToString());
            element.SetAttribute("RDGatewayUsername", node.RDGatewayUsername);
            element.SetAttribute("RDGatewayPassword",
                _saveFilter.SavePassword
                    ? _cryptoProvider.Encrypt(node.RDGatewayPassword, _encryptionKey)
                    : "");
            element.SetAttribute("RDGatewayDomain", node.RDGatewayDomain);
            element.SetAttribute("RDGatewayExternalCredentialProvider", node.RDGatewayExternalCredentialProvider.ToString());
            element.SetAttribute("RDGatewayUserViaAPI", node.RDGatewayUserViaAPI);

            // VNC
            element.SetAttribute("VNCCompression", node.VNCCompression.ToString());
            element.SetAttribute("VNCEncoding", node.VNCEncoding.ToString());
            element.SetAttribute("VNCAuthMode", node.VNCAuthMode.ToString());
            element.SetAttribute("VNCProxyType", node.VNCProxyType.ToString());
            element.SetAttribute("VNCProxyIP", node.VNCProxyIP);
            element.SetAttribute("VNCProxyPort", node.VNCProxyPort.ToString());
            element.SetAttribute("VNCProxyUsername", node.VNCProxyUsername);
            element.SetAttribute("VNCProxyPassword",
                _saveFilter.SavePassword
                    ? _cryptoProvider.Encrypt(node.VNCProxyPassword, _encryptionKey)
                    : "");
            element.SetAttribute("VNCColors", node.VNCColors.ToString());
            element.SetAttribute("VNCSmartSizeMode", node.VNCSmartSizeMode.ToString());
            element.SetAttribute("VNCViewOnly", node.VNCViewOnly.ToString().ToLowerInvariant());

            // HTTP
            element.SetAttribute("RenderingEngine", node.RenderingEngine.ToString());

            // Misc
            element.SetAttribute("ExtApp", node.ExtApp);
            element.SetAttribute("PreExtApp", node.PreExtApp);
            element.SetAttribute("PostExtApp", node.PostExtApp);
            element.SetAttribute("MacAddress", node.MacAddress);
            element.SetAttribute("UserField", node.UserField);
            element.SetAttribute("Favorite", node.Favorite.ToString().ToLowerInvariant());
            element.SetAttribute("EnvironmentTags", node.EnvironmentTags);

            // Inheritance
            if (_saveFilter.SaveInheritance)
                SerializeInheritance(element, node.Inheritance);

            return element;
        }

        private static void SerializeInheritance(XmlElement element, ConnectionInfoInheritance inh)
        {
            element.SetAttribute("InheritCacheBitmaps", inh.CacheBitmaps.ToString().ToLowerInvariant());
            element.SetAttribute("InheritColors", inh.Colors.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDescription", inh.Description.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDisplayThemes", inh.DisplayThemes.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDisplayWallpaper", inh.DisplayWallpaper.ToString().ToLowerInvariant());
            element.SetAttribute("InheritEnableFontSmoothing", inh.EnableFontSmoothing.ToString().ToLowerInvariant());
            element.SetAttribute("InheritEnableDesktopComposition", inh.EnableDesktopComposition.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDisableFullWindowDrag", inh.DisableFullWindowDrag.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDisableMenuAnimations", inh.DisableMenuAnimations.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDisableCursorShadow", inh.DisableCursorShadow.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDisableCursorBlinking", inh.DisableCursorBlinking.ToString().ToLowerInvariant());
            element.SetAttribute("InheritDomain", inh.Domain.ToString().ToLowerInvariant());
            element.SetAttribute("InheritIcon", inh.Icon.ToString().ToLowerInvariant());
            element.SetAttribute("InheritPanel", inh.Panel.ToString().ToLowerInvariant());
            element.SetAttribute("InheritTabColor", inh.TabColor.ToString().ToLowerInvariant());
            element.SetAttribute("InheritConnectionFrameColor", inh.ConnectionFrameColor.ToString().ToLowerInvariant());
            element.SetAttribute("InheritPassword", inh.Password.ToString().ToLowerInvariant());
            element.SetAttribute("InheritPort", inh.Port.ToString().ToLowerInvariant());
            element.SetAttribute("InheritProtocol", inh.Protocol.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRdpVersion", inh.RdpVersion.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUsername", inh.Username.ToString().ToLowerInvariant());
            element.SetAttribute("InheritResolution", inh.Resolution.ToString().ToLowerInvariant());
            element.SetAttribute("InheritAutomaticResize", inh.AutomaticResize.ToString().ToLowerInvariant());
            element.SetAttribute("InheritColors", inh.Colors.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectKeys", inh.RedirectKeys.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectDiskDrives", inh.RedirectDiskDrives.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectDiskDrivesCustom", inh.RedirectDiskDrivesCustom.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectPorts", inh.RedirectPorts.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectPrinters", inh.RedirectPrinters.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectClipboard", inh.RedirectClipboard.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectSmartCards", inh.RedirectSmartCards.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectSound", inh.RedirectSound.ToString().ToLowerInvariant());
            element.SetAttribute("InheritSoundQuality", inh.SoundQuality.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRedirectAudioCapture", inh.RedirectAudioCapture.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUseConsoleSession", inh.UseConsoleSession.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUseCredSsp", inh.UseCredSsp.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUseRestrictedAdmin", inh.UseRestrictedAdmin.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUseRCG", inh.UseRCG.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRenderingEngine", inh.RenderingEngine.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDPAuthenticationLevel", inh.RDPAuthenticationLevel.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDPMinutesToIdleTimeout", inh.RDPMinutesToIdleTimeout.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDPAlertIdleTimeout", inh.RDPAlertIdleTimeout.ToString().ToLowerInvariant());
            element.SetAttribute("InheritLoadBalanceInfo", inh.LoadBalanceInfo.ToString().ToLowerInvariant());
            element.SetAttribute("InheritSSHTunnelConnectionName", inh.SSHTunnelConnectionName.ToString().ToLowerInvariant());
            element.SetAttribute("InheritOpeningCommand", inh.OpeningCommand.ToString().ToLowerInvariant());
            element.SetAttribute("InheritSSHOptions", inh.SSHOptions.ToString().ToLowerInvariant());
            element.SetAttribute("InheritPuttySession", inh.PuttySession.ToString().ToLowerInvariant());
            element.SetAttribute("InheritPreExtApp", inh.PreExtApp.ToString().ToLowerInvariant());
            element.SetAttribute("InheritPostExtApp", inh.PostExtApp.ToString().ToLowerInvariant());
            element.SetAttribute("InheritMacAddress", inh.MacAddress.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUserField", inh.UserField.ToString().ToLowerInvariant());
            element.SetAttribute("InheritExtApp", inh.ExtApp.ToString().ToLowerInvariant());
            element.SetAttribute("InheritFavorite", inh.Favorite.ToString().ToLowerInvariant());
            element.SetAttribute("InheritEnvironmentTags", inh.EnvironmentTags.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCCompression", inh.VNCCompression.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCEncoding", inh.VNCEncoding.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCAuthMode", inh.VNCAuthMode.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCProxyType", inh.VNCProxyType.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCProxyIP", inh.VNCProxyIP.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCProxyPort", inh.VNCProxyPort.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCProxyUsername", inh.VNCProxyUsername.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCProxyPassword", inh.VNCProxyPassword.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCColors", inh.VNCColors.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCSmartSizeMode", inh.VNCSmartSizeMode.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVNCViewOnly", inh.VNCViewOnly.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayUsageMethod", inh.RDGatewayUsageMethod.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayHostname", inh.RDGatewayHostname.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayUseConnectionCredentials", inh.RDGatewayUseConnectionCredentials.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayUsername", inh.RDGatewayUsername.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayPassword", inh.RDGatewayPassword.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayDomain", inh.RDGatewayDomain.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayExternalCredentialProvider", inh.RDGatewayExternalCredentialProvider.ToString().ToLowerInvariant());
            element.SetAttribute("InheritRDGatewayUserViaAPI", inh.RDGatewayUserViaAPI.ToString().ToLowerInvariant());
            element.SetAttribute("InheritVmId", inh.VmId.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUseVmId", inh.UseVmId.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUseEnhancedMode", inh.UseEnhancedMode.ToString().ToLowerInvariant());
            element.SetAttribute("InheritExternalCredentialProvider", inh.ExternalCredentialProvider.ToString().ToLowerInvariant());
            element.SetAttribute("InheritUserViaAPI", inh.UserViaAPI.ToString().ToLowerInvariant());
            element.SetAttribute("InheritStartProgram", inh.RDPStartProgram.ToString().ToLowerInvariant());
            element.SetAttribute("InheritStartProgramWorkDir", inh.RDPStartProgramWorkDir.ToString().ToLowerInvariant());
        }
    }
}
