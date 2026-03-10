using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Platform;

namespace mRemoteNG.Avalonia;

internal sealed class Program
{
    // Avalonia entry point — must remain synchronous and unmodified.
    [STAThread]
    public static void Main(string[] args)
    {
        // Bootstrap the DI container with platform-specific services.
        var services = new ServiceCollection();
        PlatformServiceFactory.Register(services);

        // Register application-level services.
        AppServices.Register(services);

        AppServices.Provider = services.BuildServiceProvider();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration — do not modify (used by the designer).
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
