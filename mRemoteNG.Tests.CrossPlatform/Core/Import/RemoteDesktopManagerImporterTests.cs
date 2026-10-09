using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Serializers.Csv;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Security.Factories;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

public class RemoteDesktopManagerImporterTests
{
    private static readonly string RdmCsv = Fixture("Import", "rdm_export.csv");

    [Fact]
    public void Import_BuildsNestedGroupsAndUnsortedFolder()
    {
        var root = NewRoot();

        var result = new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.RemoteDesktopManager, RdmCsv, root);

        var fileFolder = Folder(root.Children, "rdm_export");
        fileFolder.Children.Select(c => c.Name).Should().Equal("Datacenter", "Unsorted");
        var datacenter = Folder(fileFolder.Children, "Datacenter");
        Folder(datacenter.Children, "Domain Controllers").Children.Select(c => c.Name).Should().Equal("dc01");
        Folder(datacenter.Children, "Linux").Children.Select(c => c.Name).Should().Equal("web01");
        Folder(fileFolder.Children, "Unsorted").Children.Select(c => c.Name).Should().Equal("laptop");
        result.ConnectionCount.Should().Be(3);
    }

    [Fact]
    public void Deserialize_ReadsQuotedFieldsAndCredentials()
    {
        var model = new CsvConnectionsDeserializerRdmFormat().Deserialize(File.ReadAllText(RdmCsv));
        var all = model.GetRecursiveChildList().ToList();

        var dc = Connection(all, "dc01");
        dc.Protocol.Should().Be(ProtocolType.RDP);
        dc.Hostname.Should().Be("dc01.corp.example");
        dc.Port.Should().Be(3390);
        dc.Description.Should().Be("Primary DC, site A");
        dc.Username.Should().Be("administrator");
        dc.Domain.Should().Be("CORP");
        dc.Password.Should().Be("P@ss");
        dc.Icon.Should().Be("Remote Desktop");

        var web = Connection(all, "web01");
        web.Protocol.Should().Be(ProtocolType.SSH2);
        web.Port.Should().Be(22);
        web.Username.Should().Be("root");

        Connection(all, "laptop").Port.Should().Be(3389);
    }

    [Fact]
    public void Deserialize_UnsupportedTypeAndMissingHost_AreSkippedWithWarnings()
    {
        var deserializer = new CsvConnectionsDeserializerRdmFormat();
        var model = deserializer.Deserialize(File.ReadAllText(RdmCsv));

        model.GetRecursiveChildList().Select(n => n.Name).Should().NotContain(["vnc01", "broken"]);
        deserializer.Warnings.Should().HaveCount(2);
        deserializer.Warnings.Should().Contain(w => w.Contains("VNC"));
    }

    [Fact]
    public void Deserialize_WithoutRequiredColumns_Throws()
    {
        var act = () => new CsvConnectionsDeserializerRdmFormat().Deserialize("Name;Id\r\na;b\r\n");

        act.Should().Throw<InvalidDataException>();
    }
}
