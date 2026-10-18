using System.Data.Common;
using System.Data.Odbc;
using FluentAssertions;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Sql;

/// <summary>The ODBC connector without a server: connection strings, parameter markers and SQL flavour detection.</summary>
public sealed class OdbcDatabaseConnectorTests
{
    private static DatabaseConnectionSettings Settings(string host, string database = "", string user = "", string password = "") =>
        new(DatabaseServerType.Odbc, host, database, user, password);

    private static DbParameter Parameter(string name) => new OdbcParameter { ParameterName = name, Value = name };

    [Fact]
    public void ToPositional_ReplacesTheParameters_InMarkerOrder()
    {
        var a = Parameter("@a");
        var b = Parameter("@b");

        var sql = OdbcDatabaseConnector.ToPositional("SELECT * FROM t WHERE x = @a AND y = @B OR z = @a", [a, b], out var ordered);

        sql.Should().Be("SELECT * FROM t WHERE x = ? AND y = ? OR z = ?");
        ordered.Should().Equal(a, b, a);
    }

    [Fact]
    public void ToPositional_LeavesQuotedText_Comments_AndOtherAtSigns_Alone()
    {
        var name = Parameter("@name");

        var sql = OdbcDatabaseConnector.ToPositional(
            "SELECT '@name', \"@name\", `@name`, [@name], @@version, @session_var, mail@name -- @name\n" +
            "/* @name */ FROM t WHERE n = @name",
            [name], out var ordered);

        sql.Should().Be(
            "SELECT '@name', \"@name\", `@name`, [@name], @@version, @session_var, mail@name -- @name\n" +
            "/* @name */ FROM t WHERE n = ?");
        ordered.Should().ContainSingle().Which.Should().BeSameAs(name);
    }

    [Fact]
    public void ToPositional_DoesNotConfuseNamesThatStartAlike()
    {
        var p10 = Parameter("@p1_0");
        var p100 = Parameter("@p1_00");

        OdbcDatabaseConnector.ToPositional("VALUES (@p1_00, @p1_0)", [p10, p100], out var ordered).Should().Be("VALUES (?, ?)");
        ordered.Should().Equal(p100, p10);
    }

    [Fact]
    public void ConnectionString_FromADsnName_GetsTheDatabase_User_AndPassword()
    {
        OdbcDatabaseConnector.TryBuildConnectionString(Settings("mremoteng", "conns", "alice", "pa;ss}"), out var text, out _)
            .Should().BeTrue();

        var parsed = new OdbcConnectionStringBuilder(text);
        parsed["DSN"].Should().Be("mremoteng");
        parsed["Database"].Should().Be("conns");
        parsed["UID"].Should().Be("alice");
        parsed["PWD"].Should().Be("{pa;ss}}}", "a password with ODBC special characters is sent in braces");
    }

    [Fact]
    public void ConnectionString_KeepsTheDatabaseItNames()
    {
        OdbcDatabaseConnector.TryBuildConnectionString(Settings("Driver={MariaDB Unicode};Server=db;DATABASE=own", "other"), out var text, out _)
            .Should().BeTrue();

        var parsed = new OdbcConnectionStringBuilder(text);
        parsed.Driver.Should().Be("{MariaDB Unicode}");
        parsed["Database"].Should().Be("own");
        parsed.ContainsKey("UID").Should().BeFalse();
    }

    [Theory]
    [InlineData("", "Enter the ODBC data source name")]
    [InlineData("Server=db;Port=3306", "needs a DSN or a Driver")]
    [InlineData("DSN=x;=broken", "not valid")]
    public void Validate_ReportsUnusableDataSources(string host, string message)
    {
        Settings(host, "db").Validate().Should().ContainSingle().Which.Should().Contain(message);
    }

    [Fact]
    public void Validate_DoesNotNeedADatabase_OrAPort()
    {
        Settings("mremoteng").Validate().Should().BeEmpty();
        Settings("Driver={ODBC Driver 18 for SQL Server};Server=db,1433").Validate().Should().BeEmpty();
    }

    [Fact]
    public void AppSettings_AcceptOdbcWithoutADatabaseName()
    {
        var settings = new AppSettings { UseSqlServer = true, SqlServerType = DatabaseServerType.Odbc, SqlHost = "mremoteng" };
        settings.Validate().Should().BeEmpty();

        settings.SqlHost = string.Empty;
        settings.Validate().Should().ContainSingle().Which.Should().Contain("ODBC data source");
    }

    [Fact]
    public void DisplayName_NeverShowsThePassword()
    {
        var settings = Settings("Driver={MariaDB Unicode};Server=db.example.com;PWD=hunter2;UID=bob", "conns", "u", "p");

        settings.DisplayName.Should().Be("Driver={MariaDB Unicode};Server=db.example.com/conns");
        Settings("mremoteng").DisplayName.Should().Be("DSN=mremoteng");
        new DatabaseConnectionSettings(DatabaseServerType.MySql, "db:3307", "conns", "u", "p").DisplayName.Should().Be("db:3307/conns");
    }

    [Theory]
    [InlineData("MariaDB", "libmaodbc.so", DatabaseServerType.MySql)]
    [InlineData("MySQL", "libmyodbc8w.so", DatabaseServerType.MySql)]
    [InlineData("", "libmaodbc.so", DatabaseServerType.MySql)]
    [InlineData("Microsoft SQL Server", "libmsodbcsql-18.so", DatabaseServerType.MsSql)]
    [InlineData("", "msodbcsql18.dll", DatabaseServerType.MsSql)]
    [InlineData(null, "libtdsodbc.so", DatabaseServerType.MsSql)]
    [InlineData("PostgreSQL", "psqlodbcw.so", null)]
    public void DetectServerType_UsesTheDbmsName_ThenTheDriver(string? product, string driver, DatabaseServerType? expected)
    {
        OdbcDatabaseConnector.DetectServerType(product, driver).Should().Be(expected);
    }

    [Fact]
    public void Factory_CreatesTheOdbcConnector_WhoseDialectNeedsAConnection()
    {
        using var connector = DatabaseConnectorFactory.Create(Settings("mremoteng", "db"));

        connector.Should().BeOfType<OdbcDatabaseConnector>().Which.ServerType.Should().Be(DatabaseServerType.Odbc);
        connector.IsConnected.Should().BeFalse();
        connector.Invoking(c => c.Dialect).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => SqlDialect.For(DatabaseServerType.Odbc)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
