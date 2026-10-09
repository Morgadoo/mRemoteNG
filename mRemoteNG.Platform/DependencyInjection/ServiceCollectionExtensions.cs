using Microsoft.Extensions.DependencyInjection;

namespace mRemoteNG.Platform.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all platform-specific services into <paramref name="services"/>
    /// by delegating to <see cref="PlatformServiceFactory.Register"/>.
    /// The correct implementations are selected at runtime based on the current OS.
    /// </summary>
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        PlatformServiceFactory.Register(services);
        return services;
    }
}
