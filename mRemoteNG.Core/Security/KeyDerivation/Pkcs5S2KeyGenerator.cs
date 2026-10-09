using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace mRemoteNG.Core.Security.KeyDerivation
{
    /// <summary>
    /// PBKDF2 (HMAC-SHA1) key derivation, byte-compatible with the legacy WinForms implementation.
    /// </summary>
    public class Pkcs5S2KeyGenerator : IKeyDerivationFunction
    {
        public const int MinimumIterations = 1000;

        public byte[] DeriveKey(string password, byte[] salt, int iterations, int keySizeInBits)
        {
            if (iterations < MinimumIterations)
                throw new ArgumentOutOfRangeException(nameof(iterations), $"Minimum value of {nameof(iterations)} is {MinimumIterations}");

            // The legacy app used PKCS#5 password encoding (one byte per char), not UTF-8.
            // Changing this would make every existing connection file undecryptable.
            var passwordBytes = PbeParametersGenerator.Pkcs5PasswordToBytes(password.ToCharArray());

            var generator = new Pkcs5S2ParametersGenerator(new Sha1Digest());
            generator.Init(passwordBytes, salt, iterations);
            return ((KeyParameter)generator.GenerateDerivedMacParameters(keySizeInBits)).GetKey();
        }
    }
}
