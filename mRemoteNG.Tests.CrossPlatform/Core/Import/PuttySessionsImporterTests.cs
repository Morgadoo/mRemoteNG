using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Platform;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

public class PuttySessionsImporterTests
{
    private static readonly string SessionsDirectory = Fixture("Import", "putty-sessions");

    [Fact]
    public async Task FilesProvider_DecodesNamesAndReadsKeyValues()
    {
        var sessions = await new PuttySessionFilesProvider(SessionsDirectory).GetSessionsAsync();

        sessions.Select(s => s.Name).Should().BeEquivalentTo("Default Settings", "My Server", "raw-sock", "router", "serial console");
        var server = sessions.Single(s => s.Name == "My Server");
        server.Should().Be(new PuttySession("My Server", "myserver.example.com", 2222, "alice", "ssh"));
    }

    [Fact]
    public void Import_SessionsFolder_SkipsDefaultsAndSessionsWithoutHost()
    {
        var root = NewRoot();

        var result = new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.PuttySessions, SessionsDirectory, root);

        var folder = Folder(root.Children, PuttySessionsImporter.FolderName);
        folder.Children.Select(c => c.Name).Should().BeEquivalentTo("My Server", "raw-sock", "router");

        var server = Connection(folder.Children, "My Server");
        server.Protocol.Should().Be(ProtocolType.SSH2);
        server.Hostname.Should().Be("myserver.example.com");
        server.Port.Should().Be(2222);
        server.Username.Should().Be("alice");

        var router = Connection(folder.Children, "router");
        router.Protocol.Should().Be(ProtocolType.Telnet);
        router.Port.Should().Be(23);

        var raw = Connection(folder.Children, "raw-sock");
        raw.Protocol.Should().Be(ProtocolType.RAW);
        raw.Port.Should().Be(ConnectionInfoDefaultPort(ProtocolType.RAW), "a zero port falls back to the protocol default");

        result.ConnectionCount.Should().Be(3);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("serial console");
    }

    [Fact]
    public void ImportPuttySessions_FromProviderList_MapsProtocolsAndSkipsUnsupported()
    {
        var root = NewRoot();
        PuttySession[] sessions =
        [
            new("Default%20Settings", "ignored", 22, "", "ssh"),
            new("box", "box.example", 22, "bob", "ssh"),
            new("legacy", "old.example", 513, "", "rlogin"),
            new("tty", "tty.example", 0, "", "serial"),
        ];

        var result = new ConnectionImportService(new CryptoProviderFactory()).ImportPuttySessions(sessions, root);

        var folder = Folder(root.Children, PuttySessionsImporter.FolderName);
        folder.Children.Select(c => c.Name).Should().Equal("box", "legacy");
        Connection(folder.Children, "legacy").Protocol.Should().Be(ProtocolType.Rlogin);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("serial");
    }

    [Fact]
    public void Import_MissingFolder_Throws()
    {
        var act = () => new PuttySessionsImporter().Import(Path.Combine(SessionsDirectory, "does-not-exist"), NewRoot());

        act.Should().Throw<DirectoryNotFoundException>();
    }

    private static int ConnectionInfoDefaultPort(ProtocolType protocol) =>
        mRemoteNG.Core.Connection.ConnectionInfo.GetDefaultPort(protocol);
}
