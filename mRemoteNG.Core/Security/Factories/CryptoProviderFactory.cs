using mRemoteNG.Core.Security.SymmetricEncryption;

namespace mRemoteNG.Core.Security.Factories
{
    public class CryptoProviderFactory : ICryptoProviderFactory
    {
        public ICryptographyProvider Build()
        {
            return new AeadCryptographyProvider();
        }

        public ICryptographyProvider Build(BlockCipherEngines engine, BlockCipherModes mode)
        {
            return new AeadCryptographyProvider(engine, mode);
        }
    }
}
