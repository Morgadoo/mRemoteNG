using System.Globalization;
using System.Reactive;
using Avalonia.Controls;
using mRemoteNG.Core.Connection;
using ReactiveUI;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.ViewModels;

public sealed class QuickConnectViewModel : ReactiveObject
{
    /// <summary>Protocols offered for quick connect (network protocols with a cross-platform client).</summary>
    public static readonly CoreProtocolType[] SupportedProtocols =
    [
        CoreProtocolType.SSH2, CoreProtocolType.RDP, CoreProtocolType.VNC, CoreProtocolType.Telnet,
        CoreProtocolType.HTTP, CoreProtocolType.HTTPS, CoreProtocolType.RAW, CoreProtocolType.Rlogin,
    ];

    private string _hostname = string.Empty;
    private CoreProtocolType _selectedProtocol = CoreProtocolType.SSH2;
    private string _username = string.Empty;
    private string _password = string.Empty;

    private readonly Window? _owner;

    public QuickConnectViewModel(Window? owner = null) => _owner = owner;

    public string Hostname { get => _hostname; set => this.RaiseAndSetIfChanged(ref _hostname, value); }
    public CoreProtocolType SelectedProtocol { get => _selectedProtocol; set => this.RaiseAndSetIfChanged(ref _selectedProtocol, value); }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }

    public CoreProtocolType[] Protocols { get; } = SupportedProtocols;

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; set; } = ReactiveCommand.Create(() => { });

    public QuickConnectResult? BuildResult() => string.IsNullOrWhiteSpace(Hostname)
        ? null
        : new QuickConnectResult(Hostname.Trim(), SelectedProtocol, Username, Password);

    /// <summary>
    /// Splits "host[:port]" (IPv6 as "[addr]:port"); without a port the protocol's default is used
    /// and <c>PortSpecified</c> is false. URLs (for HTTP/HTTPS) are returned unchanged.
    /// </summary>
    public static (string Host, int Port, bool PortSpecified) ParseHost(string input, CoreProtocolType protocol)
    {
        var host = input.Trim();
        var port = ConnectionInfo.GetDefaultPort(protocol);
        var portSpecified = false;
        if (host.Contains("://", StringComparison.Ordinal))
            return (host, port, false);

        // Keep a URL path ("host:8080/status") out of the port parsing.
        var path = string.Empty;
        var slash = host.IndexOf('/');
        if (slash >= 0)
        {
            path = host[slash..];
            host = host[..slash];
        }

        var colon = host.LastIndexOf(':');
        var isBareIpv6 = host.Count(c => c == ':') > 1 && !host.StartsWith('[');
        if (colon > 0 && !isBareIpv6
            && int.TryParse(host[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65535)
        {
            port = parsed;
            host = host[..colon];
            portSpecified = true;
        }

        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];
        return (host + path, port, portSpecified);
    }
}

public record QuickConnectResult(string Hostname, CoreProtocolType Protocol, string Username, string Password);
