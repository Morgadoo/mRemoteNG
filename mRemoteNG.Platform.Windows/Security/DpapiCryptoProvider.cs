using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.Platform.Windows.Security;

/// <summary>
/// Windows DPAPI-based crypto provider.
/// Encrypts with CurrentUser scope so credentials are tied to the Windows user account.
/// Ciphertext is prefixed with "DPAPI:" to distinguish it from AES-GCM blobs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCryptoProvider : ICryptoProvider
{
    private const string Prefix = "DPAPI:";

    // Optional entropy adds extra security; use a fixed app-level secret.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("mRemoteNG-DPAPI-v1");

    /// <inheritdoc/>
    public string Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    /// <inheritdoc/>
    public string Unprotect(string ciphertext)
    {
        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Ciphertext is not in DPAPI format.");

        var encrypted = Convert.FromBase64String(ciphertext[Prefix.Length..]);
        var decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    /// <inheritdoc/>
    public bool CanDecrypt(string ciphertext) =>
        ciphertext.StartsWith(Prefix, StringComparison.Ordinal);
}
