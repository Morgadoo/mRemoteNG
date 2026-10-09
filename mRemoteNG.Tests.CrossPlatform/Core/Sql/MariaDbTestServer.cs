using MySqlConnector;
using mRemoteNG.Core.Config.DatabaseConnectors;

namespace mRemoteNG.Tests.CrossPlatform.Core.Sql;

/// <summary>
/// A MySQL/MariaDB server for the SQL integration tests. Configured with environment variables
/// (defaults in brackets): MRNG_TEST_MYSQL_HOST [127.0.0.1:3307], MRNG_TEST_MYSQL_USER [mrng],
/// MRNG_TEST_MYSQL_PASSWORD [mrng-pass]. The user needs CREATE/DROP DATABASE rights.
/// Tests using it are skipped when the server cannot be reached.
/// </summary>
internal static class MariaDbTestServer
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    public static string Host => Environment.GetEnvironmentVariable("MRNG_TEST_MYSQL_HOST") ?? "127.0.0.1:3307";

    public static string User => Environment.GetEnvironmentVariable("MRNG_TEST_MYSQL_USER") ?? "mrng";

    public static string Password => Environment.GetEnvironmentVariable("MRNG_TEST_MYSQL_PASSWORD") ?? "mrng-pass";

    /// <summary>Why the server cannot be used, or null when it can.</summary>
    public static string? UnavailableReason => Unavailable.Value;

    public static DatabaseConnectionSettings Settings(string database, bool readOnly = false) =>
        new(DatabaseServerType.MySql, Host, database, User, Password, readOnly) { ConnectTimeoutSeconds = 5 };

    /// <summary>Creates an empty database and returns a handle that drops it.</summary>
    public static TestDatabase CreateDatabase()
    {
        var name = $"mrng_test_{Guid.NewGuid():N}"[..30];
        Execute($"CREATE DATABASE `{name}`");
        return new TestDatabase(name);
    }

    public static void Execute(string sql, string? database = null)
    {
        using var connection = new MySqlConnection(ConnectionString(database));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static object? Scalar(string sql, string database)
    {
        using var connection = new MySqlConnection(ConnectionString(database));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ConnectionString(string? database)
    {
        var builder = new MySqlConnectionStringBuilder(MySqlDatabaseConnector.BuildConnectionString(Settings(database ?? string.Empty)))
        {
            AllowUserVariables = true,
        };
        if (database is null)
            builder.Database = string.Empty;
        return builder.ConnectionString;
    }

    private static string? Probe()
    {
        try
        {
            Execute("SELECT 1");
            return null;
        }
        catch (Exception ex)
        {
            return $"MySQL/MariaDB test server {Host} not reachable ({ex.Message}). Set MRNG_TEST_MYSQL_HOST/USER/PASSWORD.";
        }
    }

    internal sealed class TestDatabase(string name) : IDisposable
    {
        public string Name { get; } = name;

        public DatabaseConnectionSettings Settings(bool readOnly = false) => MariaDbTestServer.Settings(Name, readOnly);

        public void Dispose()
        {
            try
            {
                Execute($"DROP DATABASE IF EXISTS `{Name}`");
            }
            catch (MySqlException)
            {
                // Best effort.
            }
        }
    }
}
