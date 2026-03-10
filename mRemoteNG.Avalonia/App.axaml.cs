using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views;

namespace mRemoteNG.Avalonia;

public partial class App : Application
{
    private TrayIconService? _trayService;

    public override void Initialize() =>
        AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Apply persisted theme before showing any window.
            ThemeService.Instance.Apply(ThemeService.Instance.CurrentTheme);

            var mainVm = AppServices.GetRequired<MainWindowViewModel>();
            var mainWindow = new MainWindow { DataContext = mainVm };

            desktop.MainWindow = mainWindow;

            // System tray (minimise-to-tray support).
            _trayService = new TrayIconService();
            _trayService.Initialize("mRemoteNG");

            desktop.Exit += (_, _) => _trayService.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
