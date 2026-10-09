using System.Text;
using mRemoteNG.Core.Security.KeyDerivation;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace mRemoteNG.Core.Security.SymmetricEncryption
{
    /// <summary>
    /// Password-based authenticated encryption used by connection files version 2.6 and later.
    /// </summary>
    /// <remarks>
    /// The output is byte-compatible with the legacy WinForms <c>AeadCryptographyProvider</c>:
    /// <c>Base64(salt[16] || nonce || ciphertext+tag[16])</c>, where the salt is also the AEAD
    /// associated data and the key is PBKDF2-HMAC-SHA1(password, salt).
    /// </remarks>
    public class AeadCryptographyProvider : ICryptographyProvider
    {
        private const int SaltBitSize = 128;
        private const int MacBitSize = 128;
        private const int KeyBitSize = 256;

        private readonly IKeyDerivationFunction _keyDerivationFunction = new Pkcs5S2KeyGenerator();
        private readonly SecureRandom _random = new();
        private int _keyDerivationIterations;

        public BlockCipherEngines CipherEngine { get; }

        public BlockCipherModes CipherMode { get; }

        public int KeyDerivationIterations
        {
            get => _keyDerivationIterations;
            set
            {
                if (value < Pkcs5S2KeyGenerator.MinimumIterations)
                    throw new ArgumentOutOfRangeException(nameof(value), $"Minimum value is {Pkcs5S2KeyGenerator.MinimumIterations}");
                _keyDerivationIterations = value;
            }
        }

        public int BlockSizeInBytes => CreateCipher().GetBlockSize();

        // CCM only allows nonces of 7..13 bytes; the legacy app used 88 bits for it.
        private int NonceBitSize => CipherMode == BlockCipherModes.CCM ? 88 : 128;

        public AeadCryptographyProvider(
            BlockCipherEngines engine = BlockCipherEngines.AES,
            BlockCipherModes mode = BlockCipherModes.GCM,
            int keyDerivationIterations = 1000)
        {
            CipherEngine = engine;
            CipherMode = mode;
            KeyDerivationIterations = keyDerivationIterations;
        }

        public string Encrypt(string plaintext, string key)
        {
            if (string.IsNullOrEmpty(plaintext)) return string.Empty;
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("An encryption key is required.", nameof(key));

            var salt = new byte[SaltBitSize / 8];
            _random.NextBytes(salt);
            var nonce = new byte[NonceBitSize / 8];
            _random.NextBytes(nonce);

            var keyBytes = _keyDerivationFunction.DeriveKey(key, salt, KeyDerivationIterations, KeyBitSize);
            var cipher = CreateCipher();
            cipher.Init(true, new AeadParameters(new KeyParameter(keyBytes), MacBitSize, nonce, salt));

            var input = Encoding.UTF8.GetBytes(plaintext);
            var output = new byte[cipher.GetOutputSize(input.Length)];
            var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            cipher.DoFinal(output, len);

            var result = new byte[salt.Length + nonce.Length + output.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(nonce, 0, result, salt.Length, nonce.Length);
            Buffer.BlockCopy(output, 0, result, salt.Length + nonce.Length, output.Length);
            return Convert.ToBase64String(result);
        }

        public string Decrypt(string ciphertext, string key)
        {
            if (string.IsNullOrWhiteSpace(ciphertext)) return string.Empty;
            if (string.IsNullOrEmpty(key))
                throw new EncryptionException("A decryption key is required.");

            try
            {
                var allBytes = Convert.FromBase64String(ciphertext);
                var saltLength = SaltBitSize / 8;
                var nonceLength = NonceBitSize / 8;
                if (allBytes.Length < saltLength + nonceLength + MacBitSize / 8)
                    throw new EncryptionException("Ciphertext is too short.");

                var salt = allBytes.AsSpan(0, saltLength).ToArray();
                var nonce = allBytes.AsSpan(saltLength, nonceLength).ToArray();
                var encrypted = allBytes.AsSpan(saltLength + nonceLength).ToArray();

                var keyBytes = _keyDerivationFunction.DeriveKey(key, salt, KeyDerivationIterations, KeyBitSize);
                var cipher = CreateCipher();
                cipher.Init(false, new AeadParameters(new KeyParameter(keyBytes), MacBitSize, nonce, salt));

                var output = new byte[cipher.GetOutputSize(encrypted.Length)];
                var len = cipher.ProcessBytes(encrypted, 0, encrypted.Length, output, 0);
                len += cipher.DoFinal(output, len);
                return Encoding.UTF8.GetString(output, 0, len);
            }
            catch (Exception ex) when (ex is InvalidCipherTextException or FormatException or ArgumentException)
            {
                throw new EncryptionException("Decryption failed. The password may be wrong or the data corrupt.", ex);
            }
        }

        private IAeadBlockCipher CreateCipher()
        {
            IBlockCipher engine = CipherEngine switch
            {
                BlockCipherEngines.AES => new AesEngine(),
                BlockCipherEngines.Twofish => new TwofishEngine(),
                BlockCipherEngines.Serpent => new SerpentEngine(),
                _ => throw new NotSupportedException($"Cipher engine {CipherEngine} is not supported.")
            };

            return CipherMode switch
            {
                BlockCipherModes.GCM => new GcmBlockCipher(engine),
                BlockCipherModes.CCM => new CcmBlockCipher(engine),
                BlockCipherModes.EAX => new EaxBlockCipher(engine),
                _ => throw new NotSupportedException($"Cipher mode {CipherMode} is not supported.")
            };
        }
    }
}
