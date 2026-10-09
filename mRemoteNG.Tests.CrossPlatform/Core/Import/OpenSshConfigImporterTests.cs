using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Security.Factories;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

public class OpenSshConfigImporterTests
{
    private static readonly string ConfigFile = Fixture("Import", "ssh", "config");

    [Fact]
    public void ParseFile_ReturnsConcreteHostsWithSshFirstMatchSemantics()
    {
        var entries = OpenSshConfigParser.ParseFile(ConfigFile);

        entries.Should().Equal(
            new OpenSshHostEntry("bastion", "bastion.example.com", 2200, "admin"),
            new OpenSshHostEntry("web1", "web1.internal.example.com", 2022, "fallbackuser"),
            new OpenSshHostEntry("web2", "web2.internal.example.com", 2022, "fallbackuser"),
            new OpenSshHostEntry("db", "10.0.0.20", 5432, "fallbackuser"),
            new OpenSshHostEntry("app.example.com", "app.example.com", 8022, "wildcarduser"),
            new OpenSshHostEntry("skip.example.com", "skip.example.com", 2022, "fallbackuser"),
            new OpenSshHostEntry("quoted host", "quoted.example.com", 2022, "fallbackuser"),
            new OpenSshHostEntry("included", "included.example.com", 2022, "includeduser"));
    }

    [Fact]
    public void Parse_GlobalSettingsBeforeFirstHostWin()
    {
        const string config = "User early\nHost a\n  User late\n";

        OpenSshConfigParser.Parse(config).Should().ContainSingle()
            .Which.Should().Be(new OpenSshHostEntry("a", "a", 22, "early"));
    }

    [Fact]
    public void Parse_OnlyWildcards_ReturnsNothing()
    {
        OpenSshConfigParser.Parse("Host *\n  User x\nHost web-?\n  Port 1\n").Should().BeEmpty();
    }

    [Fact]
    public void Import_CreatesSsh2ConnectionsInSshConfigFolder()
    {
        var root = NewRoot();

        var result = new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.OpenSshConfig, ConfigFile, root);

        var folder = Folder(root.Children, OpenSshConfigImporter.FolderName);
        folder.Children.Should().HaveCount(8);
        folder.Children.Should().OnlyContain(c => c.Protocol == ProtocolType.SSH2);

        var bastion = Connection(folder.Children, "bastion");
        bastion.Hostname.Should().Be("bastion.example.com");
        bastion.Port.Should().Be(2200);
        bastion.Username.Should().Be("admin");
        result.ConnectionCount.Should().Be(8);
    }

    [Fact]
    public void Import_MissingFile_Throws()
    {
        var act = () => new OpenSshConfigImporter().Import(Fixture("Import", "ssh", "missing"), NewRoot());

        act.Should().Throw<FileNotFoundException>();
    }
}
