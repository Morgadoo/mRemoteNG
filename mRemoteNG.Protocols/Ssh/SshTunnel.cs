using Microsoft.Extensions.Logging;
using Renci.SshNet;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// An SSH connection that forwards a local loopback port to a target host and port (OpenSSH
/// <c>-L 0:target:port</c>). Disposing it stops the forwarding and closes the SSH connection.
/// </summary>
public sealed class SshTunnel : IAsyncDisposable
{
    private static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromSeconds(30);

    private readonly SshClient _client;
    private readonly ForwardedPortLocal _port;
    private readonly List<ForwardedPort> _extraPorts;
    private readonly ILogger _logger;
    private int _disposed;

    private SshTunnel(SshClient client, ForwardedPortLocal port, List<ForwardedPort> extraPorts, string name, string targetHost, int targetPort, ILogger logger)
    {
        _client = client;
        _port = port;
        _extraPorts = extraPorts;
        Name = name;
        TargetHost = targetHost;
        TargetPort = targetPort;
        _logger = logger;
        LocalPort = (int)port.BoundPort;
    }

    /// <summary>The tunnel connection's name.</summary>
    public string Name { get; }

    /// <summary>The loopback port that leads to <see cref="TargetHost"/>:<see cref="TargetPort"/>.</summary>
    public int LocalPort { get; }

    public string TargetHost { get; }

    public int TargetPort { get; }

    /// <summary>True while the SSH connection is up and the local port is listening.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _client.IsConnected && _port.IsStarted;

    /// <summary>Connects with <paramref name="connector"/> and starts the forwarding.</summary>
    /// <param name="tunnelParameters">The SSH connection to tunnel through; its own forwardings (SSH options) start too.</param>
    internal static async Task<SshTunnel> OpenAsync(
        SshConnector connector,
        ConnectionParameters tunnelParameters,
        string name,
        string targetHost,
        int targetPort,
        ILogger logger,
        CancellationToken ct)
    {
        var client = await connector.ConnectAsync(tunnelParameters, info => new SshClient(info), ct);
        try
        {
            if (client.KeepAliveInterval <= TimeSpan.Zero)
                client.KeepAliveInterval = DefaultKeepAlive;

            var extraPorts = SshPortForwarding.Start(
                client,
                SshExtras.GetForwards(tunnelParameters),
                logger,
                (message, ok) => logger.Log(ok ? LogLevel.Information : LogLevel.Warning, "SSH tunnel {Tunnel}: {Message}", name, message));

            var port = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            port.Exception += (_, e) => logger.LogWarning(e.Exception, "SSH tunnel {Tunnel} to {Host}:{Port} failed", name, targetHost, targetPort);
            client.AddForwardedPort(port);
            await Task.Run(port.Start, ct);

            var tunnel = new SshTunnel(client, port, extraPorts, name, targetHost, targetPort, logger);
            logger.LogInformation("SSH tunnel {Tunnel}: 127.0.0.1:{LocalPort} → {Host}:{Port}", name, tunnel.LocalPort, targetHost, targetPort);
            return tunnel;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await Task.Run(() =>
        {
            SshPortForwarding.Stop(_client, [_port, .. _extraPorts], _logger);
            try
            {
                _client.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing SSH tunnel {Tunnel} failed", Name);
            }
            _client.Dispose();
        });
        _logger.LogInformation("SSH tunnel {Tunnel} to {Host}:{Port} closed", Name, TargetHost, TargetPort);
    }
}
