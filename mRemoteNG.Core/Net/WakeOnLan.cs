using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace mRemoteNG.Core.Net
{
    /// <summary>Sends Wake-on-LAN "magic packets".</summary>
    public static class WakeOnLan
    {
        /// <summary>The port magic packets are usually sent to (discard).</summary>
        public const int DefaultPort = 9;

        /// <summary>Size of a magic packet: 6 × 0xFF followed by the MAC address 16 times.</summary>
        public const int PacketLength = 6 + 16 * 6;

        /// <summary>
        /// Parses a MAC address written as <c>aa:bb:cc:dd:ee:ff</c>, <c>aa-bb-cc-dd-ee-ff</c>,
        /// <c>aabb.ccdd.eeff</c> or <c>aabbccddeeff</c> (case-insensitive).
        /// </summary>
        public static bool TryParseMacAddress(string? text, out byte[] mac)
        {
            mac = [];
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var hex = new string(text.Trim().Where(c => c is not (':' or '-' or '.')).ToArray());
            if (hex.Length != 12)
                return false;

            var bytes = new byte[6];
            for (var i = 0; i < 6; i++)
            {
                if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                    return false;
            }

            mac = bytes;
            return true;
        }

        /// <summary>Builds the 102-byte magic packet for <paramref name="mac"/>.</summary>
        public static byte[] BuildMagicPacket(ReadOnlySpan<byte> mac)
        {
            if (mac.Length != 6)
                throw new ArgumentException("A MAC address has 6 bytes.", nameof(mac));

            var packet = new byte[PacketLength];
            packet.AsSpan(0, 6).Fill(0xFF);
            for (var i = 0; i < 16; i++)
                mac.CopyTo(packet.AsSpan(6 + i * 6, 6));
            return packet;
        }

        /// <summary>
        /// Sends a magic packet for <paramref name="macAddress"/> to <paramref name="target"/>
        /// (default: the limited broadcast address, UDP port 9).
        /// </summary>
        /// <exception cref="FormatException">The MAC address is not valid.</exception>
        public static async Task SendAsync(string macAddress, IPEndPoint? target = null, CancellationToken ct = default)
        {
            if (!TryParseMacAddress(macAddress, out var mac))
                throw new FormatException($"\"{macAddress}\" is not a valid MAC address.");

            target ??= new IPEndPoint(IPAddress.Broadcast, DefaultPort);
            var packet = BuildMagicPacket(mac);
            using var client = new UdpClient(target.AddressFamily);
            client.EnableBroadcast = true;
            await client.SendAsync(packet, target, ct);
        }
    }
}
