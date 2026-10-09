namespace mRemoteNG.Core.Credential
{
    /// <summary>
    /// Read access to stored credentials, so a connection can reference a credential by id
    /// instead of embedding a username/password.
    /// </summary>
    public interface ICredentialLookup
    {
        /// <summary>The credential with the given id, or null.</summary>
        ICredentialRecord? GetCredentialRecord(Guid id);

        /// <summary>The first credential whose title matches (case-insensitive), or null.</summary>
        ICredentialRecord? FindByTitle(string title);

        /// <summary>A snapshot of all stored credentials.</summary>
        IReadOnlyList<ICredentialRecord> GetAll();
    }
}
