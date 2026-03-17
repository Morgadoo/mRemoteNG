using System.Collections.Generic;
using System.Threading.Tasks;

namespace mRemoteNG.Platform;

public interface IPuttySessionsProvider
{
    Task<IReadOnlyList<PuttySession>> GetSessionsAsync();
}

public record PuttySession(string Name, string Hostname, int Port, string Username, string Protocol);
