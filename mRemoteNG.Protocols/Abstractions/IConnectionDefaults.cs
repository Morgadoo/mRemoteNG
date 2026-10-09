namespace mRemoteNG.Protocols.Abstractions;

/// <summary>
/// Fills in application-wide defaults (Options → Connections: default port, user, SSH key, timeouts…)
/// for parameters that leave them unset. Used where the protocol layer connects on its own behalf,
/// e.g. the SSH connection behind a tunnel.
/// </summary>
public interface IConnectionDefaults
{
    ConnectionParameters Apply(ConnectionParameters parameters);
}

/// <summary><see cref="IConnectionDefaults"/> backed by a function.</summary>
public sealed class DelegateConnectionDefaults(Func<ConnectionParameters, ConnectionParameters> apply) : IConnectionDefaults
{
    public ConnectionParameters Apply(ConnectionParameters parameters) => apply(parameters);
}
