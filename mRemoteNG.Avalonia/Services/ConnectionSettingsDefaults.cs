using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Connection defaults from the Options window (Connections page), expressed for the
/// cross-platform protocol layer. Read from <see cref="AppSettingsService.Current"/> at connect time.
/// </summary>
public static class ConnectionSettingsDefaults
{
    /// <summary>Extras key carrying the connect timeout in seconds.</summary>
    public const string ConnectTimeoutSecondsKey = ConnectionParametersFactory.Keys.ConnectTimeoutSeconds;

    /// <summary>Extras key carrying the SSH keep-alive interval in seconds ("0" = disabled).</summary>
    public const string SshKeepAliveSecondsKey = ConnectionParametersFactory.Keys.SshKeepAliveSeconds;

    /// <summary>The configured default port for <paramref name="protocol"/>, or null when it has none.</summary>
    public static int? GetDefaultPort(this AppSettings settings, ProtocolType protocol) => protocol switch
    {
        ProtocolType.Ssh or ProtocolType.SshSftp => settings.SshPort,
        ProtocolType.Telnet => settings.TelnetPort,
        ProtocolType.Rlogin => settings.RloginPort,
        ProtocolType.Rdp => settings.RdpPort,
        ProtocolType.Vnc => settings.VncPort,
        ProtocolType.Http => settings.HttpPort,
        ProtocolType.Https => settings.HttpsPort,
        _ => null,
    };

    /// <summary>
    /// Returns parameters with the global defaults filled in where the connection left them empty:
    /// port (when 0), username, SSH private key, plus the connect timeout and SSH keep-alive as extras.
    /// </summary>
    public static ConnectionParameters WithDefaults(this AppSettings settings, ConnectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var isSsh = parameters.Protocol is ProtocolType.Ssh or ProtocolType.SshSftp;
        var extras = new Dictionary<string, string>(parameters.Extras);
        extras.TryAdd(ConnectTimeoutSecondsKey, settings.ConnectTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (isSsh)
        {
            var keepAlive = settings.SshKeepAliveEnabled ? settings.SshKeepAliveIntervalSeconds : 0;
            extras.TryAdd(SshKeepAliveSecondsKey, keepAlive.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new ConnectionParameters
        {
            Hostname = parameters.Hostname,
            Port = parameters.Port > 0 ? parameters.Port : settings.GetDefaultPort(parameters.Protocol) ?? parameters.Port,
            Protocol = parameters.Protocol,
            Username = string.IsNullOrEmpty(parameters.Username) && settings.DefaultUsername.Length > 0
                ? settings.DefaultUsername
                : parameters.Username,
            Password = parameters.Password,
            Domain = parameters.Domain,
            PrivateKeyPath = isSsh && string.IsNullOrEmpty(parameters.PrivateKeyPath) && settings.SshPrivateKeyPath.Length > 0
                ? settings.SshPrivateKeyPath
                : parameters.PrivateKeyPath,
            PrivateKeyPassphrase = parameters.PrivateKeyPassphrase,
            Extras = extras,
        };
    }
}
