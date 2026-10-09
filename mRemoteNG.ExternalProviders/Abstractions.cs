using mRemoteNG.Core.Connection;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// Credentials returned by an external provider. A null member means "the provider has no value
/// for it — keep the connection's own value"; at least a password or a private key is always set.
/// </summary>
public sealed record ExternalCredential
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Domain { get; init; }

    /// <summary>Private key file content (OpenSSH, PEM, PKCS#8 or PuTTY format).</summary>
    public string? PrivateKey { get; init; }
    public string? PrivateKeyPassphrase { get; init; }

    public bool HasSecret => !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(PrivateKey);
}

/// <summary>What to fetch from a credential provider for one connection attempt.</summary>
public sealed record ExternalCredentialRequest
{
    /// <summary>
    /// The connection's UserViaAPI: a secret ID (Delinea, Passwordstate) or an item reference
    /// (1Password: op://vault/item?account=…). Not used by Vault/OpenBao.
    /// </summary>
    public string Reference { get; init; } = string.Empty;

    /// <summary>The connection's username (Vault KV: the key that holds the password; SSH OTP: the login user).</summary>
    public string? Username { get; init; }

    /// <summary>The (already address-resolved) hostname; Vault SSH OTP is issued for its IP.</summary>
    public string? Hostname { get; init; }

    public string VaultMount { get; init; } = string.Empty;

    /// <summary>KV: the secret path. LDAP / SSH OTP: the role name.</summary>
    public string VaultRole { get; init; } = string.Empty;

    public VaultOpenbaoSecretEngine VaultEngine { get; init; } = VaultOpenbaoSecretEngine.Kv;

    public static ExternalCredentialRequest ForReference(string reference) => new() { Reference = reference };
}

/// <summary>Fetches connection credentials from a secret store.</summary>
public interface IExternalCredentialProvider
{
    ExternalCredentialProvider Kind { get; }

    string DisplayName { get; }

    /// <exception cref="ExternalProviderException">The secret could not be fetched.</exception>
    Task<ExternalCredential> GetAsync(ExternalCredentialRequest request, CancellationToken ct = default);

    /// <summary>Checks the configuration (login, connectivity). Returns a short success message.</summary>
    /// <exception cref="ExternalProviderException">The check failed.</exception>
    Task<string> TestAsync(CancellationToken ct = default);
}

public static class ExternalCredentialProviderExtensions
{
    /// <summary>Fetches the secret identified by <paramref name="reference"/> (the connection's UserViaAPI).</summary>
    public static Task<ExternalCredential> GetAsync(this IExternalCredentialProvider provider, string reference, CancellationToken ct = default) =>
        provider.GetAsync(ExternalCredentialRequest.ForReference(reference), ct);
}

/// <param name="InstanceId">EC2 instance ID, e.g. i-066f750a76c97583d.</param>
/// <param name="Region">AWS region, e.g. eu-central-1; empty uses the configured default region.</param>
public sealed record ExternalAddressRequest(string InstanceId, string? Region);

/// <summary>Looks up the address to connect to (e.g. the current IP of a cloud VM).</summary>
public interface IExternalAddressProvider
{
    ExternalAddressProvider Kind { get; }

    string DisplayName { get; }

    /// <exception cref="ExternalProviderException">The address could not be resolved.</exception>
    Task<string> ResolveAsync(ExternalAddressRequest request, CancellationToken ct = default);

    /// <exception cref="ExternalProviderException">The check failed.</exception>
    Task<string> TestAsync(CancellationToken ct = default);
}

/// <summary>A provider failed; the message is meant for the user and names the provider.</summary>
public sealed class ExternalProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>A question for the user (a password, an OTP code…) asked by a provider.</summary>
/// <param name="Title">Dialog title, e.g. "Delinea Secret Server".</param>
/// <param name="Message">What is asked and why.</param>
/// <param name="IsSecret">Mask the input.</param>
/// <param name="Watermark">Placeholder text of the input box.</param>
public sealed record ExternalProviderPromptRequest(string Title, string Message, bool IsSecret, string? Watermark = null);

/// <summary>Asks the user for values that are not saved (passwords, OTP codes).</summary>
public interface IExternalProviderPrompt
{
    /// <returns>The answer, or null when the user cancelled.</returns>
    Task<string?> PromptAsync(ExternalProviderPromptRequest request, CancellationToken ct = default);
}

/// <summary>Used when no UI is available: every question is answered with "cancel".</summary>
public sealed class NonInteractiveExternalProviderPrompt : IExternalProviderPrompt
{
    public Task<string?> PromptAsync(ExternalProviderPromptRequest request, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
