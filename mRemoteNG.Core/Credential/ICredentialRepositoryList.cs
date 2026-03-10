namespace mRemoteNG.Core.Credential
{
    public interface ICredentialRepositoryList
    {
        IEnumerable<ICredentialRepository> CredentialProviders { get; }
        void AddProvider(ICredentialRepository provider);
        void RemoveProvider(ICredentialRepository provider);
        ICredentialRecord? GetCredentialRecord(Guid id);
    }
}
