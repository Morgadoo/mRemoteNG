using System.Xml.Linq;
using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Connections.Multiuser;
using mRemoteNG.Core.Config.Connections.Sql;
using mRemoteNG.Core.Config.Serializers.Sql;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Security.SymmetricEncryption;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Tests.CrossPlatform.Core.Export;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Sql;

/// <summary>
/// Integration tests of the SQL connection storage against a real MariaDB/MySQL server
/// (see <see cref="MariaDbTestServer"/>; skipped when none is reachable).
/// </summary>
public sealed class MySqlConnectionsStoreTests
{
    private static void RequireServer() =>
        Skip.If(MariaDbTestServer.UnavailableReason is not null, MariaDbTestServer.UnavailableReason);

    private static ConnectionTreeModel SampleTree()
    {
        var tree = new ExportTestTree();
        return new ConnectionTreeModel(tree.Root);
    }

    [SkippableFact]
    public void Load_EmptyDatabase_CreatesTheLegacyTables_AndReturnsAnEmptyTree()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());

        var result = store.Load();

        result.Model.RootNode.Children.Should().BeEmpty();
        foreach (var table in SqlSchema.Tables)
            Convert.ToInt32(MariaDbTestServer.Scalar($"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '{table}'", db.Name))
                .Should().Be(1, $"{table} is created");
    }

    [SkippableFact]
    public void SaveThenLoad_RoundTripsTheTree()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());
        var tree = new ExportTestTree();
        tree.Root.Name = "Team connections";
        tree.Db1.Description = "Ünïcödé — 数据库";
        tree.Db1.RDPAuthenticationLevel = AuthenticationLevel.WarnOnFailedAuth;
        tree.Db1.Inheritance.Domain = true;
        tree.Prod.IsExpanded = false;

        var lastUpdate = store.Save(new ConnectionTreeModel(tree.Root));
        var loaded = store.Load();

        lastUpdate.Should().NotBeNull();
        loaded.LastUpdate.Should().Be(lastUpdate);
        var root = loaded.Model.RootNode;
        root.Name.Should().Be("Team connections");
        root.Children.Select(c => c.Name).Should().Equal("Prod", "lab");

        var prod = root.Children[0].Should().BeOfType<ContainerInfo>().Subject;
        prod.IsExpanded.Should().BeFalse();
        prod.Children.Select(c => c.Name).Should().Equal("web", "Databases");
        prod.Username.Should().Be("produser");

        var web = prod.Children[0];
        web.Protocol.Should().Be(ProtocolType.SSH2);
        web.Port.Should().Be(2222);
        web.Password.Should().Be("webpass");
        web.Inheritance.Username.Should().BeTrue();

        var db1 = ((ContainerInfo)prod.Children[1]).Children.Single();
        db1.ConstantID.Should().Be(tree.Db1.ConstantID);
        db1.Hostname.Should().Be("db1.example.com");
        db1.Password.Should().Be("dbpass");
        db1.RDGatewayPassword.Should().Be("gwpass");
        db1.Colors.Should().Be(RDPColors.Colors16Bit);
        db1.Resolution.Should().Be(RDPResolutions.Res1366x768);
        db1.RDPAuthenticationLevel.Should().Be(AuthenticationLevel.WarnOnFailedAuth);
        db1.RedirectPorts.Should().BeTrue();
        db1.Description.Should().Be("Ünïcödé — 数据库");
        db1.Inheritance.Domain.Should().BeTrue();

        root.Children[1].Favorite.Should().BeTrue();
    }

    [SkippableFact]
    public void Save_StoresPasswordsWithTheLegacyCipher_AndEnumsByName()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());
        var tree = new ExportTestTree();
        store.Save(new ConnectionTreeModel(tree.Root));

        var cipher = (string)MariaDbTestServer.Scalar($"SELECT Password FROM tblCons WHERE ConstantID = '{tree.Db1.ConstantID}'", db.Name)!;
        cipher.Should().NotBe("dbpass");
        new LegacyRijndaelCryptographyProvider().Decrypt(cipher, "mR3m").Should().Be("dbpass");
        MariaDbTestServer.Scalar($"SELECT Colors FROM tblCons WHERE ConstantID = '{tree.Db1.ConstantID}'", db.Name).Should().Be("Colors16Bit");
        MariaDbTestServer.Scalar($"SELECT ParentID FROM tblCons WHERE ConstantID = '{tree.Prod.ConstantID}'", db.Name).Should().Be("0");
        MariaDbTestServer.Scalar("SELECT ConfVersion FROM tblRoot", db.Name).Should().Be("3.2");
        var marker = (string)MariaDbTestServer.Scalar("SELECT Protected FROM tblRoot", db.Name)!;
        new LegacyRijndaelCryptographyProvider().Decrypt(marker, "mR3m").Should().Be("ThisIsNotProtected");
    }

    [SkippableFact]
    public void MasterPassword_IsRequiredToLoad()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());
        var tree = new ExportTestTree();
        tree.Root.PasswordString = "s3cret";
        store.Save(new ConnectionTreeModel(tree.Root));

        store.Invoking(s => s.Load()).Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeFalse();
        store.Invoking(s => s.Load("wrong")).Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeTrue();

        var loaded = store.Load("s3cret").Model;
        loaded.RootNode.IsPasswordProtected.Should().BeTrue();
        loaded.GetRecursiveChildList().Single(c => c.Name == "db1").Password.Should().Be("dbpass");
    }

    [SkippableFact]
    public void ReadOnly_RefusesToSave_AndDoesNotCreateTables()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var readOnly = new SqlConnectionsStore(db.Settings(readOnly: true));

        readOnly.Invoking(s => s.Load()).Should().Throw<SqlConnectionsException>();
        readOnly.Invoking(s => s.Save(SampleTree())).Should().Throw<InvalidOperationException>();

        new SqlConnectionsStore(db.Settings()).Save(SampleTree());
        readOnly.Load().Model.GetRecursiveChildList().Should().HaveCount(5, "a read-only store still loads");
    }

    [SkippableFact]
    public void LegacySchemaScript_IsLoadedAndSaved_AndGainsTheMissingColumns()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "Sql", "legacy_mysql_schema_3_0.sql"));
        MariaDbTestServer.Execute(script, db.Name);

        // A row as the legacy DataTableSerializer writes it: every column of its script, no NULLs.
        string[] addedAfter30 = ["PluginData", "TerminalBackend", "InheritTerminalBackend"];
        var crypto = new LegacyRijndaelCryptographyProvider();
        var values = SqlSchema.ConnectionColumns
            .Where(c => !c.IsExtension && c.Kind != SqlColumnKind.Identity && !addedAfter30.Contains(c.Name))
            .ToDictionary(c => c.Name, c => c.Kind == SqlColumnKind.String ? "''" : c.Kind == SqlColumnKind.DateTime ? "NOW()" : "0");
        void Set(string column, string value) => values[column] = "'" + value + "'";
        Set("ConstantID", "legacy-1");
        Set("ParentID", "0");
        values["PositionID"] = "1";
        Set("Name", "legacy rdp");
        Set("Type", "Connection");
        Set("Protocol", "RDP");
        Set("Hostname", "rdp.legacy");
        values["Port"] = "3390";
        Set("Username", "legacyuser");
        Set("Password", crypto.Encrypt("legacypw", "mR3m"));
        Set("Colors", "Colors24Bit");
        Set("Resolution", "FitToWindow");
        Set("RedirectDiskDrives", "Local");
        Set("RedirectSound", "DoNotPlay");
        Set("RDGatewayUsageMethod", "Never");
        Set("RDGatewayUseConnectionCredentials", "Yes");
        Set("RDPAuthenticationLevel", "NoAuth");
        Set("RenderingEngine", "IE");
        Set("SoundQuality", "Dynamic");
        Set("VNCAuthMode", "AuthVNC");
        Set("Icon", "mRemoteNG");
        Set("Panel", "General");
        MariaDbTestServer.Execute(
            "INSERT INTO tblRoot (Name, Export, Protected, ConfVersion) VALUES ('Legacy root', 0, '" + crypto.Encrypt("ThisIsNotProtected", "mR3m") + "', '3.0');" +
            $"INSERT INTO tblCons ({string.Join(", ", values.Keys)}) VALUES ({string.Join(", ", values.Values)})",
            db.Name);

        var store = new SqlConnectionsStore(db.Settings());
        var model = store.Load().Model;

        model.RootNode.Name.Should().Be("Legacy root");
        var rdp = model.RootNode.Children.Should().ContainSingle().Subject;
        rdp.Protocol.Should().Be(ProtocolType.RDP);
        rdp.Hostname.Should().Be("rdp.legacy");
        rdp.Port.Should().Be(3390);
        rdp.Password.Should().Be("legacypw");
        rdp.Username.Should().Be("legacyuser");
        rdp.Colors.Should().Be(RDPColors.Colors24Bit);
        rdp.Resolution.Should().Be(RDPResolutions.FitToWindow);
        rdp.RedirectDiskDrives.Should().Be(RDPDiskDrives.Local);

        // The columns the legacy serializer writes but its script does not create were added.
        var columns = Convert.ToInt32(MariaDbTestServer.Scalar(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'tblCons' AND column_name IN ('EnvironmentTags', 'RDGatewayUserViaAPI', 'PluginData', 'TerminalBackend', 'InheritTerminalBackend', 'TabColor')", db.Name));
        columns.Should().Be(6, "the 3.1/3.2 columns and the shared extension columns are added too");
        MariaDbTestServer.Scalar("SELECT ConfVersion FROM tblRoot", db.Name).Should().Be("3.2");

        rdp.Name = "renamed";
        store.Save(model);
        store.Load().Model.RootNode.Children.Single().Name.Should().Be("renamed");
    }

    [SkippableFact]
    public void OlderSchemaVersion_IsUpgradedTo30()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());
        store.Save(SampleTree());

        // Make it look like a 2.8 database: drop the columns added by the 2.9 and 3.0 upgrades.
        MariaDbTestServer.Execute(
            "ALTER TABLE tblCons DROP COLUMN UseRCG, DROP COLUMN EC2Region, DROP COLUMN UserViaAPI, DROP COLUMN RedirectDiskDrivesCustom;" +
            "UPDATE tblRoot SET ConfVersion = '2.8';", db.Name);

        var result = store.Load();

        result.SchemaVersion.Should().Be(new Version(2, 8));
        result.Model.GetRecursiveChildList().Should().HaveCount(5);
        MariaDbTestServer.Scalar("SELECT ConfVersion FROM tblRoot", db.Name).Should().Be("3.2");
        Convert.ToInt32(MariaDbTestServer.Scalar(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'tblCons' AND column_name IN ('UseRCG', 'EC2Region', 'UserViaAPI', 'RedirectDiskDrivesCustom')", db.Name))
            .Should().Be(4);
    }

    [SkippableFact]
    public void NewerSchemaVersion_IsRefused()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());
        store.Save(SampleTree());
        MariaDbTestServer.Execute("UPDATE tblRoot SET ConfVersion = '3.3'", db.Name);

        store.Invoking(s => s.Load()).Should().Throw<ConnectionFileVersionException>();
    }

    [SkippableFact]
    public void TwoClients_SecondClientSeesTheFirstClientsSave()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var factory = new CryptoProviderFactory();
        var clientA = new ConnectionsService(factory);
        var clientB = new ConnectionsService(factory);
        new SqlConnectionsStore(db.Settings()).Save(SampleTree());

        clientA.LoadFromDatabase(new SqlConnectionsStore(db.Settings()));
        clientB.LoadFromDatabase(new SqlConnectionsStore(db.Settings()));

        using var checkerA = new SqlConnectionsUpdateChecker(clientA.Database!.GetLastUpdate, () => clientA.LastDatabaseUpdate);
        var notified = new List<DateTime>();
        checkerA.UpdateAvailable += (_, e) => notified.Add(e.UpdateTime);

        checkerA.CheckNow().Should().BeFalse("nothing changed yet");

        Thread.Sleep(20);
        clientB.ConnectionTreeModel!.RootNode.Children[0].Name = "Changed by B";
        clientB.Save();

        checkerA.CheckNow().Should().BeTrue();
        checkerA.CheckNow().Should().BeTrue("still out of date");
        notified.Should().ContainSingle("each update is reported once");

        clientA.ReloadFromDatabase();
        clientA.ConnectionTreeModel!.RootNode.Children[0].Name.Should().Be("Changed by B");
        checkerA.CheckNow().Should().BeFalse("A is up to date after reloading");

        // A's own save does not count as a change made by someone else.
        clientA.ConnectionTreeModel.RootNode.Children[0].Name = "Changed by A";
        clientA.Save();
        checkerA.CheckNow().Should().BeFalse();
    }

    [SkippableFact]
    public async Task UpdateChecker_PollsInTheBackground()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var factory = new CryptoProviderFactory();
        var clientA = new ConnectionsService(factory);
        var clientB = new ConnectionsService(factory);
        new SqlConnectionsStore(db.Settings()).Save(SampleTree());
        clientA.LoadFromDatabase(new SqlConnectionsStore(db.Settings()));
        clientB.LoadFromDatabase(new SqlConnectionsStore(db.Settings()));

        using var checker = new SqlConnectionsUpdateChecker(clientA.Database!.GetLastUpdate, () => clientA.LastDatabaseUpdate);
        var signal = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        checker.UpdateAvailable += (_, e) => signal.TrySetResult(e.UpdateTime);
        checker.Start(TimeSpan.FromSeconds(1));

        await Task.Delay(50);
        clientB.Save();

        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(signal.Task, "the poller notices the other client's save");
        checker.Stop();
    }

    [SkippableFact]
    public void ConnectionsService_SaveToFileWithoutPath_GoesToTheDatabase()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.LoadFromDatabase(new SqlConnectionsStore(db.Settings()));
        service.UsingDatabase.Should().BeTrue();
        service.CurrentFilePath.Should().BeNull();

        service.ConnectionTreeModel!.RootNode.AddChild(new ConnectionInfo { Name = "added", Hostname = "h" });
        service.SaveToFile();

        new SqlConnectionsStore(db.Settings()).Load().Model.RootNode.Children.Single().Name.Should().Be("added");
        service.LastDatabaseUpdate.Should().NotBeNull();
    }

    [SkippableFact]
    public void WinForms32Database_IsAccepted_AndItsNewColumnsRoundTrip()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());
        var root = new RootNodeInfo(RootNodeType.Connection);
        var ssh = new ConnectionInfo
        {
            Name = "ssh", Hostname = "h", Protocol = mRemoteNG.Core.Connection.Protocol.ProtocolType.SSH2,
            TerminalBackend = mRemoteNG.Core.Connection.Protocol.TerminalBackend.WindowsTerminal,
        };
        ssh.Inheritance.TerminalBackend = true;
        ssh.SetPluginProperty("mRp.AWS.Enabled", "True");
        root.AddChild(ssh);
        store.Save(new ConnectionTreeModel(root));
        MariaDbTestServer.Execute("UPDATE tblRoot SET ConfVersion = '3.2'", db.Name);

        var loaded = store.Load().Model.GetRecursiveChildList().Single();

        MariaDbTestServer.Scalar("SELECT TerminalBackend FROM tblCons", db.Name).Should().Be("WindowsTerminal");
        loaded.Inheritance.TerminalBackend.Should().BeTrue();
        loaded.Inheritance.DisableInheritance();
        loaded.TerminalBackend.Should().Be(mRemoteNG.Core.Connection.Protocol.TerminalBackend.WindowsTerminal);
        loaded.GetPluginProperty("mRp.AWS.Enabled").Should().Be("True");
    }

    [SkippableFact]
    public void SharedColumns_ReachOtherClients_AndTheAccessTokenIsEncrypted()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var store = new SqlConnectionsStore(db.Settings());
        var tree = new ExportTestTree();
        tree.Web.TabColor = "#c0392b";
        tree.Web.ConnectionFrameColor = ConnectionFrameColor.Red;
        tree.Web.VaultOpenbaoMount = "secret";
        tree.Web.VaultOpenbaoSecretEngine = VaultOpenbaoSecretEngine.SSHOTP;
        tree.Web.RDGatewayAccessToken = "tok&en<1>";
        tree.Web.UseRedirectionServerName = true;
        tree.Web.Inheritance.UseRedirectionServerName = true;
        store.Save(new ConnectionTreeModel(tree.Root));

        var cipher = (string)MariaDbTestServer.Scalar($"SELECT RDGatewayAccessToken FROM tblCons WHERE ConstantID = '{tree.Web.ConstantID}'", db.Name)!;
        new LegacyRijndaelCryptographyProvider().Decrypt(cipher, "mR3m").Should().Be("tok&en<1>");

        var web = new SqlConnectionsStore(db.Settings()).Load().Model.GetRecursiveChildList().Single(c => c.Name == "web");
        web.Inheritance.UseRedirectionServerName.Should().BeTrue();
        web.Inheritance.DisableInheritance();
        web.TabColor.Should().Be("#c0392b");
        web.ConnectionFrameColor.Should().Be(ConnectionFrameColor.Red);
        web.VaultOpenbaoMount.Should().Be("secret");
        web.VaultOpenbaoSecretEngine.Should().Be(VaultOpenbaoSecretEngine.SSHOTP);
        web.RDGatewayAccessToken.Should().Be("tok&en<1>");
        web.UseRedirectionServerName.Should().BeTrue();
    }

    [SkippableFact]
    public void LocalFallback_FillsRowsWrittenByAClientWithoutTheSharedColumns()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var directory = Path.Combine(Path.GetTempPath(), "mrng-sql-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new SqlLocalConnectionState(directory);
            var tree = new ExportTestTree();
            tree.Web.TabColor = "#c0392b";
            state.Save(new ConnectionTreeModel(tree.Root));
            new SqlConnectionsStore(db.Settings()).Save(new ConnectionTreeModel(tree.Root));
            // An older client inserted the row: the column it does not know is NULL.
            MariaDbTestServer.Execute($"UPDATE tblCons SET TabColor = NULL WHERE ConstantID = '{tree.Web.ConstantID}'", db.Name);

            var service = new ConnectionsService(new CryptoProviderFactory()) { SqlLocalState = state };
            service.LoadFromDatabase(new SqlConnectionsStore(db.Settings()));
            service.ConnectionTreeModel!.GetRecursiveChildList().Single(c => c.Name == "web").TabColor.Should().Be("#c0392b");

            service.SaveToDatabase();
            MariaDbTestServer.Scalar($"SELECT TabColor FROM tblCons WHERE ConstantID = '{tree.Web.ConstantID}'", db.Name)
                .Should().Be("#c0392b", "the next save moves the local value into the shared column");
            XDocument.Load(state.SchemaGapPath).Descendants("Node").Should().BeEmpty("the database now stores every fallback property");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [SkippableFact]
    public void LocalState_SurvivesTheDatabase_AndIsNotSharedWithIt()
    {
        RequireServer();
        using var db = MariaDbTestServer.CreateDatabase();
        var directory = Path.Combine(Path.GetTempPath(), "mrng-sql-local-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new SqlLocalConnectionState(directory);
            var service = new ConnectionsService(new CryptoProviderFactory()) { SqlLocalState = state };
            var store = new SqlConnectionsStore(db.Settings());

            var tree = new ExportTestTree();
            tree.Prod.IsExpanded = false;
            store.Save(new ConnectionTreeModel(tree.Root));

            // Open sessions, expanded folders and favourites are per user: the shared tables stay without them
            // and the per-user file keeps them. The other properties are shared through the database.
            service.LoadFromDatabase(store);
            var loadedProd = service.ConnectionTreeModel!.RootNode.Children.OfType<ContainerInfo>().Single(c => c.Name == "Prod");
            loadedProd.IsExpanded = false;
            var loadedWeb = loadedProd.Children.Single(c => c.Name == "web");
            loadedWeb.PleaseConnect = true;
            loadedWeb.Favorite = true;
            loadedWeb.TabColor = "#c0392b";
            loadedWeb.ConnectionFrameColor = ConnectionFrameColor.Red;
            loadedWeb.VaultOpenbaoMount = "secret";
            loadedWeb.VaultOpenbaoRole = "ops";
            loadedWeb.VaultOpenbaoSecretEngine = VaultOpenbaoSecretEngine.SSHOTP;
            loadedWeb.RDGatewayAccessToken = "tok&en<1>";
            service.SaveToDatabase();

            var bare = new SqlConnectionsStore(db.Settings()).Load().Model.GetRecursiveChildList().Single(c => c.Name == "web");
            bare.PleaseConnect.Should().BeFalse();
            bare.TabColor.Should().Be("#c0392b");
            bare.ConnectionFrameColor.Should().Be(ConnectionFrameColor.Red);
            bare.VaultOpenbaoMount.Should().Be("secret");
            bare.VaultOpenbaoSecretEngine.Should().Be(VaultOpenbaoSecretEngine.SSHOTP);
            bare.RDGatewayAccessToken.Should().Be("tok&en<1>");

            // Another client expands the folder in the shared database. This client's file still says collapsed.
            var other = new SqlConnectionsStore(db.Settings()).Load().Model;
            other.RootNode.Children.OfType<ContainerInfo>().Single(c => c.Name == "Prod").IsExpanded = true;
            new SqlConnectionsStore(db.Settings()).Save(other);

            var again = new ConnectionsService(new CryptoProviderFactory()) { SqlLocalState = state };
            again.LoadFromDatabase(new SqlConnectionsStore(db.Settings()));
            var prod = again.ConnectionTreeModel!.RootNode.Children.OfType<ContainerInfo>().Single(c => c.Name == "Prod");
            prod.IsExpanded.Should().BeFalse();
            var web = prod.Children.Single(c => c.Name == "web");
            web.PleaseConnect.Should().BeTrue();
            web.Favorite.Should().BeTrue();
            web.TabColor.Should().Be("#c0392b");
            web.ConnectionFrameColor.Should().Be(ConnectionFrameColor.Red);
            web.VaultOpenbaoMount.Should().Be("secret");
            web.VaultOpenbaoRole.Should().Be("ops");
            web.VaultOpenbaoSecretEngine.Should().Be(VaultOpenbaoSecretEngine.SSHOTP);
            web.RDGatewayAccessToken.Should().Be("tok&en<1>");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
