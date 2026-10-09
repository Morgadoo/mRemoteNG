using FluentAssertions;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.SymmetricEncryption;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

public class AeadCryptographyProviderTests
{
    public static TheoryData<BlockCipherEngines, BlockCipherModes> AllCiphers()
    {
        var data = new TheoryData<BlockCipherEngines, BlockCipherModes>();
        foreach (var engine in Enum.GetValues<BlockCipherEngines>())
            foreach (var mode in Enum.GetValues<BlockCipherModes>())
                data.Add(engine, mode);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllCiphers))]
    public void EncryptThenDecrypt_RoundTrips(BlockCipherEngines engine, BlockCipherModes mode)
    {
        var provider = new AeadCryptographyProvider(engine, mode);

        var cipherText = provider.Encrypt("pässwörd ✓", "key");

        provider.Decrypt(cipherText, "key").Should().Be("pässwörd ✓");
    }

    [Fact]
    public void Encrypt_ProducesLegacyLayout_SaltNonceCiphertextTag()
    {
        var cipherText = new AeadCryptographyProvider().Encrypt("abc", "key");

        // 16-byte salt + 16-byte nonce + 3-byte ciphertext + 16-byte tag
        Convert.FromBase64String(cipherText).Should().HaveCount(16 + 16 + 3 + 16);
    }

    [Fact]
    public void Decrypt_WithWrongKey_ThrowsEncryptionException()
    {
        var provider = new AeadCryptographyProvider();
        var cipherText = provider.Encrypt("secret", "right");

        var act = () => provider.Decrypt(cipherText, "wrong");

        act.Should().Throw<EncryptionException>();
    }

    [Fact]
    public void Decrypt_WithDifferentIterationCount_Fails()
    {
        var cipherText = new AeadCryptographyProvider(keyDerivationIterations: 5000).Encrypt("secret", "key");

        var act = () => new AeadCryptographyProvider(keyDerivationIterations: 1000).Decrypt(cipherText, "key");

        act.Should().Throw<EncryptionException>();
    }

    [Fact]
    public void LegacyRijndael_RoundTrips()
    {
        var provider = new LegacyRijndaelCryptographyProvider();

        provider.Decrypt(provider.Encrypt("secret", "mR3m"), "mR3m").Should().Be("secret");
    }
}
