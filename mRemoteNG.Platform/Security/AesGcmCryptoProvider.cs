using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace mRemoteNG.Platform.Security;

/// <summary>
/// Cross-platform AES-256-GCM encryption provider using BouncyCastle.
/// Produces ciphertext prefixed with "AESGCM:" for format detection.
/// Key derivation uses PBKDF2-HMAC-SHA256 with 600,000 iterations (NIST SP 800-132).
/// </summary>
public sealed class AesGcmCryptoProvider : ICryptoProvider
{
    private const string Prefix = "AESGCM:";
    private const int KeySize = 32;      // AES-256
    private const int NonceSize = 12;    // GCM standard nonce
    private const int TagSize = 16;      // GCM authentication tag
    private const int SaltSize = 16;
    private const int Pbkdf2Iterations = 600_000;

    private readonly byte[] _masterKey;

    /// <param name="masterKey">
    /// A 32-byte master key, typically derived from a user password or
    /// retrieved from the platform keychain. Must be kept secret.
    /// </param>
    public AesGcmCryptoProvider(byte[] masterKey)
    {
        if (masterKey == null || masterKey.Length != KeySize)
            throw new ArgumentException($"Master key must be exactly {KeySize} bytes.", nameof(masterKey));
        _masterKey = masterKey;
    }

    /// <summary>
    /// Creates a provider using a password-derived key via PBKDF2.
    /// </summary>
    public static AesGcmCryptoProvider FromPassword(string password, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
        return new AesGcmCryptoProvider(key);
    }

    /// <inheritdoc/>
    public string Protect(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(_masterKey), TagSize * 8, nonce));

        var ciphertext = new byte[cipher.GetOutputSize(plaintextBytes.Length)];
        var len = cipher.ProcessBytes(plaintextBytes, 0, plaintextBytes.Length, ciphertext, 0);
        cipher.DoFinal(ciphertext, len);

        // Format: nonce (12) + ciphertext+tag (N+16)
        var combined = new byte[NonceSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);

        return Prefix + Convert.ToBase64String(combined);
    }

    /// <inheritdoc/>
    public string Unprotect(string ciphertext)
    {
        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Ciphertext is not in AES-GCM format. Use CanDecrypt() first.");

        var combined = Convert.FromBase64String(ciphertext[Prefix.Length..]);
        var nonce = combined[..NonceSize];
        var encryptedBytes = combined[NonceSize..];

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(_masterKey), TagSize * 8, nonce));

        var plaintext = new byte[cipher.GetOutputSize(encryptedBytes.Length)];
        var len = cipher.ProcessBytes(encryptedBytes, 0, encryptedBytes.Length, plaintext, 0);
        cipher.DoFinal(plaintext, len);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <inheritdoc/>
    public bool CanDecrypt(string ciphertext) =>
        ciphertext.StartsWith(Prefix, StringComparison.Ordinal);
}
