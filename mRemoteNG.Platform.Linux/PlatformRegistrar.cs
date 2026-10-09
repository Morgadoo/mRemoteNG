using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Platform.Linux.Clipboard;
using mRemoteNG.Platform.Linux.Notifications;
using mRemoteNG.Platform.Linux.Process;
using mRemoteNG.Platform.Linux.Security;
using mRemoteNG.Platform.Linux.Settings;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.Platform.Linux;

/// <summary>
/// Entry point called by <see cref="mRemoteNG.Platform.PlatformServiceFactory"/>
/// to register all Linux-specific service implementations.
/// </summary>
public static class PlatformRegistrar
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IClipboardService, LinuxClipboardService>();
        services.AddSingleton<IWindowService, NullWindowService>();   // Avalonia handles window management
        services.AddSingleton<IProcessService, LinuxProcessService>();
        services.AddSingleton<ISettingsProvider, LinuxSettingsProvider>();
        services.AddSingleton<ICryptoProvider, LinuxCryptoProvider>();
        services.AddSingleton<INotificationService, LinuxNotificationService>();
        services.AddSingleton<ISystemTrayService, LinuxSystemTrayService>();
    }
}
