using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using mRemoteNG.Platform;

namespace mRemoteNG.Platform.Linux;

public class FilePuttySessionsProvider : IPuttySessionsProvider
{
    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public Task<IReadOnlyList<PuttySession>> GetSessionsAsync()
    {
        var sessions = new List<PuttySession>();
        if (!File.Exists(_configPath)) return Task.FromResult<IReadOnlyList<PuttySession>>(sessions);

        string? currentHost = null;
        string hostname = string.Empty, username = string.Empty;
        int port = 22;

        foreach (var rawLine in File.ReadAllLines(_configPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var key = parts[0]; var value = parts[1].Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (currentHost != null && !currentHost.Contains('*'))
                    sessions.Add(new PuttySession(currentHost, hostname.Length > 0 ? hostname : currentHost, port, username, "ssh"));
                currentHost = value; hostname = string.Empty; username = string.Empty; port = 22;
            }
            else if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase)) hostname = value;
            else if (key.Equals("User", StringComparison.OrdinalIgnoreCase)) username = value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var p)) port = p;
        }
        if (currentHost != null && !currentHost.Contains('*'))
            sessions.Add(new PuttySession(currentHost, hostname.Length > 0 ? hostname : currentHost, port, username, "ssh"));

        return Task.FromResult<IReadOnlyList<PuttySession>>(sessions);
    }
}
