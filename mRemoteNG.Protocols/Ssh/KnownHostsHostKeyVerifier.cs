using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// Verifies host keys against <see cref="KnownHostsStore"/>:
/// a known matching key is trusted silently; an unknown key is shown to the user, who may accept it
/// (optionally storing it); a changed key is refused unless the user explicitly replaces the stored
/// one; a revoked key is always refused.
/// </summary>
public sealed class KnownHostsHostKeyVerifier : IHostKeyVerifier
{
    private readonly KnownHostsStore _store;
    private readonly ISshUserPrompt _prompt;
    private readonly ILogger<KnownHostsHostKeyVerifier> _logger;

    // Keys the user approved in this run. Covers "connect once" and a failed known_hosts write.
    private readonly HashSet<string> _approvedThisSession = new(StringComparer.Ordinal);
    private readonly object _approvedLock = new();

    public KnownHostsHostKeyVerifier(
        KnownHostsStore store,
        ISshUserPrompt prompt,
        ILogger<KnownHostsHostKeyVerifier>? logger = null)
    {
        _store = store;
        _prompt = prompt;
        _logger = logger ?? NullLogger<KnownHostsHostKeyVerifier>.Instance;
    }

    public IReadOnlyCollection<string> GetKnownKeyTypes(string host, int port) =>
        _store.GetKnownKeyTypes(host, port);

    public HostKeyVerdict Verify(HostKeyInfo hostKey)
    {
        var hostName = KnownHostsStore.FormatHost(hostKey.Host, hostKey.Port);
        var lookup = _store.Check(hostKey);

        switch (lookup.Status)
        {
            case HostKeyStatus.Trusted:
                return HostKeyVerdict.Trust($"Host key for {hostName} matches known_hosts.");

            case HostKeyStatus.Revoked:
                _logger.LogError("Host key {Fingerprint} for {Host} is revoked", hostKey.Fingerprint, hostName);
                return HostKeyVerdict.Reject(
                    $"The {hostKey.KeyType} host key {hostKey.Fingerprint} presented by {hostName} is marked as revoked.");
        }

        if (IsApprovedThisSession(hostKey))
            return HostKeyVerdict.Trust($"Host key for {hostName} was accepted earlier in this session.");

        if (lookup.Status == HostKeyStatus.Mismatch)
        {
            _logger.LogWarning(
                "HOST KEY MISMATCH for {Host}: presented {Type} {Fingerprint} differs from the stored key",
                hostName, hostKey.KeyType, hostKey.Fingerprint);
        }

        var request = new HostKeyPromptRequest(hostKey, lookup.Status, lookup.KnownEntries, _store.WritableFile);
        HostKeyDecision decision;
        try
        {
            decision = _prompt.ConfirmHostKey(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Host key prompt failed for {Host}", hostName);
            decision = HostKeyDecision.Reject;
        }

        return (lookup.Status, decision) switch
        {
            (HostKeyStatus.Unknown, HostKeyDecision.AcceptAndSave) => AcceptAndStore(hostKey, replace: false),
            (HostKeyStatus.Unknown, HostKeyDecision.AcceptOnce) => AcceptOnce(hostKey),
            (HostKeyStatus.Mismatch, HostKeyDecision.ReplaceAndSave) => AcceptAndStore(hostKey, replace: true),
            (HostKeyStatus.Mismatch, _) => HostKeyVerdict.Reject(
                $"The host key for {hostName} has changed ({hostKey.KeyType} {hostKey.Fingerprint}). " +
                "The connection was refused because this can indicate a man-in-the-middle attack."),
            _ => HostKeyVerdict.Reject($"The host key for {hostName} ({hostKey.Fingerprint}) was not accepted."),
        };
    }

    private HostKeyVerdict AcceptOnce(HostKeyInfo hostKey)
    {
        Approve(hostKey);
        return HostKeyVerdict.Trust("Host key accepted for this session.");
    }

    private HostKeyVerdict AcceptAndStore(HostKeyInfo hostKey, bool replace)
    {
        Approve(hostKey);
        try
        {
            if (replace)
                _store.Replace(hostKey);
            else
                _store.Add(hostKey);
            _logger.LogInformation("Stored {Type} host key {Fingerprint} for {Host} in {File}",
                hostKey.KeyType, hostKey.Fingerprint, KnownHostsStore.FormatHost(hostKey.Host, hostKey.Port),
                _store.WritableFile);
            return HostKeyVerdict.Trust("Host key accepted and saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write {File}; the host key is trusted for this session only",
                _store.WritableFile);
            return HostKeyVerdict.Trust("Host key accepted, but it could not be saved: " + ex.Message);
        }
    }

    private static string ApprovalKey(HostKeyInfo hostKey) =>
        $"{KnownHostsStore.FormatHost(hostKey.Host, hostKey.Port)} {hostKey.KeyType} {hostKey.KeyBase64}";

    private void Approve(HostKeyInfo hostKey)
    {
        lock (_approvedLock)
            _approvedThisSession.Add(ApprovalKey(hostKey));
    }

    private bool IsApprovedThisSession(HostKeyInfo hostKey)
    {
        lock (_approvedLock)
            return _approvedThisSession.Contains(ApprovalKey(hostKey));
    }
}
