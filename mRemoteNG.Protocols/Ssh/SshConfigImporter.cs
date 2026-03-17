namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// Parses an OpenSSH client config file (<c>~/.ssh/config</c>) and returns
/// a list of <see cref="SshConfigEntry"/> records that can be imported as
/// mRemoteNG connection nodes.
///
/// Supported directives:
///   Host, HostName, Port, User, IdentityFile, ProxyJump, ForwardAgent
/// </summary>
public static class SshConfigImporter
{
    public static async Task<IReadOnlyList<SshConfigEntry>> ImportAsync(
        string? filePath = null,
        CancellationToken ct = default)
    {
        filePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh", "config");

        if (!File.Exists(filePath))
            return [];

        var lines = await File.ReadAllLinesAsync(filePath, ct);
        return Parse(lines);
    }

    private static List<SshConfigEntry> Parse(string[] lines)
    {
        var entries = new List<SshConfigEntry>();
        SshConfigEntry? current = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            int spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0) continue;

            string key = line[..spaceIdx].ToLowerInvariant();
            string value = line[(spaceIdx + 1)..].Trim();

            if (key == "host")
            {
                if (current is not null) entries.Add(current);
                // Skip wildcard hosts
                if (value is "*" || value.Contains('*') || value.Contains('?'))
                {
                    current = null;
                }
                else
                {
                    current = new SshConfigEntry { Alias = value };
                }
                continue;
            }

            if (current is null) continue;

            switch (key)
            {
                case "hostname": current.Hostname = value; break;
                case "port" when int.TryParse(value, out int port): current.Port = port; break;
                case "user": current.Username = value; break;
                case "identityfile": current.IdentityFile = ExpandHome(value); break;
                case "proxyjump": current.ProxyJump = value; break;
                case "forwardagent": current.ForwardAgent = value.Equals("yes", StringComparison.OrdinalIgnoreCase); break;
            }
        }

        if (current is not null) entries.Add(current);

        // Set effective hostname = alias if HostName was not specified
        foreach (var e in entries)
            e.Hostname ??= e.Alias;

        return entries;
    }

    private static string ExpandHome(string path)
    {
        if (path.StartsWith("~/"))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        return path;
    }
}

public sealed class SshConfigEntry
{
    /// <summary>The <c>Host</c> label (alias).</summary>
    public required string Alias { get; set; }

    /// <summary>Resolved hostname or IP (<c>HostName</c>).</summary>
    public string? Hostname { get; set; }

    public int Port { get; set; } = 22;
    public string? Username { get; set; }
    public string? IdentityFile { get; set; }
    public string? ProxyJump { get; set; }
    public bool ForwardAgent { get; set; }
}
