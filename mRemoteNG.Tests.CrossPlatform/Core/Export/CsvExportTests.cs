using FluentAssertions;
using mRemoteNG.Core.Config.Export;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Serializers.Csv;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree.Root;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Export;

public class CsvExportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mrng-csv-{Guid.NewGuid():N}");
    private readonly ConnectionExporter _exporter = new(new CryptoProviderFactory());
    private readonly ExportTestTree _tree = new();

    public CsvExportTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Export_ThenImport_RoundTripsTreeValuesAndInheritance()
    {
        var file = Path.Combine(_tempDir, "export.csv");
        _exporter.ExportToFile(file, _tree.Root, new ExportOptions { Format = ExportFormat.Csv });

        var target = new RootNodeInfo(RootNodeType.Connection);
        var result = new ConnectionImportService(new CryptoProviderFactory()).Import(ImportSourceType.MRemoteNGCsv, file, target);

        result.Warnings.Should().BeEmpty();
        var imported = (ContainerInfo)target.Children.Single();
        imported.Name.Should().Be("export");
        imported.Children.Select(c => c.Name).Should().Equal("Prod", "lab");

        var prod = (ContainerInfo)imported.Children[0];
        prod.ConstantID.Should().Be(_tree.Prod.ConstantID);
        prod.Username.Should().Be("produser");
        prod.Description.Should().Be("Production critical", "the format cannot hold ';' so it is removed, as in the legacy app");
        prod.Children.Select(c => c.Name).Should().Equal("web", "Databases");

        var web = prod.Children[0];
        web.Inheritance.Username.Should().BeTrue();
        web.Username.Should().Be("produser");
        web.Password.Should().Be("webpass");
        web.Protocol.Should().Be(ProtocolType.SSH2);
        web.Port.Should().Be(2222);

        var db1 = ((ContainerInfo)prod.Children[1]).Children.Single();
        db1.ConstantID.Should().Be(_tree.Db1.ConstantID);
        db1.Username.Should().Be("dbadmin");
        db1.Password.Should().Be("dbpass");
        db1.Domain.Should().Be("CORP");
        db1.Colors.Should().Be(RDPColors.Colors16Bit);
        db1.Resolution.Should().Be(RDPResolutions.Res1366x768);
        db1.RedirectPorts.Should().BeTrue();
        db1.RedirectAudioCapture.Should().BeTrue();
        db1.RDGatewayPassword.Should().Be("gwpass");
        db1.Description.Should().Be("multi line");

        var lab = imported.Children[1];
        lab.Protocol.Should().Be(ProtocolType.VNC);
        lab.Favorite.Should().BeTrue();
        lab.Port.Should().Be(5900);
    }

    [Fact]
    public void Export_UsesLegacyLayout()
    {
        var csv = _exporter.Serialize(_tree.Root, new ExportOptions { Format = ExportFormat.Csv });
        var lines = csv.Split("\r\n");

        lines[0].Should().StartWith("Name;Id;Parent;NodeType;Description;Icon;Panel;TabColor;ConnectionFrameColor;Username;Password;Domain;Hostname;Port;");
        lines[0].Should().Contain("RedirectDiskDrives;RedirectDiskDrivesCustom;RedirectPorts;RedirectPrinters;");
        lines[0].Should().EndWith(";InheritRedirectAudioCapture;InheritRdpVersion;InheritExternalCredentialProvider");
        lines.Should().HaveCount(6, "a header and one row per node except the root");
        lines.Skip(1).Should().OnlyContain(l => l.EndsWith(';'));

        var headerCount = lines[0].Split(';').Length;
        lines.Skip(1).Should().OnlyContain(l => l.Split(';').Length == headerCount + 1, "every value is followed by ';'");

        // Children are written before their folder, as the legacy writer did.
        var names = lines.Skip(1).Select(l => l.Split(';')[0]).ToList();
        names.Should().Equal("web", "db1", "Databases", "Prod", "lab");
        lines[1].Split(';')[2].Should().Be(_tree.Prod.ConstantID);
        lines[4].Split(';')[2].Should().Be(_tree.Root.ConstantID);
        lines[4].Split(';')[3].Should().Be("Container");
    }

    [Fact]
    public void Export_WithCredentialsFilteredOut_OmitsColumnsAndSecrets()
    {
        var filter = new SaveFilter { SaveUsername = false, SavePassword = false, SaveDomain = false, SaveInheritance = false };

        var csv = _exporter.Serialize(_tree.Root, new ExportOptions { Format = ExportFormat.Csv, SaveFilter = filter });

        var header = csv.Split("\r\n")[0];
        header.Split(';').Should().NotContain(["Username", "Password", "Domain"]);
        header.Should().NotContain("Inherit");
        csv.Should().NotContain("dbadmin").And.NotContain("dbpass").And.NotContain("webpass").And.NotContain("gwpass").And.NotContain("CORP");

        var model = new CsvConnectionsDeserializerMremotengFormat().Deserialize(csv);
        var db1 = model.GetRecursiveChildList().Single(n => n.Name == "db1");
        db1.Username.Should().BeEmpty();
        db1.Password.Should().BeEmpty();
        db1.Hostname.Should().Be("db1.example.com");
        model.GetRecursiveChildList().Single(n => n.Name == "web").Inheritance.Username.Should().BeFalse();
    }

    [Fact]
    public void Export_SelectedFolder_WritesOnlyThatSubtree()
    {
        var csv = _exporter.Serialize(_tree.Databases, new ExportOptions { Format = ExportFormat.Csv });

        var model = new CsvConnectionsDeserializerMremotengFormat().Deserialize(csv);
        var databases = model.RootNode.Children.Should().ContainSingle().Which.Should().BeOfType<ContainerInfo>().Subject;
        databases.Name.Should().Be("Databases");
        databases.Children.Select(c => c.Name).Should().Equal("db1");
    }

    [Fact]
    public void Export_DoesNotChangeLiveTree()
    {
        _exporter.Serialize(_tree.Databases, new ExportOptions { Format = ExportFormat.Csv });
        _exporter.Serialize(_tree.Databases, new ExportOptions { Format = ExportFormat.Xml });

        _tree.Databases.Parent.Should().BeSameAs(_tree.Prod);
        _tree.Db1.Parent.Should().BeSameAs(_tree.Databases);
        _tree.Root.GetRecursiveChildList().Should().HaveCount(5);
    }
}
