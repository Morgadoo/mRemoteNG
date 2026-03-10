using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace mRemoteNG.Core.Security.KeyDerivation
{
    public class Pkcs5S2KeyGenerator : IKeyDerivationFunction
    {
        public byte[] DeriveKey(string password, byte[] salt, int iterations, int keySizeInBits)
        {
            var generator = new Pkcs5S2ParametersGenerator(new Sha1Digest());
            generator.Init(Encoding.UTF8.GetBytes(password), salt, iterations);
            return ((KeyParameter)generator.GenerateDerivedMacParameters(keySizeInBits)).GetKey();
        }
    }
}
