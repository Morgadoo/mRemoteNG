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
                var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var valueName in sessionKey.GetValueNames())
                {
                    if (sessionKey.GetValue(valueName) is { } value)
                        settings[valueName] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }

                var hostname = settings.GetValueOrDefault("HostName") ?? string.Empty;
                var portStr = settings.GetValueOrDefault("PortNumber") ?? "22";
                var username = settings.GetValueOrDefault("UserName") ?? string.Empty;
                var protocol = settings.GetValueOrDefault("Protocol") ?? "ssh";
                if (int.TryParse(portStr, out var port))
                    sessions.Add(new PuttySession(Uri.UnescapeDataString(sessionName), hostname, port, username, protocol, settings));
            }
        }
        return Task.FromResult<IReadOnlyList<PuttySession>>(sessions);
    }
}
