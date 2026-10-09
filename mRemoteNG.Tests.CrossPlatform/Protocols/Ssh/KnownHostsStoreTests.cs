using FluentAssertions;
using mRemoteNG.Protocols.Ssh;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Protocols.Ssh.SshTestKeys;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

public sealed class KnownHostsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mremoteng-kh-" + Guid.NewGuid().ToString("N"));
    private readonly string _appFile;
    private readonly string _userFile;

    public KnownHostsStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _appFile = Path.Combine(_dir, "settings", "known_hosts");
        _userFile = Path.Combine(_dir, "ssh_known_hosts");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private KnownHostsStore CreateStore() => new(_appFile, [_userFile]);

    [Theory]
    [InlineData(Ed25519, "ssh-ed25519", Ed25519Fingerprint)]
    [InlineData(Ecdsa256, "ecdsa-sha2-nistp256", Ecdsa256Fingerprint)]
    [InlineData(Rsa, "ssh-rsa", RsaFingerprint)]
    public void HostKeyInfo_ReadsTypeAndMatchesOpenSshFingerprint(string base64, string type, string fingerprint)
    {
        var key = Key("host", 22, base64);

        key.KeyType.Should().Be(type);
        key.Fingerprint.Should().Be(fingerprint);
    }

    [Theory]
    [InlineData("Example.COM", 22, "example.com")]
    [InlineData("example.com", 2222, "[example.com]:2222")]
    [InlineData("10.0.0.1", 22, "10.0.0.1")]
    public void FormatHost_UsesBracketsOnlyForNonDefaultPorts(string host, int port, string expected) =>
        KnownHostsStore.FormatHost(host, port).Should().Be(expected);

    [Fact]
    public void Parse_ReadsPlainEntriesForAllKeyTypes_AndSkipsCommentsAndGarbage()
    {
        var content = $"""
            # comment line

            server1,10.0.0.1 ssh-ed25519 {Ed25519} root@server1
            [server2]:2200 ecdsa-sha2-nistp256 {Ecdsa256}
            server3 ssh-rsa {Rsa}
            broken-line ssh-ed25519
            server4 ssh-ed25519 not-base64!!
            server5 ssh-rsa {Ed25519}
            @revoked server6 ssh-ed25519 {Ed25519}
            @cert-authority *.example.com ssh-ed25519 {Ed25519}
            """;

        var entries = KnownHostsStore.Parse(content, "test", isReadOnly: true);

        entries.Select(e => e.HostPatterns).Should().Equal("server1,10.0.0.1", "[server2]:2200", "server3", "server6", "*.example.com");
        entries[0].KeyType.Should().Be("ssh-ed25519");
        entries[1].KeyType.Should().Be("ecdsa-sha2-nistp256");
        entries[2].KeyType.Should().Be("ssh-rsa");
        entries[3].Marker.Should().Be(KnownHostMarker.Revoked);
        entries[4].Marker.Should().Be(KnownHostMarker.CertAuthority);
        entries.Should().OnlyContain(e => e.IsReadOnly && e.Source == "test");
    }

    [Theory]
    [InlineData("server1,10.0.0.1", "10.0.0.1", true)]
    [InlineData("server1,10.0.0.1", "server2", false)]
    [InlineData("*.example.com", "web.example.com", true)]
    [InlineData("*.example.com,!bad.example.com", "bad.example.com", false)]
    [InlineData("web?.example.com", "web1.example.com", true)]
    [InlineData("web?.example.com", "web10.example.com", false)]
    [InlineData("[server]:2200", "[server]:2200", true)]
    [InlineData("server", "[server]:2200", false)]
    [InlineData("SERVER", "server", true)]
    public void HostPatternMatches_HandlesListsWildcardsAndNegation(string patterns, string host, bool expected) =>
        KnownHostsStore.HostPatternMatches(patterns, host).Should().Be(expected);

    [Fact]
    public void HashedEntriesFromSshKeygen_MatchTheirHosts()
    {
        File.WriteAllText(_userFile, HashedKnownHosts);
        var store = CreateStore();

        store.Check(Key("example.com", 22, Ed25519)).Status.Should().Be(HostKeyStatus.Trusted);
        store.Check(Key("example.com", 2222, Ecdsa256)).Status.Should().Be(HostKeyStatus.Trusted);
        store.Check(Key("example.com", 2222, Ed25519)).Status.Should().Be(HostKeyStatus.Unknown);
        store.Check(Key("other.com", 22, Ed25519)).Status.Should().Be(HostKeyStatus.Unknown);
    }

    [Fact]
    public void HashHostName_IsVerifiedByHostPatternMatches()
    {
        var hashed = KnownHostsStore.HashHostName("[db]:5022", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);

        hashed.Should().StartWith("|1|AQIDBAUGBwgJCgsMDQ4PEBESExQ=|");
        KnownHostsStore.HostPatternMatches(hashed, "[db]:5022").Should().BeTrue();
        KnownHostsStore.HostPatternMatches(hashed, "db").Should().BeFalse();
    }

    [Fact]
    public void Check_ReportsMismatchForDifferentKeyOfSameType()
    {
        File.WriteAllText(_userFile, $"server ssh-ed25519 {Ed25519}\n");
        var other = Convert.FromBase64String(Ed25519);
        other[^1] ^= 0xFF;

        var lookup = CreateStore().Check(new HostKeyInfo("server", 22, other));

        lookup.Status.Should().Be(HostKeyStatus.Mismatch);
        lookup.KnownEntries.Should().ContainSingle(e => e.KeyType == "ssh-ed25519");
    }

    [Fact]
    public void Check_TreatsOtherKeyTypeAsUnknown_ButReportsKnownEntries()
    {
        File.WriteAllText(_userFile, $"server ssh-ed25519 {Ed25519}\n");

        var lookup = CreateStore().Check(Key("server", 22, Ecdsa256));

        lookup.Status.Should().Be(HostKeyStatus.Unknown);
        lookup.KnownEntries.Should().ContainSingle();
    }

    [Fact]
    public void Check_RevokedKeyIsRevokedEvenIfAlsoListedAsTrusted()
    {
        File.WriteAllText(_userFile, $"server ssh-ed25519 {Ed25519}\n@revoked * ssh-ed25519 {Ed25519}\n");

        CreateStore().Check(Key("server", 22, Ed25519)).Status.Should().Be(HostKeyStatus.Revoked);
    }

    [Fact]
    public void Add_CreatesTheFileInOpenSshFormat_AndKeyBecomesTrusted()
    {
        var store = CreateStore();
        var key = Key("Server.Example", 2222, Ecdsa256);

        store.Add(key);

        File.ReadAllText(_appFile).Should().Be($"[server.example]:2222 ecdsa-sha2-nistp256 {Ecdsa256}\n");
        store.Check(key).Status.Should().Be(HostKeyStatus.Trusted);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(_appFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Replace_RemovesOnlyMatchingHostAndType_AndKeepsOtherLines()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_appFile)!);
        var oldKey = Convert.FromBase64String(Ed25519);
        oldKey[^1] ^= 0x55;
        File.WriteAllText(_appFile,
            "# my hosts\n" +
            $"server ssh-ed25519 {Convert.ToBase64String(oldKey)}\n" +
            $"server ecdsa-sha2-nistp256 {Ecdsa256}\n" +
            $"other ssh-ed25519 {Convert.ToBase64String(oldKey)}\n");
        var store = CreateStore();

        store.Replace(Key("server", 22, Ed25519));

        File.ReadAllLines(_appFile).Should().Equal(
            "# my hosts",
            $"server ecdsa-sha2-nistp256 {Ecdsa256}",
            $"other ssh-ed25519 {Convert.ToBase64String(oldKey)}",
            $"server ssh-ed25519 {Ed25519}");
        store.Check(Key("server", 22, Ed25519)).Status.Should().Be(HostKeyStatus.Trusted);
    }

    [Fact]
    public void WritableFileTakesPrecedence_AndReadOnlyFileIsNeverModified()
    {
        var stale = Convert.FromBase64String(Ed25519);
        stale[^1] ^= 0x01;
        var userContent = $"server ssh-ed25519 {Convert.ToBase64String(stale)}\n";
        File.WriteAllText(_userFile, userContent);
        var store = CreateStore();
        var current = Key("server", 22, Ed25519);
        store.Check(current).Status.Should().Be(HostKeyStatus.Mismatch);

        store.Replace(current);

        store.Check(current).Status.Should().Be(HostKeyStatus.Trusted);
        store.Check(new HostKeyInfo("server", 22, stale)).Status.Should().Be(HostKeyStatus.Mismatch);
        File.ReadAllText(_userFile).Should().Be(userContent);
    }

    [Fact]
    public void GetKnownKeyTypes_ListsTypesAcrossFiles()
    {
        File.WriteAllText(_userFile, $"server ssh-rsa {Rsa}\n");
        var store = CreateStore();
        store.Add(Key("server", 22, Ed25519));

        store.GetKnownKeyTypes("server", 22).Should().BeEquivalentTo("ssh-ed25519", "ssh-rsa");
        store.GetKnownKeyTypes("server", 2222).Should().BeEmpty();
    }

    [Fact]
    public void MissingFiles_AreTreatedAsEmpty()
    {
        CreateStore().Check(Key("server", 22, Ed25519)).Status.Should().Be(HostKeyStatus.Unknown);
    }
}
