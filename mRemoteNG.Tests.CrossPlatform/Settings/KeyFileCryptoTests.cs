using System.Security.Cryptography;
using FluentAssertions;
using mRemoteNG.Platform.Linux.Security;
using mRemoteNG.Platform.Mac.Security;
using mRemoteNG.Platform.Security;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

public sealed class KeyFileCryptoTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void LoadOrCreateKey_CreatesRandom256BitKey_AndReturnsItAgainLater()
    {
        var pathA = _dir.Combine("a.key");
        var pathB = _dir.Combine("b.key");

        var keyA = KeyFileStore.LoadOrCreateKey(pathA);
        var keyB = KeyFileStore.LoadOrCreateKey(pathB);

        keyA.Should().HaveCount(32);
        keyA.Should().NotEqual(keyB, "every key file gets an independently generated key");
        keyA.Should().NotEqual(new byte[32]);
        KeyFileStore.LoadOrCreateKey(pathA).Should().Equal(keyA);
    }

    [SkippableFact]
    public void LoadOrCreateKey_CreatesFileWithOwnerOnlyPermissions()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix permissions only");
        var path = _dir.Combine(".keyfile");

        KeyFileStore.LoadOrCreateKey(path);

        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SkippableFact]
    public void LoadOrCreateKey_TightensPermissionsOfExistingKeyFile()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix permissions only");
        var path = _dir.Combine(".keyfile");
        var key = KeyFileStore.LoadOrCreateKey(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        KeyFileStore.LoadOrCreateKey(path).Should().Equal(key);

        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Theory]
    [InlineData("not base64 !!!")]
    [InlineData("AAAA")] // valid Base64, wrong length
    [InlineData("")]
    public void LoadOrCreateKey_MovesMalformedKeyAside_AndGeneratesNewKey(string contents)
    {
        var path = _dir.Combine(".keyfile");
        File.WriteAllText(path, contents);

        var key = KeyFileStore.LoadOrCreateKey(path);

        key.Should().HaveCount(32);
        Directory.GetFiles(_dir.Path, ".keyfile.invalid-*").Should().ContainSingle()
            .Which.Should().Match(p => File.ReadAllText(p) == contents);
    }

    [Fact]
    public void LoadOrCreateKey_ConcurrentCallers_AllGetTheSameKey()
    {
        var path = _dir.Combine(".keyfile");
        var keys = new byte[16][];

        Parallel.For(0, keys.Length, i => keys[i] = KeyFileStore.LoadOrCreateKey(path));

        keys.Should().AllSatisfy(k => k.Should().Equal(keys[0]));
        Directory.GetFiles(_dir.Path).Should().ContainSingle("no temp files may be left behind");
    }

    [Fact]
    public void LinuxProvider_RoundTrips_AndSurvivesRestart()
    {
        var path = _dir.Combine(".keyfile");
        var encrypted = LinuxCryptoProvider.FromKeyFile(path).Protect("p@ssw0rd ü");

        encrypted.Should().StartWith("AESGCM:").And.NotContain("p@ssw0rd");
        LinuxCryptoProvider.FromKeyFile(path).Unprotect(encrypted).Should().Be("p@ssw0rd ü");
    }

    [Fact]
    public void MacProvider_RoundTrips()
    {
        var provider = MacCryptoProvider.FromKeyFile(_dir.Combine(".keyfile"));
        provider.Unprotect(provider.Protect("secret")).Should().Be("secret");
    }

    [Fact]
    public void Protect_UsesFreshNonceEachTime()
    {
        var provider = LinuxCryptoProvider.FromKeyFile(_dir.Combine(".keyfile"));
        provider.Protect("same").Should().NotBe(provider.Protect("same"));
    }

    [Fact]
    public void Unprotect_WithAnotherKey_ThrowsCryptographicException()
    {
        var encrypted = LinuxCryptoProvider.FromKeyFile(_dir.Combine("one.key")).Protect("secret");
        var other = LinuxCryptoProvider.FromKeyFile(_dir.Combine("two.key"));

        var act = () => other.Unprotect(encrypted);

        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData("AESGCM:")]
    [InlineData("AESGCM:AAAA")]
    [InlineData("AESGCM:%%%")]
    public void Unprotect_TruncatedOrInvalidCiphertext_ThrowsCryptographicException(string ciphertext)
    {
        var provider = LinuxCryptoProvider.FromKeyFile(_dir.Combine(".keyfile"));

        var act = () => provider.Unprotect(ciphertext);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_ThrowsCryptographicException()
    {
        var provider = LinuxCryptoProvider.FromKeyFile(_dir.Combine(".keyfile"));
        var bytes = Convert.FromBase64String(provider.Protect("secret")["AESGCM:".Length..]);
        bytes[^1] ^= 0x01;

        var act = () => provider.Unprotect("AESGCM:" + Convert.ToBase64String(bytes));

        act.Should().Throw<CryptographicException>();
    }
}
