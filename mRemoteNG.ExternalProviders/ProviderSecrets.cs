using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// Provider secrets (passwords, API keys, tokens): saved ones are stored as <see cref="ICryptoProvider"/>
/// ciphertext in the settings; unsaved ones are asked for once and kept in memory until the app exits
/// (as the legacy app did).
/// </summary>
public sealed class ProviderSecrets
{
    private readonly ICryptoProvider _crypto;
    private readonly IExternalProviderPrompt _prompt;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _session = new(StringComparer.Ordinal);

    public ProviderSecrets(ICryptoProvider crypto, IExternalProviderPrompt prompt, ILogger<ProviderSecrets>? logger = null)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Encrypts a secret for storage in the settings; empty stays empty.</summary>
    public string Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? string.Empty : _crypto.Protect(plaintext);

    /// <summary>Decrypts a saved secret; null when nothing is saved or it cannot be decrypted (e.g. another machine).</summary>
    public string? TryUnprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return null;
        try
        {
            var value = _crypto.Unprotect(protectedValue);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A saved external provider secret could not be decrypted; asking for it instead");
            return null;
        }
    }

    /// <summary>
    /// Returns the saved secret, else the one entered earlier in this session, else asks the user.
    /// </summary>
    /// <param name="protectedValue">The saved ciphertext from the settings (may be empty).</param>
    /// <param name="sessionKey">Identifies the secret (provider, server, user) in the session cache.</param>
    /// <exception cref="ExternalProviderException">Nothing is saved and the user cancelled (or no UI is available).</exception>
    public async Task<string> GetAsync(string? protectedValue, string sessionKey, ExternalProviderPromptRequest prompt, CancellationToken ct)
    {
        if (TryUnprotect(protectedValue) is { } saved)
            return saved;
        if (_session.TryGetValue(sessionKey, out var cached))
            return cached;

        var answer = await AskAsync(prompt, ct);
        _session[sessionKey] = answer;
        return answer;
    }

    /// <summary>Asks every time (one-time passwords).</summary>
    /// <exception cref="ExternalProviderException">The user cancelled.</exception>
    public async Task<string> AskAsync(ExternalProviderPromptRequest prompt, CancellationToken ct)
    {
        var answer = await _prompt.PromptAsync(prompt, ct);
        if (string.IsNullOrEmpty(answer))
            throw new ExternalProviderException($"{prompt.Title}: {prompt.Watermark ?? "the value"} was not entered.");
        return answer;
    }

    /// <summary>Forgets a secret entered in this session (after the server rejected it).</summary>
    public void Forget(string sessionKey) => _session.TryRemove(sessionKey, out _);

    /// <summary>True when the secret came from the settings, so a rejection means the saved value is wrong.</summary>
    public bool IsSaved(string? protectedValue) => TryUnprotect(protectedValue) is not null;
}
