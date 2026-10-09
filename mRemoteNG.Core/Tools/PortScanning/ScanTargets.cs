using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace mRemoteNG.Core.Tools.PortScanning
{
    /// <summary>
    /// Parses the hosts to scan. Entries are separated by commas, semicolons or white space; each is an IPv4 or IPv6
    /// address, a host name, a CIDR block ("192.168.1.0/24"), a range ("10.0.0.1-10.0.0.20") or a last-octet range
    /// ("10.0.0.1-20").
    /// </summary>
    public static class HostRangeParser
    {
        /// <summary>Upper bound on hosts per scan, to keep a typo like /8 from queueing 16 million hosts.</summary>
        public const int MaxHosts = 65536;

        private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

        /// <exception cref="FormatException">An entry is not a valid host, range or block, or the list is too long.</exception>
        public static IReadOnlyList<string> Parse(string text)
        {
            var hosts = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var host in ParseEntry(entry))
                {
                    if (seen.Add(host))
                        hosts.Add(host);
                    if (hosts.Count > MaxHosts)
                        throw new FormatException($"Too many hosts: at most {MaxHosts} can be scanned at once.");
                }
            }
            return hosts;
        }

        private static IEnumerable<string> ParseEntry(string entry)
        {
            var slash = entry.IndexOf('/');
            if (slash > 0)
                return ParseCidr(entry[..slash], entry[(slash + 1)..], entry);

            var dash = entry.IndexOf('-');
            if (dash > 0 && IPAddress.TryParse(entry[..dash], out var first) && first.AddressFamily == AddressFamily.InterNetwork)
                return ParseRange(first, entry[(dash + 1)..], entry);

            if (IPAddress.TryParse(entry, out var address))
                return [address.ToString()];
            if (Uri.CheckHostName(entry) == UriHostNameType.Dns)
                return [entry];
            throw new FormatException($"\"{entry}\" is not a host name, IP address, range or CIDR block.");
        }

        private static IEnumerable<string> ParseCidr(string addressText, string prefixText, string entry)
        {
            if (!IPAddress.TryParse(addressText, out var address) || address.AddressFamily != AddressFamily.InterNetwork
                || !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 0 or > 32)
                throw new FormatException($"\"{entry}\" is not a valid IPv4 CIDR block.");

            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var network = ToUInt32(address) & mask;
            var broadcast = network | ~mask;
            // Network and broadcast addresses are not hosts, except in /31 and /32.
            var start = prefix <= 30 ? network + 1 : network;
            var end = prefix <= 30 ? broadcast - 1 : broadcast;
            return Enumerate(start, end, entry);
        }

        private static IEnumerable<string> ParseRange(IPAddress first, string lastText, string entry)
        {
            var start = ToUInt32(first);
            uint end;
            if (lastText.Contains('.') && IPAddress.TryParse(lastText, out var last) && last.AddressFamily == AddressFamily.InterNetwork)
                end = ToUInt32(last);
            else if (byte.TryParse(lastText, NumberStyles.None, CultureInfo.InvariantCulture, out var lastOctet))
                end = (start & 0xFFFFFF00u) | lastOctet;
            else
                throw new FormatException($"\"{entry}\" is not a valid IP address range.");
            if (end < start) (start, end) = (end, start);
            return Enumerate(start, end, entry);
        }

        private static IEnumerable<string> Enumerate(uint start, uint end, string entry)
        {
            if ((long)end - start + 1 > MaxHosts)
                throw new FormatException($"\"{entry}\" covers more than {MaxHosts} hosts.");
            var list = new List<string>((int)(end - start + 1));
            for (var value = (long)start; value <= end; value++)
                list.Add(FromUInt32((uint)value).ToString());
            return list;
        }

        internal static uint ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

        private static IPAddress FromUInt32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return new IPAddress(bytes);
        }
    }

    /// <summary>Parses port lists such as "22, 80, 443, 5900-5910".</summary>
    public static class PortListParser
    {
        /// <exception cref="FormatException">An entry is not a port (1-65535) or range.</exception>
        public static IReadOnlyList<int> Parse(string text)
        {
            var ports = new SortedSet<int>();
            foreach (var entry in (text ?? "").Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = entry.IndexOf('-');
                if (dash > 0)
                {
                    var from = ParsePort(entry[..dash], entry);
                    var to = ParsePort(entry[(dash + 1)..], entry);
                    if (to < from) (from, to) = (to, from);
                    for (var port = from; port <= to; port++) ports.Add(port);
                }
                else
                {
                    ports.Add(ParsePort(entry, entry));
                }
            }
            return ports.ToList();
        }

        private static int ParsePort(string text, string entry) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
                ? port
                : throw new FormatException($"\"{entry}\" is not a port or port range (1-65535).");
    }
}
