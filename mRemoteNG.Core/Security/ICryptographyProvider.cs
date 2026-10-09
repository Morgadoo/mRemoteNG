namespace mRemoteNG.Core.Security
{
    public interface ICryptographyProvider
    {
        string Encrypt(string plaintext, string key);

        /// <summary>
        /// Decrypts <paramref name="ciphertext"/>.
        /// </summary>
        /// <exception cref="EncryptionException">The key is wrong or the ciphertext is corrupt.</exception>
        string Decrypt(string ciphertext, string key);

        int BlockSizeInBytes { get; }

        BlockCipherEngines CipherEngine { get; }

        BlockCipherModes CipherMode { get; }

        int KeyDerivationIterations { get; set; }
    }
}
