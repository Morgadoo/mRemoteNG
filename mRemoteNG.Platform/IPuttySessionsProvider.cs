using System.Collections.Generic;
using System.Threading.Tasks;

namespace mRemoteNG.Platform;

public interface IPuttySessionsProvider
{
    Task<IReadOnlyList<PuttySession>> GetSessionsAsync();
}

/// <summary>A PuTTY saved session.</summary>
/// <param name="Settings">
/// Every value PuTTY stored for the session (registry values on Windows, <c>Key=Value</c> lines in the
/// session file elsewhere), keyed case-insensitively by PuTTY's setting name (HostName, PortNumber,
/// PortForwardings, ProxyMethod, …). Null when the provider only read the summary fields.
/// </param>
public record PuttySession(
    string Name,
    string Hostname,
    int Port,
    string Username,
    string Protocol,
    IReadOnlyDictionary<string, string>? Settings = null);
