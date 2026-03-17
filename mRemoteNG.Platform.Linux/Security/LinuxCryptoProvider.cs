using mRemoteNG.Platform.Security;

namespace mRemoteNG.Platform.Linux.Security;

/// <summary>
/// Linux crypto provider: uses AES-256-GCM via BouncyCastle.
/// The master key is derived from a machine-unique secret stored in
/// ~/.config/mRemoteNG/.keyfile (chmod 600).
/// For production use, integrate libsecret (GNOME Keyring / KDE Wallet)
/// via D-Bus to store the key securely.
/// </summary>
public sealed class LinuxCryptoProvider : ICryptoProvider
{
    private readonly AesGcmCryptoProvider _inner;

    public LinuxCryptoProvider(ISettingsProvider settingsProvider)
    {
        var keyFilePath = Path.Combine(settingsProvider.ApplicationDataDirectory, ".keyfile");
        var masterKey = LoadOrCreateKey(keyFilePath);
        _inner = new AesGcmCryptoProvider(masterKey);
    }

    public string Protect(string plaintext) => _inner.Protect(plaintext);
    public string Unprotect(string ciphertext) => _inner.Unprotect(ciphertext);
    public bool CanDecrypt(string ciphertext) => _inner.CanDecrypt(ciphertext);

    private static byte[] LoadOrCreateKey(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            var b64 = File.ReadAllText(keyFilePath).Trim();
            return Convert.FromBase64String(b64);
        }

        // Generate a new 256-bit key.
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyFilePath, Convert.ToBase64String(key));

        // Restrict permissions to owner-read-only (chmod 600).
        try
        {
            File.SetUnixFileMode(keyFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* Not on a Unix FS — ignore */ }

        return key;
    }
}
