using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using MySqlConnector;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Connections.Sql;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Platform.Security;
using Xunit;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>
/// The SQL connection database in the running app, against MariaDB/MySQL (MRNG_TEST_MYSQL_HOST/USER/PASSWORD,
/// default 127.0.0.1:3307 mrng/mrng-pass). Without a reachable server the test only checks the error path.
/// </summary>
public class SqlStorageUiTests
{
    private static string Host => Environment.GetEnvironmentVariable("MRNG_TEST_MYSQL_HOST") ?? "127.0.0.1:3307";
    private static string User => Environment.GetEnvironmentVariable("MRNG_TEST_MYSQL_USER") ?? "mrng";
    private static string Password => Environment.GetEnvironmentVariable("MRNG_TEST_MYSQL_PASSWORD") ?? "mrng-pass";

    private static string? TryCreateDatabase()
    {
        var name = $"mrng_ui_{Guid.NewGuid():N}"[..24];
        try
        {
            var builder = new MySqlConnectionStringBuilder(MySqlDatabaseConnector.BuildConnectionString(
                new DatabaseConnectionSettings(DatabaseServerType.MySql, Host, string.Empty, User, Password) { ConnectTimeoutSeconds = 3 }));
            using var connection = new MySqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{name}`";
            command.ExecuteNonQuery();
            return name;
        }
        catch (MySqlException)
        {
            return null;
        }
    }

    private static void DropDatabase(string name)
    {
        var builder = new MySqlConnectionStringBuilder(MySqlDatabaseConnector.BuildConnectionString(
            new DatabaseConnectionSettings(DatabaseServerType.MySql, Host, name, User, Password)));
        using var connection = new MySqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `{name}`";
        command.ExecuteNonQuery();
    }

    [AvaloniaFact]
    public async Task OpenDatabase_LoadsTheTree_AndAnotherClientsSaveIsReloaded()
    {
        _ = TestHost.MainWindow;
        var settings = AppServices.GetRequired<AppSettingsService>();
        var runtime = AppServices.GetRequired<StorageRuntime>();
        var connections = AppServices.GetRequired<ConnectionsService>();
        var tree = TestHost.ViewModel.ConnectionTree;
        var crypto = AppServices.GetRequired<ICryptoProvider>();
        var before = settings.CreateEditableCopy();
        var database = TryCreateDatabase();
        try
        {
            settings.Update(s =>
            {
                s.UseSqlServer = true;
                s.SqlServerType = DatabaseServerType.MySql;
                s.SqlHost = database is null ? "127.0.0.1:1" : Host;
                s.SqlDatabaseName = database ?? "none";
                s.SqlUsername = User;
                s.SqlPasswordProtected = crypto.Protect(Password);
                s.SqlAutoReload = true;
            });

            if (database is null)
            {
                (await runtime.OpenDatabaseAsync(null)).Should().BeFalse("no server is reachable");
                return;
            }

            // Another client puts a tree into the database.
            var dbSettings = runtime.GetDatabaseSettings(settings.Current);
            var root = new RootNodeInfo(RootNodeType.Connection) { Name = "Shared" };
            root.AddChild(new ConnectionInfo { Name = "from-db", Hostname = "db-host" });
            new SqlConnectionsStore(dbSettings).Save(new ConnectionTreeModel(root));

            (await runtime.OpenDatabaseAsync(TestHost.MainWindow)).Should().BeTrue();
            Dispatcher.UIThread.RunJobs();
            connections.UsingDatabase.Should().BeTrue();
            tree.Nodes.Single().Name.Should().Be("Shared");
            tree.Nodes.Single().Children.Select(c => c.Name).Should().Equal("from-db");
            tree.IsDirty.Should().BeFalse();

            // The other client saves again; the runtime is told and reloads (no local changes).
            Thread.Sleep(20);
            root.AddChild(new ConnectionInfo { Name = "added-elsewhere", Hostname = "h2" });
            var stamp = new SqlConnectionsStore(dbSettings).Save(new ConnectionTreeModel(root))!.Value;
            RemoteUpdateEventArgs? update = null;
            runtime.RemoteUpdateDetected += (_, e) => update = e;
            runtime.OnRemoteUpdate(stamp);

            update!.Reloaded.Should().BeTrue();
            tree.Nodes.Single().Children.Select(c => c.Name).Should().Equal("from-db", "added-elsewhere");

            // Saving from the app goes back to the database.
            tree.AddConnection("added-here", Core.Connection.Protocol.ProtocolType.SSH2, "h3");
            tree.SaveToFile();
            tree.IsDirty.Should().BeFalse();
            new SqlConnectionsStore(dbSettings).Load().Model.GetRecursiveChildList().Select(c => c.Name)
                .Should().Contain("added-here");
        }
        finally
        {
            settings.Apply(before);
            tree.CreateNewTree();
            if (database is not null)
                DropDatabase(database);
        }
    }
}
