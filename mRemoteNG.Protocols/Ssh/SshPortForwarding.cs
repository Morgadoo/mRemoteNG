using Microsoft.Extensions.Logging;
using Renci.SshNet;
using mRemoteNG.Core.Config.Putty;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>Starts and stops <see cref="PortForwardSpec"/> forwardings on a connected SSH client.</summary>
internal static class SshPortForwarding
{
    /// <summary>
    /// Adds and starts each forwarding. A forwarding that cannot start (port in use, refused by the
    /// server…) is reported through <paramref name="report"/> and skipped; the others still start.
    /// </summary>
    /// <param name="report">Receives one line per forwarding: what was started, or why it failed.</param>
    public static List<ForwardedPort> Start(
        SshClient client,
        IEnumerable<PortForwardSpec> forwards,
        ILogger logger,
        Action<string, bool> report)
    {
        var started = new List<ForwardedPort>();
        foreach (var spec in forwards)
        {
            ForwardedPort? port = null;
            try
            {
                port = Create(spec);
                port.Exception += (_, e) => logger.LogWarning(e.Exception, "SSH port forwarding {Forward} failed", spec.Describe());
                client.AddForwardedPort(port);
                port.Start();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not start SSH port forwarding {Forward}", spec.Describe());
                report($"Port forwarding {spec.Describe()} failed: {ex.Message}", false);
                if (port is not null)
                    Stop(client, [port], logger);
                continue;
            }
            started.Add(port);
            report($"Port forwarding {DescribeBound(spec, port)}", true);
        }
        return started;
    }

    /// <summary>Stops and removes forwardings; errors are logged, not thrown.</summary>
    public static void Stop(SshClient? client, IEnumerable<ForwardedPort> ports, ILogger logger)
    {
        foreach (var port in ports)
        {
            try
            {
                if (port.IsStarted)
                    port.Stop();
                client?.RemoveForwardedPort(port);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Stopping an SSH port forwarding failed");
            }
            finally
            {
                port.Dispose();
            }
        }
    }

    private static ForwardedPort Create(PortForwardSpec spec) => spec.Kind switch
    {
        PortForwardKind.Local => new ForwardedPortLocal(spec.BindAddress ?? "127.0.0.1", (uint)spec.BindPort, spec.DestinationHost!, (uint)spec.DestinationPort),
        PortForwardKind.Remote => new ForwardedPortRemote(spec.BindAddress ?? "127.0.0.1", (uint)spec.BindPort, spec.DestinationHost!, (uint)spec.DestinationPort),
        _ => new ForwardedPortDynamic(spec.BindAddress ?? "127.0.0.1", (uint)spec.BindPort),
    };

    /// <summary>Describes the forwarding with the port actually bound (for port 0).</summary>
    private static string DescribeBound(PortForwardSpec spec, ForwardedPort port)
    {
        var bound = port switch
        {
            ForwardedPortLocal local => (int)local.BoundPort,
            ForwardedPortRemote remote => (int)remote.BoundPort,
            ForwardedPortDynamic dynamic => (int)dynamic.BoundPort,
            _ => spec.BindPort,
        };
        return (spec with { BindPort = bound }).Describe();
    }
}
