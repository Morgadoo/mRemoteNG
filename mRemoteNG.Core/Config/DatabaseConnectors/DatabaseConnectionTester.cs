using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    /// <summary>Outcome of <see cref="DatabaseConnectionTester"/> (legacy <c>ConnectionTestResult</c>).</summary>
    public enum ConnectionTestStatus
    {
        Succeeded,
        ServerNotAccessible,
        CredentialsRejected,
        UnknownDatabase,
        InvalidSettings,

        /// <summary>The ODBC driver manager, driver or data source is not installed.</summary>
        DriverMissing,
        UnknownError,
    }

    public sealed record ConnectionTestResult(ConnectionTestStatus Status, string Message)
    {
        public bool Succeeded => Status == ConnectionTestStatus.Succeeded;
    }

    /// <summary>Checks that the SQL server can be reached with the given settings (Options ▸ SQL Server ▸ Test).</summary>
    public sealed class DatabaseConnectionTester
    {
        private readonly Func<DatabaseConnectionSettings, IDatabaseConnector> _connectorFactory;

        public DatabaseConnectionTester()
            : this(DatabaseConnectorFactory.Create)
        {
        }

        public DatabaseConnectionTester(Func<DatabaseConnectionSettings, IDatabaseConnector> connectorFactory)
        {
            _connectorFactory = connectorFactory ?? throw new ArgumentNullException(nameof(connectorFactory));
        }

        public async Task<ConnectionTestResult> TestAsync(DatabaseConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var errors = settings.Validate();
            if (errors.Count > 0)
                return new ConnectionTestResult(ConnectionTestStatus.InvalidSettings, string.Join(" ", errors));

            try
            {
                using var connector = _connectorFactory(settings);
                await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connector.CreateCommand("SELECT 1");
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                var schema = await SchemaStateAsync(connector, cancellationToken).ConfigureAwait(false);
                var flavour = connector.ServerType == DatabaseServerType.Odbc
                    ? connector.Dialect.ServerType == DatabaseServerType.MySql ? " (MySQL/MariaDB)" : " (SQL Server)"
                    : string.Empty;
                return new ConnectionTestResult(ConnectionTestStatus.Succeeded, $"Connected to {settings.DisplayName}{flavour}. {schema}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var status = Classify(ex);
                return new ConnectionTestResult(status, Describe(status, ex));
            }
        }

        private static async Task<string> SchemaStateAsync(IDatabaseConnector connector, CancellationToken cancellationToken)
        {
            await using var command = connector.CreateCommand(connector.Dialect.TableExistsSql);
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = Serializers.Sql.SqlSchema.RootTable;
            command.Parameters.Add(parameter);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            return count > 0
                ? "The mRemoteNG tables exist."
                : "The database is empty; the mRemoteNG tables are created on the first load.";
        }

        public static ConnectionTestStatus Classify(Exception exception)
        {
            switch (exception)
            {
                case MySqlException mySql:
                    if (mySql.ErrorCode is MySqlErrorCode.AccessDenied or MySqlErrorCode.DatabaseAccessDenied)
                        return ConnectionTestStatus.CredentialsRejected;
                    if (mySql.ErrorCode == MySqlErrorCode.UnknownDatabase)
                        return ConnectionTestStatus.UnknownDatabase;
                    if (mySql.ErrorCode == MySqlErrorCode.UnableToConnectToHost)
                        return ConnectionTestStatus.ServerNotAccessible;
                    return ClassifyMessage(mySql.Message);
                case SqlException sql:
                    return sql.Number switch
                    {
                        18456 => ConnectionTestStatus.CredentialsRejected,
                        4060 => ConnectionTestStatus.UnknownDatabase,
                        -1 or 2 or 53 or 10060 or 10061 or 11001 => ConnectionTestStatus.ServerNotAccessible,
                        _ => ClassifyMessage(sql.Message),
                    };
                case OdbcException odbc:
                    return (odbc.Errors.Count > 0 ? odbc.Errors[0].SQLState : string.Empty) switch
                    {
                        "28000" => ConnectionTestStatus.CredentialsRejected,
                        "08001" or "08004" or "08S01" or "HYT00" or "HYT01" => ConnectionTestStatus.ServerNotAccessible,
                        // Data source name not found / driver could not be loaded.
                        "IM002" or "IM003" => ConnectionTestStatus.DriverMissing,
                        _ => ClassifyMessage(odbc.Message),
                    };
                case DbException db:
                    return ClassifyMessage(db.Message);
                case System.Net.Sockets.SocketException or TimeoutException:
                    return ConnectionTestStatus.ServerNotAccessible;
                case DllNotFoundException or TypeInitializationException { InnerException: DllNotFoundException }:
                    return ConnectionTestStatus.DriverMissing;
                default:
                    return ClassifyMessage(exception.Message);
            }
        }

        private static ConnectionTestStatus ClassifyMessage(string message)
        {
            if (message.Contains("server was not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("network-related", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Can't connect", StringComparison.OrdinalIgnoreCase))
                return ConnectionTestStatus.ServerNotAccessible;
            if (message.Contains("unixODBC", StringComparison.OrdinalIgnoreCase))
                return ConnectionTestStatus.DriverMissing;
            if (message.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Unknown database", StringComparison.OrdinalIgnoreCase))
                return ConnectionTestStatus.UnknownDatabase;
            if (message.Contains("Login failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
                return ConnectionTestStatus.CredentialsRejected;
            return ConnectionTestStatus.UnknownError;
        }

        private static string Describe(ConnectionTestStatus status, Exception ex) => status switch
        {
            ConnectionTestStatus.ServerNotAccessible => $"The server could not be reached: {ex.Message}",
            ConnectionTestStatus.CredentialsRejected => $"The server rejected the user name or password: {ex.Message}",
            ConnectionTestStatus.UnknownDatabase => $"The database does not exist: {ex.Message}",
            ConnectionTestStatus.DriverMissing =>
                $"The ODBC data source or driver was not found. ODBC needs the driver manager (unixODBC on Linux and macOS) and the database's ODBC driver: {ex.Message}",
            _ => $"Connection failed: {ex.Message}",
        };
    }
}
