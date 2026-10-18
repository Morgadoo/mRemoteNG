using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    /// <summary>
    /// An ODBC data source (legacy <c>SQLServerType</c> "odbc") through System.Data.Odbc. The host is a DSN name or an
    /// ODBC connection string ("DSN=mrng" or "Driver={MariaDB Unicode};Server=db;Port=3306"); the database, user name
    /// and password are added to it when given. The database behind the data source must be SQL Server or
    /// MySQL/MariaDB: its SQL flavour is detected from the open connection. Commands take named <c>@parameters</c>
    /// like the other connectors and are sent with ODBC's positional <c>?</c> markers.
    /// </summary>
    public sealed class OdbcDatabaseConnector : DatabaseConnectorBase
    {
        private SqlDialect? _dialect;

        public OdbcDatabaseConnector(DatabaseConnectionSettings settings)
            : base(CreateConnection(settings))
        {
        }

        public override DatabaseServerType ServerType => DatabaseServerType.Odbc;

        /// <summary>The SQL flavour of the database behind the data source; available once connected.</summary>
        /// <exception cref="InvalidOperationException">Not connected yet.</exception>
        /// <exception cref="NotSupportedException">The database is neither SQL Server nor MySQL/MariaDB.</exception>
        public override SqlDialect Dialect => _dialect ??= SqlDialect.For(DetectServerType());

        public override DbCommand CreateCommand(string sql, DbTransaction? transaction = null) =>
            new PositionalParameterCommand((OdbcConnection)Connection)
            {
                CommandText = sql,
                Transaction = transaction,
            };

        private static OdbcConnection CreateConnection(DatabaseConnectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (!TryBuildConnectionString(settings, out var connectionString, out var error))
                throw new ArgumentException(error, nameof(settings));
            return new OdbcConnection(connectionString) { ConnectionTimeout = Math.Max(1, settings.ConnectTimeoutSeconds) };
        }

        /// <summary>
        /// The ODBC connection string for <paramref name="settings"/>: a plain DSN name becomes "DSN=name"; the
        /// database (unless the string names one), user name (UID) and password (PWD) are added when given.
        /// </summary>
        public static bool TryBuildConnectionString(DatabaseConnectionSettings settings, out string connectionString, out string error)
        {
            ArgumentNullException.ThrowIfNull(settings);
            connectionString = string.Empty;
            if (!TryParse(settings.Host, out var builder, out error))
                return false;

            if (!string.IsNullOrWhiteSpace(settings.DatabaseName) && !builder.ContainsKey("Database"))
                builder["Database"] = settings.DatabaseName.Trim();
            if (!string.IsNullOrEmpty(settings.Username))
                builder["UID"] = settings.Username;
            if (!string.IsNullOrEmpty(settings.Password))
                builder["PWD"] = settings.Password;
            connectionString = builder.ConnectionString;
            return true;
        }

        /// <summary>The DSN, driver and server of a data source, for display (never the password).</summary>
        public static string DescribeDataSource(string? source)
        {
            if (!TryParse(source, out var builder, out _))
                return "ODBC data source";
            var parts = new List<string>();
            foreach (var key in new[] { "DSN", "FILEDSN", "Driver", "Server" })
            {
                if (builder.TryGetValue(key, out var value) && Convert.ToString(value) is { Length: > 0 } text)
                    parts.Add($"{key}={text}");
            }
            return parts.Count == 0 ? "ODBC data source" : string.Join(";", parts);
        }

        private static bool TryParse(string? source, out OdbcConnectionStringBuilder builder, out string error)
        {
            builder = new OdbcConnectionStringBuilder();
            error = string.Empty;
            var text = (source ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "Enter the ODBC data source name (DSN) or connection string.";
                return false;
            }

            try
            {
                builder.ConnectionString = text.Contains('=') ? text : $"DSN={text}";
            }
            catch (ArgumentException ex)
            {
                error = $"The ODBC connection string is not valid: {ex.Message}";
                return false;
            }

            // Dsn and Driver are the builder's own keywords: ContainsKey is always true for them.
            if (string.IsNullOrEmpty(builder.Dsn) && string.IsNullOrEmpty(builder.Driver)
                && !(builder.TryGetValue("FILEDSN", out var fileDsn) && Convert.ToString(fileDsn) is { Length: > 0 }))
            {
                error = "The ODBC connection string needs a DSN or a Driver.";
                return false;
            }
            return true;
        }

        private DatabaseServerType DetectServerType()
        {
            if (!IsConnected)
                throw new InvalidOperationException("The SQL flavour of an ODBC data source is known once it is connected.");

            var connection = (OdbcConnection)Connection;
            var product = string.Empty;
            try
            {
                using var information = connection.GetSchema(DbMetaDataCollectionNames.DataSourceInformation);
                if (information.Rows.Count > 0)
                    product = Convert.ToString(information.Rows[0][DbMetaDataColumnNames.DataSourceProductName]) ?? string.Empty;
            }
            catch (Exception ex) when (ex is DbException or NotSupportedException or ArgumentException)
            {
                // Some drivers do not report it; the driver's file name is tried next.
            }

            return DetectServerType(product, connection.Driver)
                ?? throw new NotSupportedException(
                    $"The ODBC data source is {(product.Length > 0 ? product : connection.Driver)}; connections can be stored in SQL Server or MySQL/MariaDB.");
        }

        /// <summary>The SQL flavour from the DBMS name (SQLGetInfo SQL_DBMS_NAME) or the driver's file name.</summary>
        public static DatabaseServerType? DetectServerType(string? productName, string? driver)
        {
            foreach (var text in new[] { productName, driver })
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (ContainsAny(text, "SQL Server", "msodbcsql", "sqlncli", "sqlsrv", "freetds", "tdsodbc"))
                    return DatabaseServerType.MsSql;
                if (ContainsAny(text, "MySQL", "MariaDB", "maodbc", "myodbc"))
                    return DatabaseServerType.MySql;
            }
            return null;
        }

        private static bool ContainsAny(string text, params string[] values) =>
            values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Rewrites the <c>@name</c> markers of <paramref name="parameters"/> in <paramref name="sql"/> to ODBC's
        /// positional <c>?</c> and returns the parameters in marker order (a name used twice appears twice). Text in
        /// quotes, quoted identifiers and comments is left alone, and so are <c>@names</c> that are not parameters
        /// (MySQL user variables).
        /// </summary>
        public static string ToPositional(string sql, IReadOnlyList<DbParameter> parameters, out List<DbParameter> ordered)
        {
            ordered = [];
            if (string.IsNullOrEmpty(sql))
                return sql ?? string.Empty;

            var byName = new Dictionary<string, DbParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in parameters)
                byName.TryAdd(parameter.ParameterName.TrimStart('@', '?', ':'), parameter);

            var result = new StringBuilder(sql.Length);
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                var close = c switch
                {
                    '\'' => '\'',
                    '"' => '"',
                    '`' => '`',
                    '[' => ']',
                    _ => '\0',
                };
                if (close != '\0')
                {
                    var end = sql.IndexOf(close, i + 1);
                    end = end < 0 ? sql.Length : end + 1;
                    result.Append(sql, i, end - i);
                    i = end;
                    continue;
                }

                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    var end = sql.IndexOf('\n', i);
                    end = end < 0 ? sql.Length : end;
                    result.Append(sql, i, end - i);
                    i = end;
                    continue;
                }

                if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? sql.Length : end + 2;
                    result.Append(sql, i, end - i);
                    i = end;
                    continue;
                }

                if (c == '@' && (i == 0 || !IsIdentifierChar(sql[i - 1]) && sql[i - 1] != '@'))
                {
                    var start = i + 1;
                    var end = start;
                    while (end < sql.Length && IsIdentifierChar(sql[end]))
                        end++;
                    if (end > start && byName.TryGetValue(sql[start..end], out var parameter))
                    {
                        result.Append('?');
                        ordered.Add(parameter);
                        i = end;
                        continue;
                    }
                }

                result.Append(c);
                i++;
            }
            return result.ToString();
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// An <see cref="OdbcCommand"/> that takes named parameters: they are collected here and bound positionally
        /// (see <see cref="ToPositional"/>) when the command runs.
        /// </summary>
        private sealed class PositionalParameterCommand(OdbcConnection connection) : DbCommand
        {
            private readonly OdbcCommand _inner = connection.CreateCommand();
            private readonly OdbcCommand _named = new();
            private string _commandText = string.Empty;

            [AllowNull]
            public override string CommandText
            {
                get => _commandText;
                set => _commandText = value ?? string.Empty;
            }

            public override int CommandTimeout
            {
                get => _inner.CommandTimeout;
                set => _inner.CommandTimeout = value;
            }

            public override CommandType CommandType
            {
                get => _inner.CommandType;
                set => _inner.CommandType = value;
            }

            public override bool DesignTimeVisible { get; set; }

            public override UpdateRowSource UpdatedRowSource
            {
                get => _inner.UpdatedRowSource;
                set => _inner.UpdatedRowSource = value;
            }

            protected override DbConnection? DbConnection
            {
                get => _inner.Connection;
                set => _inner.Connection = (OdbcConnection?)value;
            }

            protected override DbParameterCollection DbParameterCollection => _named.Parameters;

            protected override DbTransaction? DbTransaction
            {
                get => _inner.Transaction;
                set => _inner.Transaction = (OdbcTransaction?)value;
            }

            public override void Cancel() => _inner.Cancel();

            public override int ExecuteNonQuery()
            {
                Bind();
                return _inner.ExecuteNonQuery();
            }

            public override object? ExecuteScalar()
            {
                Bind();
                return _inner.ExecuteScalar();
            }

            public override void Prepare()
            {
                Bind();
                _inner.Prepare();
            }

            protected override DbParameter CreateDbParameter() => new OdbcParameter();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                Bind();
                return _inner.ExecuteReader(behavior);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    _named.Dispose();
                }
                base.Dispose(disposing);
            }

            private void Bind()
            {
                _inner.Parameters.Clear();
                _inner.CommandText = ToPositional(_commandText, _named.Parameters.Cast<DbParameter>().ToList(), out var ordered);
                foreach (var parameter in ordered)
                    _inner.Parameters.Add((OdbcParameter)((ICloneable)parameter).Clone());
            }
        }
    }
}
