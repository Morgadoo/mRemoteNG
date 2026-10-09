using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Platform;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Imports connections from any supported source into a folder of the connection tree.
    /// Nodes are first imported into a detached staging folder, given new IDs where theirs already
    /// exist in the target tree, and then moved into the destination in one step, so a failed import
    /// leaves the tree untouched.
    /// </summary>
    public sealed class ConnectionImportService
    {
        private readonly ICryptoProviderFactory _cryptoProviderFactory;

        public ConnectionImportService(ICryptoProviderFactory cryptoProviderFactory)
        {
            _cryptoProviderFactory = cryptoProviderFactory ?? throw new ArgumentNullException(nameof(cryptoProviderFactory));
        }

        /// <param name="source">The file (or PuTTY sessions folder) to import; may be empty for default locations.</param>
        /// <param name="password">Master password for an mRemoteNG XML file.</param>
        /// <exception cref="Serializers.Xml.ConnectionFilePasswordException">An XML file needs a (different) password.</exception>
        public ImportResult Import(ImportSourceType type, string source, ContainerInfo destination, string? password = null) =>
            Import(CreateImporter(type, password), source, destination);

        /// <summary>Imports already-loaded PuTTY sessions (for example from the Windows registry provider).</summary>
        public ImportResult ImportPuttySessions(IReadOnlyList<PuttySession> sessions, ContainerInfo destination) =>
            Import(new PuttySessionsImporter(sessions), "", destination);

        public ImportResult Import(IConnectionImporter importer, string source, ContainerInfo destination)
        {
            ArgumentNullException.ThrowIfNull(importer);
            ArgumentNullException.ThrowIfNull(destination);

            var staging = new ContainerInfo();
            var stagingResult = importer.Import(source, staging);

            var usedIds = ConnectionIdRemapper.CollectIds(destination.GetRootParent());
            var reassigned = ConnectionIdRemapper.EnsureUniqueIds(staging, usedIds);

            var nodes = staging.Children.ToArray();
            destination.AddChildRange(nodes);
            return new ImportResult(nodes, stagingResult.Warnings, reassigned);
        }

        public IConnectionImporter CreateImporter(ImportSourceType type, string? password = null) => type switch
        {
            ImportSourceType.MRemoteNGXml => new MRemoteNGXmlImporter(_cryptoProviderFactory, password),
            ImportSourceType.MRemoteNGCsv => new MRemoteNGCsvImporter(),
            ImportSourceType.PuttySessions => new PuttySessionsImporter(),
            ImportSourceType.OpenSshConfig => new OpenSshConfigImporter(),
            ImportSourceType.RemoteDesktopConnectionManager => new RemoteDesktopConnectionManagerImporter(),
            ImportSourceType.RemoteDesktopConnectionFile => new RemoteDesktopConnectionImporter(),
            ImportSourceType.RemoteDesktopManager => new RemoteDesktopManagerImporter(),
            ImportSourceType.SecureCrt => new SecureCrtImporter(),
            // The source is an LDAP URL (ActiveDirectoryImportRequest.ToUrl); the password is the bind password.
            ImportSourceType.ActiveDirectory => new ActiveDirectoryImporter(password: password),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

        /// <summary>The well-known location of a source for the current user, if it exists.</summary>
        public static string? GetDefaultSource(ImportSourceType type) => type switch
        {
            ImportSourceType.OpenSshConfig => File.Exists(OpenSshConfigImporter.DefaultConfigPath)
                ? OpenSshConfigImporter.DefaultConfigPath
                : null,
            ImportSourceType.PuttySessions => PuttySessionFilesProvider.FindDefaultSessionsDirectory(),
            _ => null
        };
    }
}
