using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Security;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// Creates providers bound to a settings source: the live settings for connections, or the Options
/// window's unsaved working copy for its "Test" buttons.
/// </summary>
public sealed class ExternalProviderFactory(ProviderSecrets secrets, IProviderHttpClientFactory http)
{
    public ProviderSecrets Secrets { get; } = secrets;

    public IExternalCredentialProvider CreateCredentialProvider(ExternalCredentialProvider kind, Func<AppSettings> settings) => kind switch
    {
        ExternalCredentialProvider.DelineaSecretServer => new DelineaSecretServerProvider(settings, http, Secrets),
        ExternalCredentialProvider.ClickstudiosPasswordState => new PasswordstateProvider(settings, http, Secrets),
        ExternalCredentialProvider.OnePassword => new OnePasswordCliProvider(settings),
        ExternalCredentialProvider.VaultOpenbao => new VaultOpenbaoProvider(settings, http, Secrets),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No provider for this kind."),
    };

    public IExternalAddressProvider CreateAddressProvider(ExternalAddressProvider kind, Func<AppSettings> settings) => kind switch
    {
        ExternalAddressProvider.AmazonWebServices => new AwsEc2AddressProvider(settings, Secrets),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No provider for this kind."),
    };

    public static IReadOnlyList<ExternalCredentialProvider> CredentialKinds { get; } =
        Enum.GetValues<ExternalCredentialProvider>().Where(k => k != ExternalCredentialProvider.None).ToList();

    public static IReadOnlyList<ExternalAddressProvider> AddressKinds { get; } =
        Enum.GetValues<ExternalAddressProvider>().Where(k => k != ExternalAddressProvider.None).ToList();
}

public static class ExternalProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the providers and the two connection preparation steps (address: order 100,
    /// credentials: order 200). Needs <see cref="AppSettingsService"/> and <see cref="ICryptoProvider"/>;
    /// register an <see cref="IExternalProviderPrompt"/> to let providers ask for unsaved secrets.
    /// </summary>
    public static IServiceCollection AddExternalProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExternalProviderPrompt, NonInteractiveExternalProviderPrompt>();
        services.TryAddSingleton<IProviderHttpClientFactory, DefaultProviderHttpClientFactory>();
        services.TryAddSingleton(sp => new ProviderSecrets(
            sp.GetRequiredService<ICryptoProvider>(),
            sp.GetRequiredService<IExternalProviderPrompt>(),
            sp.GetService<ILogger<ProviderSecrets>>()));
        services.TryAddSingleton<ExternalProviderFactory>();

        foreach (var kind in ExternalProviderFactory.CredentialKinds)
        {
            services.AddSingleton(sp => sp.GetRequiredService<ExternalProviderFactory>()
                .CreateCredentialProvider(kind, LiveSettings(sp)));
        }
        foreach (var kind in ExternalProviderFactory.AddressKinds)
        {
            services.AddSingleton(sp => sp.GetRequiredService<ExternalProviderFactory>()
                .CreateAddressProvider(kind, LiveSettings(sp)));
        }

        services.AddSingleton<IConnectionPreparationStep>(sp => new ExternalAddressPreparationStep(
            sp.GetServices<IExternalAddressProvider>(),
            sp.GetService<ILogger<ExternalAddressPreparationStep>>()));
        services.AddSingleton<IConnectionPreparationStep>(sp => new ExternalCredentialPreparationStep(
            sp.GetServices<IExternalCredentialProvider>(),
            LiveSettings(sp),
            sp.GetService<ILogger<ExternalCredentialPreparationStep>>()));
        return services;
    }

    private static Func<AppSettings> LiveSettings(IServiceProvider sp)
    {
        var service = sp.GetRequiredService<AppSettingsService>();
        return () => service.Current;
    }
}
