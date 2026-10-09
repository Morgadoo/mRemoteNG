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

        /// <summary>
        /// Encryption settings used when saving. Loaded files keep their cipher and KDF settings;
        /// files older than 2.6 are upgraded to the default (AES-GCM) on save, as in the legacy app.
        /// </summary>
        public ConnectionFileEncryption Encryption { get; set; } = new();

        public ConnectionsService(ICryptoProviderFactory cryptoProviderFactory)
        {
            _cryptoProviderFactory = cryptoProviderFactory ?? throw new ArgumentNullException(nameof(cryptoProviderFactory));
        }

        /// <summary>Loads a connection file.</summary>
        /// <param name="password">Master password, or null to use the default key.</param>
        /// <exception cref="ConnectionFilePasswordException">The file needs a (different) master password.</exception>
        /// <exception cref="ConnectionFileVersionException">The file was written by a newer version.</exception>
        public ConnectionTreeModel LoadFromFile(string filePath, string? password = null)
        {
            var xml = File.ReadAllText(filePath);
            var deserializer = new XmlConnectionsDeserializer(_cryptoProviderFactory, password);
            ConnectionTreeModel = deserializer.Deserialize(xml);
            Encryption = deserializer.Encryption;
            CurrentFilePath = filePath;
            return ConnectionTreeModel;
        }

        /// <summary>
        /// Saves the current tree. The file is encrypted with the root node's master password,
        /// or the default key when none is set.
        /// </summary>
        public void SaveToFile(string? filePath = null)
        {
            if (ConnectionTreeModel is null)
                throw new InvalidOperationException("No connection tree loaded.");

            var targetPath = filePath ?? CurrentFilePath
                ?? throw new InvalidOperationException("No file path specified.");

            var cryptoProvider = _cryptoProviderFactory.Build(Encryption.Engine, Encryption.Mode, Encryption.KeyDerivationIterations);
            var serializer = new XmlConnectionsSerializer(cryptoProvider, fullFileEncryption: Encryption.FullFileEncryption);
            var xml = serializer.Serialize(ConnectionTreeModel);

            // Write to a temp file first so a crash mid-write never truncates the user's connections.
            var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, xml);
            File.Move(tempPath, targetPath, overwrite: true);
            CurrentFilePath = targetPath;
        }

        public ConnectionTreeModel CreateNew(string name = "Connections")
        {
            var rootNode = new Tree.Root.RootNodeInfo(Tree.Root.RootNodeType.Connection)
            {
                Name = name
            };
            ConnectionTreeModel = new ConnectionTreeModel(rootNode);
            Encryption = new ConnectionFileEncryption();
            CurrentFilePath = null;
            return ConnectionTreeModel;
        }
    }
}
