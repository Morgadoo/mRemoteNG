using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Core.Bootstrap;

namespace mRemoteNG.Avalonia;

internal sealed class Program
{
    // Avalonia entry point — must remain synchronous and unmodified.
    /// <summary>
    /// Connection file passed on the command line (<c>mRemoteNG.Avalonia &lt;file&gt;</c>);
    /// the main window opens it once shown.
    /// </summary>
    public static string? StartupFilePath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        StartupFilePath = args.FirstOrDefault(a => !a.StartsWith('-'));

        // Register services but do NOT build the provider yet.
        // ViewModels must be created AFTER Avalonia + ReactiveUI initialize
        // the main thread scheduler, otherwise ReactiveCommands capture a
        // background scheduler and crash with "Call from invalid thread".
        var services = new ServiceCollection();
        services.AddMRemoteNgCore();
        AppServices.Register(services);
        AppServices.ServiceCollection = services;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration — do not modify (used by the designer).
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .LogToTrace();
}
