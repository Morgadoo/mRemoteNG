namespace mRemoteNG.Core.Security
{
    public interface ICryptographyProvider
    {
        string Encrypt(string plaintext, string key);
        string Decrypt(string ciphertext, string key);
        int BlockSizeInBytes { get; }
    }
}
