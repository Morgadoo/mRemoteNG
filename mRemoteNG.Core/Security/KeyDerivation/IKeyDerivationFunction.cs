namespace mRemoteNG.Core.Security.KeyDerivation
{
    public interface IKeyDerivationFunction
    {
        byte[] DeriveKey(string password, byte[] salt, int iterations, int keySizeInBits);
    }
}
