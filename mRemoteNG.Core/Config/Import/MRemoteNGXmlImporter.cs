using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Imports another mRemoteNG connection file (any version the loader supports) into a folder
    /// named after the file.
    /// </summary>
    public sealed class MRemoteNGXmlImporter : IConnectionImporter
    {
        private readonly ICryptoProviderFactory _cryptoProviderFactory;
        private readonly string? _password;

        /// <param name="password">The file's master password, or null to try the default key.</param>
        public MRemoteNGXmlImporter(ICryptoProviderFactory cryptoProviderFactory, string? password = null)
        {
            _cryptoProviderFactory = cryptoProviderFactory ?? throw new ArgumentNullException(nameof(cryptoProviderFactory));
            _password = password;
        }

        /// <exception cref="ConnectionFilePasswordException">
        /// The file is protected by a master password and none, or the wrong one, was given.
        /// Ask the user and import again with the password.
        /// </exception>
        /// <exception cref="ConnectionFileVersionException">The file was written by a newer version.</exception>
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var xml = ImportHelpers.ReadFile(source);
            var model = new XmlConnectionsDeserializer(_cryptoProviderFactory, _password).Deserialize(xml);
            return ImportHelpers.AddAsFolder(source, model.RootNode.Children, destinationContainer);
        }
    }
}
