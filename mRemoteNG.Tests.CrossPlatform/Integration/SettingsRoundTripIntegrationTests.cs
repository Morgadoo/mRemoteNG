using System.IO;
using mRemoteNG.Platform;
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
            // TODO: instantiate a file-based settings provider with tempDir
            // Set a value, dispose, reload, verify value persists
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
