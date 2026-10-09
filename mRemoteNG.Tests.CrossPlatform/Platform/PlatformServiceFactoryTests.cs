using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Platform;
using mRemoteNG.Platform.Security;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Platform;

public class PlatformServiceFactoryTests
{
    [SkippableFact]
    public void Register_ResolvesAllServices_OnCurrentOs()
    {
        // The Windows assembly is not referenced by this net10.0 test project.
        Skip.If(OperatingSystem.IsWindows(), "Windows platform assembly is loaded only by the app build.");

        var services = new ServiceCollection();
        PlatformServiceFactory.Register(services);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IClipboardService>().Should().NotBeNull();
        provider.GetService<IWindowService>().Should().NotBeNull();
        provider.GetService<IProcessService>().Should().NotBeNull();
        provider.GetService<INotificationService>().Should().NotBeNull();
        provider.GetService<ISystemTrayService>().Should().NotBeNull();

        // Settings and crypto create files in the user's config directory when constructed, so only
        // check they are registered; their own tests construct them against temporary directories.
        services.Should().Contain(d => d.ServiceType == typeof(ISettingsProvider));
        services.Should().Contain(d => d.ServiceType == typeof(ICryptoProvider));
    }
}
