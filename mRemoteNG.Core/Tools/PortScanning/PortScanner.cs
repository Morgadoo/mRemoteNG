using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace mRemoteNG.Core.Tools.PortScanning
{
    /// <summary>Services the scanner recognises (the legacy scanner's protocol columns).</summary>
    public enum ScannedService
    {
        Unknown,
        Ssh,
        Telnet,
        Http,
        Https,
        Rlogin,
        Rdp,
        Vnc,
    }

    /// <summary>An open port and what was found listening on it.</summary>
    public sealed record OpenPort(int Port, ScannedService Service, string? Banner, long LatencyMs);

    /// <summary>The result for one host.</summary>
    public sealed class ScanHost
    {
        public ScanHost(string address, string hostName, IReadOnlyList<OpenPort> openPorts)
        {
            Address = address;
            HostName = string.IsNullOrEmpty(hostName) ? address : hostName;
            OpenPorts = openPorts.OrderBy(p => p.Port).ToList();
        }

        /// <summary>What was scanned: an IP address or a host name.</summary>
        public string Address { get; }

        /// <summary>Resolved DNS name, or <see cref="Address"/> when it could not be resolved.</summary>
        public string HostName { get; }

        /// <summary>The first label of <see cref="HostName"/> (or the address), as the legacy importer named connections.</summary>
        public string HostNameWithoutDomain =>
            HostName == Address || IPAddress.TryParse(HostName, out _) ? HostName : HostName.Split('.')[0];

        public IReadOnlyList<OpenPort> OpenPorts { get; }

        public bool IsReachable => OpenPorts.Count > 0;

        public bool Has(ScannedService service) => OpenPorts.Any(p => p.Service == service);

        /// <summary>The port <paramref name="service"/> was found on (the lowest if several), or null.</summary>
        public int? PortOf(ScannedService service) => OpenPorts.FirstOrDefault(p => p.Service == service)?.Port;

        public bool Ssh => Has(ScannedService.Ssh);
        public bool Telnet => Has(ScannedService.Telnet);
        public bool Http => Has(ScannedService.Http);
        public bool Https => Has(ScannedService.Https);
        public bool Rlogin => Has(ScannedService.Rlogin);
        public bool Rdp => Has(ScannedService.Rdp);
        public bool Vnc => Has(ScannedService.Vnc);

        public override string ToString() =>
            $"{HostName} ({Address}): {string.Join(", ", OpenPorts.Select(p => $"{p.Port}/{p.Service}"))}";
    }

    public sealed record PortScanOptions
    {
        /// <summary>Ports the legacy "default ports only" option scanned: SSH, Telnet, HTTP, HTTPS, rlogin, RDP, VNC.</summary>
        public static IReadOnlyList<int> DefaultPorts { get; } = [22, 23, 80, 443, 513, 3389, 5900];

        public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1000);

        /// <summary>Connection attempts in flight at once.</summary>
        public int Parallelism { get; init; } = 64;

        /// <summary>Look up DNS names of hosts with open ports.</summary>
        public bool ResolveHostNames { get; init; } = true;

        /// <summary>Read banners (and send a harmless HTTP probe on unknown ports) to identify services.</summary>
        public bool DetectServices { get; init; } = true;
    }

    /// <summary>Progress of a scan: ports probed so far out of the total.</summary>
    public readonly record struct PortScanProgress(int Completed, int Total, int HostsCompleted, int HostCount);

    /// <summary>
    /// TCP connect scanner (cross-platform replacement for the legacy ping + connect scanner; ICMP needs privileges
    /// on Linux and macOS, so reachability is judged by open ports). Services are identified from well-known ports
    /// and, when enabled, from what the service sends first (SSH and VNC banners, Telnet negotiation) or answers to
    /// an HTTP request.
    /// </summary>
    public sealed class PortScanner
    {
        /// <summary>Scans every host on every port.</summary>
        /// <param name="hostScanned">Called (on a worker thread) as soon as each host is finished.</param>
        public async Task<IReadOnlyList<ScanHost>> ScanAsync(
            IReadOnlyList<string> hosts,
            IReadOnlyList<int> ports,
            PortScanOptions? options = null,
            Action<ScanHost>? hostScanned = null,
            IProgress<PortScanProgress>? progress = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(hosts);
            ArgumentNullException.ThrowIfNull(ports);
            options ??= new PortScanOptions();
            hosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var total = hosts.Count * ports.Count;
            var completed = 0;
            var hostsCompleted = 0;
            var remaining = hosts.ToDictionary(h => h, _ => ports.Count);
            var found = new ConcurrentDictionary<string, ConcurrentBag<OpenPort>>();
            var results = new ConcurrentDictionary<string, ScanHost>();

            var work = hosts.SelectMany(host => ports.Select(port => (Host: host, Port: port)));
            await Parallel.ForEachAsync(work,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = ct },
                async (item, token) =>
                {
                    var open = await ProbeAsync(item.Host, item.Port, options, token).ConfigureAwait(false);
                    if (open is not null)
                        found.GetOrAdd(item.Host, _ => []).Add(open);

                    var done = Interlocked.Increment(ref completed);
                    bool hostDone;
                    lock (remaining) hostDone = --remaining[item.Host] == 0;
                    if (hostDone)
                    {
                        var host = await CompleteHostAsync(item.Host, found.GetValueOrDefault(item.Host), options, token).ConfigureAwait(false);
                        results[item.Host] = host;
                        Interlocked.Increment(ref hostsCompleted);
                        hostScanned?.Invoke(host);
                    }
                    progress?.Report(new PortScanProgress(done, total, Volatile.Read(ref hostsCompleted), hosts.Count));
                }).ConfigureAwait(false);

            return hosts.Where(results.ContainsKey).Select(h => results[h]).ToList();
        }

        private static async Task<ScanHost> CompleteHostAsync(string host, ConcurrentBag<OpenPort>? open, PortScanOptions options, CancellationToken ct)
        {
            var openPorts = open?.ToList() ?? [];
            var name = host;
            if (openPorts.Count > 0 && options.ResolveHostNames && IPAddress.TryParse(host, out var address))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(entry.HostName)) name = entry.HostName;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // No reverse DNS entry: keep the address.
                }
            }
            return new ScanHost(host, name, openPorts);
        }

        /// <summary>Connects to one port; returns null when it is closed, filtered or timed out.</summary>
        public static async Task<OpenPort?> ProbeAsync(string host, int port, PortScanOptions options, CancellationToken ct)
        {
            using var tcp = new TcpClient { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            var watch = Stopwatch.StartNew();
            try
            {
                await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            var latency = watch.ElapsedMilliseconds;

            var byPort = ServiceForPort(port);
            if (!options.DetectServices)
                return new OpenPort(port, byPort, null, latency);

            var (service, banner) = await IdentifyAsync(tcp.GetStream(), port, byPort, options.Timeout, ct).ConfigureAwait(false);
            return new OpenPort(port, service, banner, latency);
        }

        /// <summary>The service usually found on a port.</summary>
        public static ScannedService ServiceForPort(int port) => port switch
        {
            22 => ScannedService.Ssh,
            23 => ScannedService.Telnet,
            80 or 8080 or 8000 => ScannedService.Http,
            443 or 8443 => ScannedService.Https,
            513 => ScannedService.Rlogin,
            3389 => ScannedService.Rdp,
            >= 5900 and <= 5999 => ScannedService.Vnc,
            _ => ScannedService.Unknown,
        };

        /// <summary>Recognises a service from the first bytes it sends.</summary>
        public static ScannedService ServiceForBanner(ReadOnlySpan<byte> banner)
        {
            if (banner.StartsWith("SSH-"u8)) return ScannedService.Ssh;
            if (banner.StartsWith("RFB "u8)) return ScannedService.Vnc;
            if (banner.Length > 0 && banner[0] == 0xFF) return ScannedService.Telnet; // IAC: option negotiation
            if (banner.StartsWith("HTTP/"u8)) return ScannedService.Http;
            return ScannedService.Unknown;
        }

        private static async Task<(ScannedService Service, string? Banner)> IdentifyAsync(
            NetworkStream stream, int port, ScannedService byPort, TimeSpan timeout, CancellationToken ct)
        {
            // Services that talk first answer quickly; don't spend the whole timeout on silent ones.
            var wait = TimeSpan.FromMilliseconds(Math.Min(timeout.TotalMilliseconds, 400));
            var banner = await ReadSomeAsync(stream, wait, ct).ConfigureAwait(false);
            var service = ServiceForBanner(banner);
            if (service != ScannedService.Unknown)
                return (service, Printable(banner));

            // HTTPS, RDP and rlogin wait for the client; trust the well-known port.
            if (byPort is ScannedService.Https or ScannedService.Rdp or ScannedService.Rlogin)
                return (byPort, banner.Length > 0 ? Printable(banner) : null);

            if (banner.Length == 0)
            {
                // A silent service on any other port: ask it an HTTP question.
                try
                {
                    await stream.WriteAsync("HEAD / HTTP/1.0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                    var reply = await ReadSomeAsync(stream, wait, ct).ConfigureAwait(false);
                    if (reply.AsSpan().StartsWith("HTTP/"u8))
                        return (ScannedService.Http, Printable(reply));
                }
                catch (IOException)
                {
                    // The service closed the connection: nothing more to learn.
                }
            }
            return (byPort, banner.Length > 0 ? Printable(banner) : null);
        }

        private static async Task<byte[]> ReadSomeAsync(NetworkStream stream, TimeSpan wait, CancellationToken ct)
        {
            var buffer = new byte[256];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            try
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                return buffer[..read];
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return [];
            }
            catch (IOException)
            {
                return [];
            }
        }

        private static string Printable(byte[] bytes)
        {
            var text = new StringBuilder();
            foreach (var b in bytes)
            {
                if (b is (byte)'\r' or (byte)'\n')
                {
                    if (text.Length > 0) break;
                    continue;
                }
                text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            return text.ToString().Trim();
        }
    }
}
