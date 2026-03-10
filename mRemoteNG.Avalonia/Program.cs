using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Core.Bootstrap;

namespace mRemoteNG.Avalonia;

internal sealed class Program
{
    // Avalonia entry point — must remain synchronous and unmodified.
    [STAThread]
    public static void Main(string[] args)
    {
        // Bootstrap DI with cross-platform core + platform services.
        var services = new ServiceCollection();
        services.AddMRemoteNgCore();

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
            .UseReactiveUI()
            .LogToTrace();
}
