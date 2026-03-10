using FluentAssertions;
using mRemoteNG.Platform.Security;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Platform;

/// <summary>
/// Unit tests for <see cref="AesGcmCryptoProvider"/>.
/// All tests run on Linux, macOS, and Windows without additional setup.
/// </summary>
public sealed class AesGcmCryptoProviderTests : IDisposable
{
    private readonly string _testKeyFile;
    private readonly AesGcmCryptoProvider _sut;

    public AesGcmCryptoProviderTests()
    {
        _testKeyFile = Path.Combine(Path.GetTempPath(), $"test-key-{Guid.NewGuid():N}");
        _sut = new AesGcmCryptoProvider(_testKeyFile);
    }

    [Fact]
    public void Protect_ShouldReturnAesGcmPrefixedString()
    {
        // Arrange
        const string plaintext = "super-secret-password";

        // Act
        string encrypted = _sut.Protect(plaintext);

        // Assert
        encrypted.Should().StartWith("AESGCM:");
        encrypted.Should().NotContain(plaintext);
    }

    [Fact]
    public void Unprotect_ShouldRoundtripCorrectly()
    {
        // Arrange
        const string original = "hunter2";

        // Act
        string encrypted = _sut.Protect(original);
        string decrypted = _sut.Unprotect(encrypted);

        // Assert
        decrypted.Should().Be(original);
    }

    [Fact]
    public void Protect_ShouldProduceDifferentCiphertextForSamePlaintext()
    {
        // Random nonce means each call produces a unique ciphertext
        const string plaintext = "same-password";

        string enc1 = _sut.Protect(plaintext);
        string enc2 = _sut.Protect(plaintext);

        enc1.Should().NotBe(enc2, "AES-GCM uses a random nonce per encrypt call");
    }

    [Fact]
    public void Unprotect_ShouldThrowOnTamperedCiphertext()
    {
        // Arrange
        string encrypted = _sut.Protect("original");
        // Flip a byte in the middle (after the AESGCM: prefix + base64 header)
        char[] chars = encrypted.ToCharArray();
        chars[20] = chars[20] == 'A' ? 'B' : 'A';
        string tampered = new(chars);

        // Act & Assert
        Action act = () => _sut.Unprotect(tampered);
        act.Should().Throw<Exception>("AEAD tag verification must fail on tampered data");
    }

    [Fact]
    public void CanDecrypt_ReturnsTrue_ForAesGcmPrefix()
    {
        string encrypted = _sut.Protect("test");
        _sut.CanDecrypt(encrypted).Should().BeTrue();
    }

    [Fact]
    public void CanDecrypt_ReturnsFalse_ForDpapiPrefix()
    {
        _sut.CanDecrypt("DPAPI:AQIDBA==").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("パスワード")]                    // Unicode — Japanese
    [InlineData("p@$$w0rd!#&*()_+-=[]{}|;':\",./<>?")]  // All special chars
    [InlineData("Ⅷ㊗🔑")]                         // Emoji + symbols
    public void Protect_Unprotect_ShouldHandleVariousInputs(string input)
    {
        string encrypted = _sut.Protect(input);
        string decrypted = _sut.Unprotect(encrypted);
        decrypted.Should().Be(input);
    }

    [Fact]
    public void KeyFile_ShouldBePersisted_AcrossInstances()
    {
        // Encrypt with first instance
        const string secret = "shared-key-test";
        string encrypted = _sut.Protect(secret);

        // Decrypt with a new instance pointing to the same key file
        var provider2 = new AesGcmCryptoProvider(_testKeyFile);
        string decrypted = provider2.Unprotect(encrypted);

        decrypted.Should().Be(secret);
    }

    public void Dispose()
    {
        if (File.Exists(_testKeyFile))
            File.Delete(_testKeyFile);
    }
}
