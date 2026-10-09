using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using mRemoteNG.Core.App.Info;
using mRemoteNG.Core.Tools;
using mRemoteNG.Platform;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.External;

/// <summary>DI registration of the external tools services.</summary>
public static class ExternalToolsRegistration
{
    /// <summary>
    /// Registers <see cref="ExternalToolsService"/> (tools in <c>extApps.xml</c> next to the other settings: the
    /// settings provider's data directory, or <see cref="ApplicationPaths.SettingsDirectory"/>), the launcher and the
    /// pre-/post-connection step. Existing registrations (e.g. in tests) are kept.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            string directory = sp.GetService<ISettingsProvider>()?.ApplicationDataDirectory ?? ApplicationPaths.SettingsDirectory;
            return new ExternalToolsRepository(Path.Combine(directory, ExternalToolsRepository.FileName),
                sp.GetService<ILogger<ExternalToolsRepository>>());
        });
        services.TryAddSingleton(sp => new ExternalToolLauncher(sp.GetService<ILogger<ExternalToolLauncher>>()));
        services.TryAddSingleton(sp => new ExternalToolsService(
            sp.GetRequiredService<ExternalToolsRepository>(),
            sp.GetRequiredService<ExternalToolLauncher>(),
            sp.GetService<ILogger<ExternalToolsService>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionPreparationStep, ExternalToolPreparationStep>(sp =>
            new ExternalToolPreparationStep(
                sp.GetRequiredService<ExternalToolsService>(),
                sp.GetService<ILogger<ExternalToolPreparationStep>>())));
    }
}
