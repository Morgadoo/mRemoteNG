using System.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Misc
{
    /// <summary>
    /// Reads Microsoft Remote Desktop Connection Manager files (.rdg): RDCMan 2.2 (schema 1)
    /// and 2.7/2.83 (schema 3). The file's top-level group becomes a folder under the root.
    /// </summary>
    /// <remarks>
    /// RDCMan encrypts stored passwords with Windows DPAPI (machine scope). The legacy importer
    /// decrypted them on Windows; this cross-platform port cannot, so encrypted passwords are left
    /// empty and counted in <see cref="Warnings"/>. Passwords stored as clear text (2.2 only) are kept.
    /// </remarks>
    public class RemoteDesktopConnectionManagerDeserializer : IDeserializer<string, ConnectionTreeModel>
    {
        private int _schemaVersion; // 1 = RDCMan 2.2, 3 = RDCMan 2.7
        private int _skippedPasswords;
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        /// <exception cref="InvalidDataException">The file is not a supported RDCMan file.</exception>
        public ConnectionTreeModel Deserialize(string rdcmConnectionsXml)
        {
            _warnings.Clear();
            _skippedPasswords = 0;

            var root = new RootNodeInfo(RootNodeType.Connection);
            var xmlDocument = SecureXml.Load(rdcmConnectionsXml);

            var rdcManNode = xmlDocument.SelectSingleNode("/RDCMan")
                ?? throw new InvalidDataException("Not a Remote Desktop Connection Manager file.");
            VerifySchemaVersion(rdcManNode);
            VerifyFileVersion(rdcManNode);

            var fileNode = rdcManNode.SelectSingleNode("./file");
            if (fileNode is not null)
                ImportFileOrGroup(fileNode, root);

            if (_skippedPasswords > 0)
                _warnings.Add($"{_skippedPasswords} password(s) are encrypted with Windows DPAPI by RDCMan and were not imported.");

            return new ConnectionTreeModel(root);
        }

        private void VerifySchemaVersion(XmlNode rdcManNode)
        {
            if (!int.TryParse(rdcManNode.Attributes?["schemaVersion"]?.Value, out var version))
                throw new InvalidDataException("Could not find schema version attribute.");

            if (version != 1 && version != 3)
                throw new InvalidDataException($"Unsupported schema version ({version}).");

            _schemaVersion = version;
        }

        private static void VerifyFileVersion(XmlNode rdcManNode)
        {
            var versionText = rdcManNode.Attributes?["programVersion"]?.Value;
            if (versionText is not null)
            {
                if (!Version.TryParse(versionText, out var version)
                    || (version != new Version(2, 7) && version != new Version(2, 83)))
                    throw new InvalidDataException($"Unsupported file version ({versionText}).");
                return;
            }

            versionText = rdcManNode.SelectSingleNode("./version")?.InnerText
                ?? throw new InvalidDataException("Unknown file version.");
            if (!Version.TryParse(versionText, out var legacyVersion) || legacyVersion != new Version(2, 2))
                throw new InvalidDataException($"Unsupported file version ({versionText}).");
        }

        private void ImportFileOrGroup(XmlNode xmlNode, ContainerInfo parentContainer)
        {
            var newContainer = ImportContainer(xmlNode, parentContainer);

            var childNodes = xmlNode.SelectNodes("./group|./server");
            if (childNodes is null) return;
            foreach (XmlNode childNode in childNodes)
            {
                switch (childNode.Name)
                {
                    case "group":
                        ImportFileOrGroup(childNode, newContainer);
                        break;
                    case "server":
                        newContainer.AddChild(ConnectionInfoFromXml(childNode));
                        break;
                }
            }
        }

        private ContainerInfo ImportContainer(XmlNode containerNode, ContainerInfo parentContainer)
        {
            // 2.2 keeps every container setting inside <properties>; 2.7 only the name and expanded state.
            var settingsNode = _schemaVersion == 1
                ? containerNode.SelectSingleNode("./properties") ?? containerNode
                : containerNode;

            var newContainer = ConnectionDefaults.NewContainer("New Folder");
            newContainer.CopyFrom(ConnectionInfoFromXml(settingsNode));

            var propertiesNode = containerNode.SelectSingleNode("./properties");
            var name = propertiesNode?.SelectSingleNode("./name")?.InnerText;
            newContainer.Name = string.IsNullOrWhiteSpace(name) ? "New Folder" : name;
            newContainer.IsExpanded = !bool.TryParse(propertiesNode?.SelectSingleNode("./expanded")?.InnerText, out var expanded) || expanded;

            parentContainer.AddChild(newContainer);
            return newContainer;
        }

        private ConnectionInfo ConnectionInfoFromXml(XmlNode xmlNode)
        {
            var connectionInfo = ConnectionDefaults.NewConnection(ProtocolType.RDP);

            // 2.2 stores server properties directly on <server>; 2.7 wraps them in <properties>.
            var propertiesNode = _schemaVersion == 1 ? xmlNode : xmlNode.SelectSingleNode("./properties");

            connectionInfo.VmId = propertiesNode?.SelectSingleNode("./vmid")?.InnerText ?? "";
            connectionInfo.Hostname = propertiesNode?.SelectSingleNode("./name")?.InnerText ?? "";

            var displayName = propertiesNode?.SelectSingleNode("./displayName")?.InnerText;
            connectionInfo.Name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : string.IsNullOrWhiteSpace(connectionInfo.Hostname)
                    ? connectionInfo.Name
                    : connectionInfo.Hostname;

            connectionInfo.Description = propertiesNode?.SelectSingleNode("./comment")?.InnerText ?? "";

            ReadLogonCredentials(xmlNode, connectionInfo);
            ReadConnectionSettings(xmlNode, connectionInfo);
            ReadGatewaySettings(xmlNode, connectionInfo);
            ReadRemoteDesktopSettings(xmlNode, connectionInfo);
            ReadLocalResources(xmlNode, connectionInfo);
            ReadSecuritySettings(xmlNode, connectionInfo);

            return connectionInfo;
        }

        private void ReadLogonCredentials(XmlNode xmlNode, ConnectionInfo connectionInfo)
        {
            var node = xmlNode.SelectSingleNode("./logonCredentials");
            if (node?.Attributes?["inherit"]?.Value == "None")
            {
                connectionInfo.Username = node.SelectSingleNode("./userName")?.InnerText ?? "";
                connectionInfo.Password = ReadPassword(node.SelectSingleNode("./password"));
                connectionInfo.Domain = node.SelectSingleNode("./domain")?.InnerText ?? "";
            }
            else
            {
                connectionInfo.Inheritance.Username = true;
                connectionInfo.Inheritance.Password = true;
                connectionInfo.Inheritance.Domain = true;
            }
        }

        private static void ReadConnectionSettings(XmlNode xmlNode, ConnectionInfo connectionInfo)
        {
            var node = xmlNode.SelectSingleNode("./connectionSettings");
            if (node?.Attributes?["inherit"]?.Value == "None")
            {
                if (bool.TryParse(node.SelectSingleNode("./connectToConsole")?.InnerText, out var useConsole))
                    connectionInfo.UseConsoleSession = useConsole;
                connectionInfo.RDPStartProgram = node.SelectSingleNode("./startProgram")?.InnerText ?? "";
                connectionInfo.RDPStartProgramWorkDir = node.SelectSingleNode("./workingDir")?.InnerText ?? "";
                if (int.TryParse(node.SelectSingleNode("./port")?.InnerText, out var port))
                    connectionInfo.Port = port;
                connectionInfo.LoadBalanceInfo = node.SelectSingleNode("./loadBalanceInfo")?.InnerText ?? "";
            }
            else
            {
                connectionInfo.Inheritance.UseConsoleSession = true;
                connectionInfo.Inheritance.Port = true;
            }
        }

        private void ReadGatewaySettings(XmlNode xmlNode, ConnectionInfo connectionInfo)
        {
            var node = xmlNode.SelectSingleNode("./gatewaySettings");
            if (node?.Attributes?["inherit"]?.Value == "None")
            {
                connectionInfo.RDGatewayUsageMethod = node.SelectSingleNode("./enabled")?.InnerText == "True"
                    ? RDGatewayUsageMethod.Always
                    : RDGatewayUsageMethod.Never;
                connectionInfo.RDGatewayHostname = node.SelectSingleNode("./hostName")?.InnerText ?? "";
                connectionInfo.RDGatewayUsername = node.SelectSingleNode("./userName")?.InnerText ?? "";
                connectionInfo.RDGatewayPassword = ReadPassword(node.SelectSingleNode("./password"));
                connectionInfo.RDGatewayDomain = node.SelectSingleNode("./domain")?.InnerText ?? "";
            }
            else
            {
                connectionInfo.Inheritance.RDGatewayUsageMethod = true;
                connectionInfo.Inheritance.RDGatewayHostname = true;
                connectionInfo.Inheritance.RDGatewayUsername = true;
                connectionInfo.Inheritance.RDGatewayPassword = true;
                connectionInfo.Inheritance.RDGatewayDomain = true;
            }
        }

        private static void ReadRemoteDesktopSettings(XmlNode xmlNode, ConnectionInfo connectionInfo)
        {
            var node = xmlNode.SelectSingleNode("./remoteDesktop");
            if (node?.Attributes?["inherit"]?.Value == "None")
            {
                var size = node.SelectSingleNode("./size")?.InnerText.Replace(" ", "") ?? "";
                connectionInfo.Resolution =
                    Enum.TryParse<RDPResolutions>(size, true, out var resolution) && Enum.IsDefined(resolution)
                        ? resolution
                        : Enum.TryParse("Res" + size, true, out resolution) && Enum.IsDefined(resolution)
                            ? resolution
                            : RDPResolutions.FitToWindow;

                if (node.SelectSingleNode("./sameSizeAsClientArea")?.InnerText == "True")
                    connectionInfo.Resolution = RDPResolutions.FitToWindow;

                if (node.SelectSingleNode("./fullScreen")?.InnerText == "True")
                    connectionInfo.Resolution = RDPResolutions.Fullscreen;

                if (int.TryParse(node.SelectSingleNode("./colorDepth")?.InnerText, out var depth)
                    && Enum.IsDefined(typeof(RDPColors), depth))
                    connectionInfo.Colors = (RDPColors)depth;
            }
            else
            {
                connectionInfo.Inheritance.Resolution = true;
                connectionInfo.Inheritance.Colors = true;
            }
        }

        private static void ReadLocalResources(XmlNode xmlNode, ConnectionInfo connectionInfo)
        {
            var node = xmlNode.SelectSingleNode("./localResources");
            if (node?.Attributes?["inherit"]?.Value == "None")
            {
                switch (node.SelectSingleNode("./audioRedirection")?.InnerText)
                {
                    case "0": // Bring to this computer
                    case "Client":
                        connectionInfo.RedirectSound = RDPSounds.BringToThisComputer;
                        break;
                    case "1": // Leave at remote computer
                    case "Remote":
                        connectionInfo.RedirectSound = RDPSounds.LeaveAtRemoteComputer;
                        break;
                    case "2": // Do not play
                    case "NoSound":
                        connectionInfo.RedirectSound = RDPSounds.DoNotPlay;
                        break;
                }

                switch (node.SelectSingleNode("./keyboardHook")?.InnerText)
                {
                    case "0": // On the local computer
                    case "Client":
                    case "2": // In full screen mode only
                    case "FullScreenClient":
                        connectionInfo.RedirectKeys = false;
                        break;
                    case "1": // On the remote computer
                    case "Remote":
                        connectionInfo.RedirectKeys = true;
                        break;
                }

                if (bool.TryParse(node.SelectSingleNode("./redirectDrives")?.InnerText, out var redirectDisks))
                    connectionInfo.RedirectDiskDrives = redirectDisks ? RDPDiskDrives.Local : RDPDiskDrives.None;
                if (bool.TryParse(node.SelectSingleNode("./redirectPorts")?.InnerText, out var redirectPorts))
                    connectionInfo.RedirectPorts = redirectPorts;
                if (bool.TryParse(node.SelectSingleNode("./redirectPrinters")?.InnerText, out var redirectPrinters))
                    connectionInfo.RedirectPrinters = redirectPrinters;
                if (bool.TryParse(node.SelectSingleNode("./redirectSmartCards")?.InnerText, out var redirectSmartCards))
                    connectionInfo.RedirectSmartCards = redirectSmartCards;
                if (bool.TryParse(node.SelectSingleNode("./redirectClipboard")?.InnerText, out var redirectClipboard))
                    connectionInfo.RedirectClipboard = redirectClipboard;
            }
            else
            {
                connectionInfo.Inheritance.RedirectSound = true;
                connectionInfo.Inheritance.RedirectKeys = true;
                connectionInfo.Inheritance.RedirectDiskDrives = true;
                connectionInfo.Inheritance.RedirectPorts = true;
                connectionInfo.Inheritance.RedirectPrinters = true;
                connectionInfo.Inheritance.RedirectSmartCards = true;
                connectionInfo.Inheritance.RedirectClipboard = true;
            }
        }

        private static void ReadSecuritySettings(XmlNode xmlNode, ConnectionInfo connectionInfo)
        {
            var node = xmlNode.SelectSingleNode("./securitySettings");
            if (node?.Attributes?["inherit"]?.Value == "None")
            {
                switch (node.SelectSingleNode("./authentication")?.InnerText)
                {
                    case "0": // No authentication
                    case "None":
                        connectionInfo.RDPAuthenticationLevel = AuthenticationLevel.NoAuth;
                        break;
                    case "1": // Do not connect if authentication fails
                    case "Required":
                        connectionInfo.RDPAuthenticationLevel = AuthenticationLevel.AuthRequired;
                        break;
                    case "2": // Warn if authentication fails
                    case "Warn":
                        connectionInfo.RDPAuthenticationLevel = AuthenticationLevel.WarnOnFailedAuth;
                        break;
                }
            }
            else
            {
                connectionInfo.Inheritance.RDPAuthenticationLevel = true;
            }
        }

        private string ReadPassword(XmlNode? passwordNode)
        {
            var value = passwordNode?.InnerText;
            if (string.IsNullOrEmpty(value))
                return "";

            if (passwordNode?.Attributes?["storeAsClearText"]?.Value == "True")
                return value;

            _skippedPasswords++;
            return "";
        }
    }
}
