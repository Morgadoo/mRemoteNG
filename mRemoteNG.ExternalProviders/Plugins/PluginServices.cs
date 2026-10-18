using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.ExternalProviders.Plugins;

/// <summary>
/// Order 90 (before the external address provider): the plugins' address resolvers rewrite the host name of the
/// connection attempt (e.g. the multi-address plugin's hostname/IP choice).
/// </summary>
public sealed class PluginAddressPreparationStep(PluginHost host) : IConnectionPreparationStep
{
    public int Order => 90;

    public Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct) =>
        host.ResolveAddressAsync(context.Connection, parameters, ct);
}

public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="PluginHost"/> (bound to the live settings of <see cref="AppSettingsService"/>) and its
    /// address preparation step. Call <see cref="PluginHost.Load"/> once the app has started.
    /// </summary>
    public static IServiceCollection AddPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettingsService>();
            return new PluginHost(() => settings.Current, sp.GetService<ILogger<PluginHost>>());
        });
        services.AddSingleton<IConnectionPreparationStep>(sp => new PluginAddressPreparationStep(sp.GetRequiredService<PluginHost>()));
        return services;
    }
}
