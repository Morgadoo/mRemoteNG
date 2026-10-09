using System.Security.Cryptography;
using System.Text;

namespace mRemoteNG.Core.Security.SymmetricEncryption
{
    /// <summary>
    /// Read-compatibility provider for connection files older than version 2.6
    /// (AES-128-CBC, key = MD5(password), IV prepended to the ciphertext).
    /// </summary>
    /// <remarks>
    /// This scheme is weak and unauthenticated. It is only used to read old files;
    /// files are always written back using <see cref="AeadCryptographyProvider"/>.
    /// </remarks>
    public class LegacyRijndaelCryptographyProvider : ICryptographyProvider
    {
        public int BlockSizeInBytes => 16;

        public BlockCipherEngines CipherEngine => BlockCipherEngines.AES;

        // Not an AEAD mode; reported as the closest value so callers have something to display.
        public BlockCipherModes CipherMode => BlockCipherModes.GCM;

        public int KeyDerivationIterations { get; set; }

        public string Encrypt(string plaintext, string key)
        {
            if (string.IsNullOrWhiteSpace(plaintext) || string.IsNullOrEmpty(key))
                return plaintext;

            using var aes = Aes.Create();
            aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(key));
            aes.GenerateIV();

            var encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), aes.IV);
            var result = new byte[aes.IV.Length + encrypted.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);
            return Convert.ToBase64String(result);
        }

        public string Decrypt(string ciphertext, string key)
        {
            if (string.IsNullOrEmpty(ciphertext) || string.IsNullOrEmpty(key))
                return ciphertext;

            try
            {
                var allBytes = Convert.FromBase64String(ciphertext);
                using var aes = Aes.Create();
                aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(key));

                var iv = allBytes.AsSpan(0, BlockSizeInBytes);
                var plain = aes.DecryptCbc(allBytes.AsSpan(BlockSizeInBytes), iv);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                throw new EncryptionException("Decryption failed. The password may be wrong or the data corrupt.", ex);
            }
        }
    }
}
