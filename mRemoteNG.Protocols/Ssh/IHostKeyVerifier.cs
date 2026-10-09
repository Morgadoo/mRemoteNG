namespace mRemoteNG.Protocols.Ssh;

/// <summary>Outcome of a host key verification.</summary>
public readonly record struct HostKeyVerdict(bool IsTrusted, string Reason)
{
    public static HostKeyVerdict Trust(string reason) => new(true, reason);
    public static HostKeyVerdict Reject(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether an SSH server's host key is trusted. Used by every SSH.NET client
/// (shell sessions and SFTP) before authentication.
/// </summary>
public interface IHostKeyVerifier
{
    /// <summary>
    /// Verifies <paramref name="hostKey"/>. May block while the user is asked, so it must be
    /// called from a background thread (SSH.NET raises <c>HostKeyReceived</c> on one).
    /// </summary>
    HostKeyVerdict Verify(HostKeyInfo hostKey);

    /// <summary>Key types already known for the host; the client prefers these during negotiation.</summary>
    IReadOnlyCollection<string> GetKnownKeyTypes(string host, int port);
}

/// <summary>Raised when a connection is refused because the host key was not trusted.</summary>
public sealed class HostKeyVerificationException : Exception
{
    public HostKeyVerificationException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
