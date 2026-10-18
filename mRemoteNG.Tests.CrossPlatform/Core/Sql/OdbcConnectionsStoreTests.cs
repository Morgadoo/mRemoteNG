using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Connections.Sql;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;
using mRemoteNG.Tests.CrossPlatform.Core.Export;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Sql;

/// <summary>
/// The SQL connection storage through an ODBC data source: the MariaDB test server (<see cref="MariaDbTestServer"/>)
/// reached with unixODBC and MariaDB Connector/ODBC (driver name from MRNG_TEST_ODBC_DRIVER, default
/// "MariaDB Unicode"). Skipped when the server or the driver is missing.
/// </summary>
public sealed class OdbcConnectionsStoreTests
{
    private static readonly Lazy<string?> DriverUnavailable = new(ProbeDriver);

    private static string Driver => Environment.GetEnvironmentVariable("MRNG_TEST_ODBC_DRIVER") ?? "MariaDB Unicode";

    private static void RequireServerAndDriver()
    {
        Skip.If(MariaDbTestServer.UnavailableReason is not null, MariaDbTestServer.UnavailableReason);
        Skip.If(DriverUnavailable.Value is not null, DriverUnavailable.Value);
    }

    private static string DataSource()
    {
        var (host, port) = DatabaseConnectionSettings.SplitHost(MariaDbTestServer.Host);
        return $"Driver={{{Driver}}};Server={host};Port={port ?? DatabaseConnectionSettings.DefaultMySqlPort}";
    }

    private static DatabaseConnectionSettings Odbc(string database, string? password = null) =>
        new(DatabaseServerType.Odbc, DataSource(), database, MariaDbTestServer.User, password ?? MariaDbTestServer.Password)
        {
            ConnectTimeoutSeconds = 5,
        };

    private static string? ProbeDriver()
    {
        try
        {
            using var connector = new OdbcDatabaseConnector(Odbc(string.Empty));
            connector.Connect();
            return null;
        }
        catch (Exception ex)
        {
            return $"ODBC driver \"{Driver}\" cannot reach {MariaDbTestServer.Host} ({ex.Message}). Install unixodbc and odbc-mariadb, or set MRNG_TEST_ODBC_DRIVER.";
        }
    }

    [SkippableFact]
    public void Connector_DetectsMariaDb_AndRunsNamedParameterCommands()
    {
        RequireServerAndDriver();
        using var connector = new OdbcDatabaseConnector(Odbc(string.Empty));
        connector.Connect();

        connector.Dialect.Should().BeSameAs(SqlDialect.MySql);
        using var command = connector.CreateCommand("SELECT CONCAT(@a, '-@a-', @b, @a)");
        foreach (var (name, value) in new[] { ("@a", "x"), ("@b", "ü") })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        command.ExecuteScalar().Should().Be("x-@a-üx");
    }

    [SkippableFact]
    public void SaveThroughOdbc_IsTheSameDatabaseTheMySqlConnectorReads_AndBack()
    {
        RequireServerAndDriver();
        using var db = MariaDbTestServer.CreateDatabase();
        var odbc = new SqlConnectionsStore(Odbc(db.Name));
        var native = new SqlConnectionsStore(db.Settings());
        var tree = new ExportTestTree();
        tree.Root.Name = "Through ODBC";
        tree.Db1.Description = "Ünïcödé — 数据库";
        tree.Prod.IsExpanded = false;

        var saved = odbc.Save(new ConnectionTreeModel(tree.Root));

        var viaMySql = native.Load();
        viaMySql.LastUpdate.Should().Be(saved);
        viaMySql.Model.RootNode.Name.Should().Be("Through ODBC");
        var all = viaMySql.Model.GetRecursiveChildList();
        all.Should().HaveCount(new ConnectionTreeModel(tree.Root).GetRecursiveChildList().Count());
        var db1 = all.Single(c => c.ConstantID == tree.Db1.ConstantID);
        db1.Description.Should().Be("Ünïcödé — 数据库");
        db1.Password.Should().Be("dbpass");
        all.OfType<ContainerInfo>().Single(c => c.Name == "Prod").IsExpanded.Should().BeFalse();

        db1.Hostname = "db1-renamed.example.com";
        native.Save(viaMySql.Model);

        var viaOdbc = odbc.Load();
        viaOdbc.Model.GetRecursiveChildList().Single(c => c.ConstantID == tree.Db1.ConstantID).Hostname.Should().Be("db1-renamed.example.com");
        viaOdbc.LastUpdate.Should().Be(native.GetLastUpdate());
        odbc.GetMetaData()!.Name.Should().Be("Through ODBC");
    }

    [SkippableFact]
    public void MasterPassword_AndReadOnly_WorkThroughOdbc()
    {
        RequireServerAndDriver();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(Odbc(db.Name));
        var tree = new ExportTestTree();
        tree.Root.PasswordString = "s3cret";
        store.Save(new ConnectionTreeModel(tree.Root));

        store.Invoking(s => s.Load()).Should().Throw<ConnectionFilePasswordException>();
        store.Load("s3cret").Model.GetRecursiveChildList().Single(c => c.Name == "db1").Password.Should().Be("dbpass");

        var readOnly = new SqlConnectionsStore(Odbc(db.Name) with { ReadOnly = true });
        readOnly.Invoking(s => s.Save(new ConnectionTreeModel(tree.Root))).Should().Throw<InvalidOperationException>();
        readOnly.Load("s3cret").Model.GetRecursiveChildList().Should().HaveCount(new ConnectionTreeModel(tree.Root).GetRecursiveChildList().Count());
    }

    [SkippableFact]
    public async Task Tester_ReportsTheDetectedServer_AndClassifiesFailures()
    {
        RequireServerAndDriver();
        using var db = MariaDbTestServer.CreateDatabase();
        var tester = new DatabaseConnectionTester();

        var ok = await tester.TestAsync(Odbc(db.Name));
        ok.Succeeded.Should().BeTrue(ok.Message);
        ok.Message.Should().Contain("(MySQL/MariaDB)").And.Contain(db.Name).And.NotContain(MariaDbTestServer.Password);

        (await tester.TestAsync(Odbc(db.Name, password: "wrong-password"))).Status.Should().Be(ConnectionTestStatus.CredentialsRejected);
        (await tester.TestAsync(Odbc(db.Name) with { Host = "Driver={No Such Driver};Server=127.0.0.1" }))
            .Status.Should().Be(ConnectionTestStatus.DriverMissing);
        (await tester.TestAsync(Odbc(db.Name) with { Host = "no-such-dsn" })).Status.Should().Be(ConnectionTestStatus.DriverMissing);
    }

    [SkippableFact]
    public void ConnectionsService_LoadsAndSavesThroughOdbc()
    {
        RequireServerAndDriver();
        using var db = MariaDbTestServer.CreateDatabase();
        new SqlConnectionsStore(db.Settings()).Save(new ConnectionTreeModel(new ExportTestTree().Root));
        var service = new ConnectionsService(new CryptoProviderFactory());

        service.LoadFromDatabase(new SqlConnectionsStore(Odbc(db.Name)));
        service.ConnectionTreeModel!.RootNode.Children[0].Name = "Edited over ODBC";
        service.Save();

        new SqlConnectionsStore(db.Settings()).Load().Model.RootNode.Children[0].Name.Should().Be("Edited over ODBC");
    }
}
