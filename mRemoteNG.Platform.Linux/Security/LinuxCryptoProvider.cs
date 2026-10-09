using Microsoft.Extensions.Logging;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.Platform.Linux.Security;

/// <summary>
/// Linux crypto provider: AES-256-GCM (authenticated encryption) via BouncyCastle.
/// The random 256-bit master key lives in ~/.config/mRemoteNG/.keyfile (mode 0600),
/// managed by <see cref="KeyFileStore"/>.
/// For stronger protection, a future version can store the key in libsecret
/// (GNOME Keyring / KDE Wallet) via D-Bus.
/// </summary>
public sealed class LinuxCryptoProvider : ICryptoProvider
{
    public const string KeyFileName = ".keyfile";

    private readonly AesGcmCryptoProvider _inner;

    public LinuxCryptoProvider(ISettingsProvider settingsProvider, ILogger<LinuxCryptoProvider>? logger = null)
        : this(Path.Combine(settingsProvider.ApplicationDataDirectory, KeyFileName), logger)
    {
    }

    private LinuxCryptoProvider(string keyFilePath, ILogger? logger)
    {
        KeyFilePath = keyFilePath;
        _inner = new AesGcmCryptoProvider(KeyFileStore.LoadOrCreateKey(keyFilePath, logger));
    }

    /// <summary>Creates a provider using an explicit key file path.</summary>
    public static LinuxCryptoProvider FromKeyFile(string keyFilePath, ILogger? logger = null) =>
        new(keyFilePath, logger);

    public string KeyFilePath { get; }

    public string Protect(string plaintext) => _inner.Protect(plaintext);
    public string Unprotect(string ciphertext) => _inner.Unprotect(ciphertext);
    public bool CanDecrypt(string ciphertext) => _inner.CanDecrypt(ciphertext);
}
