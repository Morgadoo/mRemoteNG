using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Ssh;
using mRemoteNG.Protocols.Telnet;
using mRemoteNG.Protocols.Rdp;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Web;
using mRemoteNG.Protocols.Shell;
using mRemoteNG.Protocols.External;

namespace mRemoteNG.Protocols.Abstractions;

/// <summary>
/// Default factory.  Resolves protocol from DI where registered,
/// otherwise creates directly (for protocols without DI deps).
/// </summary>
public sealed class ProtocolFactory : IProtocolFactory
{
    private readonly IServiceProvider _services;

    public ProtocolFactory(IServiceProvider services) => _services = services;

    public IProtocol Create(ProtocolType type) => type switch
    {
        ProtocolType.Ssh or ProtocolType.SshSftp
            => _services.GetRequiredService<SshNetProtocol>(),

        ProtocolType.Telnet
            => _services.GetRequiredService<TelnetProtocol>(),

        ProtocolType.Rlogin
            => _services.GetRequiredService<RloginProtocol>(),

        ProtocolType.Rdp
            => _services.GetRequiredService<RdpProtocol>(),

        ProtocolType.Vnc
            => _services.GetRequiredService<VncProtocol>(),

        ProtocolType.Http or ProtocolType.Https
            => _services.GetRequiredService<WebViewProtocol>(),

        ProtocolType.PowerShell
            => _services.GetRequiredService<PowerShellProtocol>(),

        ProtocolType.Serial
            => _services.GetRequiredService<SerialProtocol>(),

        ProtocolType.ExternalApp
            => _services.GetRequiredService<ExternalAppProtocol>(),

        _ => throw new NotSupportedException($"No protocol handler for {type}.")
    };

    /// <summary>Registers all protocol types as transient services.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddTransient<SshNetProtocol>();
        services.AddTransient<TelnetProtocol>();
        services.AddTransient<RloginProtocol>();
        services.AddTransient<RdpProtocol>();
        services.AddTransient<VncProtocol>();
        services.AddTransient<WebViewProtocol>();
        services.AddTransient<PowerShellProtocol>();
        services.AddTransient<SerialProtocol>();
        services.AddTransient<ExternalAppProtocol>();
        services.AddSingleton<IProtocolFactory, ProtocolFactory>();
    }
}
