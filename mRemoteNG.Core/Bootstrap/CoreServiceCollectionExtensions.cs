using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<CoreRuntimeDescriptor>();
        return services;
    }
}
