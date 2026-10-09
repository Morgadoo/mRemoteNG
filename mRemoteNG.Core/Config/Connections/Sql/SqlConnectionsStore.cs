using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Config.Serializers.Sql;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.SymmetricEncryption;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Connections.Sql
{
    /// <summary>Contents of <c>tblRoot</c>.</summary>
    public sealed record SqlConnectionListMetaData(string Name, string Protected, bool Export, Version ConfVersion);

    /// <summary>Thrown when the database cannot be used (e.g. not initialised while read-only).</summary>
    public sealed class SqlConnectionsException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// Loads and saves the whole connection tree in a SQL database with the legacy schema
    /// (<c>tblCons</c>, <c>tblRoot</c>, <c>tblUpdate</c>), so the legacy WinForms app and this version can share it.
    /// <list type="bullet">
    ///   <item>Missing tables are created on first use; missing columns of older schema versions are added
    ///         and <c>tblRoot.ConfVersion</c> raised to 3.0 (not in read-only mode).</item>
    ///   <item>A newer schema version than 3.0 is refused (<see cref="ConnectionFileVersionException"/>).</item>
    ///   <item>Passwords and the <c>tblRoot.Protected</c> marker use the legacy AES-CBC scheme
    ///         (<see cref="LegacyRijndaelCryptographyProvider"/>) keyed with the root password ("mR3m" unless a
    ///         master password is set), exactly as the legacy SQL backend does.</item>
    ///   <item>A save replaces <c>tblRoot</c> and <c>tblCons</c> in one transaction and stamps <c>tblUpdate.LastUpdate</c>,
    ///         which other clients poll (<see cref="Multiuser.SqlConnectionsUpdateChecker"/>).</item>
    /// </list>
    /// Every call opens its own connection (pooled by the driver).
    /// </summary>
    public sealed class SqlConnectionsStore
    {
        private const string ProtectedMarker = "ThisIsProtected";
        private const string NotProtectedMarker = "ThisIsNotProtected";

        private readonly Func<IDatabaseConnector> _connectorFactory;
        private readonly ICryptographyProvider _crypto;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _clock;

        public SqlConnectionsStore(DatabaseConnectionSettings settings, ILogger? logger = null)
            : this(settings, () => DatabaseConnectorFactory.Create(settings), logger)
        {
        }

        public SqlConnectionsStore(
            DatabaseConnectionSettings settings,
            Func<IDatabaseConnector> connectorFactory,
            ILogger? logger = null,
            ICryptographyProvider? crypto = null,
            Func<DateTime>? clock = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _connectorFactory = connectorFactory ?? throw new ArgumentNullException(nameof(connectorFactory));
            _logger = logger ?? NullLogger.Instance;
            _crypto = crypto ?? new LegacyRijndaelCryptographyProvider();
            _clock = clock ?? (() => DateTime.Now);
        }

        public DatabaseConnectionSettings Settings { get; }

        public bool ReadOnly => Settings.ReadOnly;

        /// <summary>A short description for logs and the window title, e.g. "MySql db:3306/mremoteng".</summary>
        public string DisplayName => $"{Settings.ServerType} {Settings.Host}/{Settings.DatabaseName}";

        // ── Load ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the tree. <paramref name="password"/> is the master password, or null for the default key.
        /// </summary>
        /// <exception cref="ConnectionFilePasswordException">The database is protected and the password is missing or wrong.</exception>
        /// <exception cref="ConnectionFileVersionException">The schema is newer than this version understands.</exception>
        public SqlLoadResult Load(string? password = null)
        {
            using var connector = Open();
            EnsureTables(connector);

            var metaData = ReadMetaData(connector);
            if (metaData is not null && metaData.ConfVersion > SqlSchema.CurrentVersion)
            {
                throw new ConnectionFileVersionException(
                    double.Parse(metaData.ConfVersion.ToString(2), CultureInfo.InvariantCulture),
                    double.Parse(SqlSchema.CurrentVersion.ToString(2), CultureInfo.InvariantCulture));
            }

            if (!ReadOnly)
                UpgradeSchema(connector, metaData);

            var rootName = string.IsNullOrWhiteSpace(metaData?.Name) ? "Connections" : metaData!.Name;
            var key = ResolveKey(metaData?.Protected, password);

            var rows = ReadRows(connector);
            var mapper = new SqlConnectionRowMapper(_crypto);
            var model = mapper.FromRows(rows, key, rootName);
            if (key != model.RootNode.DefaultPassword)
                model.RootNode.PasswordString = key;

            var lastUpdate = ReadLastUpdate(connector);
            _logger.LogInformation("Loaded {Count} connections from {Database}", rows.Count, DisplayName);
            return new SqlLoadResult(model, lastUpdate, metaData?.ConfVersion ?? SqlSchema.CurrentVersion);
        }

        /// <summary>The <c>tblRoot</c> row, or null when the database has no tree yet.</summary>
        public SqlConnectionListMetaData? GetMetaData()
        {
            using var connector = Open();
            return TableExists(connector, SqlSchema.RootTable) ? ReadMetaData(connector) : null;
        }

        /// <summary>When the tree was last saved by any client (<c>tblUpdate.LastUpdate</c>), or null.</summary>
        public DateTime? GetLastUpdate()
        {
            using var connector = Open();
            return ReadLastUpdate(connector);
        }

        // ── Save ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces the stored tree with <paramref name="model"/> and returns the new <c>tblUpdate.LastUpdate</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The store is read-only.</exception>
        public DateTime? Save(ConnectionTreeModel model, SaveFilter? saveFilter = null)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (ReadOnly)
                throw new InvalidOperationException("The SQL connection database is configured as read-only; nothing was saved.");

            using var connector = Open();
            EnsureTables(connector);
            UpgradeSchema(connector, ReadMetaData(connector));

            var key = model.RootNode.PasswordString;
            var rows = new SqlConnectionRowMapper(_crypto, saveFilter, _clock).ToRows(model, key);
            var dbColumns = GetConnectionTableColumns(connector);
            var columns = SqlConnectionRowMapper.MappedColumns
                .Where(dbColumns.Contains)
                .ToList();

            using (var transaction = connector.Connection.BeginTransaction())
            {
                WriteRoot(connector, transaction, model.RootNode, key);
                Execute(connector, transaction, $"DELETE FROM {connector.Dialect.Quote(SqlSchema.ConnectionsTable)}");
                InsertRows(connector, transaction, columns, rows);
                WriteUpdateStamp(connector, transaction);
                transaction.Commit();
            }

            var lastUpdate = ReadLastUpdate(connector);
            _logger.LogInformation("Saved {Count} connections to {Database}", rows.Count, DisplayName);
            return lastUpdate;
        }

        // ── Schema ────────────────────────────────────────────────────────────

        /// <summary>Creates any missing table (never drops existing ones).</summary>
        public void EnsureTables(IDatabaseConnector connector)
        {
            foreach (var table in SqlSchema.Tables)
            {
                if (TableExists(connector, table))
                    continue;
                if (ReadOnly)
                    throw new SqlConnectionsException($"The database has no {table} table and the SQL connection is read-only, so it cannot be initialised.");

                _logger.LogInformation("Creating table {Table} in {Database}", table, DisplayName);
                Execute(connector, null, connector.Dialect.CreateTableSql(table));
            }
        }

        /// <summary>Adds columns that older schema versions lack and records the current version.</summary>
        private void UpgradeSchema(IDatabaseConnector connector, SqlConnectionListMetaData? metaData)
        {
            var existing = GetConnectionTableColumns(connector);
            foreach (var column in SqlSchema.ConnectionColumns)
            {
                if (existing.Contains(column.Name) || column.Kind == SqlColumnKind.Identity)
                    continue;

                _logger.LogInformation("Adding column {Column} to {Table}", column.Name, SqlSchema.ConnectionsTable);
                Execute(connector, null, connector.Dialect.AddColumnSql(column));
            }

            if (metaData is not null && metaData.ConfVersion < SqlSchema.CurrentVersion)
            {
                _logger.LogInformation("Upgrading connection database {Database} from version {Old} to {New}",
                    DisplayName, metaData.ConfVersion, SqlSchema.CurrentVersion);
                using var command = connector.CreateCommand(
                    $"UPDATE {connector.Dialect.Quote(SqlSchema.RootTable)} SET {connector.Dialect.Quote("ConfVersion")} = @version");
                AddParameter(command, "@version", SqlSchema.CurrentVersion.ToString(2));
                command.ExecuteNonQuery();
            }
        }

        public static bool TableExists(IDatabaseConnector connector, string table)
        {
            using var command = connector.CreateCommand(connector.Dialect.TableExistsSql);
            AddParameter(command, "@name", table);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        /// <summary>The column names of tblCons as they exist in the database.</summary>
        public static HashSet<string> GetConnectionTableColumns(IDatabaseConnector connector)
        {
            using var command = connector.CreateCommand(
                $"SELECT * FROM {connector.Dialect.Quote(SqlSchema.ConnectionsTable)} WHERE 1 = 0");
            using var reader = command.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));
            return columns;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private IDatabaseConnector Open()
        {
            var connector = _connectorFactory();
            try
            {
                connector.Connect();
                return connector;
            }
            catch
            {
                connector.Dispose();
                throw;
            }
        }

        private static SqlConnectionListMetaData? ReadMetaData(IDatabaseConnector connector)
        {
            var d = connector.Dialect;
            using var command = connector.CreateCommand(
                $"SELECT {d.Quote("Name")}, {d.Quote("Export")}, {d.Quote("Protected")}, {d.Quote("ConfVersion")} FROM {d.Quote(SqlSchema.RootTable)}");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var versionText = reader.IsDBNull(3) ? string.Empty : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture);
            if (!Version.TryParse(versionText, out var version))
                version = new Version(2, 2);

            SqlConnectionRowMapper.TryConvert(reader.GetValue(1), typeof(bool), out var export);
            return new SqlConnectionListMetaData(
                reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString() ?? string.Empty,
                reader.IsDBNull(2) ? string.Empty : reader.GetValue(2).ToString() ?? string.Empty,
                export as bool? ?? false,
                version);
        }

        /// <summary>
        /// The key that decrypts the passwords: the default key when <c>tblRoot.Protected</c> decrypts to
        /// "ThisIsNotProtected" with it, otherwise <paramref name="password"/> if it decrypts the marker.
        /// </summary>
        private string ResolveKey(string? protectedValue, string? password)
        {
            var defaultKey = new RootNodeInfo(RootNodeType.Connection).DefaultPassword;
            if (string.IsNullOrEmpty(protectedValue))
                return defaultKey;

            if (TryDecrypt(protectedValue, defaultKey) == NotProtectedMarker)
                return defaultKey;

            if (string.IsNullOrEmpty(password))
                throw new ConnectionFilePasswordException(passwordWasSupplied: false);

            return TryDecrypt(protectedValue, password) == ProtectedMarker
                ? password
                : throw new ConnectionFilePasswordException(passwordWasSupplied: true);
        }

        private string? TryDecrypt(string cipherText, string key)
        {
            try
            {
                return _crypto.Decrypt(cipherText, key);
            }
            catch (EncryptionException)
            {
                return null;
            }
        }

        private static List<IReadOnlyDictionary<string, object?>> ReadRows(IDatabaseConnector connector)
        {
            var d = connector.Dialect;
            using var command = connector.CreateCommand(
                $"SELECT * FROM {d.Quote(SqlSchema.ConnectionsTable)} ORDER BY {d.Quote("PositionID")} ASC");
            using var reader = command.ExecuteReader();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return rows;
        }

        private static DateTime? ReadLastUpdate(IDatabaseConnector connector)
        {
            var d = connector.Dialect;
            using var command = connector.CreateCommand(
                $"SELECT MAX({d.Quote("LastUpdate")}) FROM {d.Quote(SqlSchema.UpdateTable)}");
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        private void WriteRoot(IDatabaseConnector connector, DbTransaction transaction, RootNodeInfo root, string key)
        {
            var d = connector.Dialect;
            Execute(connector, transaction, $"DELETE FROM {d.Quote(SqlSchema.RootTable)}");

            var marker = root.IsPasswordProtected ? ProtectedMarker : NotProtectedMarker;
            using var command = connector.CreateCommand(
                $"INSERT INTO {d.Quote(SqlSchema.RootTable)} ({d.Quote("Name")}, {d.Quote("Export")}, {d.Quote("Protected")}, {d.Quote("ConfVersion")}) " +
                "VALUES (@name, @export, @protected, @version)",
                transaction);
            AddParameter(command, "@name", root.Name ?? "Connections");
            AddParameter(command, "@export", false);
            AddParameter(command, "@protected", _crypto.Encrypt(marker, key));
            AddParameter(command, "@version", SqlSchema.CurrentVersion.ToString(2));
            command.ExecuteNonQuery();
        }

        private void WriteUpdateStamp(IDatabaseConnector connector, DbTransaction transaction)
        {
            var d = connector.Dialect;
            Execute(connector, transaction, $"DELETE FROM {d.Quote(SqlSchema.UpdateTable)}");
            using var command = connector.CreateCommand(
                $"INSERT INTO {d.Quote(SqlSchema.UpdateTable)} ({d.Quote("LastUpdate")}) VALUES (@lastUpdate)", transaction);
            var now = _clock();
            // Millisecond precision (MySQL datetime(3)); SQL Server datetime rounds to 1/300 s.
            AddParameter(command, "@lastUpdate", new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Kind));
            command.ExecuteNonQuery();
        }

        private static void InsertRows(
            IDatabaseConnector connector,
            DbTransaction transaction,
            IReadOnlyList<string> columns,
            IReadOnlyList<Dictionary<string, object>> rows)
        {
            if (rows.Count == 0 || columns.Count == 0)
                return;

            var d = connector.Dialect;
            var rowsPerCommand = Math.Max(1, Math.Min(100, d.MaxParametersPerCommand / columns.Count));
            var columnList = string.Join(", ", columns.Select(d.Quote));

            for (var start = 0; start < rows.Count; start += rowsPerCommand)
            {
                var batch = rows.Skip(start).Take(rowsPerCommand).ToList();
                using var command = connector.CreateCommand(string.Empty, transaction);
                var values = new List<string>(batch.Count);
                for (var r = 0; r < batch.Count; r++)
                {
                    var names = new List<string>(columns.Count);
                    for (var c = 0; c < columns.Count; c++)
                    {
                        var name = $"@p{r}_{c}";
                        names.Add(name);
                        AddParameter(command, name, batch[r].TryGetValue(columns[c], out var value) ? value : DBNull.Value);
                    }

                    values.Add($"({string.Join(", ", names)})");
                }

                command.CommandText = $"INSERT INTO {d.Quote(SqlSchema.ConnectionsTable)} ({columnList}) VALUES {string.Join(", ", values)}";
                command.ExecuteNonQuery();
            }
        }

        private static void Execute(IDatabaseConnector connector, DbTransaction? transaction, string sql)
        {
            using var command = connector.CreateCommand(sql, transaction);
            command.ExecuteNonQuery();
        }

        private static void AddParameter(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    /// <summary>Result of <see cref="SqlConnectionsStore.Load"/>.</summary>
    /// <param name="LastUpdate">The database's <c>tblUpdate.LastUpdate</c> at load time.</param>
    /// <param name="SchemaVersion">The schema version found in <c>tblRoot</c> (before any upgrade).</param>
    public sealed record SqlLoadResult(ConnectionTreeModel Model, DateTime? LastUpdate, Version SchemaVersion);
}
