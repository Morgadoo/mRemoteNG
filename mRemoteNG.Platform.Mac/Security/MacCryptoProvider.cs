using mRemoteNG.Platform.Security;

namespace mRemoteNG.Platform.Mac.Security;

/// <summary>
/// macOS crypto provider using AES-256-GCM.
/// Key is stored in ~/Library/Application Support/mRemoteNG/.keyfile.
/// TODO Phase 4: Replace keyfile with macOS Keychain via Security.framework P/Invoke.
/// </summary>
public sealed class MacCryptoProvider : ICryptoProvider
{
    private readonly AesGcmCryptoProvider _inner;

    public MacCryptoProvider(ISettingsProvider settingsProvider)
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
            return Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim());

        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyFilePath, Convert.ToBase64String(key));

        try
        {
            // macOS supports UnixFileMode via .NET 7+
            File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }

        return key;
    }
}
