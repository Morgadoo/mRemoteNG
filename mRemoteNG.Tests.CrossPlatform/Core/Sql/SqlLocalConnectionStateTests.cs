using System.Xml.Linq;
using FluentAssertions;
using mRemoteNG.Core.Config.Connections.Sql;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Sql;

public sealed class SqlLocalConnectionStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mrng-local-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Apply_ReadsAWinFormsLocalConnectionPropertiesFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, SqlLocalConnectionState.LocalPropertiesFileName), """
            <?xml version="1.0" encoding="utf-8"?>
            <LocalConnections>
                <Node ConnectionId="folder-1" Connected="False" Expanded="True" Favorite="False" />
                <Node ConnectionId="conn-1" Connected="True" Expanded="False" Favorite="True" />
            </LocalConnections>
            """);

        var folder = new ContainerInfo("folder-1") { Name = "Servers", IsExpanded = false, Favorite = true };
        var connection = new ConnectionInfo("conn-1") { Name = "host", Favorite = false };
        var root = new RootNodeInfo(RootNodeType.Connection);
        root.AddChild(folder);
        folder.AddChild(connection);
        var model = new ConnectionTreeModel(root);

        new SqlLocalConnectionState(_directory).Apply(model);

        folder.IsExpanded.Should().BeTrue();
        folder.Favorite.Should().BeFalse();
        connection.PleaseConnect.Should().BeTrue();
        connection.Favorite.Should().BeTrue();
    }

    [Fact]
    public void Save_WritesTheWinFormsAttributes_AndNotTheSchemaGapFields()
    {
        var model = Tree();
        var connection = model.GetRecursiveChildList().Single(n => n.Name == "host");
        connection.PleaseConnect = true;
        connection.Favorite = true;
        connection.TabColor = "#112233";
        connection.RDGatewayAccessToken = "secret-token";

        var state = new SqlLocalConnectionState(_directory);
        state.Save(model);

        var document = XDocument.Load(state.LocalPropertiesPath);
        var node = document.Descendants("Node").Single(e => e.Attribute("ConnectionId")!.Value == connection.ConstantID);
        bool.Parse(node.Attribute("Connected")!.Value).Should().BeTrue();
        bool.Parse(node.Attribute("Expanded")!.Value).Should().BeFalse();
        bool.Parse(node.Attribute("Favorite")!.Value).Should().BeTrue();
        node.Attribute("TabColor").Should().BeNull();
        node.Attribute("RDGatewayAccessToken").Should().BeNull();

        var folder = document.Descendants("Node").Single(e => e.Attribute("ConnectionId")!.Value == "folder-1");
        bool.Parse(folder.Attribute("Expanded")!.Value).Should().BeTrue();
    }

    [Fact]
    public void SchemaGap_RoundTripsPropertiesTheDatabaseCannotStore_AndClearsThem()
    {
        var model = Tree();
        var connection = model.GetRecursiveChildList().Single(n => n.Name == "host");
        connection.TabColor = "#c0392b";
        connection.ConnectionFrameColor = ConnectionFrameColor.Red;
        connection.Inheritance.TabColor = true;
        connection.VaultOpenbaoMount = "secret";
        connection.VaultOpenbaoRole = "ops";
        connection.VaultOpenbaoSecretEngine = VaultOpenbaoSecretEngine.SSHOTP;
        connection.RDGatewayAccessToken = "tok&en<1>";
        connection.UseRedirectionServerName = true;
        connection.Inheritance.UseRedirectionServerName = true;

        var state = new SqlLocalConnectionState(_directory);
        state.Save(model);

        var restored = Tree();
        state.Apply(restored);
        var host = restored.GetRecursiveChildList().Single(n => n.Name == "host");
        host.Inheritance.TabColor.Should().BeTrue();
        host.Inheritance.ConnectionFrameColor.Should().BeFalse();
        host.Inheritance.UseRedirectionServerName.Should().BeTrue();
        host.Inheritance.DisableInheritance();
        host.TabColor.Should().Be("#c0392b");
        host.ConnectionFrameColor.Should().Be(ConnectionFrameColor.Red);
        host.VaultOpenbaoMount.Should().Be("secret");
        host.VaultOpenbaoRole.Should().Be("ops");
        host.VaultOpenbaoSecretEngine.Should().Be(VaultOpenbaoSecretEngine.SSHOTP);
        host.RDGatewayAccessToken.Should().Be("tok&en<1>");
        host.UseRedirectionServerName.Should().BeTrue();

        connection.TabColor = "";
        connection.ConnectionFrameColor = ConnectionFrameColor.None;
        connection.Inheritance.TabColor = false;
        connection.VaultOpenbaoMount = "";
        connection.VaultOpenbaoRole = "";
        connection.VaultOpenbaoSecretEngine = VaultOpenbaoSecretEngine.Kv;
        connection.RDGatewayAccessToken = "";
        connection.UseRedirectionServerName = false;
        connection.Inheritance.UseRedirectionServerName = false;
        state.Save(model);

        var cleared = Tree();
        cleared.GetRecursiveChildList().Single(n => n.Name == "host").TabColor = "stale";
        state.Apply(cleared);
        var after = cleared.GetRecursiveChildList().Single(n => n.Name == "host");
        after.TabColor.Should().Be("stale", "a node omitted from the file is left as it was loaded");

        XDocument.Load(state.SchemaGapPath).Descendants("Node").Should().BeEmpty();
    }

    [Fact]
    public void SchemaGap_IsNotWrittenForPropertiesTheDatabaseHasColumnsFor()
    {
        var model = Tree();
        var connection = model.GetRecursiveChildList().Single(n => n.Name == "host");
        connection.TabColor = "#c0392b";
        connection.RDGatewayAccessToken = "secret-token";
        connection.UseRedirectionServerName = true;

        var state = new SqlLocalConnectionState(_directory);
        state.Save(model, new HashSet<string>(["Name", "TabColor", "RDGatewayAccessToken"], StringComparer.OrdinalIgnoreCase));

        var node = XDocument.Load(state.SchemaGapPath).Descendants("Node").Single();
        node.Attribute("TabColor").Should().BeNull();
        node.Attribute("RDGatewayAccessToken").Should().BeNull("the encrypted database column holds the token");
        node.Attribute("UseRedirectionServerName")!.Value.Should().Be("true");
    }

    [Fact]
    public void SchemaGap_OnlyFillsColumnsTheDatabaseLeftNull()
    {
        var model = Tree();
        var connection = model.GetRecursiveChildList().Single(n => n.Name == "host");
        connection.TabColor = "#c0392b";
        connection.VaultOpenbaoMount = "local-mount";
        var state = new SqlLocalConnectionState(_directory);
        state.Save(model);

        var loaded = Tree();
        var host = loaded.GetRecursiveChildList().Single(n => n.Name == "host");
        host.TabColor = "#00ff00";
        state.Apply(loaded, new Dictionary<string, IReadOnlySet<string>>
        {
            [host.ConstantID] = new HashSet<string>(["TabColor"], StringComparer.OrdinalIgnoreCase),
        });

        host.TabColor.Should().Be("#00ff00", "the shared database value wins");
        host.VaultOpenbaoMount.Should().Be("local-mount", "the column was NULL, so the local value is used");
    }

    [Fact]
    public void Apply_IgnoresACorruptFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, SqlLocalConnectionState.LocalPropertiesFileName), "<not-xml");
        File.WriteAllText(Path.Combine(_directory, SqlLocalConnectionState.SchemaGapFileName), "<also");

        var model = Tree();
        var act = () => new SqlLocalConnectionState(_directory).Apply(model);

        act.Should().NotThrow();
        model.GetRecursiveChildList().Single(n => n.Name == "host").PleaseConnect.Should().BeFalse();
    }

    [Fact]
    public void Apply_WithNoFiles_LeavesTheTreeAlone()
    {
        var model = Tree();
        new SqlLocalConnectionState(_directory).Apply(model);

        var folder = (ContainerInfo)model.RootNode.Children.Single();
        folder.IsExpanded.Should().BeTrue();
        folder.Children.Single().Favorite.Should().BeFalse();
    }

    private static ConnectionTreeModel Tree()
    {
        var folder = new ContainerInfo("folder-1") { Name = "Servers", IsExpanded = true };
        var connection = new ConnectionInfo("conn-1") { Name = "host" };
        var root = new RootNodeInfo(RootNodeType.Connection);
        root.AddChild(folder);
        folder.AddChild(connection);
        return new ConnectionTreeModel(root);
    }
}
