using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using NSubstitute;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

public class ConnectionImportServiceTests
{
    private readonly ConnectionImportService _service = new(new CryptoProviderFactory());

    [Fact]
    public void Import_IdAlreadyInTree_GetsNewIdAndKeepsValuesAndChildren()
    {
        var root = NewRoot();
        var existing = new ConnectionInfo("shared-id") { Name = "existing" };
        root.AddChild(existing);

        var importer = Substitute.For<IConnectionImporter>();
        importer.Import(Arg.Any<string>(), Arg.Any<ContainerInfo>()).Returns(call =>
        {
            var destination = call.Arg<ContainerInfo>();
            var folder = new ContainerInfo("shared-id") { Name = "imported folder", IsExpanded = false };
            folder.AddChild(new ConnectionInfo("child-id") { Name = "child", Hostname = "child.example", Port = 2222 });
            destination.AddChild(folder);
            return new ImportResult([folder], ["a warning"]);
        });

        var result = _service.Import(importer, "source", root);

        root.Children.Should().HaveCount(2);
        var imported = root.Children[1].Should().BeOfType<ContainerInfo>().Subject;
        imported.ConstantID.Should().NotBe("shared-id");
        imported.Name.Should().Be("imported folder");
        imported.IsExpanded.Should().BeFalse();
        var child = imported.Children.Should().ContainSingle().Subject;
        child.ConstantID.Should().Be("child-id");
        child.Hostname.Should().Be("child.example");
        child.Port.Should().Be(2222);
        child.Parent.Should().BeSameAs(imported);
        existing.ConstantID.Should().Be("shared-id");

        result.ReassignedIdCount.Should().Be(1);
        result.ImportedNodes.Should().ContainSingle().Which.Should().BeSameAs(imported);
        result.Warnings.Should().Equal("a warning");
        result.Summary.Should().Contain("1 connection").And.Contain("1 duplicate ID");
    }

    [Fact]
    public void Import_ImporterFails_LeavesDestinationUntouched()
    {
        var root = NewRoot();
        var importer = Substitute.For<IConnectionImporter>();
        importer.Import(Arg.Any<string>(), Arg.Any<ContainerInfo>()).Returns(call =>
        {
            call.Arg<ContainerInfo>().AddChild(new ConnectionInfo());
            throw new InvalidDataException("broken");
        });

        var act = () => _service.Import(importer, "source", root);

        act.Should().Throw<InvalidDataException>();
        root.Children.Should().BeEmpty();
    }

    [Fact]
    public void Import_MissingFile_ThrowsFileNotFound()
    {
        var act = () => _service.Import(ImportSourceType.MRemoteNGCsv, Fixture("does-not-exist.csv"), NewRoot());

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Descriptors_CoverEverySourceType()
    {
        ImportSourceDescriptor.All.Select(d => d.Type).Should().BeEquivalentTo(Enum.GetValues<ImportSourceType>());
        foreach (var type in Enum.GetValues<ImportSourceType>())
            _service.CreateImporter(type).Should().NotBeNull();
    }
}
