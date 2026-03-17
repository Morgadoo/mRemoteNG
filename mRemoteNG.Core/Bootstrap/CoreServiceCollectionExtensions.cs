using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Security.SymmetricEncryption;
using mRemoteNG.Platform;
using mRemoteNG.Platform.DependencyInjection;

namespace mRemoteNG.Core.Bootstrap;

/// <summary>
/// Registers the shared cross-platform service baseline required by both
/// legacy WinForms and Avalonia startup pipelines.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddMRemoteNgCore(this IServiceCollection services)
    {
        services.AddPlatformServices();
        services.AddLogging();
        services.AddSingleton<CoreRuntimeDescriptor>();

        // Security / Cryptography
        services.AddSingleton<ICryptoProviderFactory, CryptoProviderFactory>();
        services.AddSingleton<ICryptographyProvider, AeadCryptographyProvider>();

        // Connection file management
        services.AddSingleton<ConnectionsService>();

        return services;
    }
}
