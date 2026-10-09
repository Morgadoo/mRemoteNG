using System.Globalization;
using mRemoteNG.Platform;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Reads PuTTY saved sessions as stored by PuTTY on Linux and macOS: one file per session in
    /// <c>~/.putty/sessions</c> (or <c>$XDG_CONFIG_HOME/putty/sessions</c>), named with the
    /// URL-encoded session name and holding <c>Key=Value</c> lines.
    /// On Windows PuTTY uses the registry instead (see the Windows platform provider).
    /// </summary>
    public sealed class PuttySessionFilesProvider : IPuttySessionsProvider
    {
        private readonly string? _sessionsDirectory;

        /// <param name="sessionsDirectory">The sessions folder, or null for <see cref="FindDefaultSessionsDirectory"/>.</param>
        public PuttySessionFilesProvider(string? sessionsDirectory = null)
        {
            _sessionsDirectory = sessionsDirectory;
        }

        public Task<IReadOnlyList<PuttySession>> GetSessionsAsync()
        {
            var directory = _sessionsDirectory ?? FindDefaultSessionsDirectory();
            return Task.FromResult(directory is null ? (IReadOnlyList<PuttySession>)[] : ReadSessions(directory));
        }

        /// <summary>The first existing PuTTY sessions folder of the current user, or null.</summary>
        public static string? FindDefaultSessionsDirectory() =>
            CandidateDirectories().FirstOrDefault(Directory.Exists);

        public static IEnumerable<string> CandidateDirectories()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(xdgConfig))
                xdgConfig = Path.Combine(home, ".config");

            yield return Path.Combine(home, ".putty", "sessions");
            yield return Path.Combine(xdgConfig, "putty", "sessions");
        }

        /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
        public static IReadOnlyList<PuttySession> ReadSessions(string sessionsDirectory)
        {
            if (!Directory.Exists(sessionsDirectory))
                throw new DirectoryNotFoundException($"PuTTY sessions folder not found: {sessionsDirectory}");

            var sessions = new List<PuttySession>();
            foreach (var file in Directory.EnumerateFiles(sessionsDirectory).Order(StringComparer.Ordinal))
            {
                var values = ReadValues(file);
                var name = Uri.UnescapeDataString(Path.GetFileName(file));
                var port = int.TryParse(values.GetValueOrDefault("PortNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
                sessions.Add(new PuttySession(
                    name,
                    values.GetValueOrDefault("HostName") ?? "",
                    port,
                    values.GetValueOrDefault("UserName") ?? "",
                    values.GetValueOrDefault("Protocol") ?? "ssh",
                    values));
            }
            return sessions;
        }

        /// <summary>Reads the <c>Key=Value</c> lines of one PuTTY session file.</summary>
        public static Dictionary<string, string> ReadValues(string file)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(file))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }
            return values;
        }
    }
}
