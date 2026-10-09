using mRemoteNG.Core.Config.Import;
using mRemoteNG.Platform;

namespace mRemoteNG.Core.Config.Putty
{
    /// <summary>
    /// The current user's PuTTY saved sessions, read on demand from the platform provider (the registry
    /// on Windows) or, when no provider is registered, from the PuTTY sessions folder (~/.putty/sessions).
    /// </summary>
    public sealed class PuttySessionCatalog
    {
        private readonly IPuttySessionsProvider _provider;

        public PuttySessionCatalog(IPuttySessionsProvider? provider = null)
        {
            _provider = provider ?? new PuttySessionFilesProvider();
        }

        public async Task<IReadOnlyList<PuttySession>> GetSessionsAsync()
        {
            try
            {
                return await _provider.GetSessionsAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        /// <summary>The settings of the session named <paramref name="name"/>, or null when there is none.</summary>
        public async Task<PuttySessionSettings?> FindAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var sessions = await GetSessionsAsync();
            var session = sessions.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
                          ?? sessions.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            return session is null ? null : PuttySessionSettings.FromSession(session);
        }
    }
}
