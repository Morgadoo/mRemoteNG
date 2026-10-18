using mRemoteNG.Core.Connection;
using mRemoteNG.PluginContracts;
using mRemoteNG.Protocols.Abstractions;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.ExternalProviders.Plugins;

/// <summary>Plugin protocol ids (<see cref="PluginProtocolIds"/>) for the Core protocols, as in the WinForms app.</summary>
public static class PluginProtocols
{
    private static readonly IReadOnlyDictionary<string, CoreProtocolType> ToCore =
        new Dictionary<string, CoreProtocolType>(StringComparer.OrdinalIgnoreCase)
        {
            [PluginProtocolIds.Ard] = CoreProtocolType.ARD,
            [PluginProtocolIds.Http] = CoreProtocolType.HTTP,
            [PluginProtocolIds.Https] = CoreProtocolType.HTTPS,
            [PluginProtocolIds.Rdp] = CoreProtocolType.RDP,
            [PluginProtocolIds.Rlogin] = CoreProtocolType.Rlogin,
            [PluginProtocolIds.Ssh2] = CoreProtocolType.SSH2,
            [PluginProtocolIds.Telnet] = CoreProtocolType.Telnet,
            [PluginProtocolIds.Vnc] = CoreProtocolType.VNC,
        };

    /// <summary>The id plugins see; protocols without a <see cref="PluginProtocolIds"/> constant use their lower-case name.</summary>
    public static string ToPluginId(CoreProtocolType protocol) =>
        ToCore.FirstOrDefault(p => p.Value == protocol).Key ?? protocol.ToString().ToLowerInvariant();

    public static bool TryToCore(string? protocolId, out CoreProtocolType protocol)
    {
        protocol = default;
        if (string.IsNullOrWhiteSpace(protocolId))
            return false;
        if (ToCore.TryGetValue(protocolId, out protocol))
            return true;
        return Enum.TryParse(protocolId, ignoreCase: true, out protocol) && Enum.IsDefined(protocol);
    }
}

/// <summary>A tree node as plugins see it; changes are written to the node (tree context actions).</summary>
internal sealed class NodePluginConnection(ConnectionInfo node) : IPluginConnection
{
    public string Hostname
    {
        get => node.Hostname;
        set => node.Hostname = value ?? string.Empty;
    }

    public bool IsContainer => node.IsContainer;

    public string Name
    {
        get => node.Name;
        set => node.Name = value ?? string.Empty;
    }

    public string ProtocolId => PluginProtocols.ToPluginId(node.Protocol);

    public int Port
    {
        get => node.Port;
        set => node.Port = value;
    }

    public string Username
    {
        get => node.Username;
        set => node.Username = value ?? string.Empty;
    }

    public string Password
    {
        get => node.Password;
        set => node.Password = value ?? string.Empty;
    }

    public string Domain
    {
        get => node.Domain;
        set => node.Domain = value ?? string.Empty;
    }

    public string GetPluginProperty(string key, string defaultValue = "") => node.GetPluginProperty(key, defaultValue);

    public IReadOnlyDictionary<string, string> GetPluginProperties() => new Dictionary<string, string>(node.PluginProperties);

    public void SetPluginProperty(string key, string? value) => node.SetPluginProperty(key, value);

    public bool TryGetPluginProperty(string key, out string value) => node.TryGetPluginProperty(key, out value);
}

/// <summary>
/// The connection being opened, as an address resolver sees it: host name, port and credentials come from (and
/// go back to) this attempt's <see cref="ConnectionParameters"/>, so a resolver never rewrites the saved host
/// name. Plugin properties are the node's.
/// </summary>
internal sealed class PreparingPluginConnection(ConnectionInfo node, ConnectionParameters parameters) : IPluginConnection
{
    public string Hostname { get; set; } = parameters.Hostname;

    public bool IsContainer => node.IsContainer;

    public string Name
    {
        get => node.Name;
        set { }
    }

    public string ProtocolId => PluginProtocols.ToPluginId(node.Protocol);

    public int Port { get; set; } = parameters.Port;

    public string Username { get; set; } = parameters.Username ?? string.Empty;

    public string Password { get; set; } = parameters.Password ?? string.Empty;

    public string Domain { get; set; } = parameters.Domain ?? string.Empty;

    public string GetPluginProperty(string key, string defaultValue = "") => node.GetPluginProperty(key, defaultValue);

    public IReadOnlyDictionary<string, string> GetPluginProperties() => new Dictionary<string, string>(node.PluginProperties);

    public void SetPluginProperty(string key, string? value) => node.SetPluginProperty(key, value);

    public bool TryGetPluginProperty(string key, out string value) => node.TryGetPluginProperty(key, out value);

    public ConnectionParameters ApplyTo(ConnectionParameters current) => current with
    {
        Hostname = (Hostname ?? string.Empty).Trim(),
        Port = Port is > 0 and <= 65535 ? Port : current.Port,
        Username = Changed(current.Username, Username),
        Password = Changed(current.Password, Password),
        Domain = Changed(current.Domain, Domain),
    };

    private static string? Changed(string? before, string after) =>
        string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.Ordinal) ? before : after;
}
