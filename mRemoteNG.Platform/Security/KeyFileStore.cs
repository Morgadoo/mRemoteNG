using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Platform.Security;

/// <summary>
/// Loads or creates the 256-bit master key used by the keyfile-based crypto providers (Linux, macOS).
/// <list type="bullet">
///   <item>New keys come from <see cref="RandomNumberGenerator"/>.</item>
///   <item>The key file is created with mode 0600 from the start (no window where it is world-readable)
///         and is written atomically, so a crash never leaves a truncated key.</item>
///   <item>If two processes start at once, only one key wins; the other process reads it.</item>
///   <item>An existing key file with group/other permissions is tightened back to 0600.</item>
///   <item>A malformed key file is moved aside (never silently overwritten) and a new key is generated.</item>
/// </list>
/// The key protects secrets against disclosure of the data files alone; it does not protect against
/// code running as the same user (the same trust model as the legacy default-password encryption).
/// </summary>
public static class KeyFileStore
{
    public const int KeySize = 32;

    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static byte[] LoadOrCreateKey(string keyFilePath, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (File.Exists(keyFilePath))
            {
                if (TryReadKey(keyFilePath, logger, out var existing))
                    return existing;

                MoveAside(keyFilePath, logger);
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            var contents = Encoding.ASCII.GetBytes(Convert.ToBase64String(key));
            if (AtomicFile.TryCreateNew(keyFilePath, contents, AtomicFile.OwnerOnly))
            {
                logger.LogInformation("Generated new master key file {Path}", keyFilePath);
                return key;
            }

            // Another process created the key between our existence check and the rename: use theirs.
        }

        throw new CryptographicException($"Could not load or create the key file '{keyFilePath}'.");
    }

    private static bool TryReadKey(string keyFilePath, ILogger logger, out byte[] key)
    {
        key = [];
        string text;
        try
        {
            text = File.ReadAllText(keyFilePath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable (not malformed) keys must not be replaced: that would orphan every stored secret.
            throw new CryptographicException($"The key file '{keyFilePath}' exists but cannot be read.", ex);
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            logger.LogError("Key file {Path} is not valid Base64", keyFilePath);
            return false;
        }

        if (decoded.Length != KeySize)
        {
            logger.LogError("Key file {Path} holds a {Length}-byte key; expected {Expected}", keyFilePath, decoded.Length, KeySize);
            return false;
        }

        EnsureOwnerOnly(keyFilePath, logger);
        key = decoded;
        return true;
    }

    private static void EnsureOwnerOnly(string keyFilePath, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var mode = File.GetUnixFileMode(keyFilePath);
            if ((mode & GroupOrOther) != 0)
            {
                File.SetUnixFileMode(keyFilePath, AtomicFile.OwnerOnly);
                logger.LogWarning("Key file {Path} was accessible by other users ({Mode}); permissions reset to 0600", keyFilePath, mode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not verify permissions of key file {Path}", keyFilePath);
        }
    }

    private static void MoveAside(string keyFilePath, ILogger logger)
    {
        var backup = $"{keyFilePath}.invalid-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        File.Move(keyFilePath, backup);
        logger.LogError("Malformed key file moved to {Backup}; a new key is generated. Secrets encrypted with the old key cannot be decrypted.", backup);
    }
}
