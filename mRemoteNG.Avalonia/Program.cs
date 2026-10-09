using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Core.App;
using mRemoteNG.Core.App.Info;
using mRemoteNG.Core.Bootstrap;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Avalonia;

internal sealed class Program
{
    /// <summary>The parsed command line (legacy-compatible switches, see <see cref="StartupArguments"/>).</summary>
    public static StartupArguments Arguments { get; private set; } = StartupArguments.Empty;

    /// <summary>
    /// Connection file passed on the command line (<c>mRemoteNG.Avalonia &lt;file&gt;</c> or <c>/cons:&lt;file&gt;</c>),
    /// resolved like the legacy app; the main window opens it once shown.
    /// </summary>
    public static string? StartupFilePath { get; private set; }

    // Avalonia entry point — must remain synchronous.
    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = StartupArguments.Parse(args);

        // Portable mode (marker file next to the executable or --portable) must be decided before any
        // settings provider, key file or known_hosts store resolves its directory.
        AppDataLocation.Initialize(ApplicationPaths.ExecutableDirectory, Arguments.Portable);

        if (Arguments.ResetSettings)
            ResetSettingsFile();

        StartupFilePath = Arguments.ResolveConnectionFile(ApplicationPaths.SettingsDirectory);

        // Register services but do NOT build the provider yet.
        // ViewModels must be created AFTER Avalonia + ReactiveUI initialize
        // the main thread scheduler, otherwise ReactiveCommands capture a
        // background scheduler and crash with "Call from invalid thread".
        var services = new ServiceCollection();
        services.AddSingleton(Arguments);
        services.AddMRemoteNgCore();
        AppServices.Register(services);
        AppServices.ServiceCollection = services;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>/resetsettings: moves settings.xml aside (kept as a backup), so every option starts at its default.</summary>
    private static void ResetSettingsFile()
    {
        var settingsFile = Path.Combine(ApplicationPaths.SettingsDirectory, XmlFileSettingsProvider.DefaultFileName);
        if (!File.Exists(settingsFile))
            return;

        var backup = $"{settingsFile}.reset-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(settingsFile, backup);
            Console.Error.WriteLine($"mRemoteNG: settings reset to defaults; the old settings were saved as {backup}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"mRemoteNG: could not reset the settings: {ex.Message}");
        }
    }

    // Avalonia configuration — do not modify (used by the designer).
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI()
            .LogToTrace();
}
