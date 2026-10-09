namespace mRemoteNG.Core.Credential
{
    public interface ICredentialRecord
    {
        Guid Id { get; }
        string Title { get; set; }
        string Username { get; set; }
        string Password { get; set; }
        string Domain { get; set; }
    }
}
