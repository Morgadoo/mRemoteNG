using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Platform;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Imports PuTTY saved sessions into a folder named "Imported from PuTTY". Sessions come either
    /// from a list (for example from a registry-backed <see cref="IPuttySessionsProvider"/> on Windows)
    /// or from a PuTTY sessions folder given as the import source.
    /// "Default Settings" and sessions without a host name are skipped, as in the legacy importer.
    /// </summary>
    public sealed class PuttySessionsImporter : IConnectionImporter
    {
        public const string FolderName = "Imported from PuTTY";

        private readonly IReadOnlyList<PuttySession>? _sessions;

        /// <param name="sessions">Sessions to import; when null the import source is read as a sessions folder.</param>
        public PuttySessionsImporter(IReadOnlyList<PuttySession>? sessions = null)
        {
            _sessions = sessions;
        }

        /// <param name="source">
        /// A PuTTY sessions folder; ignored when sessions were given to the constructor.
        /// Empty means the current user's default folder.
        /// </param>
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);

            var sessions = _sessions;
            if (sessions is null)
            {
                var directory = string.IsNullOrWhiteSpace(source)
                    ? PuttySessionFilesProvider.FindDefaultSessionsDirectory()
                      ?? throw new DirectoryNotFoundException("No PuTTY sessions folder was found (~/.putty/sessions).")
                    : source;
                sessions = PuttySessionFilesProvider.ReadSessions(directory);
            }

            var warnings = new List<string>();
            var folder = ImportNodeFactory.NewContainer(FolderName);
            foreach (var session in sessions)
            {
                if (session.Name is "Default Settings" or "Default%20Settings")
                    continue;
                if (string.IsNullOrWhiteSpace(session.Hostname))
                {
                    warnings.Add($"Session \"{session.Name}\" skipped: it has no host name.");
                    continue;
                }

                var protocol = MapProtocol(session.Protocol);
                if (protocol is null)
                {
                    warnings.Add($"Session \"{session.Name}\" skipped: unsupported protocol \"{session.Protocol}\".");
                    continue;
                }

                var connection = ImportNodeFactory.NewConnection(protocol.Value);
                connection.Name = session.Name;
                connection.Hostname = session.Hostname;
                connection.Port = session.Port > 0 ? session.Port : connection.Port;
                connection.Username = session.Username ?? "";
                folder.AddChild(connection);
            }

            destinationContainer.AddChild(folder);
            return new ImportResult([folder], warnings);
        }

        private static ProtocolType? MapProtocol(string? puttyProtocol) =>
            (puttyProtocol ?? "ssh").Trim().ToLowerInvariant() switch
            {
                "" or "ssh" => ProtocolType.SSH2,
                "telnet" => ProtocolType.Telnet,
                "rlogin" => ProtocolType.Rlogin,
                "raw" => ProtocolType.RAW,
                _ => null
            };
    }
}
