using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Platform.Linux;
using mRemoteNG.Platform.Mac.Clipboard;
using mRemoteNG.Platform.Mac.Notifications;
using mRemoteNG.Platform.Mac.Process;
using mRemoteNG.Platform.Mac.Security;
using mRemoteNG.Platform.Mac.Settings;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.Platform.Mac;

/// <summary>
/// Entry point called by <see cref="mRemoteNG.Platform.PlatformServiceFactory"/>
/// to register all macOS-specific service implementations.
/// </summary>
public static class PlatformRegistrar
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IClipboardService, MacClipboardService>();
        services.AddSingleton<IWindowService, NullWindowService>(); // Avalonia handles window management
        services.AddSingleton<IProcessService, MacProcessService>();
        services.AddSingleton<ISettingsProvider, MacSettingsProvider>();
        services.AddSingleton<ICryptoProvider, MacCryptoProvider>();
        services.AddSingleton<INotificationService, MacNotificationService>();
        services.AddSingleton<ISystemTrayService, MacSystemTrayService>();
    }
}
