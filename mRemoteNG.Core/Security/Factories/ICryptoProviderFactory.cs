namespace mRemoteNG.Core.Security.Factories
{
    public interface ICryptoProviderFactory
    {
        /// <summary>Builds the default provider used for new files (AES-GCM, 1000 KDF iterations).</summary>
        ICryptographyProvider Build();

        ICryptographyProvider Build(BlockCipherEngines engine, BlockCipherModes mode, int keyDerivationIterations = 1000);

        /// <summary>Builds the provider for reading connection files older than version 2.6.</summary>
        ICryptographyProvider BuildLegacy();
    }
}
