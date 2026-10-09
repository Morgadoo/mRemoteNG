using System.DirectoryServices.Protocols;
using mRemoteNG.Core.Config.Import.ActiveDirectory;
using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>Lists the computers below a directory node (implemented by <see cref="ActiveDirectoryBrowser"/>).</summary>
    public interface IDirectoryComputerSource : IDisposable
    {
        IReadOnlyList<DirectoryComputer> GetComputers(string dn, bool includeSubtree);
    }

    /// <summary>
    /// Imports Active Directory computer accounts as RDP connections (legacy Import ▸ From Active Directory).
    /// <para>
    /// Like the legacy importer, the computers go into a folder named after the chosen OU, nested OUs become
    /// sub-folders (when sub-OUs are included and <see cref="ActiveDirectoryImportRequest.CreateOuFolders"/> is set),
    /// each connection is named after the computer, connects to its DNS host name, keeps its description and
    /// inherits everything else from its folder.
    /// </para>
    /// The import source is an LDAP URL (<see cref="ActiveDirectoryImportRequest.ToUrl"/>), so the import also runs
    /// through <see cref="ConnectionImportService"/>; the bind password is passed separately.
    /// </summary>
    public sealed class ActiveDirectoryImporter : IConnectionImporter
    {
        /// <summary>Folder name when importing from the domain root rather than an OU (legacy text).</summary>
        public const string DomainFolderName = "Active Directory";

        private readonly ActiveDirectoryImportRequest? _request;
        private readonly string? _password;
        private readonly Func<LdapServerSettings, IDirectoryComputerSource> _sourceFactory;

        /// <param name="request">What to import; null reads it from the import source URL.</param>
        /// <param name="password">Bind password used with a source URL (URLs never carry passwords).</param>
        /// <param name="sourceFactory">Creates the directory connection; the default uses LDAP.</param>
        public ActiveDirectoryImporter(
            ActiveDirectoryImportRequest? request = null,
            string? password = null,
            Func<LdapServerSettings, IDirectoryComputerSource>? sourceFactory = null)
        {
            _request = request;
            _password = password;
            _sourceFactory = sourceFactory ?? (settings => new ActiveDirectoryBrowser(settings));
        }

        /// <exception cref="ConnectionFilePasswordException">A simple bind needs a (different) password.</exception>
        /// <exception cref="IOException">The directory could not be read.</exception>
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var request = _request ?? ParseSource(source);
            if (string.IsNullOrWhiteSpace(request.BaseDn))
                throw new InvalidDataException("Choose the organizational unit or domain to import from.");

            var server = request.Server;
            if (server.BindMode == LdapBindMode.Simple && !string.IsNullOrEmpty(server.Username) && server.Password is null)
                throw new ConnectionFilePasswordException(passwordWasSupplied: false);

            IReadOnlyList<DirectoryComputer> computers;
            try
            {
                using var directory = _sourceFactory(server);
                computers = directory.GetComputers(request.BaseDn, request.IncludeSubOus);
            }
            catch (LdapException ex) when (ex.ErrorCode == 49)
            {
                // LDAP_INVALID_CREDENTIALS
                throw new ConnectionFilePasswordException(passwordWasSupplied: server.Password is not null);
            }
            catch (LdapException ex)
            {
                throw new IOException($"Active Directory query failed: {ex.Message}", ex);
            }
            catch (DirectoryOperationException ex)
            {
                throw new IOException($"Active Directory query failed: {ex.Message}", ex);
            }

            var warnings = new List<string>();
            if (request.SelectedComputers.Count > 0)
            {
                var wanted = new HashSet<string>(request.SelectedComputers.Select(NormalizeDn), StringComparer.OrdinalIgnoreCase);
                computers = computers.Where(c => wanted.Remove(NormalizeDn(c.DistinguishedName))).ToList();
                foreach (var missing in wanted)
                    warnings.Add($"Computer \"{missing}\" was not found below {request.BaseDn}.");
            }

            var baseRdns = ActiveDirectoryBrowser.SplitDn(request.BaseDn);
            var folder = ImportNodeFactory.NewContainer(FolderName(request.BaseDn));
            var subFolders = new Dictionary<string, ContainerInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var computer in computers)
            {
                var parent = folder;
                if (request.IncludeSubOus && request.CreateOuFolders)
                    parent = FolderFor(computer, baseRdns, folder, subFolders);
                parent.AddChild(CreateConnection(computer));
            }

            destinationContainer.AddChild(folder);
            return new ImportResult([folder], warnings);
        }

        private ActiveDirectoryImportRequest ParseSource(string source)
        {
            try
            {
                return ActiveDirectoryImportRequest.Parse(source, _password);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(ex.Message, ex);
            }
        }

        /// <summary>The folder for a computer: one per OU between the base and the computer, created on demand.</summary>
        private static ContainerInfo FolderFor(DirectoryComputer computer, IReadOnlyList<string> baseRdns,
            ContainerInfo root, Dictionary<string, ContainerInfo> folders)
        {
            var rdns = ActiveDirectoryBrowser.SplitDn(computer.DistinguishedName);
            // RDNs between the computer's own (index 0) and the base, outermost first.
            var depth = rdns.Count - baseRdns.Count;
            var parent = root;
            var key = "";
            for (var i = depth - 1; i >= 1; i--)
            {
                key = rdns[i] + "," + key;
                if (!folders.TryGetValue(key, out var folder))
                {
                    folder = ImportNodeFactory.NewContainer(ActiveDirectoryBrowser.RdnValue(rdns[i]));
                    parent.AddChild(folder);
                    folders[key] = folder;
                }
                parent = folder;
            }
            return parent;
        }

        private static ConnectionInfo CreateConnection(DirectoryComputer computer)
        {
            var connection = ImportNodeFactory.NewConnection(ProtocolType.RDP);
            connection.Name = computer.Name;
            connection.Hostname = computer.HostName;
            connection.Description = computer.Description ?? "";
            // As the legacy importer: everything but the description comes from the folder.
            connection.Inheritance.TurnOnInheritanceCompletely();
            connection.Inheritance.Description = false;
            return connection;
        }

        /// <summary>The OU name of the base (OU=Servers,DC=… → Servers), or "Active Directory" for a domain root.</summary>
        public static string FolderName(string baseDn)
        {
            var first = ActiveDirectoryBrowser.SplitDn(baseDn).FirstOrDefault() ?? "";
            return first.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)
                ? ActiveDirectoryBrowser.RdnValue(first)
                : DomainFolderName;
        }

        private static string NormalizeDn(string dn) => string.Join(",", ActiveDirectoryBrowser.SplitDn(dn));
    }
}
