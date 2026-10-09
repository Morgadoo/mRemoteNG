using System.Globalization;
using System.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Misc
{
    /// <summary>
    /// Reads a SecureCRT XML export (VanDyke configuration, "Sessions" key). Session folders become
    /// folders directly under the root; sessions become connections.
    /// </summary>
    /// <remarks>
    /// Differences from the legacy importer: the "Sessions" key is not added as an extra folder level,
    /// a session without a port gets the protocol's default port (the legacy importer used 0), and a
    /// session with an unknown protocol is skipped with a warning instead of aborting the import.
    /// Saved session passwords are encrypted by SecureCRT and are not imported.
    /// </remarks>
    public class SecureCrtFileDeserializer : IDeserializer<string, ConnectionTreeModel>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        /// <exception cref="InvalidDataException">The document has no SecureCRT sessions key.</exception>
        public ConnectionTreeModel Deserialize(string content)
        {
            _warnings.Clear();
            var root = new RootNodeInfo(RootNodeType.Connection);

            var xmlDocument = SecureXml.Load(content);
            var sessionsNode = xmlDocument.SelectSingleNode("/VanDyke/key[@name=\"Sessions\"]")
                ?? throw new InvalidDataException("Not a SecureCRT export: no Sessions key found.");

            ImportChildren(sessionsNode, root);
            return new ConnectionTreeModel(root);
        }

        private void ImportChildren(XmlNode keyNode, ContainerInfo parentContainer)
        {
            foreach (XmlNode child in keyNode.ChildNodes)
            {
                if (child is not XmlElement { Name: "key" } element)
                    continue;

                var name = element.GetAttribute("name");
                if (name is "Default" or "Default_LocalShell")
                    continue;

                if (GetString(element, "Hostname") is null)
                {
                    var folder = ConnectionDefaults.NewContainer(name);
                    parentContainer.AddChild(folder);
                    ImportChildren(element, folder);
                }
                else
                {
                    var connection = ConnectionInfoFromXml(element, name);
                    if (connection is not null)
                        parentContainer.AddChild(connection);
                }
            }
        }

        private ConnectionInfo? ConnectionInfoFromXml(XmlElement sessionNode, string name)
        {
            var protocolName = GetString(sessionNode, "Protocol Name");
            var protocol = ParseProtocol(protocolName);
            if (protocol is null)
            {
                _warnings.Add($"Session \"{name}\" skipped: unsupported protocol \"{protocolName}\".");
                return null;
            }

            var connection = ConnectionDefaults.NewConnection(protocol.Value);
            connection.Name = name;
            connection.Hostname = GetString(sessionNode, "Hostname") ?? "";
            connection.Username = GetString(sessionNode, "Username") ?? "";
            connection.Description = GetDescription(sessionNode);

            var portName = protocol switch
            {
                ProtocolType.SSH1 => "[SSH1] Port",
                ProtocolType.SSH2 => "[SSH2] Port",
                _ => "Port"
            };
            var portText = sessionNode.SelectSingleNode($"dword[@name=\"{portName}\"]")?.InnerText;
            if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0)
                connection.Port = port;

            return connection;
        }

        private static ProtocolType? ParseProtocol(string? protocolName) =>
            protocolName?.Trim().ToUpperInvariant() switch
            {
                "RDP" => ProtocolType.RDP,
                "RAW" => ProtocolType.RAW,
                "RLOGIN" => ProtocolType.Rlogin,
                "SSH1" => ProtocolType.SSH1,
                "SSH2" => ProtocolType.SSH2,
                "TELNET" => ProtocolType.Telnet,
                _ => null
            };

        private static string? GetString(XmlNode node, string name) =>
            node.SelectSingleNode($"string[@name=\"{name}\"]")?.InnerText;

        private static string GetDescription(XmlNode node)
        {
            var descriptionNode = node.SelectSingleNode("array[@name=\"Description\"]");
            if (descriptionNode is null)
                return "";

            return string.Join(" ", descriptionNode.ChildNodes.Cast<XmlNode>().Select(n => n.InnerText)).Trim();
        }
    }
}
