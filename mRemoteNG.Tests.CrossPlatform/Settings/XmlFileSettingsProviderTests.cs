using System.Globalization;
using FluentAssertions;
using mRemoteNG.Platform.Linux.Settings;
using mRemoteNG.Platform.Mac.Settings;
using mRemoteNG.Platform.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

public sealed class XmlFileSettingsProviderTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    private enum Sample { Alpha, Beta }

    public void Dispose() => _dir.Dispose();

    private XmlFileSettingsProvider Create() => new(_dir.Path);

    [Fact]
    public void Save_ThenNewInstance_RoundTripsAllSupportedTypes()
    {
        var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        var provider = Create();
        provider.SetValue("s", "string", "multi\nline \"quoted\" <xml> & ünïcödé");
        provider.SetValue("s", "int", -42);
        provider.SetValue("s", "long", long.MaxValue);
        provider.SetValue("s", "bool", true);
        provider.SetValue("s", "double", 1.5);
        provider.SetValue("s", "enum", Sample.Beta);
        provider.SetValue("s", "date", when);
        provider.SetValue("s", "guid", id);
        provider.SetValue("s", "span", TimeSpan.FromMinutes(90));
        provider.Save();

        var reloaded = Create();
        reloaded.GetValue("s", "string", "").Should().Be("multi\nline \"quoted\" <xml> & ünïcödé");
        reloaded.GetValue("s", "int", 0).Should().Be(-42);
        reloaded.GetValue("s", "long", 0L).Should().Be(long.MaxValue);
        reloaded.GetValue("s", "bool", false).Should().BeTrue();
        reloaded.GetValue("s", "double", 0d).Should().Be(1.5);
        reloaded.GetValue("s", "enum", Sample.Alpha).Should().Be(Sample.Beta);
        reloaded.GetValue("s", "date", DateTime.MinValue).Should().Be(when);
        reloaded.GetValue("s", "guid", Guid.Empty).Should().Be(id);
        reloaded.GetValue("s", "span", TimeSpan.Zero).Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void Values_AreStoredWithInvariantCulture_RegardlessOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var provider = Create();
            provider.SetValue("s", "double", 1234.5);
            provider.Save();

            File.ReadAllText(provider.SettingsFilePath).Should().Contain("value=\"1234.5\"");

            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Create().GetValue("s", "double", 0d).Should().Be(1234.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void GetValue_ReturnsDefault_ForUnparsableOrUndefinedValues()
    {
        var provider = Create();
        provider.SetValue("s", "int", "not a number");
        provider.SetValue("s", "enum", "42");

        provider.GetValue("s", "int", 7).Should().Be(7);
        provider.GetValue("s", "enum", Sample.Alpha).Should().Be(Sample.Alpha);
    }

    [Fact]
    public void Save_WithoutChanges_DoesNotCreateFile()
    {
        var provider = Create();
        provider.Save();
        File.Exists(provider.SettingsFilePath).Should().BeFalse();
    }

    [Fact]
    public void Save_LeavesNoTemporaryFiles()
    {
        var provider = Create();
        for (var i = 0; i < 5; i++)
        {
            provider.SetValue("s", "counter", i);
            provider.Save();
        }

        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().BeEquivalentTo(["settings.xml"]);
        File.ReadAllText(provider.SettingsFilePath).Should().StartWith("<?xml");
    }

    [Fact]
    public void Reload_DiscardsUnsavedChanges()
    {
        var provider = Create();
        provider.SetValue("s", "k", "saved");
        provider.Save();
        provider.SetValue("s", "k", "unsaved");

        provider.Reload();

        provider.GetValue("s", "k", "").Should().Be("saved");
    }

    [Fact]
    public void RemoveValue_IsPersisted()
    {
        var provider = Create();
        provider.SetValue("s", "a", 1);
        provider.SetValue("s", "b", 2);
        provider.Save();

        provider.RemoveValue("s", "a");
        provider.Save();

        Create().GetKeys("s").Should().BeEquivalentTo(["b"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Settings><Section name=\"s\"><Entry key=\"k\" value=\"v\"")]
    [InlineData("this is not xml")]
    [InlineData("<Other />")]
    public void CorruptFile_IsBackedUp_AndDefaultsAreUsed(string contents)
    {
        var path = _dir.Combine(XmlFileSettingsProvider.DefaultFileName);
        File.WriteAllText(path, contents);

        var provider = Create();

        provider.GetKeys("s").Should().BeEmpty();
        provider.CorruptFileBackupPath.Should().NotBeNull();
        File.Exists(provider.CorruptFileBackupPath).Should().BeTrue();
        File.ReadAllText(provider.CorruptFileBackupPath!).Should().Be(contents);
        File.Exists(path).Should().BeFalse("the corrupt file is moved aside, not deleted");

        // The provider keeps working and writes a fresh, valid file.
        provider.SetValue("s", "k", "v");
        provider.Save();
        Create().GetValue("s", "k", "").Should().Be("v");
    }

    [Fact]
    public void DtdInSettingsFile_IsRejectedAsCorrupt()
    {
        File.WriteAllText(_dir.Combine("settings.xml"),
            "<?xml version=\"1.0\"?><!DOCTYPE Settings [<!ENTITY x \"boom\">]><Settings><Section name=\"s\"><Entry key=\"k\" value=\"&x;\"/></Section></Settings>");

        var provider = Create();

        provider.GetValue("s", "k", "default").Should().Be("default");
        provider.CorruptFileBackupPath.Should().NotBeNull();
    }

    [Fact]
    public void ConcurrentReadsWritesAndSaves_DoNotThrowOrLoseValues()
    {
        var provider = Create();

        Parallel.For(0, 200, i =>
        {
            provider.SetValue($"section{i % 5}", $"key{i}", i);
            provider.GetValue($"section{i % 5}", $"key{i}", -1).Should().Be(i);
            _ = provider.GetKeys($"section{(i + 1) % 5}");
            if (i % 20 == 0)
                provider.Save();
        });
        provider.Save();

        var reloaded = Create();
        for (var i = 0; i < 200; i++)
            reloaded.GetValue($"section{i % 5}", $"key{i}", -1).Should().Be(i);
    }

    [Fact]
    public void ExistingFileFormat_FromPreviousBuilds_IsReadable()
    {
        File.WriteAllText(_dir.Combine("settings.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Settings><Section name=\"Appearance\"><Entry key=\"Theme\" value=\"Light\" /></Section></Settings>");

        Create().GetValue("Appearance", "Theme", "Dark").Should().Be("Light");
    }
}

/// <summary>Tests that change process-wide environment variables; must not run in parallel with each other.</summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class PlatformSettingsDirectoryTests
{
    [Fact]
    public void LinuxProvider_UsesXdgConfigHome_WhenAbsolute()
    {
        using var dir = new TempDirectory();
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir.Path);
            var provider = new LinuxSettingsProvider();

            provider.ApplicationDataDirectory.Should().Be(Path.Combine(dir.Path, "mRemoteNG"));
            provider.SettingsFilePath.Should().Be(Path.Combine(dir.Path, "mRemoteNG", "settings.xml"));
            Directory.Exists(provider.ApplicationDataDirectory).Should().BeTrue();

            provider.SetValue("s", "k", 5);
            provider.Save();
            new LinuxSettingsProvider().GetValue("s", "k", 0).Should().Be(5);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    public void LinuxProvider_IgnoresEmptyOrRelativeXdgConfigHome(string value)
    {
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", value);
            LinuxSettingsProvider.ResolveApplicationDataDirectory().Should().Be(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mRemoteNG"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
        }
    }

    [Fact]
    public void MacProvider_UsesApplicationSupport()
    {
        MacSettingsProvider.ResolveApplicationDataDirectory().Should().EndWith(
            Path.Combine("Library", "Application Support", "mRemoteNG"));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "Process environment";
}
