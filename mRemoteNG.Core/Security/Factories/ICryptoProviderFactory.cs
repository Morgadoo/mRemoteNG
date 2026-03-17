namespace mRemoteNG.Core.Security.Factories
{
    public interface ICryptoProviderFactory
    {
        ICryptographyProvider Build();
        ICryptographyProvider Build(BlockCipherEngines engine, BlockCipherModes mode);
    }
}
