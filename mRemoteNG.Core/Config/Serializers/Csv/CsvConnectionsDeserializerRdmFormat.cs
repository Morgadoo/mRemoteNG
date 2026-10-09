using System.Globalization;
using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Csv
{
    /// <summary>
    /// Reads a Remote Desktop Manager (Devolutions) CSV export. Like the legacy importer it understands
    /// the RDP and SSH shell entry types; groups ("Folder\Sub folder") become nested folders and entries
    /// without a group go to an "Unsorted" folder.
    /// </summary>
    public class CsvConnectionsDeserializerRdmFormat : IDeserializer<string, ConnectionTreeModel>
    {
        private sealed record RdmConnectionType(ProtocolType Protocol, string Name, int Port, string IconName);

        private static readonly RdmConnectionType[] ConnectionTypes =
        [
            new(ProtocolType.RDP, "RDP (Microsoft Remote Desktop)", 3389, "Remote Desktop"),
            new(ProtocolType.SSH2, "SSH Shell", 22, "SSH")
        ];

        private readonly List<string> _warnings = [];

        /// <summary>Rows skipped in the last file read, and why.</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        public ConnectionTreeModel Deserialize(string serializedData)
        {
            ArgumentNullException.ThrowIfNull(serializedData);
            _warnings.Clear();

            var root = new RootNodeInfo(RootNodeType.Connection);
            var rows = QuotedCsvReader.ReadRows(serializedData);
            if (rows.Count == 0)
                return new ConnectionTreeModel(root);

            var headers = rows[0].Select(h => h.Trim()).ToList();
            int Column(string name) => headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));

            var hostColumn = Column("Host");
            var typeColumn = Column("ConnectionType");
            if (hostColumn < 0 || typeColumn < 0)
                throw new InvalidDataException("Not a Remote Desktop Manager CSV export: the Host and ConnectionType columns are missing.");

            var nameColumn = Column("Name");
            var portColumn = Column("Port");
            var groupColumn = Column("Group");
            var descriptionColumn = Column("Description");
            var userColumn = Column("CredentialUserName");
            var domainColumn = Column("CredentialDomain");
            var passwordColumn = Column("CredentialPassword");

            var groups = new Dictionary<string, ContainerInfo>(StringComparer.OrdinalIgnoreCase);
            ContainerInfo? unsorted = null;

            for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                string Get(int column) => column >= 0 && column < row.Count ? row[column] : "";
                var lineLabel = $"Row {rowIndex + 1}";

                var host = Get(hostColumn).Trim();
                if (string.IsNullOrEmpty(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                {
                    _warnings.Add($"{lineLabel}: skipped, missing or invalid host \"{host}\".");
                    continue;
                }

                var typeName = Get(typeColumn).Trim();
                var type = ConnectionTypes.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                if (type is null)
                {
                    _warnings.Add($"{lineLabel}: skipped, unsupported connection type \"{typeName}\".");
                    continue;
                }

                var connection = ConnectionDefaults.NewConnection(type.Protocol);
                var name = Get(nameColumn);
                connection.Name = string.IsNullOrWhiteSpace(name) ? host : name;
                connection.Hostname = host;
                connection.Port = int.TryParse(Get(portColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
                    ? port
                    : type.Port;
                connection.Description = Get(descriptionColumn);
                connection.Username = Get(userColumn);
                connection.Domain = Get(domainColumn);
                connection.Password = Get(passwordColumn);
                connection.Icon = type.IconName;

                var group = Get(groupColumn).Trim().Trim('\\');
                if (group.Length == 0)
                {
                    unsorted ??= ConnectionDefaults.NewContainer("Unsorted");
                    unsorted.AddChild(connection);
                }
                else
                {
                    GetOrCreateGroup(root, groups, group).AddChild(connection);
                }
            }

            if (unsorted is not null)
                root.AddChild(unsorted);

            return new ConnectionTreeModel(root);
        }

        private static ContainerInfo GetOrCreateGroup(ContainerInfo root, Dictionary<string, ContainerInfo> groups, string path)
        {
            if (groups.TryGetValue(path, out var existing))
                return existing;

            var separator = path.LastIndexOf('\\');
            var parent = separator < 0 ? root : GetOrCreateGroup(root, groups, path[..separator]);
            var container = ConnectionDefaults.NewContainer(path[(separator + 1)..]);
            parent.AddChild(container);
            groups[path] = container;
            return container;
        }
    }
}
