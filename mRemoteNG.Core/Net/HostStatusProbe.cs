using System.Net.NetworkInformation;
using System.Net.Sockets;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Core.Net
{
    /// <summary>Result of <see cref="HostStatusProbe.ProbeAsync"/>.</summary>
    /// <param name="PingSucceeded">True when the host answered an ICMP echo.</param>
    /// <param name="PingRoundtripMs">Round-trip time of the echo, when it succeeded.</param>
    /// <param name="PingError">Why the ping failed (or null).</param>
    /// <param name="Port">The probed TCP port (0 when none was probed).</param>
    /// <param name="PortOpen">True when a TCP connection to <paramref name="Port"/> succeeded.</param>
    /// <param name="PortError">Why the TCP probe failed (or null).</param>
    public sealed record HostStatus(
        bool PingSucceeded,
        long? PingRoundtripMs,
        string? PingError,
        int Port,
        bool PortOpen,
        string? PortError)
    {
        /// <summary>True when either probe got an answer.</summary>
        public bool IsReachable => PingSucceeded || PortOpen;

        /// <summary>A one-line summary, e.g. "Online — ping 3 ms, port 22 open".</summary>
        public string Summary
        {
            get
            {
                var ping = PingSucceeded
                    ? Localizer.Format("HostStatusPingFormat", PingRoundtripMs)
                    : Localizer.Format("HostStatusNoPingFormat", PingError);
                var port = Port <= 0
                    ? null
                    : PortOpen
                        ? Localizer.Format("HostStatusPortOpenFormat", Port)
                        : Localizer.Format("HostStatusPortClosedFormat", Port, PortError);
                var detail = port is null ? ping : $"{ping}, {port}";
                return Localizer.Format(IsReachable ? "HostStatusOnlineFormat" : "HostStatusOfflineFormat", detail);
            }
        }
    }

    /// <summary>
    /// Checks whether a host is up, like the legacy host status: an ICMP ping and, because ICMP is
    /// often filtered, a TCP connect to the connection's port.
    /// </summary>
    public static class HostStatusProbe
    {
        public static async Task<HostStatus> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            var pingTask = PingAsync(host, timeout, ct);
            var portTask = port > 0 ? ConnectAsync(host, port, timeout, ct) : Task.FromResult<(bool, string?)>((false, null));
            await Task.WhenAll(pingTask, portTask);
            var (pingOk, rtt, pingError) = await pingTask;
            var (portOk, portError) = await portTask;
            return new HostStatus(pingOk, rtt, pingError, Math.Max(port, 0), portOk, portError);
        }

        private static async Task<(bool, long?, string?)> PingAsync(string host, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, timeout, cancellationToken: ct);
                return reply.Status == IPStatus.Success
                    ? (true, reply.RoundtripTime, null)
                    : (false, null, reply.Status.ToString());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, null, "timed out");
            }
            catch (PingException ex)
            {
                return (false, null, ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException or PlatformNotSupportedException)
            {
                return (false, null, ex.Message);
            }
        }

        private static async Task<(bool, string?)> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cts.Token);
                return (true, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, $"no answer within {timeout.TotalSeconds:0.#} s");
            }
            catch (SocketException ex)
            {
                return (false, ex.SocketErrorCode == SocketError.ConnectionRefused ? "refused" : ex.Message);
            }
        }
    }
}
