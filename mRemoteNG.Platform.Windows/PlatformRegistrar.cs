using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Platform.Security;
using mRemoteNG.Platform.Windows.Clipboard;
using mRemoteNG.Platform.Windows.Process;
using mRemoteNG.Platform.Windows.Security;
using mRemoteNG.Platform.Windows.Settings;
using mRemoteNG.Platform.Windows.Window;

namespace mRemoteNG.Platform.Windows;

/// <summary>
/// Entry point called by <see cref="mRemoteNG.Platform.PlatformServiceFactory"/>
/// to register all Windows-specific service implementations.
/// </summary>
public static class PlatformRegistrar
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IClipboardService, WindowsClipboardService>();
        services.AddSingleton<IWindowService, WindowsWindowService>();
        services.AddSingleton<IProcessService, WindowsProcessService>();
        services.AddSingleton<ISettingsProvider, WindowsSettingsProvider>();
        services.AddSingleton<ICryptoProvider, DpapiCryptoProvider>();
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<ISystemTrayService, WindowsSystemTrayService>();
    }
}
