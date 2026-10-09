using FluentAssertions;
using mRemoteNG.Core.Config;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform;
using mRemoteNG.Platform.Settings;
using NSubstitute;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        new AppSettings().Validate().Should().BeEmpty();
    }

    [Fact]
    public void PersistedProperties_CoverEveryPublicProperty()
    {
        var all = typeof(AppSettings).GetProperties().Where(p => p.CanWrite).Select(p => p.Name);
        AppSettings.PersistedProperties.Select(p => p.Property.Name).Should().BeEquivalentTo(all);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_RejectsOutOfRangePorts(int port)
    {
        var settings = new AppSettings { RdpPort = port };
        settings.Validate().Should().ContainSingle().Which.Should().Contain("RDP");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Validate_RejectsOutOfRangeTimeout(int seconds)
    {
        new AppSettings { ConnectTimeoutSeconds = seconds }.Validate().Should().ContainSingle()
            .Which.Should().Contain("timeout");
    }

    [Fact]
    public void Validate_RejectsInvalidKeepAliveFontAndProtocol()
    {
        var settings = new AppSettings
        {
            SshKeepAliveIntervalSeconds = 1,
            FontSize = 100,
            DefaultProtocol = "Gopher",
        };

        settings.Validate().Should().HaveCount(3);
    }

    [Fact]
    public void Validate_RequiresFile_WhenOpeningSpecificFileAtStartup()
    {
        var settings = new AppSettings { StartupBehavior = StartupFileBehavior.OpenSpecificFile };
        settings.Validate().Should().ContainSingle();

        settings.StartupFilePath = "/tmp/confCons.xml";
        settings.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Normalize_ResetsInvalidValuesToDefaults()
    {
        var settings = new AppSettings { SshPort = 0, ConnectTimeoutSeconds = 9999, DefaultProtocol = "x", VncPort = 5901 };

        settings.Normalize().Should().BeEquivalentTo(
            [nameof(AppSettings.SshPort), nameof(AppSettings.ConnectTimeoutSeconds), nameof(AppSettings.DefaultProtocol)]);

        settings.SshPort.Should().Be(22);
        settings.ConnectTimeoutSeconds.Should().Be(10);
        settings.DefaultProtocol.Should().Be("SSH");
        settings.VncPort.Should().Be(5901, "valid values are kept");
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var original = new AppSettings();
        var copy = original.Clone();
        copy.SshPort = 2222;
        copy.Theme = ThemeMode.Light;

        original.SshPort.Should().Be(22);
        original.Theme.Should().Be(ThemeMode.Dark);
        original.ValueEquals(copy).Should().BeFalse();
        original.CopyFrom(copy);
        original.ValueEquals(copy).Should().BeTrue();
    }

    [Fact]
    public void GetDefaultPort_UsesConfiguredPorts()
    {
        var settings = new AppSettings { SshPort = 2222, RdpPort = 13389 };

        settings.GetDefaultPort(ProtocolType.SSH2).Should().Be(2222);
        settings.GetDefaultPort(ProtocolType.RDP).Should().Be(13389);
        settings.GetDefaultPort("ssh").Should().Be(2222);
        settings.GetDefaultPort("HTTPS").Should().Be(443);
        settings.GetDefaultPort(ProtocolType.PowerShell).Should().BeNull();
        settings.GetDefaultPort("unknown").Should().BeNull();
    }
}

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private AppSettingsService CreateLoaded()
    {
        var service = new AppSettingsService(new XmlFileSettingsProvider(_dir.Path));
        service.Load();
        return service;
    }

    [Fact]
    public void Load_WithoutFile_GivesDefaults()
    {
        CreateLoaded().Current.ValueEquals(new AppSettings()).Should().BeTrue();
    }

    [Fact]
    public void Apply_PersistsEveryProperty_AcrossRestart()
    {
        var service = CreateLoaded();
        var edited = service.CreateEditableCopy();
        edited.StartupBehavior = StartupFileBehavior.OpenSpecificFile;
        edited.StartupFilePath = "/home/user/My Connections/confCons.xml";
        edited.LastConnectionFilePath = "/tmp/last.xml";
        edited.SingleInstance = true;
        edited.SaveConnectionsOnExit = true;
        edited.ConfirmCloseConnection = ConfirmCloseEnum.All;
        edited.ShowTrayIcon = false;
        edited.MinimizeToTray = true;
        edited.Theme = ThemeMode.System;
        edited.FontFamily = "DejaVu Sans";
        edited.FontSize = 15.5;
        edited.ShowToolbar = false;
        edited.ShowStatusBar = false;
        edited.DefaultProtocol = "RDP";
        edited.ConnectTimeoutSeconds = 42;
        edited.DefaultUsername = "admin";
        edited.SshKeepAliveEnabled = false;
        edited.SshKeepAliveIntervalSeconds = 120;
        edited.SshPrivateKeyPath = "~/.ssh/id_ed25519";
        edited.SshPort = 2222;
        edited.TelnetPort = 2323;
        edited.RloginPort = 1513;
        edited.RdpPort = 13389;
        edited.VncPort = 5901;
        edited.HttpPort = 8080;
        edited.HttpsPort = 8443;
        edited.NotifyOnConnect = true;
        edited.NotifyOnDisconnect = true;
        edited.NotifyOnError = false;
        edited.CheckForUpdatesOnStartup = false;
        edited.UpdateChannel = UpdateChannel.PreRelease;
        edited.LastUpdateCheckUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        service.Apply(edited).Should().BeEmpty();

        var reloaded = CreateLoaded();
        reloaded.Current.Should().BeEquivalentTo(edited);
        reloaded.Current.LastUpdateCheckUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void EditableCopy_ChangesAreDiscarded_UntilApplied()
    {
        var service = CreateLoaded();
        var copy = service.CreateEditableCopy();
        copy.SshPort = 2200;

        service.Current.SshPort.Should().Be(22, "cancel = never applying the copy");
        File.Exists(Path.Combine(_dir.Path, "settings.xml")).Should().BeFalse();
    }

    [Fact]
    public void Apply_InvalidSettings_ReturnsErrors_AndChangesNothing()
    {
        var service = CreateLoaded();
        var raised = false;
        service.Changed += (_, _) => raised = true;
        var edited = service.CreateEditableCopy();
        edited.SshPort = 0;
        edited.Theme = ThemeMode.Light;

        service.Apply(edited).Should().NotBeEmpty();

        service.Current.Theme.Should().Be(ThemeMode.Dark);
        raised.Should().BeFalse();
        File.Exists(Path.Combine(_dir.Path, "settings.xml")).Should().BeFalse();
    }

    [Fact]
    public void Apply_RaisesChanged_WithPreviousAndCurrentSnapshots()
    {
        var service = CreateLoaded();
        AppSettingsChangedEventArgs? args = null;
        service.Changed += (_, e) => args = e;
        var edited = service.CreateEditableCopy();
        edited.Theme = ThemeMode.Light;

        service.Apply(edited);

        args.Should().NotBeNull();
        args!.Previous.Theme.Should().Be(ThemeMode.Dark);
        args.Current.Theme.Should().Be(ThemeMode.Light);
        args.Changed(s => s.Theme).Should().BeTrue();
        args.Changed(s => s.SshPort).Should().BeFalse();
    }

    [Fact]
    public void Apply_KeepsTheSameCurrentInstance()
    {
        var service = CreateLoaded();
        var current = service.Current;
        var edited = service.CreateEditableCopy();
        edited.HttpPort = 8080;

        service.Apply(edited);

        service.Current.Should().BeSameAs(current);
        current.HttpPort.Should().Be(8080);
    }

    [Fact]
    public void Load_InvalidOrOutOfRangeValues_FallBackToDefaults()
    {
        var provider = new XmlFileSettingsProvider(_dir.Path);
        provider.SetValue("DefaultPorts", "SshPort", 0);
        provider.SetValue("Connections", "ConnectTimeoutSeconds", "ten");
        provider.SetValue("Appearance", "Theme", "Purple");
        provider.SetValue("DefaultPorts", "VncPort", 5901);
        provider.Save();

        var settings = CreateLoaded().Current;

        settings.SshPort.Should().Be(22);
        settings.ConnectTimeoutSeconds.Should().Be(10);
        settings.Theme.Should().Be(ThemeMode.Dark);
        settings.VncPort.Should().Be(5901);
    }

    [Fact]
    public void Load_ReadsThemeKeyWrittenByEarlierThemeService()
    {
        var provider = new XmlFileSettingsProvider(_dir.Path);
        provider.SetValue("Appearance", "Theme", "Light");
        provider.Save();

        CreateLoaded().Current.Theme.Should().Be(ThemeMode.Light);
    }

    [Fact]
    public void Update_ChangesSingleValue_AndSaves()
    {
        var service = CreateLoaded();

        service.Update(s => s.LastConnectionFilePath = "/tmp/x.xml");

        CreateLoaded().Current.LastConnectionFilePath.Should().Be("/tmp/x.xml");
    }

    [Fact]
    public void Apply_PropagatesSaveFailures()
    {
        var provider = Substitute.For<ISettingsProvider>();
        provider.When(p => p.Save()).Do(_ => throw new IOException("disk full"));
        var service = new AppSettingsService(provider);
        var edited = service.CreateEditableCopy();
        edited.SshPort = 2222;

        var act = () => service.Apply(edited);

        act.Should().Throw<IOException>();
    }
}

public sealed class StartupServiceTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private (AppSettingsService Settings, StartupService Startup) Create()
    {
        var settings = new AppSettingsService(new XmlFileSettingsProvider(_dir.Path));
        settings.Load();
        return (settings, new StartupService(settings));
    }

    [Fact]
    public void ReopenLastFile_ReturnsRecordedFile_WhenItExists()
    {
        var file = _dir.Combine("confCons.xml");
        File.WriteAllText(file, "<x/>");
        var (_, startup) = Create();

        startup.RecordOpenFile(file);

        // New instances = next application start.
        Create().Startup.GetFileToOpenAtStartup().Should().Be(file);
    }

    [Fact]
    public void ReopenLastFile_ReturnsNull_WhenFileWasDeleted()
    {
        var file = _dir.Combine("confCons.xml");
        File.WriteAllText(file, "<x/>");
        var (_, startup) = Create();
        startup.RecordOpenFile(file);
        File.Delete(file);

        startup.GetFileToOpenAtStartup().Should().BeNull();
    }

    [Fact]
    public void RecordOpenFile_IgnoresNull()
    {
        var (settings, startup) = Create();
        startup.RecordOpenFile("/tmp/a.xml");
        startup.RecordOpenFile(null);

        settings.Current.LastConnectionFilePath.Should().Be(Path.GetFullPath("/tmp/a.xml"));
    }

    [Fact]
    public void OpenSpecificFile_ReturnsConfiguredFile()
    {
        var (settings, _) = Create();
        var edited = settings.CreateEditableCopy();
        edited.StartupBehavior = StartupFileBehavior.OpenSpecificFile;
        edited.StartupFilePath = "/srv/shared/confCons.xml";
        edited.LastConnectionFilePath = "/tmp/other.xml";
        settings.Apply(edited);

        new StartupService(settings, p => p == "/srv/shared/confCons.xml").GetFileToOpenAtStartup()
            .Should().Be("/srv/shared/confCons.xml");
    }

    [Fact]
    public void None_ReturnsNull()
    {
        var (settings, _) = Create();
        settings.Update(s =>
        {
            s.StartupBehavior = StartupFileBehavior.None;
            s.LastConnectionFilePath = "/tmp/last.xml";
        });

        new StartupService(settings, _ => true).GetFileToOpenAtStartup().Should().BeNull();
    }
}
