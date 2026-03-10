namespace mRemoteNG.Platform.Security;

/// <summary>
/// Abstracts data protection (encryption/decryption) across platforms.
/// On Windows: wraps DPAPI (System.Security.Cryptography.ProtectedData).
/// On Linux/macOS: uses AES-256-GCM via BouncyCastle with a key stored
///   in the platform keychain (libsecret on Linux, Keychain on macOS).
/// </summary>
public interface ICryptoProvider
{
    /// <summary>
    /// Encrypts the given plaintext and returns a Base64-encoded ciphertext.
    /// The result is safe to persist in XML config files.
    /// </summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a Base64-encoded ciphertext that was previously protected
    /// with <see cref="Protect"/>.
    /// </summary>
    string Unprotect(string ciphertext);

    /// <summary>
    /// Returns true if the given ciphertext was produced by this provider
    /// and can be decrypted. Used for migration: a Windows DPAPI blob is
    /// not decodable on Linux, so the migration helper can re-encrypt.
    /// </summary>
    bool CanDecrypt(string ciphertext);
}
