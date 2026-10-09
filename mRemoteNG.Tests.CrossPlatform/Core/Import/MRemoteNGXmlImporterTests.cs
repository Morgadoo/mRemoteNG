using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

public class MRemoteNGXmlImporterTests
{
    private readonly ConnectionImportService _service = new(new CryptoProviderFactory());

    [Fact]
    public void Import_UnprotectedFile_AddsFolderNamedAfterFileWithFileContents()
    {
        var root = NewRoot();

        var result = _service.Import(ImportSourceType.MRemoteNGXml, Fixture("confCons_v2_6.xml"), root);

        var folder = root.Children.Should().ContainSingle().Which.Should().BeOfType<ContainerInfo>().Subject;
        folder.Name.Should().Be("confCons_v2_6");
        folder.Children.Should().HaveCount(3);
        Folder(folder.Children, "Folder2").Children.Should().HaveCount(3);
        result.ImportedNodes.Should().ContainSingle().Which.Should().BeSameAs(folder);
        result.ConnectionCount.Should().BeGreaterThan(0);
        result.ReassignedIdCount.Should().Be(0);
    }

    [Fact]
    public void Import_ProtectedFileWithoutPassword_ThrowsPasswordExceptionAndLeavesTreeUntouched()
    {
        var root = NewRoot();

        var act = () => _service.Import(ImportSourceType.MRemoteNGXml, Fixture("confCons_v2_6_passwordis_Password.xml"), root);

        act.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeFalse();
        root.Children.Should().BeEmpty();
    }

    [Fact]
    public void Import_ProtectedFileWithWrongPassword_ReportsPasswordWasSupplied()
    {
        var act = () => _service.Import(ImportSourceType.MRemoteNGXml, Fixture("confCons_v2_6_passwordis_Password.xml"), NewRoot(), "wrong");

        act.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeTrue();
    }

    [Theory]
    [InlineData("confCons_v2_6_passwordis_Password.xml")]
    [InlineData("confCons_v2_6_passwordis_Password_fullencryption.xml")]
    [InlineData("confCons_v2_5_passwordis_Password_fullencryption.xml")]
    public void Import_ProtectedFileWithPassword_ImportsConnections(string file)
    {
        var root = NewRoot();

        var result = _service.Import(ImportSourceType.MRemoteNGXml, Fixture(file), root, "Password");

        result.ConnectionCount.Should().BeGreaterThan(0);
        root.Children.Should().ContainSingle();
    }

    [Fact]
    public void Import_SameFileTwice_GivesSecondCopyNewUniqueIds()
    {
        // The legacy fixtures carry no node IDs, so save one with IDs first.
        var file = Path.Combine(Path.GetTempPath(), $"mrng-import-{Guid.NewGuid():N}.xml");
        try
        {
            var service = new ConnectionsService(new CryptoProviderFactory());
            service.LoadFromFile(Fixture("confCons_v2_6.xml"));
            service.SaveToFile(file);
            var root = NewRoot();

            var first = _service.Import(ImportSourceType.MRemoteNGXml, file, root);
            var second = _service.Import(ImportSourceType.MRemoteNGXml, file, root);

            first.ReassignedIdCount.Should().Be(0);
            second.ReassignedIdCount.Should().Be(second.ConnectionCount + second.FolderCount - 1,
                "every node of the second copy except its new wrapper folder clashes with the first copy");
            root.GetRecursiveChildList().Select(n => n.ConstantID).Should().OnlyHaveUniqueItems();

            var firstCopy = (ContainerInfo)root.Children[0];
            var secondCopy = (ContainerInfo)root.Children[1];
            Names(secondCopy).Should().Equal(Names(firstCopy));
            Folder(secondCopy.Children, "Folder2").Children.Should().HaveCount(3);
            Folder(Folder(secondCopy.Children, "Folder2").Children, "Folder2.2").Inheritance.Username.Should().BeTrue();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Import_IntoSubFolder_AddsUnderThatFolder()
    {
        var root = NewRoot();
        var target = new ContainerInfo { Name = "Target" };
        root.AddChild(target);

        _service.Import(ImportSourceType.MRemoteNGXml, Fixture("confCons_v2_6.xml"), target);

        target.Children.Should().ContainSingle().Which.Name.Should().Be("confCons_v2_6");
        root.Children.Should().ContainSingle();
    }

    private static List<string> Names(ContainerInfo container) =>
        container.GetRecursiveChildList().Select(n => n.Name).ToList();
}
