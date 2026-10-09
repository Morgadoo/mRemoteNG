using System.Collections.ObjectModel;

namespace mRemoteNG.Core.Credential
{
    public interface ICredentialRepository
    {
        string Title { get; set; }
        bool IsLoaded { get; }
        ReadOnlyObservableCollection<ICredentialRecord> CredentialRecords { get; }
        void LoadCredentials();
        void SaveCredentials();
        void AddCredential(ICredentialRecord credential);
        void RemoveCredential(ICredentialRecord credential);
    }
}
