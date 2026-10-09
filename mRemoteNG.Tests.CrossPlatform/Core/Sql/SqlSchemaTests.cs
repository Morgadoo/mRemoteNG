using FluentAssertions;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Config.Serializers.Sql;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.SymmetricEncryption;
using mRemoteNG.Core.Tree;
using mRemoteNG.Tests.CrossPlatform.Core.Export;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Sql;

/// <summary>
/// SQL generation and row mapping without a server. SQL Server is only covered here: there is no
/// SQL Server instance in the test environment, so its DDL/connection strings are checked as text.
/// </summary>
public sealed class SqlSchemaTests
{
    [Fact]
    public void Schema_ContainsEveryLegacyColumn()
    {
        var names = SqlSchema.ConnectionColumns.Select(c => c.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain(["ID", "ConstantID", "PositionID", "ParentID", "LastChange", "Name", "Type", "Password",
            "InheritUseRestrictedAdmin", "UserViaAPI", "RedirectDiskDrivesCustom", "VNCSmartSizeMode", "StartProgramWorkDir"]);
        SqlSchema.ConnectionColumns.Count(c => !c.IsExtension).Should().Be(163, "the legacy 3.0 script has 163 columns");
    }

    [Fact]
    public void MsSql_CreateConnectionsTable_MatchesTheLegacyLayout()
    {
        var sql = SqlDialect.MsSql.CreateTableSql(SqlSchema.ConnectionsTable);

        sql.Should().StartWith("CREATE TABLE [tblCons] (");
        sql.Should().Contain("[ID] int NOT NULL IDENTITY(1,1),");
        sql.Should().Contain("[ConstantID] nvarchar(128) NOT NULL,");
        sql.Should().Contain("[AutomaticResize] bit NOT NULL DEFAULT 1,");
        sql.Should().Contain("[Favorite] tinyint NOT NULL,");
        sql.Should().Contain("[Description] nvarchar(1024) NULL,");
        sql.Should().Contain("[LastChange] datetime NOT NULL,");
        sql.Should().Contain("CONSTRAINT [PK_tblCons] PRIMARY KEY ([ConstantID])");
        sql.Should().EndWith(") ON [PRIMARY]");
        sql.Should().NotContain(",\n) ON", "no trailing comma before the closing parenthesis");
    }

    [Fact]
    public void MsSql_RootAndUpdateTables()
    {
        SqlDialect.MsSql.CreateTableSql(SqlSchema.RootTable).Should().Be(
            "CREATE TABLE [tblRoot] (\n    [Name] nvarchar(2048) NOT NULL,\n    [Export] bit NOT NULL,\n" +
            "    [Protected] nvarchar(max) NOT NULL,\n    [ConfVersion] nvarchar(15) NOT NULL\n) ON [PRIMARY]");
        SqlDialect.MsSql.CreateTableSql(SqlSchema.UpdateTable).Should().Be(
            "CREATE TABLE [tblUpdate] (\n    [LastUpdate] datetime NULL\n) ON [PRIMARY]");
        SqlDialect.MsSql.TableExistsSql.Should().Contain("INFORMATION_SCHEMA.TABLES").And.Contain("@name");
    }

    [Fact]
    public void MySql_CreateConnectionsTable_MatchesTheLegacyLayout()
    {
        var sql = SqlDialect.MySql.CreateTableSql(SqlSchema.ConnectionsTable);

        sql.Should().Contain("`ID` int NOT NULL AUTO_INCREMENT,");
        sql.Should().Contain("`Expanded` tinyint NOT NULL,");
        sql.Should().Contain("`RDPMinutesToIdleTimeout` int NOT NULL,");
        sql.Should().Contain("PRIMARY KEY (`ConstantID`)");
        sql.Should().Contain("UNIQUE KEY `ID_UNIQUE` (`ID`)");
        sql.Should().EndWith("ENGINE=InnoDB DEFAULT CHARSET=utf8mb3");
        SqlDialect.MySql.CreateTableSql(SqlSchema.UpdateTable).Should().Contain("`LastUpdate` datetime(3) NULL");
    }

    [Fact]
    public void AddColumn_GivesNotNullColumnsADefault_SoPopulatedTablesCanBeUpgraded()
    {
        var useRcg = SqlSchema.FindColumn("UseRCG")!;
        var userViaApi = SqlSchema.FindColumn("UserViaAPI")!;
        var ec2 = SqlSchema.FindColumn("EC2Region")!;

        SqlDialect.MsSql.AddColumnSql(useRcg).Should().Be("ALTER TABLE [tblCons] ADD [UseRCG] bit NOT NULL DEFAULT 0");
        SqlDialect.MsSql.AddColumnSql(userViaApi).Should().Be("ALTER TABLE [tblCons] ADD [UserViaAPI] nvarchar(512) NOT NULL DEFAULT ''");
        SqlDialect.MySql.AddColumnSql(ec2).Should().Be("ALTER TABLE `tblCons` ADD `EC2Region` varchar(32) NULL");
    }

    [Fact]
    public void Quote_EscapesIdentifiers()
    {
        SqlDialect.MsSql.Quote("a]b").Should().Be("[a]]b]");
        SqlDialect.MySql.Quote("a`b").Should().Be("`a``b`");
    }

    [Theory]
    [InlineData("db.example.com", "db.example.com", null)]
    [InlineData("db.example.com:3307", "db.example.com", 3307)]
    [InlineData("[::1]:1444", "::1", 1444)]
    [InlineData("fe80::1", "fe80::1", null)]
    public void SplitHost_ParsesLegacyHostPort(string input, string host, int? port)
    {
        DatabaseConnectionSettings.SplitHost(input).Should().Be((host, port));
    }

    [Fact]
    public void MsSql_ConnectionString_SqlLogin_LikeTheLegacyConnector()
    {
        var settings = new DatabaseConnectionSettings(DatabaseServerType.MsSql, "sql01:1500", "mremoteng", "sa", "pw");
        var builder = new SqlConnectionStringBuilder(MsSqlDatabaseConnector.BuildConnectionString(settings));

        builder.DataSource.Should().Be("sql01,1500");
        builder.InitialCatalog.Should().Be("mremoteng");
        builder.UserID.Should().Be("sa");
        builder.Password.Should().Be("pw");
        builder.IntegratedSecurity.Should().BeFalse();
        builder.TrustServerCertificate.Should().BeTrue();
        builder.ApplicationIntent.Should().Be(ApplicationIntent.ReadWrite);
        builder.ApplicationName.Should().Be("mRemoteNG");
    }

    [Fact]
    public void MsSql_ConnectionString_WithoutUser_UsesIntegratedSecurity()
    {
        var settings = new DatabaseConnectionSettings(DatabaseServerType.MsSql, @"sql01\SQLEXPRESS", "mremoteng", "", "", ReadOnly: true);
        var builder = new SqlConnectionStringBuilder(MsSqlDatabaseConnector.BuildConnectionString(settings));

        builder.DataSource.Should().Be(@"sql01\SQLEXPRESS");
        builder.IntegratedSecurity.Should().BeTrue();
        builder.ApplicationIntent.Should().Be(ApplicationIntent.ReadOnly);
    }

    [Fact]
    public void MySql_ConnectionString_DefaultsToPort3306()
    {
        var settings = new DatabaseConnectionSettings(DatabaseServerType.MySql, "mysql.local", "mremoteng", "user", "pw");
        var builder = new MySqlConnectionStringBuilder(MySqlDatabaseConnector.BuildConnectionString(settings));

        builder.Server.Should().Be("mysql.local");
        builder.Port.Should().Be(3306u);
        builder.Database.Should().Be("mremoteng");
        builder.UserID.Should().Be("user");
    }

    [Fact]
    public void Validate_ReportsMissingHostAndDatabase()
    {
        new DatabaseConnectionSettings(DatabaseServerType.MySql, "", "", "", "").Validate().Should().HaveCount(3);
        new DatabaseConnectionSettings(DatabaseServerType.MsSql, "h:70000", "d", "", "").Validate().Should().ContainSingle();
    }

    [Fact]
    public async Task Tester_ReportsInvalidSettings_WithoutConnecting()
    {
        var result = await new DatabaseConnectionTester().TestAsync(new DatabaseConnectionSettings(DatabaseServerType.MsSql, "", "", "", ""));
        result.Status.Should().Be(ConnectionTestStatus.InvalidSettings);
    }

    [Fact]
    public async Task Tester_ReportsUnreachableServer()
    {
        var settings = new DatabaseConnectionSettings(DatabaseServerType.MySql, "127.0.0.1:1", "db", "u", "p") { ConnectTimeoutSeconds = 2 };
        var result = await new DatabaseConnectionTester().TestAsync(settings);
        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(ConnectionTestStatus.ServerNotAccessible);
    }

    // ── Row mapping ─────────────────────────────────────────────────────────

    [Fact]
    public void Mapper_RoundTripsTheTree_InPreOrder()
    {
        var tree = new ExportTestTree();
        var mapper = new SqlConnectionRowMapper(new LegacyRijndaelCryptographyProvider());

        var rows = mapper.ToRows(new ConnectionTreeModel(tree.Root), "mR3m");

        rows.Select(r => r["Name"]).Should().Equal("Prod", "web", "Databases", "db1", "lab");
        rows.Select(r => r["PositionID"]).Should().Equal(1, 2, 3, 4, 5);
        rows[0]["ParentID"].Should().Be("0");
        rows[1]["ParentID"].Should().Be(tree.Prod.ConstantID);
        rows[0]["Type"].Should().Be("Container");
        rows[3]["Protocol"].Should().Be("RDP");
        rows[3]["Colors"].Should().Be("Colors16Bit");
        rows[3]["Password"].Should().NotBe("dbpass");
        rows[1]["InheritUsername"].Should().Be(true);
        rows[4]["Favorite"].Should().Be(true);
        rows.Should().OnlyContain(r => r.Values.All(v => v != null), "the legacy reader casts every column without null checks");

        var back = mapper.FromRows(rows.Select(r => (IReadOnlyDictionary<string, object?>)r.ToDictionary(k => k.Key, k => (object?)k.Value)), "mR3m");
        back.GetRecursiveChildList().Select(n => n.Name).Should().Equal("Prod", "web", "Databases", "db1", "lab");
        var db1 = back.GetRecursiveChildList().Single(n => n.Name == "db1");
        db1.Password.Should().Be("dbpass");
        db1.Domain.Should().Be("CORP");
        db1.Parent!.Name.Should().Be("Databases");
    }

    [Fact]
    public void Mapper_StoresOwnValues_NotInheritedOnes()
    {
        var root = new mRemoteNG.Core.Tree.Root.RootNodeInfo(mRemoteNG.Core.Tree.Root.RootNodeType.Connection);
        var outer = new ContainerInfo { Name = "outer" };
        var inner = new ContainerInfo { Name = "inner", Username = "folder-user" };
        var child = new ConnectionInfo { Name = "child", Username = "own-user" };
        root.AddChild(outer);
        outer.AddChild(inner);
        inner.AddChild(child);
        child.Inheritance.Username = true;
        child.Username.Should().Be("folder-user", "inheritance is active two levels below the root");

        var rows = new SqlConnectionRowMapper(new LegacyRijndaelCryptographyProvider()).ToRows(new ConnectionTreeModel(root), "mR3m");

        var row = rows.Single(r => (string)r["Name"] == "child");
        row["Username"].Should().Be("own-user");
        row["InheritUsername"].Should().Be(true);
        child.Inheritance.Username.Should().BeTrue("the flags are restored after reading the own values");
    }

    [Fact]
    public void Mapper_ToleratesMissingNullAndUnknownValues()
    {
        var mapper = new SqlConnectionRowMapper(new LegacyRijndaelCryptographyProvider());
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["ConstantID"] = "a", ["Type"] = "Connection", ["Name"] = "a", ["PositionID"] = 2, ["ParentID"] = "missing",
                ["Protocol"] = "SSH2", ["Port"] = (sbyte)22, ["Colors"] = "NotAColor", ["Hostname"] = null, ["Password"] = "not-encrypted", ["RedirectKeys"] = (byte)1 },
            new Dictionary<string, object?> { ["ConstantID"] = "f", ["Type"] = "Container", ["Name"] = "f", ["PositionID"] = 1, ["ParentID"] = "0", ["Expanded"] = true },
            new Dictionary<string, object?> { ["ConstantID"] = "x", ["Type"] = "Something", ["Name"] = "ignored" },
        };

        var model = mapper.FromRows(rows, "mR3m");

        model.RootNode.Children.Select(c => c.Name).Should().Equal("f", "a");
        var a = model.RootNode.Children[1];
        a.Protocol.Should().Be(ProtocolType.SSH2);
        a.Port.Should().Be(22);
        a.Colors.Should().Be(new ConnectionInfo().Colors, "unknown enum names keep the default");
        a.Hostname.Should().BeEmpty();
        a.Password.Should().Be("not-encrypted", "values that do not decrypt are kept (legacy behaviour)");
        a.RedirectKeys.Should().BeTrue();
        ((ContainerInfo)model.RootNode.Children[0]).IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Mapper_SaveFilter_BlanksCredentials()
    {
        var tree = new ExportTestTree();
        var rows = new SqlConnectionRowMapper(new LegacyRijndaelCryptographyProvider(), new SaveFilter(disableEverything: true))
            .ToRows(new ConnectionTreeModel(tree.Root), "mR3m");

        var db1 = rows.Single(r => (string)r["Name"] == "db1");
        db1["Username"].Should().Be("");
        db1["Password"].Should().Be("");
        db1["Domain"].Should().Be("");
        rows.Single(r => (string)r["Name"] == "web")["InheritUsername"].Should().Be(false);
    }
}
