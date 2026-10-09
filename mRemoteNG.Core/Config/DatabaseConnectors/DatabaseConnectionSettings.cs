using System.Globalization;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    /// <summary>SQL servers that can store connections (legacy <c>SQLServerType</c>: "mssql" / "mysql").</summary>
    public enum DatabaseServerType
    {
        /// <summary>Microsoft SQL Server (Microsoft.Data.SqlClient).</summary>
        MsSql,

        /// <summary>MySQL or MariaDB (MySqlConnector).</summary>
        MySql,
    }

    /// <summary>Where the connection database lives and how to log in.</summary>
    /// <param name="Host">Server name, optionally with a port: "db.example.com", "db:3307", "db\INSTANCE".</param>
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
