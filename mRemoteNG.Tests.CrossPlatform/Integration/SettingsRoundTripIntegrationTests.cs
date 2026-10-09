using System.IO;
using FluentAssertions;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

[Trait("Category", "Integration")]
public class SettingsRoundTripIntegrationTests
{
    [Fact]
    public void Settings_SaveAndReload_PreservesValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new AppSettingsService(new XmlFileSettingsProvider(tempDir));
            service.Load();
            var edited = service.CreateEditableCopy();
            edited.Theme = ThemeMode.Light;
            edited.SshPort = 2222;
            service.Apply(edited).Should().BeEmpty();

            // Simulate an application restart.
            var reloaded = new AppSettingsService(new XmlFileSettingsProvider(tempDir));
            reloaded.Load();

            reloaded.Current.Theme.Should().Be(ThemeMode.Light);
            reloaded.Current.SshPort.Should().Be(2222);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
