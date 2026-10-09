using mRemoteNG.Core.Config.Connections.Sql;
using mRemoteNG.Core.Config.DataProviders;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;

namespace mRemoteNG.Core.Config.Connections
{
    /// <summary>Where the open connection tree is stored.</summary>
    public enum ConnectionsStorageKind
    {
        /// <summary>Nothing loaded from or saved to storage yet.</summary>
        None,
        File,
        Database,
    }

    /// <summary>Raised after the tree was loaded or saved.</summary>
    public sealed class ConnectionsStorageEventArgs(ConnectionsStorageKind kind, string location) : EventArgs
    {
        public ConnectionsStorageKind Kind { get; } = kind;

        /// <summary>The file path, or the database description.</summary>
        public string Location { get; } = location;
    }

    /// <summary>
    /// High-level service for loading and saving mRemoteNG connection files or a SQL connection database.
    /// Cross-platform replacement for the legacy ConnectionsService.
    /// </summary>
    public class ConnectionsService
    {
        private readonly ICryptoProviderFactory _cryptoProviderFactory;
        private readonly object _saveSync = new();

        public ConnectionTreeModel? ConnectionTreeModel { get; private set; }

        /// <summary>The open connection file; null when nothing is open or the tree comes from a database.</summary>
        public string? CurrentFilePath { get; private set; }

        /// <summary>The SQL database the tree was loaded from, or null for a file.</summary>
        public SqlConnectionsStore? Database { get; private set; }

        /// <summary>True when the tree is stored in a SQL database (<see cref="Database"/>).</summary>
        public bool UsingDatabase => Database is not null;

        /// <summary>True when saving is impossible because the SQL database is configured read-only.</summary>
        public bool IsReadOnly => Database?.ReadOnly == true;

        /// <summary><c>tblUpdate.LastUpdate</c> as of the last database load or save (multi-user change detection).</summary>
        public DateTime? LastDatabaseUpdate { get; private set; }

        public ConnectionsStorageKind StorageKind =>
            UsingDatabase ? ConnectionsStorageKind.Database
            : CurrentFilePath is not null ? ConnectionsStorageKind.File
            : ConnectionsStorageKind.None;

        /// <summary>True when <see cref="Save"/> has somewhere to write to.</summary>
        public bool HasStorage => ConnectionTreeModel is not null && (UsingDatabase ? !IsReadOnly : CurrentFilePath is not null);

        /// <summary>
        /// Encryption settings used when saving. Loaded files keep their cipher and KDF settings;
        /// files older than 2.6 are upgraded to the default (AES-GCM) on save, as in the legacy app.
        /// </summary>
        public ConnectionFileEncryption Encryption { get; set; } = new();

        /// <summary>When the connection file is backed up (see <see cref="BackupOptions"/>).</summary>
        public BackupFrequency BackupFrequency { get; set; } = BackupFrequency.Never;

        /// <summary>Rolling backup settings (count, folder, legacy name format).</summary>
        public FileBackupOptions BackupOptions { get; set; } = new();

        /// <summary>The backup made by the last save or <see cref="BackupCurrentFile"/>, if any.</summary>
        public string? LastBackupPath { get; private set; }

        /// <summary>Raised after a file or database was loaded.</summary>
        public event EventHandler<ConnectionsStorageEventArgs>? Loaded;

        /// <summary>Raised after a file or database was saved.</summary>
        public event EventHandler<ConnectionsStorageEventArgs>? Saved;

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
            Database = null;
            LastDatabaseUpdate = null;
            Loaded?.Invoke(this, new ConnectionsStorageEventArgs(ConnectionsStorageKind.File, filePath));
            return ConnectionTreeModel;
        }

        /// <summary>Loads the tree from a SQL database (legacy schema); later saves go back to it.</summary>
        /// <param name="password">Master password, or null to use the default key.</param>
        /// <exception cref="ConnectionFilePasswordException">The database needs a (different) master password.</exception>
        /// <exception cref="ConnectionFileVersionException">The schema is newer than supported.</exception>
        public ConnectionTreeModel LoadFromDatabase(SqlConnectionsStore store, string? password = null)
        {
            ArgumentNullException.ThrowIfNull(store);
            var result = store.Load(password);
            ConnectionTreeModel = result.Model;
            Encryption = new ConnectionFileEncryption();
            CurrentFilePath = null;
            Database = store;
            LastDatabaseUpdate = result.LastUpdate;
            Loaded?.Invoke(this, new ConnectionsStorageEventArgs(ConnectionsStorageKind.Database, store.DisplayName));
            return ConnectionTreeModel;
        }

        /// <summary>Re-reads the database (another client saved), keeping the master password of the open tree.</summary>
        public ConnectionTreeModel ReloadFromDatabase()
        {
            var store = Database ?? throw new InvalidOperationException("The connections are not stored in a database.");
            var password = ConnectionTreeModel?.RootNode is { IsPasswordProtected: true } root ? root.PasswordString : null;
            return LoadFromDatabase(store, password);
        }

        /// <summary>Saves to wherever the tree came from (file or database).</summary>
        public void Save() => SaveToFile();

        /// <summary>
        /// Saves the current tree. The file is encrypted with the root node's master password,
        /// or the default key when none is set. With no <paramref name="filePath"/> and a database open,
        /// saves to the database; an explicit path saves to that file and makes it the current storage.
        /// </summary>
        public void SaveToFile(string? filePath = null)
        {
            if (ConnectionTreeModel is null)
                throw new InvalidOperationException("No connection tree loaded.");

            if (filePath is null && Database is not null)
            {
                SaveToDatabase();
                return;
            }

            var targetPath = filePath ?? CurrentFilePath
                ?? throw new InvalidOperationException("No file path specified.");

            var cryptoProvider = _cryptoProviderFactory.Build(Encryption.Engine, Encryption.Mode, Encryption.KeyDerivationIterations);
            var serializer = new XmlConnectionsSerializer(cryptoProvider, fullFileEncryption: Encryption.FullFileEncryption);
            var xml = serializer.Serialize(ConnectionTreeModel);

            lock (_saveSync)
            {
                // Both providers write to a temp file first, so a crash mid-write never truncates the user's connections.
                if (BackupFrequency == BackupFrequency.OnSave && BackupOptions.Enabled)
                {
                    var provider = new FileDataProviderWithRollingBackup(targetPath, BackupOptions);
                    provider.Save(xml);
                    if (provider.LastBackupPath is not null)
                        LastBackupPath = provider.LastBackupPath;
                }
                else
                {
                    new FileDataProvider(targetPath).Save(xml);
                }
            }

            CurrentFilePath = targetPath;
            Database = null;
            LastDatabaseUpdate = null;
            Saved?.Invoke(this, new ConnectionsStorageEventArgs(ConnectionsStorageKind.File, targetPath));
        }

        /// <summary>Saves the tree to the open SQL database.</summary>
        /// <exception cref="InvalidOperationException">No database is open, or it is read-only.</exception>
        public void SaveToDatabase()
        {
            var store = Database ?? throw new InvalidOperationException("The connections are not stored in a database.");
            if (ConnectionTreeModel is null)
                throw new InvalidOperationException("No connection tree loaded.");

            lock (_saveSync)
                LastDatabaseUpdate = store.Save(ConnectionTreeModel);
            Saved?.Invoke(this, new ConnectionsStorageEventArgs(ConnectionsStorageKind.Database, store.DisplayName));
        }

        /// <summary>
        /// Copies the open connection file to a rolling backup now (used for "back up on exit").
        /// Returns the backup path, or null when there is no file or backups are off.
        /// </summary>
        public string? BackupCurrentFile()
        {
            if (CurrentFilePath is null || !BackupOptions.Enabled)
                return null;

            lock (_saveSync)
            {
                var path = new FileBackupCreator().CreateBackup(CurrentFilePath, BackupOptions);
                if (path is not null)
                    LastBackupPath = path;
                return path;
            }
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
            Database = null;
            LastDatabaseUpdate = null;
            return ConnectionTreeModel;
        }
    }
}
