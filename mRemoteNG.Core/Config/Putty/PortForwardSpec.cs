using System.Globalization;

namespace mRemoteNG.Core.Config.Putty
{
    public enum PortForwardKind
    {
        /// <summary>Listen locally and forward each connection through the server (<c>-L</c>).</summary>
        Local,

        /// <summary>Ask the server to listen and forward each connection back to us (<c>-R</c>).</summary>
        Remote,

        /// <summary>Listen locally as a SOCKS proxy whose connections go through the server (<c>-D</c>).</summary>
        Dynamic,
    }

    /// <summary>
    /// One SSH port forwarding, as given on the PuTTY command line (<c>-L</c>, <c>-R</c>, <c>-D</c>) or
    /// stored in a PuTTY session's <c>PortForwardings</c> setting.
    /// </summary>
    /// <param name="BindAddress">Address to listen on; null for the default (loopback).</param>
    /// <param name="DestinationHost">Target host; null for <see cref="PortForwardKind.Dynamic"/>.</param>
    public sealed record PortForwardSpec(
        PortForwardKind Kind,
        string? BindAddress,
        int BindPort,
        string? DestinationHost = null,
        int DestinationPort = 0)
    {
        private const char FieldSeparator = '|';

        /// <summary>Human-readable form, e.g. "-L 127.0.0.1:8080 → intranet:80".</summary>
        public string Describe()
        {
            var source = $"{FormatHost(BindAddress ?? "127.0.0.1")}:{BindPort}";
            return Kind switch
            {
                PortForwardKind.Local => $"local {source} → {FormatHost(DestinationHost!)}:{DestinationPort}",
                PortForwardKind.Remote => $"remote {source} (on the server) → {FormatHost(DestinationHost!)}:{DestinationPort}",
                _ => $"dynamic (SOCKS) {source}",
            };
        }

        /// <summary>Single-line form used to carry forwards in connection-parameter extras.</summary>
        public string Serialize() =>
            string.Join(FieldSeparator,
                Kind switch { PortForwardKind.Local => "L", PortForwardKind.Remote => "R", _ => "D" },
                BindAddress ?? string.Empty,
                BindPort.ToString(CultureInfo.InvariantCulture),
                DestinationHost ?? string.Empty,
                DestinationPort.ToString(CultureInfo.InvariantCulture));

        /// <summary>Parses the output of <see cref="Serialize"/>; null when malformed.</summary>
        public static PortForwardSpec? Deserialize(string line)
        {
            var fields = line.Split(FieldSeparator);
            if (fields.Length != 5)
                return null;
            PortForwardKind? kind = fields[0] switch
            {
                "L" => PortForwardKind.Local,
                "R" => PortForwardKind.Remote,
                "D" => PortForwardKind.Dynamic,
                _ => null,
            };
            if (kind is null
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bindPort)
                || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var destinationPort))
            {
                return null;
            }
            return new PortForwardSpec(
                kind.Value,
                fields[1].Length == 0 ? null : fields[1],
                bindPort,
                fields[3].Length == 0 ? null : fields[3],
                destinationPort);
        }

        public static string SerializeAll(IEnumerable<PortForwardSpec> forwards) =>
            string.Join('\n', forwards.Select(f => f.Serialize()));

        public static IReadOnlyList<PortForwardSpec> DeserializeAll(string? value) =>
            string.IsNullOrEmpty(value)
                ? []
                : value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Deserialize)
                    .OfType<PortForwardSpec>()
                    .ToList();

        /// <summary>
        /// Parses a command-line forwarding argument: <c>[bind:]port:host:hostport</c> for -L/-R and
        /// <c>[bind:]port</c> for -D. IPv6 addresses may be given in brackets.
        /// </summary>
        /// <exception cref="FormatException">The argument is not a valid forwarding.</exception>
        public static PortForwardSpec ParseCommandLine(PortForwardKind kind, string argument)
        {
            var parts = SplitHostPortList(argument);
            if (kind == PortForwardKind.Dynamic)
            {
                return parts.Count switch
                {
                    1 => new PortForwardSpec(kind, null, ParsePort(parts[0], argument)),
                    2 => new PortForwardSpec(kind, EmptyToNull(parts[0]), ParsePort(parts[1], argument)),
                    _ => throw new FormatException($"Invalid dynamic forwarding \"{argument}\" (expected [address:]port)."),
                };
            }

            return parts.Count switch
            {
                3 => new PortForwardSpec(kind, null, ParsePort(parts[0], argument), RequireHost(parts[1], argument), ParsePort(parts[2], argument)),
                4 => new PortForwardSpec(kind, EmptyToNull(parts[0]), ParsePort(parts[1], argument), RequireHost(parts[2], argument), ParsePort(parts[3], argument)),
                _ => throw new FormatException(
                    $"Invalid {(kind == PortForwardKind.Local ? "local" : "remote")} forwarding \"{argument}\" (expected [address:]port:host:hostport)."),
            };
        }

        /// <summary>Splits on ':' outside square brackets and removes the brackets.</summary>
        internal static List<string> SplitHostPortList(string value)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            var inBrackets = false;
            foreach (var c in value)
            {
                switch (c)
                {
                    case '[' when !inBrackets:
                        inBrackets = true;
                        break;
                    case ']' when inBrackets:
                        inBrackets = false;
                        break;
                    case ':' when !inBrackets:
                        parts.Add(current.ToString());
                        current.Clear();
                        break;
                    default:
                        current.Append(c);
                        break;
                }
            }
            parts.Add(current.ToString());
            return parts;
        }

        internal static int ParsePort(string value, string context)
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 0 and <= 65535)
                return port;
            throw new FormatException($"Invalid port \"{value}\" in \"{context}\".");
        }

        private static string RequireHost(string value, string context) =>
            string.IsNullOrWhiteSpace(value) ? throw new FormatException($"Missing host in \"{context}\".") : value;

        private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

        private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;
    }
}
