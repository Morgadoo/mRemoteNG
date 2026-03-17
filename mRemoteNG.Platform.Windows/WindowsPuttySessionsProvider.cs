using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Win32;
using mRemoteNG.Platform;

namespace mRemoteNG.Platform.Windows;

[SupportedOSPlatform("windows")]
public class WindowsPuttySessionsProvider : IPuttySessionsProvider
{
    private const string RegPath = @"Software\SimonTatham\PuTTY\Sessions";

    public Task<IReadOnlyList<PuttySession>> GetSessionsAsync()
    {
        var sessions = new List<PuttySession>();
        using var key = Registry.CurrentUser.OpenSubKey(RegPath);
        if (key != null)
        {
            foreach (var sessionName in key.GetSubKeyNames())
            {
                using var sessionKey = key.OpenSubKey(sessionName);
                if (sessionKey == null) continue;
                var hostname = sessionKey.GetValue("HostName")?.ToString() ?? string.Empty;
                var portStr = sessionKey.GetValue("PortNumber")?.ToString() ?? "22";
                var username = sessionKey.GetValue("UserName")?.ToString() ?? string.Empty;
                var protocol = sessionKey.GetValue("Protocol")?.ToString() ?? "ssh";
                if (int.TryParse(portStr, out var port))
                    sessions.Add(new PuttySession(Uri.UnescapeDataString(sessionName), hostname, port, username, protocol));
            }
        }
        return Task.FromResult<IReadOnlyList<PuttySession>>(sessions);
    }
}
