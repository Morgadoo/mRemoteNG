using mRemoteNG.Core.Security.SymmetricEncryption;

namespace mRemoteNG.Core.Security.Factories
{
    public class CryptoProviderFactory : ICryptoProviderFactory
    {
        public ICryptographyProvider Build()
        {
            return new AeadCryptographyProvider();
        }

        public ICryptographyProvider Build(BlockCipherEngines engine, BlockCipherModes mode, int keyDerivationIterations = 1000)
        {
            return new AeadCryptographyProvider(engine, mode, keyDerivationIterations);
        }

        public ICryptographyProvider BuildLegacy()
        {
            return new LegacyRijndaelCryptographyProvider();
        }
    }
}
