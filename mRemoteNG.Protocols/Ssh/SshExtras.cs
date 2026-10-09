using System.Globalization;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// <see cref="ConnectionParameters.Extras"/> keys for SSH settings that come from the connection's
/// SSH options (PuTTY command line), its PuTTY saved session or an SSH tunnel.
/// </summary>
public static class SshExtras
{
    /// <summary>"true" to negotiate zlib compression.</summary>
    public const string Compression = "ssh.compression";

    /// <summary>Port forwardings, one <see cref="PortForwardSpec.Serialize"/> line each.</summary>
    public const string Forwards = "ssh.forwards";

    /// <summary>"true" to open no shell (PuTTY <c>-N</c>): the session only carries the forwardings.</summary>
    public const string NoShell = "ssh.noShell";

    /// <summary>Notices about options that are not supported, one per line; shown in the terminal.</summary>
    public const string Notices = "ssh.notices";

    /// <summary>Proxy type: "socks4", "socks5" or "http".</summary>
    public const string ProxyType = "ssh.proxy.type";
    public const string ProxyHost = "ssh.proxy.host";
    public const string ProxyPort = "ssh.proxy.port";
    public const string ProxyUsername = "ssh.proxy.username";
    public const string ProxyPassword = "ssh.proxy.password";

    /// <summary>
    /// Host name to verify the host key against (and show) instead of <see cref="ConnectionParameters.Hostname"/>,
    /// set when the connection goes through a local tunnel port (like OpenSSH's HostKeyAlias).
    /// </summary>
    public const string HostKeyAlias = "ssh.hostKeyAlias";

    /// <summary>Port that goes with <see cref="HostKeyAlias"/>.</summary>
    public const string HostKeyAliasPort = "ssh.hostKeyAliasPort";

    /// <summary>Name of the SSH tunnel connection the session goes through (for display).</summary>
    public const string TunnelVia = "ssh.tunnelVia";

    public static IReadOnlyList<PortForwardSpec> GetForwards(ConnectionParameters parameters) =>
        PortForwardSpec.DeserializeAll(parameters.Extras.GetValueOrDefault(Forwards));

    public static IReadOnlyList<string> GetNotices(ConnectionParameters parameters) =>
        parameters.Extras.TryGetValue(Notices, out var notices) && notices.Length > 0
            ? notices.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

    public static bool IsTrue(ConnectionParameters parameters, string key) =>
        parameters.Extras.TryGetValue(key, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The host and port the user means: the alias behind a tunnel, otherwise the parameters' own.</summary>
    public static (string Host, int Port) GetDisplayEndpoint(ConnectionParameters parameters)
    {
        if (parameters.Extras.TryGetValue(HostKeyAlias, out var alias) && alias.Length > 0)
        {
            var port = parameters.Extras.TryGetValue(HostKeyAliasPort, out var p)
                       && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var aliasPort)
                ? aliasPort
                : parameters.Port;
            return (alias, port);
        }
        return (parameters.Hostname, parameters.Port);
    }
}
