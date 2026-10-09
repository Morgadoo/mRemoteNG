using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

/// <summary>Uses the legacy SecureCRT fixture and expectations from mRemoteNGTests.</summary>
public class SecureCrtImporterTests
{
    private readonly ContainerInfo _sessions;

    public SecureCrtImporterTests()
    {
        var root = NewRoot();
        new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.SecureCrt, Fixture("test_securecrt.xml"), root);
        _sessions = Folder(root.Children, "test_securecrt");
    }

    [Fact]
    public void Import_KeepsFolderStructure()
    {
        _sessions.Children.Should().HaveCount(3);
        Folder(_sessions.Children, "AllConnectionTypes").Children.Should().HaveCount(6);
        var subfolder = Folder(_sessions.Children, "server_subfolder");
        subfolder.Children.Should().HaveCount(2);
        Folder(subfolder.Children, "server_subsubfolder").Children.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("rawsession", "rawhost", ProtocolType.RAW, 23, "")]
    [InlineData("RDPsession", "RDPhost", ProtocolType.RDP, 3389, "RDP\\rdp")]
    [InlineData("rloginsession", "rloginhost", ProtocolType.Rlogin, 513, "rloginuser")]
    [InlineData("ssh1session", "ssh1host", ProtocolType.SSH1, 22, "ssh1user")]
    [InlineData("ssh2session", "ssh2host", ProtocolType.SSH2, 22, "ssh2user")]
    [InlineData("telnetsession", "telnethost", ProtocolType.Telnet, 23, "telnetuser")]
    public void Import_ReadsSessions(string name, string host, ProtocolType protocol, int port, string user)
    {
        var session = Connection(Folder(_sessions.Children, "AllConnectionTypes").Children, name);

        session.Hostname.Should().Be(host);
        session.Protocol.Should().Be(protocol);
        session.Port.Should().Be(port);
        session.Username.Should().Be(user);
    }

    [Fact]
    public void Import_JoinsDescriptionLines()
    {
        Connection(_sessions.Children, "host1.org").Description.Should().Be("First Second 123456");
    }
}
