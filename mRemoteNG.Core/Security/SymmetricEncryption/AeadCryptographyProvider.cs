using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace mRemoteNG.Core.Security.SymmetricEncryption
{
    public class AeadCryptographyProvider : ICryptographyProvider
    {
        private readonly BlockCipherEngines _blockCipherEngine;
        private readonly BlockCipherModes _blockCipherMode;
        private readonly int _keyDerivationIterations;

        public int BlockSizeInBytes => 16;

        public AeadCryptographyProvider(
            BlockCipherEngines engine = BlockCipherEngines.AES,
            BlockCipherModes mode = BlockCipherModes.GCM,
            int keyDerivationIterations = 1000)
        {
            _blockCipherEngine = engine;
            _blockCipherMode = mode;
            _keyDerivationIterations = keyDerivationIterations;
        }

        public string Encrypt(string plaintext, string key)
        {
            if (string.IsNullOrEmpty(plaintext)) return string.Empty;

            var engine = CreateEngine();
            var random = new SecureRandom();
            var nonce = new byte[engine.GetBlockSize()];
            random.NextBytes(nonce);

            var keyBytes = DeriveKey(key, nonce);
            var keyParam = new AeadParameters(new KeyParameter(keyBytes), 128, nonce);

            engine.Init(true, keyParam);
            var inputBytes = Encoding.UTF8.GetBytes(plaintext);
            var outputBytes = new byte[engine.GetOutputSize(inputBytes.Length)];
            var len = engine.ProcessBytes(inputBytes, 0, inputBytes.Length, outputBytes, 0);
            engine.DoFinal(outputBytes, len);

            var result = new byte[nonce.Length + outputBytes.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
            Buffer.BlockCopy(outputBytes, 0, result, nonce.Length, outputBytes.Length);

            return Convert.ToBase64String(result);
        }

        public string Decrypt(string ciphertext, string key)
        {
            if (string.IsNullOrEmpty(ciphertext)) return string.Empty;

            var allBytes = Convert.FromBase64String(ciphertext);
            var engine = CreateEngine();
            var blockSize = engine.GetBlockSize();

            var nonce = new byte[blockSize];
            var encrypted = new byte[allBytes.Length - blockSize];
            Buffer.BlockCopy(allBytes, 0, nonce, 0, blockSize);
            Buffer.BlockCopy(allBytes, blockSize, encrypted, 0, encrypted.Length);

            var keyBytes = DeriveKey(key, nonce);
            var keyParam = new AeadParameters(new KeyParameter(keyBytes), 128, nonce);

            engine.Init(false, keyParam);
            var outputBytes = new byte[engine.GetOutputSize(encrypted.Length)];
            var len = engine.ProcessBytes(encrypted, 0, encrypted.Length, outputBytes, 0);
            len += engine.DoFinal(outputBytes, len);

            return Encoding.UTF8.GetString(outputBytes, 0, len);
        }

        private IAeadBlockCipher CreateEngine()
        {
            IBlockCipher baseEngine = _blockCipherEngine switch
            {
                BlockCipherEngines.AES => new AesEngine(),
                BlockCipherEngines.Twofish => new TwofishEngine(),
                BlockCipherEngines.Serpent => new SerpentEngine(),
                _ => new AesEngine()
            };

            return _blockCipherMode switch
            {
                BlockCipherModes.GCM => new GcmBlockCipher(baseEngine),
                BlockCipherModes.CCM => new CcmBlockCipher(baseEngine),
                BlockCipherModes.EAX => new EaxBlockCipher(baseEngine),
                _ => new GcmBlockCipher(baseEngine)
            };
        }

        private byte[] DeriveKey(string password, byte[] salt)
        {
            var generator = new Org.BouncyCastle.Crypto.Generators.Pkcs5S2ParametersGenerator(
                new Org.BouncyCastle.Crypto.Digests.Sha1Digest());
            generator.Init(Encoding.UTF8.GetBytes(password), salt, _keyDerivationIterations);
            return ((KeyParameter)generator.GenerateDerivedMacParameters(256)).GetKey();
        }
    }
}
