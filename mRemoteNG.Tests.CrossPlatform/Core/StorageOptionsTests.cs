using FluentAssertions;
using Microsoft.Extensions.Logging;
using mRemoteNG.Core.App.Info;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.DataProviders;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Logging;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Linux.Settings;
using mRemoteNG.Platform.Settings;
using mRemoteNG.Tests.CrossPlatform.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

public sealed class FileBackupTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void BackupName_UsesTheLegacyFormat()
    {
        var options = new FileBackupOptions();
        var path = options.GetBackupPath(_dir.Combine("confCons.xml"), new DateTime(2024, 1, 31, 14, 5, 59, 12).AddTicks(3000));

        Path.GetFileName(path).Should().Be("confCons.xml.20240131-1405590123.backup");
        Path.GetDirectoryName(path).Should().Be(_dir.Path);
        options.GetSearchPattern("confCons.xml").Should().Be("confCons.xml.*.backup");
    }

    [Fact]
    public void Save_KeepsTheConfiguredNumberOfBackups_InTheBackupFolder()
    {
        var file = _dir.Combine("confCons.xml");
        var backups = _dir.Combine("backups");
        var time = new DateTime(2024, 5, 1, 12, 0, 0);
        var creator = new FileBackupCreator(() => time = time.AddMinutes(1));
        var provider = new FileDataProviderWithRollingBackup(file, new FileBackupOptions { KeepCount = 3, BackupDirectory = backups }, creator);

        for (var i = 1; i <= 6; i++)
            provider.Save($"version {i}");

        File.ReadAllText(file).Should().Be("version 6");
        var kept = FileBackupCreator.GetBackups(file, provider.Options);
        kept.Should().HaveCount(3);
        kept.Select(File.ReadAllText).Should().Equal("version 5", "version 4", "version 3");
        kept.Should().OnlyContain(p => Path.GetDirectoryName(p) == backups);
        Directory.GetFiles(_dir.Path).Should().ContainSingle("only the connection file stays next to itself");
    }

    [Fact]
    public void FirstSave_HasNothingToBackUp_AndKeepCountZeroDisablesBackups()
    {
        var file = _dir.Combine("a.xml");
        var provider = new FileDataProviderWithRollingBackup(file, new FileBackupOptions { KeepCount = 0 });
        provider.Save("1");
        provider.Save("2");

        provider.LastBackupPath.Should().BeNull();
        Directory.GetFiles(_dir.Path).Should().ContainSingle();
    }

    [Fact]
    public void BackupsWithinTheSameTick_DoNotOverwriteEachOther()
    {
        var file = _dir.Combine("a.xml");
        File.WriteAllText(file, "x");
        var creator = new FileBackupCreator(() => new DateTime(2024, 1, 1));
        var options = new FileBackupOptions { KeepCount = 5 };

        creator.CreateBackup(file, options);
        creator.CreateBackup(file, options);

        FileBackupCreator.GetBackups(file, options).Should().HaveCount(2);
    }

    [Fact]
    public void ConnectionsService_BacksUpOnSave_OnlyWhenConfigured()
    {
        var file = _dir.Combine("confCons.xml");
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.CreateNew();
        service.ConnectionTreeModel!.RootNode.AddChild(new ConnectionInfo { Name = "one" });
        service.SaveToFile(file);

        service.BackupFrequency = BackupFrequency.OnExit;
        service.SaveToFile();
        FileBackupCreator.GetBackups(file, service.BackupOptions).Should().BeEmpty("on-exit backups are not made on save");

        service.BackupFrequency = BackupFrequency.OnSave;
        service.SaveToFile();
        service.SaveToFile();
        FileBackupCreator.GetBackups(file, service.BackupOptions).Should().HaveCount(2);
        service.LastBackupPath.Should().NotBeNull();

        service.BackupCurrentFile().Should().NotBeNull("the on-exit backup copies the current file");
        FileBackupCreator.GetBackups(file, service.BackupOptions).Should().HaveCount(3);
    }

    [Fact]
    public void AppSettings_BackupValidation()
    {
        new AppSettings { BackupNameFormat = "{0}.bak" }.Validate().Should().ContainSingle().Which.Should().Contain("{1}");
        new AppSettings { BackupNameFormat = "{0}.{1:yyyyMMdd}.bak" }.Validate().Should().BeEmpty();
        new AppSettings { BackupKeepCount = -1 }.Validate().Should().ContainSingle();
        new AppSettings { BackupFrequency = BackupFrequency.Never }.GetBackupOptions().Enabled.Should().BeFalse();
        new AppSettings { BackupDirectory = "/b" }.GetBackupOptions().BackupDirectory.Should().Be("/b");
    }
}

public sealed class ConnectionsAutoSaverTests
{
    private sealed class Target : IAutoSaveTarget
    {
        public bool IsDirty { get; set; }
        public bool CanSave { get; set; } = true;
        public int Saves { get; private set; }

        public void Save()
        {
            Saves++;
            IsDirty = false;
        }
    }

    [Fact]
    public void SaveIfNeeded_OnlySavesDirtyTreesWithAFile()
    {
        using var saver = new ConnectionsAutoSaver();
        var target = new Target();
        saver.Attach(target);

        saver.SaveIfNeeded("test").Should().BeFalse("nothing changed");
        target.IsDirty = true;
        target.CanSave = false;
        saver.SaveIfNeeded("test").Should().BeFalse("no file to save to");
        target.CanSave = true;
        saver.SaveIfNeeded("test").Should().BeTrue();
        target.Saves.Should().Be(1);
    }

    [Fact]
    public async Task SaveOnEdit_SavesShortlyAfterTheEdit()
    {
        using var saver = new ConnectionsAutoSaver(editDelay: TimeSpan.FromMilliseconds(50));
        var target = new Target();
        saver.Attach(target);
        var saved = new TaskCompletionSource<AutoSaveEventArgs>();
        saver.AutoSaved += (_, e) => saved.TrySetResult(e);

        saver.Configure(0, saveOnEdit: true);
        target.IsDirty = true;
        saver.NotifyEdited();
        saver.NotifyEdited();

        (await saved.Task.WaitAsync(TimeSpan.FromSeconds(5))).Reason.Should().Be(ConnectionsAutoSaver.ReasonEdit);
        target.Saves.Should().Be(1, "edits within the delay are saved together");
    }

    [Fact]
    public async Task NotifyEdited_DoesNothing_WhenSaveOnEditIsOff()
    {
        using var saver = new ConnectionsAutoSaver(editDelay: TimeSpan.FromMilliseconds(10));
        var target = new Target { IsDirty = true };
        saver.Attach(target);
        saver.Configure(0, saveOnEdit: false);

        saver.NotifyEdited();
        await Task.Delay(100);

        target.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Interval_SavesPeriodically_ThroughTheDispatcher()
    {
        var dispatched = 0;
        using var saver = new ConnectionsAutoSaver(action =>
        {
            Interlocked.Increment(ref dispatched);
            action();
        });
        var target = new Target { IsDirty = true };
        saver.Attach(target);
        var saved = new TaskCompletionSource<AutoSaveEventArgs>();
        saver.AutoSaved += (_, e) => saved.TrySetResult(e);

        saver.Configure(TimeSpan.FromMilliseconds(50), saveOnEdit: false);

        (await saved.Task.WaitAsync(TimeSpan.FromSeconds(5))).Reason.Should().Be(ConnectionsAutoSaver.ReasonInterval);
        dispatched.Should().BeGreaterThan(0);
        saver.Configure(0, saveOnEdit: false);
        saver.Interval.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void FailedSave_IsReported()
    {
        using var saver = new ConnectionsAutoSaver();
        var target = new FailingTarget();
        saver.Attach(target);
        AutoSaveEventArgs? args = null;
        saver.AutoSaved += (_, e) => args = e;

        saver.SaveIfNeeded("edit").Should().BeFalse();
        args!.Error.Should().BeOfType<IOException>();
    }

    private sealed class FailingTarget : IAutoSaveTarget
    {
        public bool IsDirty => true;
        public bool CanSave => true;
        public void Save() => throw new IOException("disk full");
    }
}

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Writes_AtOrAboveTheMinimumLevel_InTheLegacyLayout()
    {
        var file = _dir.Combine("mRemoteNG.log");
        using (var provider = new RollingFileLoggerProvider(file, LogLevel.Information))
        {
            var logger = provider.CreateLogger("mRemoteNG.Core.Things");
            logger.LogDebug("hidden");
            logger.LogInformation("shown {Value}", 42);
            logger.LogError(new InvalidOperationException("boom"), "failed");

            provider.MinimumLevel = LogLevel.Debug;
            logger.LogDebug("now visible");
            provider.MinimumLevel = LogLevel.None;
            logger.LogError("off");
        }

        var lines = File.ReadAllText(file);
        lines.Should().NotContain("hidden").And.NotContain("off");
        lines.Should().MatchRegex(@"\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d{3} \[\d+\] INFO  - Things: shown 42");
        lines.Should().Contain("ERROR - Things: failed").And.Contain("InvalidOperationException: boom");
        lines.Should().Contain("DEBUG - Things: now visible");
    }

    [Fact]
    public void RollsBySize_KeepingTheConfiguredNumberOfOldFiles()
    {
        var file = _dir.Combine("app.log");
        using (var provider = new RollingFileLoggerProvider(file, LogLevel.Debug, maxFileSizeBytes: 2048, maxRollBackups: 2))
        {
            for (var i = 0; i < 200; i++)
                provider.Write(LogLevel.Information, "t", $"line {i} " + new string('x', 40));
        }

        File.Exists(file).Should().BeTrue();
        File.Exists(file + ".1").Should().BeTrue();
        File.Exists(file + ".2").Should().BeTrue();
        File.Exists(file + ".3").Should().BeFalse();
        new FileInfo(file).Length.Should().BeLessThanOrEqualTo(2048);
        File.ReadAllText(file).Should().Contain("line 199");
    }

    [Fact]
    public void ChangingTheFile_WritesToTheNewOne()
    {
        using var provider = new RollingFileLoggerProvider(_dir.Combine("a.log"));
        provider.Write(LogLevel.Warning, "", "first");
        provider.FilePath = _dir.Combine("sub", "b.log");
        provider.Write(LogLevel.Warning, "", "second");

        File.ReadAllText(_dir.Combine("a.log")).Should().Contain("first").And.NotContain("second");
        File.ReadAllText(_dir.Combine("sub", "b.log")).Should().Contain("WARN  - second");
    }
}

/// <summary>Portable mode changes process-wide paths, so these tests must not run in parallel with others using them.</summary>
[Collection(PortableModeCollection.Name)]
public sealed class PortableModeTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose()
    {
        AppDataLocation.Reset();
        _dir.Dispose();
    }

    [Fact]
    public void MarkerFile_EnablesPortableMode_AndRoutesAllDataNextToTheExecutable()
    {
        AppDataLocation.Initialize(_dir.Path, portableSwitch: false).Should().BeFalse();
        File.WriteAllText(_dir.Combine(AppDataLocation.PortableMarkerFileName), "");

        AppDataLocation.Initialize(_dir.Path, portableSwitch: false).Should().BeTrue();

        ApplicationPaths.IsPortable.Should().BeTrue();
        ApplicationPaths.SettingsDirectory.Should().Be(_dir.Path);
        ApplicationPaths.DefaultConnectionsFilePath.Should().Be(_dir.Combine("confCons.xml"));
        ApplicationPaths.DefaultLogFilePath.Should().Be(_dir.Combine("mRemoteNG.log"));
        new LinuxSettingsProvider().SettingsFilePath.Should().Be(_dir.Combine("settings.xml"));
        mRemoteNG.Protocols.Ssh.KnownHostsStore.CreateDefault().WritableFile.Should().Be(_dir.Combine("known_hosts"));
    }

    [Fact]
    public void PortableSwitch_EnablesPortableModeWithoutAMarker()
    {
        AppDataLocation.Initialize(_dir.Path, portableSwitch: true).Should().BeTrue();
        AppDataLocation.OverrideDirectory.Should().Be(_dir.Path);
    }

    [Fact]
    public void StoredPaths_InsideThePortableFolder_AreRelative()
    {
        AppDataLocation.UsePortableDirectory(_dir.Path);
        var inside = _dir.Combine("cons", "team.xml");

        ApplicationPaths.ToStoredPath(inside).Should().Be(Path.Combine("cons", "team.xml"));
        ApplicationPaths.FromStoredPath(Path.Combine("cons", "team.xml")).Should().Be(inside);
        ApplicationPaths.ToStoredPath("/elsewhere/x.xml").Should().Be(Path.GetFullPath("/elsewhere/x.xml"));
    }

    [Fact]
    public void StartupService_OpensThePortableConnectionFile()
    {
        AppDataLocation.UsePortableDirectory(_dir.Path);
        var settings = new AppSettingsService(new XmlFileSettingsProvider(_dir.Path));
        settings.Load();
        var consFile = _dir.Combine("confCons.xml");
        File.WriteAllText(consFile, "<x/>");

        var startup = new StartupService(settings);
        startup.GetFileToOpenAtStartup().Should().Be(consFile, "a portable copy opens its own confCons.xml");

        startup.RecordOpenFile(consFile);
        settings.Current.LastConnectionFilePath.Should().Be("confCons.xml");
        startup.GetFileToOpenAtStartup().Should().Be(consFile);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PortableModeCollection
{
    public const string Name = "Portable mode (process-wide paths)";
}
