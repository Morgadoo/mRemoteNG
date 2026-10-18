using System.Globalization;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    /// <summary>SQL servers that can store connections (legacy <c>SQLServerType</c>: "mssql" / "mysql" / "odbc").</summary>
    public enum DatabaseServerType
    {
        /// <summary>Microsoft SQL Server (Microsoft.Data.SqlClient).</summary>
        MsSql,

        /// <summary>MySQL or MariaDB (MySqlConnector).</summary>
        MySql,

        /// <summary>
        /// An ODBC data source (System.Data.Odbc) whose database is SQL Server or MySQL/MariaDB; the SQL flavour is
        /// detected when connecting. Needs the ODBC driver manager (unixODBC on Linux and macOS) and the driver.
        /// </summary>
        Odbc,
    }

    /// <summary>Where the connection database lives and how to log in.</summary>
    /// <param name="Host">
    /// Server name, optionally with a port: "db.example.com", "db:3307", "db\INSTANCE". For
    /// <see cref="DatabaseServerType.Odbc"/>: a DSN name or an ODBC connection string.
    /// </param>
    /// <param name="DatabaseName">The database; optional for ODBC (the data source usually names it).</param>
    /// <param name="Username">Login; empty means Windows integrated authentication on SQL Server.</param>
    /// <param name="ReadOnly">Never write to the database (legacy <c>SQLReadOnly</c>).</param>
    public sealed record DatabaseConnectionSettings(
        DatabaseServerType ServerType,
        string Host,
        string DatabaseName,
        string Username,
        string Password,
        bool ReadOnly = false)
    {
        public const int DefaultMySqlPort = 3306;
        public const int DefaultMsSqlPort = 1433;

        /// <summary>Seconds to wait for the server when opening a connection.</summary>
        public int ConnectTimeoutSeconds { get; init; } = 15;

        public int DefaultPort => ServerType == DatabaseServerType.MySql ? DefaultMySqlPort : DefaultMsSqlPort;

        /// <summary>
        /// "host/database" for messages and logs. An ODBC connection string is reduced to its DSN, driver and
        /// server so a password in it is never shown.
        /// </summary>
        public string DisplayName
        {
            get
            {
                var source = ServerType == DatabaseServerType.Odbc ? OdbcDatabaseConnector.DescribeDataSource(Host) : Host;
                return string.IsNullOrWhiteSpace(DatabaseName) ? source : $"{source}/{DatabaseName}";
            }
        }

        /// <summary>Splits "host:port" (legacy format). Returns the port as null when none is given.</summary>
        public static (string Host, int? Port) SplitHost(string host)
        {
            var value = (host ?? string.Empty).Trim();
            // [IPv6]:port
            if (value.StartsWith('['))
            {
                var close = value.IndexOf(']');
                if (close > 0)
                {
                    var address = value[1..close];
                    var rest = value[(close + 1)..];
                    return rest.StartsWith(':') && int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var p6)
                        ? (address, p6)
                        : (address, null);
                }
            }

            var colon = value.LastIndexOf(':');
            // A bare IPv6 address has several colons and no port.
            if (colon > 0 && value.IndexOf(':') == colon
                && int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                return (value[..colon], port);
            }

            return (value, null);
        }

        /// <summary>Validation messages; empty when the settings can be used.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (ServerType == DatabaseServerType.Odbc)
            {
                if (string.IsNullOrWhiteSpace(Host))
                    errors.Add("Enter the ODBC data source name (DSN) or connection string.");
                else if (!OdbcDatabaseConnector.TryBuildConnectionString(this, out _, out var error))
                    errors.Add(error);
                return errors;
            }

            if (string.IsNullOrWhiteSpace(Host))
                errors.Add("Enter the SQL server host name.");
            if (string.IsNullOrWhiteSpace(DatabaseName))
                errors.Add("Enter the database name.");
            var (_, port) = SplitHost(Host ?? string.Empty);
            if (port is < 1 or > 65535)
                errors.Add("The SQL server port must be between 1 and 65535.");
            if (ServerType == DatabaseServerType.MySql && string.IsNullOrWhiteSpace(Username))
                errors.Add("MySQL/MariaDB needs a user name.");
            return errors;
        }
    }
}
