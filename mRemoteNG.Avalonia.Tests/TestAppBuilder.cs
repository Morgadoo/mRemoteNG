using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Core.Bootstrap;

[assembly: AvaloniaTestApplication(typeof(mRemoteNG.Avalonia.Tests.TestAppBuilder))]

namespace mRemoteNG.Avalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // The tests check English UI texts: run in English whatever the machine's language is
        // (LocalizationTests switch languages themselves).
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        return AppBuilder.Configure<App>()
            .WithInterFont()
            .UseReactiveUI()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

/// <summary>
/// Boots the app the way <c>App.OnFrameworkInitializationCompleted</c> does for the desktop
/// lifetime (which headless runs don't use): DI container, settings, theme, main window.
/// Settings go to a throw-away config directory, never the user's.
/// </summary>
internal static class TestHost
{
    private static MainWindow? _window;
    private static Application? _app;

    public static string ConfigDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"mrng-ui-tests-{Environment.ProcessId}");

    /// <summary>Must be called on the UI thread (inside an [AvaloniaFact]).</summary>
    public static MainWindow MainWindow
    {
        get
        {
            // The headless runner may create a new Application between tests; a window, theme and
            // container from a previous instance would be stale, so rebuild them per instance.
            if (_window is not null && ReferenceEquals(_app, Application.Current)) return _window;
            _app = Application.Current;

            // Keep every data file (settings, extApps.xml, credentials, logs) out of the real profile:
            // the portable-mode override routes all platforms' settings providers to the test directory
            // (on Windows they would otherwise use %APPDATA%). XDG/HOME also cover code that reads them directly.
            Directory.CreateDirectory(ConfigDirectory);
            mRemoteNG.Platform.Settings.AppDataLocation.UsePortableDirectory(ConfigDirectory);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ConfigDirectory);
            if (!OperatingSystem.IsWindows())
                Environment.SetEnvironmentVariable("HOME", ConfigDirectory);

            var services = new ServiceCollection();
            services.AddMRemoteNgCore();
            AppServices.Register(services);
            AppServices.ServiceCollection = services;
            AppServices.BuildAndActivate();
            AppServices.GetRequired<AppSettingsRuntime>().ApplyTheme();

            _window = new MainWindow { DataContext = AppServices.GetRequired<MainWindowViewModel>() };
            _window.Show();
            return _window;
        }
    }

    public static MainWindowViewModel ViewModel => (MainWindowViewModel)MainWindow.DataContext!;

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);
}
