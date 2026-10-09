using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    /// <summary>Shared plumbing of the ADO.NET based connectors.</summary>
    public abstract class DatabaseConnectorBase : IDatabaseConnector
    {
        private bool _disposed;

        protected DatabaseConnectorBase(DbConnection connection)
        {
            Connection = connection;
        }

        public abstract DatabaseServerType ServerType { get; }

        public SqlDialect Dialect => SqlDialect.For(ServerType);

        public DbConnection Connection { get; }

        public bool IsConnected => Connection.State == ConnectionState.Open;

        public void Connect()
        {
            if (!IsConnected)
                Connection.Open();
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                await Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Disconnect() => Connection.Close();

        public DbCommand CreateCommand(string sql, DbTransaction? transaction = null)
        {
            var command = Connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            return command;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Connection.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>MySQL / MariaDB through MySqlConnector. Host may carry a port ("db:3307"; default 3306).</summary>
    public sealed class MySqlDatabaseConnector : DatabaseConnectorBase
    {
        public MySqlDatabaseConnector(DatabaseConnectionSettings settings)
            : base(new MySqlConnection(BuildConnectionString(settings)))
        {
        }

        public override DatabaseServerType ServerType => DatabaseServerType.MySql;

        public static string BuildConnectionString(DatabaseConnectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var (host, port) = DatabaseConnectionSettings.SplitHost(settings.Host);
            var builder = new MySqlConnectionStringBuilder
            {
                Server = host,
                Port = (uint)(port ?? DatabaseConnectionSettings.DefaultMySqlPort),
                Database = settings.DatabaseName,
                UserID = settings.Username,
                Password = settings.Password,
                ConnectionTimeout = (uint)Math.Max(1, settings.ConnectTimeoutSeconds),
                // Legacy scripts use @session variables.
                AllowUserVariables = true,
                // DATETIME values come back as DateTime even when the server stores zero dates.
                ConvertZeroDateTime = true,
                CharacterSet = "utf8mb4",
            };
            return builder.ConnectionString;
        }
    }

    /// <summary>
    /// Microsoft SQL Server through Microsoft.Data.SqlClient, configured like the legacy connector:
    /// SQL login when a user name is given (host:port, default 1433; encrypted, server certificate trusted),
    /// Windows integrated security otherwise.
    /// </summary>
    public sealed class MsSqlDatabaseConnector : DatabaseConnectorBase
    {
        public MsSqlDatabaseConnector(DatabaseConnectionSettings settings)
            : base(new SqlConnection(BuildConnectionString(settings)))
        {
        }

        public override DatabaseServerType ServerType => DatabaseServerType.MsSql;

        public static string BuildConnectionString(DatabaseConnectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var builder = new SqlConnectionStringBuilder
            {
                ApplicationName = "mRemoteNG",
                InitialCatalog = settings.DatabaseName,
                ConnectTimeout = Math.Max(1, settings.ConnectTimeoutSeconds),
                ApplicationIntent = settings.ReadOnly ? ApplicationIntent.ReadOnly : ApplicationIntent.ReadWrite,
            };

            if (string.IsNullOrEmpty(settings.Username))
            {
                builder.DataSource = settings.Host;
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.DataSource = ToDataSource(settings.Host);
                builder.UserID = settings.Username;
                builder.Password = settings.Password;
                builder.IntegratedSecurity = false;
                builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                builder.TrustServerCertificate = true;
            }

            return builder.ConnectionString;
        }

        /// <summary>"host:port" → "host,port" (SQL Server syntax); "host\instance" and "host,port" pass through.</summary>
        private static string ToDataSource(string host)
        {
            if (host.Contains(',') || host.Contains('\\'))
                return host;
            var (name, port) = DatabaseConnectionSettings.SplitHost(host);
            return $"{name},{port ?? DatabaseConnectionSettings.DefaultMsSqlPort}";
        }
    }

    /// <summary>Creates the connector for the configured server type.</summary>
    public static class DatabaseConnectorFactory
    {
        public static IDatabaseConnector Create(DatabaseConnectionSettings settings) => settings.ServerType switch
        {
            DatabaseServerType.MySql => new MySqlDatabaseConnector(settings),
            DatabaseServerType.MsSql => new MsSqlDatabaseConnector(settings),
            _ => throw new NotSupportedException($"Unsupported database type {settings.ServerType}."),
        };
    }
}
