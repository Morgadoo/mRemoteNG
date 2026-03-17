using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;

namespace mRemoteNG.Core.Config.Connections
{
    /// <summary>
    /// High-level service for loading and saving mRemoteNG connection files.
    /// Cross-platform replacement for the legacy ConnectionsService.
    /// </summary>
    public class ConnectionsService
    {
        private readonly ICryptoProviderFactory _cryptoProviderFactory;

        public ConnectionTreeModel? ConnectionTreeModel { get; private set; }
        public string? CurrentFilePath { get; private set; }

        public ConnectionsService(ICryptoProviderFactory cryptoProviderFactory)
        {
            _cryptoProviderFactory = cryptoProviderFactory ?? throw new ArgumentNullException(nameof(cryptoProviderFactory));
        }

        public ConnectionTreeModel LoadFromFile(string filePath, string password = "mR3m")
        {
            var xml = File.ReadAllText(filePath);
            var cryptoProvider = _cryptoProviderFactory.Build();
            var deserializer = new XmlConnectionsDeserializer(cryptoProvider, password);
            ConnectionTreeModel = deserializer.Deserialize(xml);
            CurrentFilePath = filePath;
            return ConnectionTreeModel;
        }

        public void SaveToFile(string? filePath = null, string password = "mR3m")
        {
            if (ConnectionTreeModel is null)
                throw new InvalidOperationException("No connection tree loaded.");

            var targetPath = filePath ?? CurrentFilePath
                ?? throw new InvalidOperationException("No file path specified.");

            var cryptoProvider = _cryptoProviderFactory.Build();
            var serializer = new XmlConnectionsSerializer(cryptoProvider, password);
            var xml = serializer.Serialize(ConnectionTreeModel);
            File.WriteAllText(targetPath, xml);
            CurrentFilePath = targetPath;
        }

        public ConnectionTreeModel CreateNew(string name = "Connections")
        {
            var rootNode = new Tree.Root.RootNodeInfo(Tree.Root.RootNodeType.Connection)
            {
                Name = name
            };
            ConnectionTreeModel = new ConnectionTreeModel(rootNode);
            return ConnectionTreeModel;
        }
    }
}
