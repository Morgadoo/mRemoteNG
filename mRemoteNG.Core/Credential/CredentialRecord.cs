namespace mRemoteNG.Core.Credential
{
    public class CredentialRecord : ICredentialRecord
    {
        public Guid Id { get; }
        public string Title { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Domain { get; set; } = "";

        public CredentialRecord(Guid? id = null)
        {
            Id = id ?? Guid.NewGuid();
        }
    }
}
