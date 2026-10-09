namespace mRemoteNG.Core.Security.PasswordCreation
{
    public interface IPasswordConstraint
    {
        string Description { get; }
        bool Validate(string password);
    }
}
