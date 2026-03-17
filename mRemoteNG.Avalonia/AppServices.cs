using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Core.Config.Connections;
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

    /// <summary>
    /// Holds the service collection until BuildAndActivate is called.
    /// This allows registration to happen early, while provider building
    /// is deferred until after Avalonia + ReactiveUI are fully initialized.
    /// </summary>
    public static IServiceCollection? ServiceCollection { get; set; }

    public static IServiceProvider Provider
    {
        get => _provider ?? throw new InvalidOperationException("DI container not initialized yet. Call BuildAndActivate first.");
        private set => _provider = value;
    }

    public static T GetRequired<T>() where T : notnull =>
        Provider.GetRequiredService<T>();

    /// <summary>
    /// Build the service provider and resolve singletons.
    /// MUST be called from the UI thread AFTER Avalonia and ReactiveUI
    /// are fully initialized (i.e. from OnFrameworkInitializationCompleted).
    /// </summary>
    public static void BuildAndActivate()
    {
        if (ServiceCollection is null)
            throw new InvalidOperationException("ServiceCollection not set. Call Register first.");

        Provider = ServiceCollection.BuildServiceProvider();
        ServiceCollection = null; // No longer needed

        WireDependencies();
    }

    /// <summary>Registers application-level (non-platform) services.</summary>
    public static void Register(IServiceCollection services)
    {
        // Core ViewModels (singleton where state must persist)
        services.AddSingleton<ViewModels.Docking.LogPanelDockable>();
        services.AddSingleton<ViewModels.Docking.DebugConsoleDockable>();
        services.AddSingleton<ViewModels.Docking.SessionsDockable>(sp =>
            new ViewModels.Docking.SessionsDockable(
                sp.GetRequiredService<ViewModels.Docking.LogPanelDockable>()));

        services.AddSingleton<ViewModels.ConnectionTreeViewModel>(sp =>
            new ViewModels.ConnectionTreeViewModel(
                sp.GetRequiredService<ConnectionsService>()));

        // Main window — explicit factory so DI resolves constructor args.
        services.AddSingleton<ViewModels.MainWindowViewModel>(sp =>
            new ViewModels.MainWindowViewModel(
                sp.GetRequiredService<ViewModels.ConnectionTreeViewModel>(),
                sp.GetRequiredService<ConnectionsService>(),
                sp.GetRequiredService<ViewModels.Docking.SessionsDockable>(),
                sp.GetRequiredService<ViewModels.Docking.LogPanelDockable>(),
                sp.GetRequiredService<ViewModels.Docking.DebugConsoleDockable>()));

        // Transient dialogs (new instance per open)
        services.AddTransient<ViewModels.OptionsWindowViewModel>();
        services.AddTransient<ViewModels.QuickConnectViewModel>();
        services.AddTransient<ViewModels.ConnectionDialogViewModel>();

        // Services
        services.AddSingleton<Services.ThemeService>(_ => Services.ThemeService.Instance);
        services.AddSingleton<Services.TrayIconService>();
        // IconService is static — accessed directly, not via DI.

        // Protocol implementations (transient — one instance per session)
        ProtocolFactory.Register(services);
    }

    /// <summary>
    /// Wire cross-cutting dependencies that can't be expressed purely through DI.
    /// Called once after the service provider is built on the UI thread.
    /// </summary>
    private static void WireDependencies()
    {
        var connectionTree = GetRequired<ViewModels.ConnectionTreeViewModel>();
        var sessions = GetRequired<ViewModels.Docking.SessionsDockable>();
        var protocolFactory = GetRequired<IProtocolFactory>();
        connectionTree.SetDependencies(sessions, protocolFactory);

        // Log application start
        var log = GetRequired<ViewModels.Docking.LogPanelDockable>();
        log.Log("Application started successfully.");
    }
}
