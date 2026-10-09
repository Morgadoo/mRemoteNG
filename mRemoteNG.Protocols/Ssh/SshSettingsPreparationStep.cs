using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Protocols.Abstractions;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// Applies a connection's PuTTY saved session (<c>PuttySession</c>) and its SSH options (<c>SSHOptions</c>,
/// PuTTY command-line arguments) to the connection parameters of the PuTTY-family protocols
/// (SSH, Telnet, Rlogin, Raw), the way the WinForms app passed <c>-load session … options</c> to PuTTY.
///
/// Precedence, as on PuTTY's command line: the saved session fills in what the connection leaves empty
/// (host name, user, private key; the port when the connection uses the protocol's default port), and
/// the SSH options override both. SSH-only settings (forwardings, compression, proxy, -N) are passed
/// to <see cref="SshNetProtocol"/> through <see cref="SshExtras"/>.
/// Runs early (order 110) so that later steps — credentials, tunnels — see the final host and port.
/// </summary>
public sealed class SshSettingsPreparationStep : IConnectionPreparationStep
{
    private readonly PuttySessionCatalog _catalog;
    private readonly ILogger _logger;

    public SshSettingsPreparationStep(PuttySessionCatalog catalog, ILogger<SshSettingsPreparationStep>? logger = null)
    {
        _catalog = catalog;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public int Order => 110;

    public async Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct)
    {
        if (parameters.Protocol is not (ProtocolType.Ssh or ProtocolType.SshSftp or ProtocolType.Telnet or ProtocolType.Rlogin or ProtocolType.Raw))
            return parameters;

        var connection = context.Connection;
        var isSsh = parameters.Protocol is ProtocolType.Ssh or ProtocolType.SshSftp;
        var options = string.IsNullOrWhiteSpace(connection.SSHOptions)
            ? new SshCommandLineOptions()
            : SshOptionsParser.Parse(connection.SSHOptions);
        var settings = new SshSettings(parameters, isSsh);

        // 1. Saved sessions: the connection's own, then one named by -load in the SSH options.
        foreach (var sessionName in new[] { connection.PuttySession, options.LoadSession })
        {
            if (PuttySessionSettings.IsDefaultSession(sessionName))
                continue;
            ct.ThrowIfCancellationRequested();
            var session = await _catalog.FindAsync(sessionName!);
            if (session is null)
            {
                settings.Notices.Add($"PuTTY saved session \"{sessionName}\" was not found; its settings were not applied.");
                continue;
            }
            ApplySession(settings, session, connection.Protocol, overridePort: connection is PuttySessionNodeInfo);
        }

        // 2. Command-line options override the connection and the saved session.
        if (options.Port is { } port)
            settings.Parameters = settings.Parameters with { Port = port };
        if (options.Username is { } user)
            settings.Parameters = settings.Parameters with { Username = user };
        if (isSsh)
        {
            if (options.Password is { } password)
                settings.Parameters = settings.Parameters with { Password = password };
            if (options.IdentityFile is { } keyFile)
                settings.Parameters = settings.Parameters with { PrivateKeyPath = keyFile };
            settings.Compression |= options.Compression;
            settings.NoShell |= options.NoShell;
            settings.AddForwards(options.Forwards);
            settings.Notices.AddRange(options.Notices);
        }
        else if (options.Forwards.Count > 0 || options.Compression || options.NoShell || options.IdentityFile is not null)
        {
            _logger.LogWarning("SSH-only options of {Connection} were ignored for a {Protocol} connection", connection.Name, parameters.Protocol);
        }

        return settings.Build();
    }

    private static void ApplySession(SshSettings settings, PuttySessionSettings session, CoreProtocol connectionProtocol, bool overridePort)
    {
        var p = settings.Parameters;
        if (string.IsNullOrWhiteSpace(p.Hostname) && session.HostName.Length > 0)
            p = p with { Hostname = session.HostName };

        // A port saved for another protocol (e.g. a Telnet session loaded into an SSH connection) does not apply.
        var sessionProtocol = PuttySessionsTree.MapProtocol(session.Protocol);
        var sameProtocol = sessionProtocol == connectionProtocol
                           || (sessionProtocol == CoreProtocol.SSH2 && connectionProtocol == CoreProtocol.SSH1);
        if (session.PortNumber > 0 && sameProtocol
            && (overridePort || p.Port <= 0 || p.Port == Core.Connection.ConnectionInfo.GetDefaultPort(connectionProtocol)))
        {
            p = p with { Port = session.PortNumber };
        }

        if (string.IsNullOrEmpty(p.Username) && session.UserName.Length > 0)
            p = p with { Username = session.UserName };
        settings.Parameters = p;

        if (!settings.IsSsh)
            return;

        if (string.IsNullOrEmpty(p.PrivateKeyPath) && session.PublicKeyFile.Length > 0)
            settings.Parameters = p with { PrivateKeyPath = SshOptionsParser.ExpandHome(session.PublicKeyFile) };
        settings.Compression |= session.Compression;
        settings.NoShell |= session.NoShell;
        settings.AddForwards(session.PortForwardings);

        if (session.AgentForwarding)
            settings.Notices.Add($"Agent forwarding (PuTTY session \"{session.Name}\") is not supported by the built-in SSH client and was ignored.");
        if (session.X11Forwarding)
            settings.Notices.Add($"X11 forwarding (PuTTY session \"{session.Name}\") is not supported by the built-in SSH client and was ignored.");
        if (session.RemoteCommand.Length > 0)
            settings.RemoteCommand ??= session.RemoteCommand;

        var proxy = session.Proxy;
        if (settings.ProxyType is null && proxy.AppliesTo(settings.Parameters.Hostname))
        {
            settings.ProxyType = proxy.Method switch
            {
                PuttyProxyMethod.Socks4 => "socks4",
                PuttyProxyMethod.Socks5 => "socks5",
                PuttyProxyMethod.Http => "http",
                _ => null,
            };
            if (settings.ProxyType is null)
            {
                settings.Notices.Add(
                    $"The {proxy.Method} proxy of PuTTY session \"{session.Name}\" is not supported (only HTTP, SOCKS 4 and SOCKS 5); connecting directly.");
            }
            else
            {
                settings.Proxy = proxy;
            }
        }
    }

    /// <summary>The parameters and SSH extras being assembled for one connection.</summary>
    private sealed class SshSettings(ConnectionParameters parameters, bool isSsh)
    {
        private readonly List<PortForwardSpec> _forwards = [.. SshExtras.GetForwards(parameters)];

        public ConnectionParameters Parameters { get; set; } = parameters;
        public bool IsSsh { get; } = isSsh;
        public bool Compression { get; set; } = SshExtras.IsTrue(parameters, SshExtras.Compression);
        public bool NoShell { get; set; } = SshExtras.IsTrue(parameters, SshExtras.NoShell);
        public List<string> Notices { get; } = [.. SshExtras.GetNotices(parameters)];
        public string? RemoteCommand { get; set; }
        public string? ProxyType { get; set; } = parameters.Extras.GetValueOrDefault(SshExtras.ProxyType);
        public PuttyProxySettings? Proxy { get; set; }

        /// <summary>Adds forwardings, skipping ones that listen where an earlier one already does.</summary>
        public void AddForwards(IEnumerable<PortForwardSpec> forwards)
        {
            foreach (var forward in forwards)
            {
                if (forward.BindPort != 0 && _forwards.Any(f =>
                        (f.Kind == PortForwardKind.Remote) == (forward.Kind == PortForwardKind.Remote)
                        && f.BindPort == forward.BindPort
                        && f.BindAddress == forward.BindAddress))
                {
                    continue;
                }
                _forwards.Add(forward);
            }
        }

        public ConnectionParameters Build()
        {
            var extras = new Dictionary<string, string>(Parameters.Extras);
            if (IsSsh)
            {
                SetOrRemove(extras, SshExtras.Compression, Compression ? "true" : null);
                SetOrRemove(extras, SshExtras.NoShell, NoShell ? "true" : null);
                SetOrRemove(extras, SshExtras.Forwards, _forwards.Count > 0 ? PortForwardSpec.SerializeAll(_forwards) : null);
                if (Proxy is { } proxy && ProxyType is not null)
                {
                    extras[SshExtras.ProxyType] = ProxyType;
                    extras[SshExtras.ProxyHost] = proxy.Host;
                    extras[SshExtras.ProxyPort] = proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    SetOrRemove(extras, SshExtras.ProxyUsername, proxy.Username.Length > 0 ? proxy.Username : null);
                    SetOrRemove(extras, SshExtras.ProxyPassword, proxy.Password.Length > 0 ? proxy.Password : null);
                }
                if (RemoteCommand is not null && !extras.ContainsKey(ConnectionParametersFactory.Keys.OpeningCommand))
                {
                    // The built-in client always opens a login shell; the command is typed into it.
                    extras[ConnectionParametersFactory.Keys.OpeningCommand] = RemoteCommand;
                    Notices.Add("The PuTTY session's remote command is run in the login shell (as the opening command).");
                }
            }
            SetOrRemove(extras, SshExtras.Notices, Notices.Count > 0 ? string.Join('\n', Notices.Distinct()) : null);
            return Parameters with { Extras = extras };
        }

        private static void SetOrRemove(Dictionary<string, string> extras, string key, string? value)
        {
            if (value is null)
                extras.Remove(key);
            else
                extras[key] = value;
        }
    }
}
