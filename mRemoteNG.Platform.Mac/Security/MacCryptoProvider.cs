using Microsoft.Extensions.Logging;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.Platform.Mac.Security;

/// <summary>
/// macOS crypto provider: AES-256-GCM (authenticated encryption) via BouncyCastle.
/// The random 256-bit master key lives in ~/Library/Application Support/mRemoteNG/.keyfile
/// (mode 0600), managed by <see cref="KeyFileStore"/>.
/// TODO Phase 4: Replace keyfile with macOS Keychain via Security.framework P/Invoke.
/// </summary>
public sealed class MacCryptoProvider : ICryptoProvider
{
    public const string KeyFileName = ".keyfile";

    private readonly AesGcmCryptoProvider _inner;

    public MacCryptoProvider(ISettingsProvider settingsProvider, ILogger<MacCryptoProvider>? logger = null)
        : this(Path.Combine(settingsProvider.ApplicationDataDirectory, KeyFileName), logger)
    {
    }

    private MacCryptoProvider(string keyFilePath, ILogger? logger)
    {
        KeyFilePath = keyFilePath;
        _inner = new AesGcmCryptoProvider(KeyFileStore.LoadOrCreateKey(keyFilePath, logger));
    }

    /// <summary>Creates a provider using an explicit key file path.</summary>
    public static MacCryptoProvider FromKeyFile(string keyFilePath, ILogger? logger = null) =>
        new(keyFilePath, logger);

    public string KeyFilePath { get; }

    public string Protect(string plaintext) => _inner.Protect(plaintext);
    public string Unprotect(string ciphertext) => _inner.Unprotect(ciphertext);
    public bool CanDecrypt(string ciphertext) => _inner.CanDecrypt(ciphertext);
}
