using Microsoft.Extensions.DependencyInjection;

namespace mRemoteNG.Avalonia;

/// <summary>
/// Global service locator for the Avalonia app.
/// Provides access to the DI container after it has been built.
/// ViewModels should prefer constructor injection; use this only at roots.
/// </summary>
public static class AppServices
{
    private static IServiceProvider? _provider;

    public static IServiceProvider Provider
    {
        get => _provider ?? throw new InvalidOperationException("DI container not initialized yet.");
        set => _provider = value;
    }

    public static T GetRequired<T>() where T : notnull =>
        Provider.GetRequiredService<T>();

    /// <summary>Registers application-level (non-platform) services.</summary>
    public static void Register(IServiceCollection services)
    {
        // ViewModels
        services.AddTransient<ViewModels.MainWindowViewModel>();
        services.AddTransient<ViewModels.ConnectionTreeViewModel>();
        services.AddTransient<ViewModels.OptionsWindowViewModel>();

        // Application services (to be expanded in Phase 2 & 3)
        // services.AddSingleton<IConnectionsService, ConnectionsService>();
        // services.AddSingleton<ICredentialService, CredentialService>();
    }
}
