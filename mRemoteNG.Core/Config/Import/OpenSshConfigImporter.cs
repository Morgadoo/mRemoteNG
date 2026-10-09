using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Imports the concrete Host aliases of an OpenSSH client configuration as SSH2 connections,
    /// in a folder named "SSH config".
    /// </summary>
    public sealed class OpenSshConfigImporter : IConnectionImporter
    {
        public const string FolderName = "SSH config";

        /// <summary><c>~/.ssh/config</c> for the current user.</summary>
        public static string DefaultConfigPath => Path.Combine(OpenSshConfigParser.DefaultSshDirectory, "config");

        /// <param name="source">The config file, or empty for <see cref="DefaultConfigPath"/>.</param>
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            if (string.IsNullOrWhiteSpace(source))
                source = DefaultConfigPath;
            if (!File.Exists(source))
                throw new FileNotFoundException($"The SSH config file does not exist: {source}", source);

            var warnings = new List<string>();
            var entries = OpenSshConfigParser.ParseFile(source, warnings);

            var folder = ConnectionDefaults.NewContainer(FolderName);
            foreach (var entry in entries)
            {
                var connection = ConnectionDefaults.NewConnection(ProtocolType.SSH2);
                connection.Name = entry.Alias;
                connection.Hostname = entry.HostName;
                connection.Port = entry.Port;
                connection.Username = entry.User;
                folder.AddChild(connection);
            }

            if (entries.Count == 0)
                warnings.Add("No concrete Host entries were found (wildcard patterns are not imported).");

            destinationContainer.AddChild(folder);
            return new ImportResult([folder], warnings);
        }
    }
}
