using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tools.PortScanning;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Imports port-scan results as connections of one protocol (legacy Port Scan ▸ Import). Only hosts where that
    /// protocol was found are imported; each becomes a connection named after the host (without its domain) that
    /// uses the port the service was found on. Connections are added directly to the destination folder.
    /// </summary>
    public sealed class PortScanImporter : IConnectionImporter
    {
        private readonly IReadOnlyList<ScanHost> _hosts;
        private readonly ProtocolType _protocol;

        public PortScanImporter(IEnumerable<ScanHost> hosts, ProtocolType protocol)
        {
            ArgumentNullException.ThrowIfNull(hosts);
            _hosts = hosts.ToList();
            _protocol = protocol;
            if (ServiceFor(protocol) is null)
                throw new ArgumentException($"Port scan results cannot be imported as {protocol} connections.", nameof(protocol));
        }

        /// <summary>Protocols a scan can produce, in the order offered to the user.</summary>
        public static IReadOnlyList<ProtocolType> SupportedProtocols { get; } =
        [
            ProtocolType.SSH2, ProtocolType.Telnet, ProtocolType.HTTP, ProtocolType.HTTPS,
            ProtocolType.Rlogin, ProtocolType.RDP, ProtocolType.VNC, ProtocolType.ARD,
        ];

        /// <summary>The scanned service a protocol needs; null when the protocol cannot come from a scan.</summary>
        public static ScannedService? ServiceFor(ProtocolType protocol) => protocol switch
        {
            ProtocolType.SSH1 or ProtocolType.SSH2 => ScannedService.Ssh,
            ProtocolType.Telnet => ScannedService.Telnet,
            ProtocolType.HTTP => ScannedService.Http,
            ProtocolType.HTTPS => ScannedService.Https,
            ProtocolType.Rlogin => ScannedService.Rlogin,
            ProtocolType.RDP => ScannedService.Rdp,
            ProtocolType.VNC or ProtocolType.ARD => ScannedService.Vnc,
            _ => null,
        };

        /// <param name="source">Not used: the hosts were given to the constructor.</param>
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var service = ServiceFor(_protocol)!.Value;
            var imported = new List<ConnectionInfo>();
            var warnings = new List<string>();

            foreach (var host in _hosts)
            {
                if (host.PortOf(service) is not { } port)
                {
                    warnings.Add($"{host.HostName} skipped: no {_protocol} service was found on it.");
                    continue;
                }

                var connection = ImportNodeFactory.NewConnection(_protocol);
                connection.Name = host.HostNameWithoutDomain;
                connection.Hostname = host.HostName;
                connection.Port = port;
                imported.Add(connection);
            }

            destinationContainer.AddChildRange(imported.ToArray());
            return new ImportResult(imported, warnings);
        }
    }
}
