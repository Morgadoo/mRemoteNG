using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
                sp.GetRequiredService<ViewModels.Docking.LogPanelDockable>(),
                sp.GetRequiredService<mRemoteNG.Core.Settings.AppSettingsService>(),
                sp.GetRequiredService<Services.CloseConfirmationService>(),
                sp.GetRequiredService<ConnectionPreparer>()));

        services.AddSingleton<ViewModels.ConnectionTreeViewModel>(sp =>
            new ViewModels.ConnectionTreeViewModel(
                sp.GetRequiredService<ConnectionsService>(),
                sp.GetRequiredService<mRemoteNG.Core.Settings.AppSettingsService>(),
                sp.GetService<mRemoteNG.Core.Config.Putty.PuttySessionsTree>()));

        // Saved PuTTY sessions for the "PuTTY Sessions" tree root: the Windows platform registers a
        // registry-backed provider; elsewhere PuTTY keeps one file per session in ~/.putty/sessions.
        services.TryAddSingleton<mRemoteNG.Platform.IPuttySessionsProvider>(_ =>
            new mRemoteNG.Core.Config.Import.PuttySessionFilesProvider());

        // Main window — explicit factory so DI resolves constructor args.
        services.AddSingleton<ViewModels.MainWindowViewModel>(sp =>
            new ViewModels.MainWindowViewModel(
                sp.GetRequiredService<ViewModels.ConnectionTreeViewModel>(),
                sp.GetRequiredService<ConnectionsService>(),
                sp.GetRequiredService<ViewModels.Docking.SessionsDockable>(),
                sp.GetRequiredService<ViewModels.Docking.LogPanelDockable>(),
                sp.GetRequiredService<ViewModels.Docking.DebugConsoleDockable>(),
                sp.GetRequiredService<Services.ToastService>(),
                sp.GetRequiredService<mRemoteNG.Core.Settings.AppSettingsService>()));

        // Transient dialogs (new instance per open)
        services.AddTransient<ViewModels.OptionsWindowViewModel>();
        services.AddTransient<ViewModels.QuickConnectViewModel>();
        // ConnectionDialogViewModel is created per edited node by ConnectionTreeViewModel (not via DI).

        // Services
        services.AddSingleton<Services.ThemeService>(_ => Services.ThemeService.Instance);
        services.AddSingleton<Services.TrayIconService>();
        // In-app toasts (bottom-right of the main window); errors written to the log panel become toasts.
        services.AddSingleton<Services.ToastService>();
        // IconService is static — accessed directly, not via DI.

        // SSH host key / credential prompts shown as dialogs (replaces the non-interactive default)
        services.AddSingleton<Protocols.Ssh.ISshUserPrompt, Services.AvaloniaSshUserPrompt>();

        // Settings persistence (ISettingsProvider / ICryptoProvider come from the platform registrar).
        services.AddSingleton<mRemoteNG.Core.Settings.AppSettingsService>(sp =>
        {
            var settings = new mRemoteNG.Core.Settings.AppSettingsService(
                sp.GetRequiredService<mRemoteNG.Platform.ISettingsProvider>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<mRemoteNG.Core.Settings.AppSettingsService>>());
            settings.Load();
            return settings;
        });
        services.AddSingleton<mRemoteNG.Core.Settings.StartupService>(sp =>
            new mRemoteNG.Core.Settings.StartupService(sp.GetRequiredService<mRemoteNG.Core.Settings.AppSettingsService>()));
        services.AddSingleton<Services.UpdateCheckService>(sp => new Services.UpdateCheckService(
            sp.GetRequiredService<mRemoteNG.Core.Settings.AppSettingsService>(),
            sp.GetService<mRemoteNG.Platform.Security.ICryptoProvider>()));
        services.AddSingleton<Services.CloseConfirmationService>();
        services.AddSingleton<Services.AppSettingsRuntime>();

        // Command line (Program registers the parsed one first; tests get the empty default).
        services.TryAddSingleton(mRemoteNG.Core.App.StartupArguments.Empty);

        // Rolling log file (mRemoteNG.log in the settings or portable folder); StorageRuntime applies the
        // level/file/on-off options. Everything logged through ILogger<T> goes there.
        var fileLog = new mRemoteNG.Core.Logging.RollingFileLoggerProvider(mRemoteNG.Core.App.Info.ApplicationPaths.DefaultLogFilePath);
        services.AddSingleton(fileLog);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(fileLog);
        });

        // Backups, autosave, SQL connection database (multi-user polling), file logging options.
        services.AddSingleton<Services.StorageRuntime>();
        services.AddSingleton<mRemoteNG.Core.Credential.FileCredentialRepository>(sp =>
        {
            var directory = sp.GetRequiredService<mRemoteNG.Platform.ISettingsProvider>().ApplicationDataDirectory;
            var repository = new mRemoteNG.Core.Credential.FileCredentialRepository(
                Path.Combine(directory, mRemoteNG.Core.Credential.FileCredentialRepository.DefaultFileName),
                sp.GetRequiredService<mRemoteNG.Platform.Security.ICryptoProvider>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<mRemoteNG.Core.Credential.FileCredentialRepository>>());
            repository.LoadCredentials();
            return repository;
        });
        services.AddSingleton<mRemoteNG.Core.Credential.ICredentialLookup>(sp =>
            sp.GetRequiredService<mRemoteNG.Core.Credential.FileCredentialRepository>());
        services.AddSingleton<mRemoteNG.Core.Credential.ICredentialRepository>(sp =>
            sp.GetRequiredService<mRemoteNG.Core.Credential.FileCredentialRepository>());
        services.AddTransient<ViewModels.CredentialManagerViewModel>();

        // Global connection defaults, for connections the protocol layer opens itself (SSH tunnels)
        services.AddSingleton<IConnectionDefaults>(sp => new DelegateConnectionDefaults(
            parameters => Services.ConnectionSettingsDefaults.WithDefaults(
                sp.GetRequiredService<mRemoteNG.Core.Settings.AppSettingsService>().Current, parameters)));

        // External tools: log panel, IntApp tabs and default user name wired in (registered before the protocol
        // defaults, which only add what is missing).
        services.AddSingleton<Protocols.External.ExternalToolsService>(Services.ExternalToolsIntegration.Create);
        services.AddSingleton<ViewModels.ExternalToolsToolbarViewModel>(sp =>
        {
            var tree = sp.GetRequiredService<ViewModels.ConnectionTreeViewModel>();
            return new ViewModels.ExternalToolsToolbarViewModel(
                sp.GetRequiredService<Protocols.External.ExternalToolsService>(),
                () => tree.SelectedNode?.Model,
                sp.GetRequiredService<mRemoteNG.Core.Settings.AppSettingsService>());
        });

        // Protocol implementations (transient — one instance per session)
        ProtocolFactory.Register(services);

        // External credential/address providers (Delinea, Passwordstate, 1Password, Vault/OpenBao, AWS EC2)
        // and their connection preparation steps; unsaved provider secrets are asked for in a dialog.
        services.AddSingleton<mRemoteNG.ExternalProviders.IExternalProviderPrompt, Services.AvaloniaExternalProviderPrompt>();
        mRemoteNG.ExternalProviders.ExternalProviderServiceCollectionExtensions.AddExternalProviders(services);
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
        var externalTools = GetRequired<Protocols.External.ExternalToolsService>();
        connectionTree.ExternalToolNames = () => externalTools.Tools.Select(t => t.DisplayName).ToList();

        // Log application start
        var log = GetRequired<ViewModels.Docking.LogPanelDockable>();
        log.Log("Application started successfully.");
    }
}
