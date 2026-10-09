namespace mRemoteNG.Protocols.Ssh;

/// <summary>What the user decided about a host key that is not (or no longer) trusted.</summary>
public enum HostKeyDecision
{
    Reject,

    /// <summary>Trust an unknown key for the lifetime of the application without storing it.</summary>
    AcceptOnce,

    /// <summary>Trust an unknown key and add it to known_hosts.</summary>
    AcceptAndSave,

    /// <summary>Replace a mismatching stored key with the presented one.</summary>
    ReplaceAndSave,
}

/// <summary>Information shown to the user when a host key needs a decision.</summary>
public sealed record HostKeyPromptRequest(
    HostKeyInfo HostKey,
    HostKeyStatus Status,
    IReadOnlyList<KnownHostEntry> KnownEntries,
    string KnownHostsFile)
{
    /// <summary>Stored keys of the presented type that differ from it (non-empty for a mismatch).</summary>
    public IReadOnlyList<KnownHostEntry> ConflictingEntries =>
        KnownEntries.Where(e => e.Marker == KnownHostMarker.None && e.KeyType == HostKey.KeyType).ToList();

    /// <summary>True when keys of other types are known for this host (unexpected key-type switch).</summary>
    public bool OtherKeyTypesKnown =>
        Status == HostKeyStatus.Unknown && KnownEntries.Any(e => e.Marker == KnownHostMarker.None);
}

/// <summary>A free-text question for the user (username, password, keyboard-interactive prompt).</summary>
public sealed record SshTextPrompt(string Title, string Message, bool IsSecret, string? Watermark = null);

/// <summary>
/// User interaction needed by SSH connections. The application supplies a UI implementation;
/// <see cref="NonInteractiveSshUserPrompt"/> is the fallback that never trusts unknown keys.
/// </summary>
public interface ISshUserPrompt
{
    /// <summary>
    /// Asks about an unknown or changed host key. Blocks until the user answers, so it must never be
    /// called from the UI thread.
    /// </summary>
    HostKeyDecision ConfirmHostKey(HostKeyPromptRequest request);

    /// <summary>Asks the user for text; returns null when cancelled or when no user is available.</summary>
    Task<string?> PromptTextAsync(SshTextPrompt prompt, CancellationToken ct = default);
}

/// <summary>Used when no UI is registered: rejects unknown/changed host keys and answers no prompts.</summary>
public sealed class NonInteractiveSshUserPrompt : ISshUserPrompt
{
    public HostKeyDecision ConfirmHostKey(HostKeyPromptRequest request) => HostKeyDecision.Reject;

    public Task<string?> PromptTextAsync(SshTextPrompt prompt, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
