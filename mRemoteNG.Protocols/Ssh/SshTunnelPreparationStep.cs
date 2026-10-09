using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Protocols.Abstractions;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// Connects through another connection of the tree when <see cref="ConnectionInfo.SSHTunnelConnectionName"/>
/// is set (legacy "SSH tunnel"): the named SSH connection is opened in the background, a local port on
/// 127.0.0.1 is forwarded through it to the target's host and port, and the session connects to that
/// local port instead. The tunnel closes when the session's tab closes.
///
/// The tunnel connection is prepared by the full <see cref="ConnectionPreparer"/> pipeline, so its PuTTY
/// session, SSH options, credentials — and its own SSH tunnel — apply: chained tunnels work, and a
/// tunnel that leads back to a connection already in the chain is refused.
/// </summary>
public sealed class SshTunnelPreparationStep : IConnectionPreparationStep
{
    /// <summary>Connections whose tunnels are being opened in the current async flow (cycle detection).</summary>
    private static readonly AsyncLocal<ImmutableList<string>?> Chain = new();

    private readonly SshConnector _connector;
    private readonly ILogger _logger;
    private readonly Func<ConnectionPreparer?> _preparer;
    private readonly Func<IEnumerable<ConnectionInfo>> _additionalRoots;
    private readonly IConnectionDefaults? _defaults;

    /// <summary>DI constructor: the preparer, the connection tree and the PuTTY sessions are resolved when needed.</summary>
    public SshTunnelPreparationStep(
        IHostKeyVerifier verifier,
        ISshUserPrompt prompt,
        IServiceProvider services,
        ILogger<SshTunnelPreparationStep>? logger = null)
        : this(
            verifier,
            prompt,
            services.GetService<ConnectionPreparer>,
            () => TreeRoots(services),
            services.GetService<IConnectionDefaults>(),
            logger)
    {
    }

    /// <param name="preparer">Prepares the tunnel connection; null maps it without running any steps.</param>
    /// <param name="additionalRoots">Roots searched after the target's own root (e.g. the loaded tree, PuTTY sessions).</param>
    /// <param name="defaults">Global defaults applied to the tunnel connection's parameters.</param>
    public SshTunnelPreparationStep(
        IHostKeyVerifier verifier,
        ISshUserPrompt prompt,
        Func<ConnectionPreparer?> preparer,
        Func<IEnumerable<ConnectionInfo>>? additionalRoots = null,
        IConnectionDefaults? defaults = null,
        ILogger<SshTunnelPreparationStep>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _connector = new SshConnector(verifier, prompt, _logger);
        _preparer = preparer;
        _additionalRoots = additionalRoots ?? (() => []);
        _defaults = defaults;
    }

    public int Order => 300;

    public async Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct)
    {
        var connection = context.Connection;
        var tunnelName = connection.SSHTunnelConnectionName;
        if (string.IsNullOrEmpty(tunnelName))
            return parameters;

        if (parameters.Protocol is not (ProtocolType.Ssh or ProtocolType.SshSftp or ProtocolType.Telnet or ProtocolType.Rlogin
            or ProtocolType.Raw or ProtocolType.Rdp or ProtocolType.Vnc or ProtocolType.Http or ProtocolType.Https))
        {
            throw new NotSupportedException(
                $"\"{connection.Name}\" is set to use the SSH tunnel \"{tunnelName}\", but {parameters.Protocol} connections cannot go through an SSH tunnel.");
        }
        if (string.IsNullOrWhiteSpace(parameters.Hostname) || parameters.Port <= 0)
            throw new InvalidOperationException($"\"{connection.Name}\" needs a host name and port to be reached through the SSH tunnel \"{tunnelName}\".");

        var tunnelConnection = FindTunnelConnection(connection, tunnelName);

        var chain = Chain.Value ?? [];
        if (ReferenceEquals(tunnelConnection, connection) || chain.Contains(tunnelConnection.ConstantID))
        {
            throw new InvalidOperationException(
                $"The SSH tunnel \"{tunnelName}\" of \"{connection.Name}\" leads back to a connection that is already part of the tunnel chain.");
        }

        // Prepare the tunnel connection itself; its own tunnel (if any) opens here, recursively.
        // The AsyncLocal change is visible to the nested preparation only and reverts when this method returns.
        Chain.Value = chain.Add(connection.ConstantID);
        var preparer = _preparer();
        var prepared = preparer is not null
            ? await preparer.PrepareAsync(tunnelConnection, ConnectOptions.Default, ct)
            : new PreparedConnection(ConnectionParametersFactory.FromConnectionInfo(tunnelConnection), []);
        context.Resources.Add(prepared); // released after the tunnel below (resources are released in reverse)

        var tunnelParameters = _defaults?.Apply(prepared.Parameters) ?? prepared.Parameters;
        _logger.LogInformation("Opening SSH tunnel {Tunnel} for {Connection} to {Host}:{Port}", tunnelName, connection.Name, parameters.Hostname, parameters.Port);

        SshTunnel tunnel;
        try
        {
            tunnel = await SshTunnel.OpenAsync(_connector, tunnelParameters, tunnelName, parameters.Hostname, parameters.Port, _logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"The SSH tunnel \"{tunnelName}\" for \"{connection.Name}\" could not be opened: {ex.Message}", ex);
        }
        context.Resources.Add(tunnel);

        var extras = new Dictionary<string, string>(parameters.Extras)
        {
            [SshExtras.HostKeyAlias] = parameters.Hostname,
            [SshExtras.HostKeyAliasPort] = parameters.Port.ToString(CultureInfo.InvariantCulture),
            [SshExtras.TunnelVia] = tunnelName,
        };
        // A proxy would now be asked to reach 127.0.0.1; the tunnel replaces it.
        foreach (var key in new[] { SshExtras.ProxyType, SshExtras.ProxyHost, SshExtras.ProxyPort, SshExtras.ProxyUsername, SshExtras.ProxyPassword })
            extras.Remove(key);

        return parameters with { Hostname = "127.0.0.1", Port = tunnel.LocalPort, Extras = extras };
    }

    /// <summary>
    /// Finds the SSH connection named <paramref name="name"/>: first in the target's own tree, then in the
    /// additional roots, depth first, as the legacy app searched its root nodes.
    /// </summary>
    /// <exception cref="InvalidOperationException">No SSH connection has that name.</exception>
    internal ConnectionInfo FindTunnelConnection(ConnectionInfo connection, string name)
    {
        var roots = new List<ConnectionInfo> { connection.GetRootParent() };
        foreach (var root in _additionalRoots())
        {
            if (!roots.Contains(root))
                roots.Add(root);
        }

        var candidates = roots.SelectMany(Flatten).Where(c => c is not ContainerInfo && c.Name == name).ToList();
        var ssh = candidates.FirstOrDefault(c => c.Protocol is CoreProtocol.SSH1 or CoreProtocol.SSH2);
        if (ssh is not null)
            return ssh;

        throw new InvalidOperationException(candidates.Count > 0
            ? $"The SSH tunnel \"{name}\" of \"{connection.Name}\" is a {candidates[0].Protocol} connection; an SSH tunnel must be an SSH connection."
            : $"The SSH tunnel connection \"{name}\" configured for \"{connection.Name}\" was not found in the connection tree.");
    }

    private static IEnumerable<ConnectionInfo> Flatten(ConnectionInfo node)
    {
        yield return node;
        if (node is ContainerInfo container)
        {
            foreach (var child in container.Children.ToList())
            {
                foreach (var descendant in Flatten(child))
                    yield return descendant;
            }
        }
    }

    private static IEnumerable<ConnectionInfo> TreeRoots(IServiceProvider services)
    {
        if (services.GetService<ConnectionsService>()?.ConnectionTreeModel?.RootNode is { } root)
            yield return root;
        if (services.GetService<PuttySessionsTree>() is { } putty)
            yield return putty.Root;
    }
}
