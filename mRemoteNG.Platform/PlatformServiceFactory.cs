using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace mRemoteNG.Platform;

/// <summary>
/// Factory that detects the current OS and registers the appropriate
/// platform service implementations into the DI container.
/// Call <see cref="Register"/> during application startup before
/// building the service provider.
/// </summary>
public static class PlatformServiceFactory
{
    /// <summary>
    /// Registers all platform-specific services into <paramref name="services"/>.
    /// The concrete implementations are resolved at runtime based on the OS.
    /// </summary>
    public static IServiceCollection Register(IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            RegisterWindows(services);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            RegisterLinux(services);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            RegisterMac(services);
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");

        return services;
    }

    private static void RegisterWindows(IServiceCollection services)
    {
        // Load the Windows platform assembly via reflection to avoid
        // hard compile-time dependency on Windows-only APIs.
        var windowsAssembly = LoadPlatformAssembly("mRemoteNG.Platform.Windows");
        RegisterFromAssembly(services, windowsAssembly);
    }

    private static void RegisterLinux(IServiceCollection services)
    {
        var linuxAssembly = LoadPlatformAssembly("mRemoteNG.Platform.Linux");
        RegisterFromAssembly(services, linuxAssembly);
    }

    private static void RegisterMac(IServiceCollection services)
    {
        var macAssembly = LoadPlatformAssembly("mRemoteNG.Platform.Mac");
        RegisterFromAssembly(services, macAssembly);
    }

    private static System.Reflection.Assembly LoadPlatformAssembly(string name)
    {
        try
        {
            return System.Reflection.Assembly.Load(name);
        }
        catch
        {
            // Fallback: try loading from the application base directory
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var dllPath = System.IO.Path.Combine(baseDir, $"{name}.dll");
                if (System.IO.File.Exists(dllPath))
                    return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
            }
            catch { /* fall through to throw below */ }

            throw new InvalidOperationException(
                $"Could not load platform assembly '{name}'. " +
                $"Ensure the platform-specific package is installed.");
        }
    }

    private static void RegisterFromAssembly(IServiceCollection services, System.Reflection.Assembly assembly)
    {
        // Each platform assembly must contain a class named 'PlatformRegistrar'
        // with a static method 'Register(IServiceCollection)'.
        var registrarType = assembly.GetType($"{assembly.GetName().Name}.PlatformRegistrar")
            ?? throw new InvalidOperationException($"Assembly '{assembly.GetName().Name}' does not contain a PlatformRegistrar class.");

        var registerMethod = registrarType.GetMethod("Register",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            [typeof(IServiceCollection)])
            ?? throw new InvalidOperationException("PlatformRegistrar.Register(IServiceCollection) not found.");

        registerMethod.Invoke(null, [services]);
    }
}
