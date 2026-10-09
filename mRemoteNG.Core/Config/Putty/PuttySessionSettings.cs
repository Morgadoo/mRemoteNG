using System.Globalization;
using System.Text;
using mRemoteNG.Platform;

namespace mRemoteNG.Core.Config.Putty
{
    /// <summary>PuTTY's <c>ProxyMethod</c> setting.</summary>
    public enum PuttyProxyMethod
    {
        None = 0,
        Socks4 = 1,
        Socks5 = 2,
        Http = 3,
        Telnet = 4,
        LocalCommand = 5,
        SshTcpip = 6,
        SshExec = 7,
        SshSubsystem = 8,
    }

    /// <summary>Proxy configured in a PuTTY session.</summary>
    public sealed record PuttyProxySettings(
        PuttyProxyMethod Method,
        string Host,
        int Port,
        string Username,
        string Password,
        string ExcludeList,
        bool ProxyLocalhost)
    {
        /// <summary>
        /// Whether PuTTY would use the proxy for <paramref name="targetHost"/>: never for loopback
        /// addresses unless "Consider proxying local host connections" is set, and never for hosts in the
        /// exclusion list (comma/space separated names, "*.example.com" wildcards or address prefixes ending in "*").
        /// </summary>
        public bool AppliesTo(string targetHost)
        {
            if (Method == PuttyProxyMethod.None || string.IsNullOrWhiteSpace(Host))
                return false;
            if (!ProxyLocalhost && IsLocalhost(targetHost))
                return false;

            foreach (var pattern in ExcludeList.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (pattern.StartsWith('*') && targetHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                    return false;
                if (pattern.EndsWith('*') && targetHost.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                    return false;
                if (string.Equals(pattern, targetHost, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static bool IsLocalhost(string host) =>
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "::1"
            || host.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Typed view of the settings of a PuTTY saved session that matter to mRemoteNG: the address and
    /// user, the private key, compression, port forwardings, the proxy, and the agent/X11 forwarding flags.
    /// </summary>
    public sealed class PuttySessionSettings
    {
        /// <summary>PuTTY's built-in default session, which is not a connection target by itself.</summary>
        public const string DefaultSessionName = "Default Settings";

        private readonly IReadOnlyDictionary<string, string> _values;

        public PuttySessionSettings(string name, IReadOnlyDictionary<string, string> values)
        {
            Name = name;
            _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public string Name { get; }

        /// <summary>Every stored value, for settings this class does not expose.</summary>
        public IReadOnlyDictionary<string, string> Values => _values;

        public string HostName => GetString("HostName");

        /// <summary>The stored port, or 0 when none is stored.</summary>
        public int PortNumber => GetInt("PortNumber");

        public string UserName => GetString("UserName");

        /// <summary>"ssh", "telnet", "rlogin", "raw", "serial"…; "ssh" when not stored.</summary>
        public string Protocol => GetString("Protocol") is { Length: > 0 } protocol ? protocol : "ssh";

        /// <summary>Private key file (PuTTY's "PublicKeyFile" setting, which names the .ppk file).</summary>
        public string PublicKeyFile => GetString("PublicKeyFile");

        public bool Compression => GetInt("Compression") != 0;

        public bool AgentForwarding => GetInt("AgentFwd") != 0;

        public bool X11Forwarding => GetInt("X11Forward") != 0;

        /// <summary>"Don't start a shell or command at all".</summary>
        public bool NoShell => GetInt("SshNoShell") != 0;

        /// <summary>"Local ports accept connections from other hosts".</summary>
        public bool LocalPortAcceptAll => GetInt("LocalPortAcceptAll") != 0;

        /// <summary>"Remote ports do the same".</summary>
        public bool RemotePortAcceptAll => GetInt("RemotePortAcceptAll") != 0;

        /// <summary>The session's "Remote command".</summary>
        public string RemoteCommand => GetString("RemoteCommand");

        public IReadOnlyList<PortForwardSpec> PortForwardings =>
            ParsePortForwardings(GetString("PortForwardings"), LocalPortAcceptAll, RemotePortAcceptAll);

        public PuttyProxySettings Proxy => new(
            (PuttyProxyMethod)GetInt("ProxyMethod"),
            GetString("ProxyHost"),
            GetInt("ProxyPort") is > 0 and var port ? port : 80,
            GetString("ProxyUsername"),
            GetString("ProxyPassword"),
            GetString("ProxyExcludeList"),
            GetString("ProxyLocalhost") is "1");

        public static PuttySessionSettings FromSession(PuttySession session) =>
            new(session.Name, session.Settings ?? SummaryValues(session));

        public static bool IsDefaultSession(string? name) =>
            string.IsNullOrWhiteSpace(name)
            || name.Equals(DefaultSessionName, StringComparison.OrdinalIgnoreCase)
            || name.Equals("Default%20Settings", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parses PuTTY's stored <c>PortForwardings</c> value: comma-separated <c>key=value</c> entries,
        /// where the key is an optional address family ("4"/"6"), the type ("L", "R" or "D") and
        /// <c>[address:]port</c>, and the value is <c>host:port</c> (empty for dynamic forwardings).
        /// Backslash escapes ',', '=' and '\'. Malformed entries are skipped.
        /// </summary>
        /// <param name="localAcceptAll">Local/dynamic forwardings without an address listen on all interfaces.</param>
        /// <param name="remoteAcceptAll">Remote forwardings without an address listen on all server interfaces.</param>
        public static IReadOnlyList<PortForwardSpec> ParsePortForwardings(string? value, bool localAcceptAll = false, bool remoteAcceptAll = false)
        {
            var forwards = new List<PortForwardSpec>();
            if (string.IsNullOrWhiteSpace(value))
                return forwards;

            foreach (var entry in SplitEscaped(value, ',').Where(e => e.Trim().Length > 0))
            {
                var keyValue = SplitEscaped(entry, '=', maxParts: 2);
                var key = keyValue[0].Trim();
                var destination = keyValue.Count > 1 ? keyValue[1].Trim() : string.Empty;
                if (key.Length > 0 && key[0] is '4' or '6')
                    key = key[1..];
                if (key.Length < 2)
                    continue;

                PortForwardKind? kind = char.ToUpperInvariant(key[0]) switch
                {
                    'L' => PortForwardKind.Local,
                    'R' => PortForwardKind.Remote,
                    'D' => PortForwardKind.Dynamic,
                    _ => null,
                };
                if (kind is null)
                    continue;
                // Newer PuTTY versions store dynamic forwardings as "L<port>=D".
                if (kind == PortForwardKind.Local && destination == "D")
                    kind = PortForwardKind.Dynamic;

                var source = PortForwardSpec.SplitHostPortList(key[1..]);
                string? bindAddress = source.Count > 1 ? string.Join(':', source.Take(source.Count - 1)) : null;
                if (!int.TryParse(source[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var bindPort))
                    continue;
                if (string.IsNullOrEmpty(bindAddress))
                    bindAddress = (kind == PortForwardKind.Remote ? remoteAcceptAll : localAcceptAll) ? "0.0.0.0" : null;

                if (kind == PortForwardKind.Dynamic)
                {
                    forwards.Add(new PortForwardSpec(kind.Value, bindAddress, bindPort));
                    continue;
                }

                var target = PortForwardSpec.SplitHostPortList(destination);
                if (target.Count < 2
                    || !int.TryParse(target[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var destinationPort))
                {
                    continue;
                }
                var destinationHost = string.Join(':', target.Take(target.Count - 1));
                if (destinationHost.Length == 0)
                    continue;
                forwards.Add(new PortForwardSpec(kind.Value, bindAddress, bindPort, destinationHost, destinationPort));
            }
            return forwards;
        }

        private static List<string> SplitEscaped(string value, char separator, int maxParts = int.MaxValue)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    current.Append(value[++i]);
                }
                else if (c == separator && parts.Count < maxParts - 1)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            parts.Add(current.ToString());
            return parts;
        }

        private static Dictionary<string, string> SummaryValues(PuttySession session)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HostName"] = session.Hostname,
                ["UserName"] = session.Username,
                ["Protocol"] = session.Protocol,
            };
            if (session.Port > 0)
                values["PortNumber"] = session.Port.ToString(CultureInfo.InvariantCulture);
            return values;
        }

        private string GetString(string key) => _values.TryGetValue(key, out var value) ? value : string.Empty;

        private int GetInt(string key) =>
            _values.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0;
    }
}
