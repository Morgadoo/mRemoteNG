using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform;

namespace mRemoteNG.Avalonia;

public partial class App : Application
{
    private TrayIconService? _trayService;
    private SingleInstanceGuard? _instanceGuard;

    public override void Initialize() =>
        AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Build the DI container NOW — after Avalonia + ReactiveUI have
            // initialized the main thread scheduler. This ensures all
            // ReactiveCommands capture the correct (UI) scheduler.
            AppServices.BuildAndActivate();

            // Loads settings.xml from the per-OS config directory.
            var settings = AppServices.GetRequired<AppSettingsService>();

            _instanceGuard = new SingleInstanceGuard();
            var isPrimary = _instanceGuard.TryAcquire(settings.Provider.ApplicationDataDirectory);
            if (!isPrimary && settings.Current.SingleInstance)
            {
                Console.Error.WriteLine("mRemoteNG is already running (single-instance mode is enabled in Options).");
                try
                {
                    AppServices.GetRequired<INotificationService>()
                        .ShowNotification("mRemoteNG", "mRemoteNG is already running.");
                }
                catch
                {
                    // Notification is a courtesy only.
                }

                // No window exists yet; leave immediately.
                Environment.Exit(1);
            }

            var runtime = AppServices.GetRequired<AppSettingsRuntime>();

            // Apply the persisted theme before any window is shown.
            runtime.ApplyTheme();

            var mainVm = AppServices.GetRequired<MainWindowViewModel>();
            var mainWindow = new MainWindow { DataContext = mainVm };

            desktop.MainWindow = mainWindow;

            // System tray (minimise-to-tray support).
            _trayService = new TrayIconService();
            _trayService.Initialize("mRemoteNG");

            // Theme/fonts/toolbars, tray visibility, minimise-to-tray, exit confirmation,
            // save-on-exit, last-file tracking, notifications, startup update check.
            runtime.Attach(desktop, mainWindow, _trayService);

            var log = AppServices.GetRequired<ViewModels.Docking.LogPanelDockable>();
            if (Core.App.Info.ApplicationPaths.IsPortable)
                log.Log($"Portable mode: settings, credentials and connections are kept in {Core.App.Info.ApplicationPaths.SettingsDirectory}.");
            foreach (var unknown in Program.Arguments.UnknownSwitches)
                log.Log($"Unknown command-line switch ignored: {unknown}", ViewModels.Docking.LogLevel.Warning);

            desktop.Exit += (_, _) =>
            {
                _trayService.Dispose();
                _instanceGuard.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
