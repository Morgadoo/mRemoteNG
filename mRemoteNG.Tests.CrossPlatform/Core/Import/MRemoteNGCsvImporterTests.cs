using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Serializers.Csv;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

/// <summary>
/// The fixture reproduces the legacy WinForms CSV writer byte for byte, including its merged
/// "RedirectDiskDrivesCustomRedirectPorts" header and its reordered inheritance values.
/// </summary>
public class MRemoteNGCsvImporterTests
{
    private static readonly string LegacyCsv = Fixture("Import", "legacy_mremoteng_export.csv");

    [Fact]
    public void Import_LegacyExport_RebuildsTreeUnderFolderNamedAfterFile()
    {
        var root = NewRoot();

        var result = new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.MRemoteNGCsv, LegacyCsv, root);

        var fileFolder = Folder(root.Children, "legacy_mremoteng_export");
        fileFolder.Children.Select(c => c.Name).Should().Equal("Servers", "rdp01");
        var servers = Folder(fileFolder.Children, "Servers");
        servers.Children.Should().ContainSingle().Which.Name.Should().Be("web01");
        result.ConnectionCount.Should().Be(2);
        result.FolderCount.Should().Be(2);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Import_LegacyExport_ReadsValuesAndCredentials()
    {
        var model = new CsvConnectionsDeserializerMremotengFormat().Deserialize(File.ReadAllText(LegacyCsv));
        var servers = Folder(model.RootNode.Children, "Servers");
        var web = Connection(servers.Children, "web01");

        web.ConstantID.Should().Be("8a7b6c5d-4e3f-4a1b-9c8d-7e6f5a4b0003");
        web.Hostname.Should().Be("web01.example.com");
        web.Port.Should().Be(2222);
        web.Protocol.Should().Be(ProtocolType.SSH2);
        web.Description.Should().Be("Web server");
        web.Username.Should().Be("admin");
        web.Password.Should().Be("s3cret");
        web.Domain.Should().Be("CORP");
        web.Colors.Should().Be(RDPColors.Colors24Bit);
        web.RdpVersion.Should().Be(RdpVersion.Highest);
    }

    [Fact]
    public void Import_LegacyExport_SplitsMergedRedirectPortsColumn()
    {
        var model = new CsvConnectionsDeserializerMremotengFormat().Deserialize(File.ReadAllText(LegacyCsv));
        var web = Connection(Folder(model.RootNode.Children, "Servers").Children, "web01");
        var rdp = Connection(model.RootNode.Children, "rdp01");

        web.RedirectPorts.Should().BeTrue();
        web.RedirectPrinters.Should().BeFalse();
        rdp.RedirectPorts.Should().BeFalse();
        rdp.RedirectPrinters.Should().BeTrue();
        web.RedirectClipboard.Should().BeTrue("columns after the merged header must stay aligned");
    }

    [Fact]
    public void Import_LegacyExport_ReadsInheritanceInLegacyValueOrder()
    {
        var model = new CsvConnectionsDeserializerMremotengFormat().Deserialize(File.ReadAllText(LegacyCsv));
        var web = Connection(Folder(model.RootNode.Children, "Servers").Children, "web01");
        var rdp = Connection(model.RootNode.Children, "rdp01");

        web.Inheritance.Username.Should().BeFalse();
        rdp.Inheritance.Username.Should().BeTrue();
        web.Inheritance.RedirectAudioCapture.Should().BeTrue();
        web.Inheritance.RdpVersion.Should().BeFalse();
        web.Inheritance.UserViaAPI.Should().BeFalse();
        rdp.Inheritance.UserViaAPI.Should().BeTrue();
        rdp.Inheritance.RedirectAudioCapture.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_MissingColumns_UsesDefaultsAndProtocolPort()
    {
        const string csv = "Name;Id;Parent;NodeType;Hostname;Protocol;\r\nbox;;;Connection;box.example;SSH2;\r\n";

        var model = new CsvConnectionsDeserializerMremotengFormat().Deserialize(csv);

        var box = model.RootNode.Children.Should().ContainSingle().Subject;
        box.Port.Should().Be(22);
        box.Username.Should().BeEmpty();
        box.ConstantID.Should().NotBeNullOrEmpty();
        box.Colors.Should().Be(RDPColors.Colors24Bit);
    }

    [Fact]
    public void Deserialize_ParentCycle_DoesNotLoseNodes()
    {
        const string csv = "Name;Id;Parent;NodeType;\r\nA;a;b;Container;\r\nB;b;a;Container;\r\n";

        var model = new CsvConnectionsDeserializerMremotengFormat().Deserialize(csv);

        model.GetRecursiveChildList().Select(n => n.Name).Should().BeEquivalentTo("A", "B");
        model.RootNode.Children.Should().NotBeEmpty();
    }

    [Fact]
    public void Deserialize_InvalidValue_IsIgnoredWithWarning()
    {
        const string csv = "Name;Id;Parent;NodeType;Port;\r\nx;;;Connection;notaport;\r\n";
        var deserializer = new CsvConnectionsDeserializerMremotengFormat();

        var model = deserializer.Deserialize(csv);

        model.RootNode.Children.Single().Port.Should().Be(3389);
        deserializer.Warnings.Should().ContainSingle().Which.Should().Contain("Port");
    }
}
