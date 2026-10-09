using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Export;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree.Root;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Export;

public class XmlExportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mrng-xml-{Guid.NewGuid():N}");
    private readonly ConnectionExporter _exporter = new(new CryptoProviderFactory());
    private readonly ExportTestTree _tree = new();

    public XmlExportTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private string Export(ConnectionInfo node, ExportOptions options)
    {
        var file = Path.Combine(_tempDir, "export.xml");
        _exporter.ExportToFile(file, node, options);
        return file;
    }

    private static ConnectionsService NewService() => new(new CryptoProviderFactory());

    [Fact]
    public void ExportWithPassword_ReloadRequiresThatPassword()
    {
        var file = Export(_tree.Root, new ExportOptions { Password = "S3cret!" });

        var withoutPassword = () => NewService().LoadFromFile(file);
        withoutPassword.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeFalse();

        var wrongPassword = () => NewService().LoadFromFile(file, "nope");
        wrongPassword.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeTrue();

        var model = NewService().LoadFromFile(file, "S3cret!");
        model.RootNode.IsPasswordProtected.Should().BeTrue();
        var db1 = model.GetRecursiveChildList().Single(n => n.Name == "db1");
        db1.Password.Should().Be("dbpass");
        db1.ConstantID.Should().Be(_tree.Db1.ConstantID);
        File.ReadAllText(file).Should().NotContain("dbpass");
    }

    [Fact]
    public void ExportWithPassword_CanBeImportedWithPassword()
    {
        var file = Export(_tree.Root, new ExportOptions { Password = "S3cret!" });
        var service = new ConnectionImportService(new CryptoProviderFactory());

        var act = () => service.Import(ImportSourceType.MRemoteNGXml, file, new RootNodeInfo(RootNodeType.Connection));
        act.Should().Throw<ConnectionFilePasswordException>();

        var result = service.Import(ImportSourceType.MRemoteNGXml, file, new RootNodeInfo(RootNodeType.Connection), "S3cret!");
        result.ConnectionCount.Should().Be(3);
    }

    [Fact]
    public void ExportWithoutPassword_IsNotProtectedEvenIfLiveTreeIs()
    {
        _tree.Root.PasswordString = "live-master-password";

        var file = Export(_tree.Root, new ExportOptions());

        var model = NewService().LoadFromFile(file);
        model.RootNode.IsPasswordProtected.Should().BeFalse();
        model.GetRecursiveChildList().Single(n => n.Name == "web").Password.Should().Be("webpass");
    }

    [Fact]
    public void ExportSelectedFolder_WritesOnlyThatFolder()
    {
        var file = Export(_tree.Prod, new ExportOptions());

        var model = NewService().LoadFromFile(file);
        var prod = model.RootNode.Children.Should().ContainSingle().Which.Should().BeOfType<ContainerInfo>().Subject;
        prod.Name.Should().Be("Prod");
        prod.ConstantID.Should().Be(_tree.Prod.ConstantID);
        prod.Children.Select(c => c.Name).Should().Equal("web", "Databases");
        ((ContainerInfo)prod.Children[1]).Children.Single().Name.Should().Be("db1");
        model.GetRecursiveChildList().Should().NotContain(n => n.Name == "lab");
    }

    [Fact]
    public void ExportSelectedFolder_KeepsValuesItInheritedFromOutsideTheExport()
    {
        _tree.Databases.Inheritance.Username = true;
        _tree.Databases.Username.Should().Be("produser", "Databases inherits from Prod in the live tree");

        var file = Export(_tree.Databases, new ExportOptions());

        var databases = NewService().LoadFromFile(file).RootNode.Children.Single();
        databases.Username.Should().Be("produser");
    }

    [Fact]
    public void ExportWithFilter_OmitsCredentialsAndInheritance()
    {
        var filter = new SaveFilter { SaveUsername = false, SavePassword = false, SaveDomain = false, SaveInheritance = false };

        var file = Export(_tree.Root, new ExportOptions { SaveFilter = filter });

        var xml = File.ReadAllText(file);
        xml.Should().NotContain("dbadmin").And.NotContain("CORP").And.NotContain("InheritUsername");
        var model = NewService().LoadFromFile(file);
        var db1 = model.GetRecursiveChildList().Single(n => n.Name == "db1");
        db1.Username.Should().BeEmpty();
        db1.Password.Should().BeEmpty();
        db1.RDGatewayPassword.Should().BeEmpty();
        model.GetRecursiveChildList().Single(n => n.Name == "web").Inheritance.Username.Should().BeFalse();
    }

    [Fact]
    public void ExportWithFullFileEncryption_ReloadsWithPassword()
    {
        var file = Export(_tree.Root, new ExportOptions
        {
            Password = "pw",
            Encryption = new ConnectionFileEncryption(FullFileEncryption: true)
        });

        File.ReadAllText(file).Should().NotContain("db1.example.com");
        NewService().LoadFromFile(file, "pw").GetRecursiveChildList().Should().HaveCount(5);
    }
}
