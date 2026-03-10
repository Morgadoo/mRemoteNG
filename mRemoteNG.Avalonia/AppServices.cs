using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Protocols.Abstractions;

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
        // Core ViewModels (singleton where state must persist)
        services.AddSingleton<ViewModels.ConnectionTreeViewModel>();
        services.AddSingleton<ViewModels.Docking.SessionsDockable>();
        services.AddSingleton<ViewModels.Docking.LogPanelDockable>();

        // Main window — explicit factory so DI resolves constructor arg.
        services.AddSingleton<ViewModels.MainWindowViewModel>(sp =>
            new ViewModels.MainWindowViewModel(
                sp.GetRequiredService<ViewModels.ConnectionTreeViewModel>()));

        // Transient dialogs (new instance per open)
        services.AddTransient<ViewModels.OptionsWindowViewModel>();
        services.AddTransient<ViewModels.QuickConnectViewModel>();
        services.AddTransient<ViewModels.ConnectionDialogViewModel>();

        // Services
        services.AddSingleton<Services.ThemeService>(_ => Services.ThemeService.Instance);
        services.AddSingleton<Services.TrayIconService>();
        services.AddSingleton<Services.IconService>();

        // Phase 3: Protocol implementations (transient — one instance per session)
        ProtocolFactory.Register(services);
    }
}
