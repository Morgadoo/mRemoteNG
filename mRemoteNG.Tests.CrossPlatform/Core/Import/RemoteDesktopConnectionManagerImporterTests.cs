using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

/// <summary>Uses the legacy RDCMan fixtures from mRemoteNGTests/Resources.</summary>
public class RemoteDesktopConnectionManagerImporterTests
{
    [Theory]
    [InlineData("test_rdcman_v2_7_schema3.rdg", "test_RDCMan_connections")]
    [InlineData("test_rdcman_v2_2_schema1.rdg", "test_rdcman_v2_2_schema1")]
    public void Import_BuildsGroupHierarchy(string file, string fileGroupName)
    {
        var root = NewRoot();

        var result = new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.RemoteDesktopConnectionManager, Fixture(file), root);

        var fileGroup = Folder(root.Children, fileGroupName);
        var group1 = Folder(fileGroup.Children, "Group1");
        group1.Children.Select(c => c.Name).Should().Equal("server1_displayname", "server2");
        var group3 = Folder(Folder(fileGroup.Children, "Group2").Children, "Group3");
        group3.Children.Select(c => c.Name).Should().Equal("server3", "server4");
        result.ConnectionCount.Should().Be(4);
    }

    [Theory]
    [InlineData("test_rdcman_v2_7_schema3.rdg")]
    [InlineData("test_rdcman_v2_2_schema1.rdg")]
    public void Import_ReadsServerSettings(string file)
    {
        var model = new RemoteDesktopConnectionManagerDeserializer().Deserialize(File.ReadAllText(Fixture(file)));
        var server = model.GetRecursiveChildList().Single(n => n.Name == "server1_displayname");

        server.Protocol.Should().Be(ProtocolType.RDP);
        server.Hostname.Should().Be("server1");
        server.Description.Should().Be("Comment text here");
        server.Username.Should().Be("myusername1");
        server.Domain.Should().Be("mydomain");
        server.UseConsoleSession.Should().BeTrue();
        server.RDPStartProgram.Should().Be("alternate shell");
        server.Port.Should().Be(9933);
        server.RDGatewayUsageMethod.Should().Be(RDGatewayUsageMethod.Always);
        server.RDGatewayHostname.Should().Be("gatewayserverhost.innerdomain.net");
        server.RDGatewayUsername.Should().Be("gatewayusername");
        server.RDGatewayDomain.Should().Be("innerdomain");
        server.Resolution.Should().Be(RDPResolutions.FitToWindow);
        server.Colors.Should().Be(RDPColors.Colors24Bit);
        server.RedirectSound.Should().Be(RDPSounds.DoNotPlay);
        server.RedirectKeys.Should().BeTrue();
        server.RedirectDiskDrives.Should().Be(RDPDiskDrives.Local);
        server.RedirectPorts.Should().BeTrue();
        server.RedirectPrinters.Should().BeTrue();
        server.RedirectSmartCards.Should().BeTrue();
        server.RedirectClipboard.Should().BeTrue();
    }

    [Fact]
    public void Import_DpapiPassword_IsLeftEmptyWithWarning()
    {
        var deserializer = new RemoteDesktopConnectionManagerDeserializer();
        var model = deserializer.Deserialize(File.ReadAllText(Fixture("test_rdcman_v2_7_schema3.rdg")));

        model.GetRecursiveChildList().Single(n => n.Name == "server1_displayname").Password.Should().BeEmpty();
        deserializer.Warnings.Should().ContainSingle().Which.Should().Contain("DPAPI");
    }

    [Fact]
    public void Import_ServerWithInheritedSettings_InheritsCredentials()
    {
        var model = new RemoteDesktopConnectionManagerDeserializer().Deserialize(File.ReadAllText(Fixture("test_rdcman_v2_7_schema3.rdg")));
        var server2 = model.GetRecursiveChildList().Single(n => n.Name == "server2");

        server2.Inheritance.Username.Should().BeTrue();
        server2.Inheritance.Password.Should().BeTrue();
        server2.Inheritance.Port.Should().BeTrue();
    }

    [Theory]
    [InlineData("test_rdcman_v2_7_schema3_empty_values.rdg")]
    [InlineData("test_rdcman_v2_7_schema3_null_values.rdg")]
    public void Import_EmptyOrMissingValues_DoesNotThrow(string file)
    {
        var model = new RemoteDesktopConnectionManagerDeserializer().Deserialize(File.ReadAllText(Fixture(file)));

        model.GetRecursiveChildList().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("test_rdcman_badVersionNumber.rdg")]
    [InlineData("test_rdcman_noversion.rdg")]
    [InlineData("test_rdcman_v2_2_badschemaversion.rdg")]
    public void Import_UnsupportedFile_ThrowsInvalidData(string file)
    {
        var root = NewRoot();
        var act = () => new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.RemoteDesktopConnectionManager, Fixture(file), root);

        act.Should().Throw<InvalidDataException>();
        root.Children.Should().BeEmpty();
    }
}
