using mRemoteNG.Core.Config.Serializers.Csv;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Export
{
    public enum ExportFormat
    {
        /// <summary>An mRemoteNG connection file (confCons.xml format, version 2.8).</summary>
        Xml,

        /// <summary>The mRemoteNG CSV format. Passwords are written in clear text.</summary>
        Csv
    }

    public sealed class ExportOptions
    {
        public ExportFormat Format { get; init; } = ExportFormat.Xml;

        /// <summary>Which credentials and whether inheritance settings are written.</summary>
        public SaveFilter SaveFilter { get; init; } = new();

        /// <summary>
        /// XML only: a master password for the exported file. Null or empty writes an unprotected
        /// file (passwords encrypted with the default key), whatever the current file uses.
        /// </summary>
        public string? Password { get; init; }

        /// <summary>XML only: cipher settings for the exported file.</summary>
        public ConnectionFileEncryption Encryption { get; init; } = new();
    }

    /// <summary>
    /// Exports the whole connection tree or one folder (or connection) to an mRemoteNG XML or CSV file,
    /// without changing the live tree.
    /// </summary>
    public sealed class ConnectionExporter
    {
        private readonly ICryptoProviderFactory _cryptoProviderFactory;

        public ConnectionExporter(ICryptoProviderFactory cryptoProviderFactory)
        {
            _cryptoProviderFactory = cryptoProviderFactory ?? throw new ArgumentNullException(nameof(cryptoProviderFactory));
        }

        /// <param name="exportTarget">The root node for the whole tree, or a folder or connection.</param>
        public string Serialize(ConnectionInfo exportTarget, ExportOptions options)
        {
            ArgumentNullException.ThrowIfNull(exportTarget);
            ArgumentNullException.ThrowIfNull(options);

            return options.Format switch
            {
                ExportFormat.Xml => SerializeXml(exportTarget, options),
                ExportFormat.Csv => new CsvConnectionsSerializerMremotengFormat(options.SaveFilter).Serialize(exportTarget),
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.Format, "Unknown export format.")
            };
        }

        /// <summary>Writes the export to <paramref name="filePath"/>, replacing it only once fully written.</summary>
        public void ExportToFile(string filePath, ConnectionInfo exportTarget, ExportOptions options)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("No export file was given.", nameof(filePath));

            var content = Serialize(exportTarget, options);

            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private string SerializeXml(ConnectionInfo exportTarget, ExportOptions options)
        {
            var root = exportTarget as RootNodeInfo ?? WrapInRoot(exportTarget);
            var key = string.IsNullOrEmpty(options.Password) ? root.DefaultPassword : options.Password;

            var encryption = options.Encryption;
            var cryptoProvider = _cryptoProviderFactory.Build(encryption.Engine, encryption.Mode, encryption.KeyDerivationIterations);
            var serializer = new XmlConnectionsSerializer(cryptoProvider, key, options.SaveFilter, encryption.FullFileEncryption);
            return serializer.Serialize(new ConnectionTreeModel(root));
        }

        /// <summary>
        /// Puts a copy of the node (keeping IDs and the values it currently resolves, including
        /// inherited ones) under a new root, so the export is self-contained.
        /// </summary>
        private static RootNodeInfo WrapInRoot(ConnectionInfo exportTarget)
        {
            var root = new RootNodeInfo(RootNodeType.Connection);
            root.AddChild(CopyWithIds(exportTarget));
            return root;
        }

        private static ConnectionInfo CopyWithIds(ConnectionInfo source)
        {
            ConnectionInfo copy = source is ContainerInfo
                ? new ContainerInfo(source.ConstantID)
                : new ConnectionInfo(source.ConstantID);
            copy.CopyFrom(source);

            if (source is ContainerInfo sourceContainer && copy is ContainerInfo copyContainer)
            {
                copyContainer.IsExpanded = sourceContainer.IsExpanded;
                foreach (var child in sourceContainer.Children)
                    copyContainer.AddChild(CopyWithIds(child));
            }

            return copy;
        }
    }
}
