using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAssertions;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.DataProviders;
using mRemoteNG.Core.Config.DatabaseConnectors;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Security;
using Xunit;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>Themes, the theme editor, the storage options pages and the storage runtime, in the real app.</summary>
public class StorageAndThemeTests
{
    private static Color PaletteColor(string key)
    {
        TestHost.MainWindow.TryFindResource(key, out var value).Should().BeTrue();
        return ((ISolidColorBrush)value!).Color;
    }

    private static int PaletteCount() =>
        Application.Current!.Styles.Count(st => (st as IResourceProvider)?.TryGetResource("AppBg0Brush", null, out _) == true);

    [AvaloniaFact]
    public void LegacyThemes_RecolourASingleFreshPalette_AndDoNotLeakIntoTheDefaultOne()
    {
        _ = TestHost.MainWindow;
        try
        {
            ThemeService.Instance.ApplyTheme(ThemeCatalog.Darcula);
            Dispatcher.UIThread.RunJobs();
            PaletteCount().Should().Be(1);
            PaletteColor("AppBg0Brush").Should().Be(Color.Parse("#3c3f41"));
            PaletteColor("TextPrimaryBrush").Should().Be(Color.Parse("#bbbbbb"));
            Application.Current!.ActualThemeVariant.Should().Be(ThemeVariant.Dark);

            ThemeService.Instance.ApplyTheme(ThemeCatalog.Vs2015Blue);
            Dispatcher.UIThread.RunJobs();
            PaletteCount().Should().Be(1);
            PaletteColor("AppBg1Brush").Should().Be(Color.Parse("#d6dbe9"));
            Application.Current!.ActualThemeVariant.Should().Be(ThemeVariant.Light);
        }
        finally
        {
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
        }

        PaletteCount().Should().Be(1);
        PaletteColor("AppBg0Brush").Should().Be(Color.Parse(ThemeCatalog.Dark.Colors["AppBg0"]), "the plain dark palette is untouched");
    }

    [AvaloniaFact]
    public void ThemeEditor_PreviewsLive_SavesAUserTheme_AndAppliesIt()
    {
        _ = TestHost.MainWindow;
        var settings = AppServices.GetRequired<AppSettingsService>();
        try
        {
            var editor = new ThemeEditorViewModel(ThemeService.Instance, settings, ThemeCatalog.DarkName);
            editor.BaseTheme!.Name.Should().Be(ThemeCatalog.DarkName);

            editor.Colors.Single(c => c.Key == "AppBg0").Value = "#102030";
            Dispatcher.UIThread.RunJobs();
            PaletteColor("AppBg0Brush").Should().Be(Color.Parse("#102030"), "edits are previewed immediately");
            PaletteCount().Should().Be(1);

            editor.Name = ThemeCatalog.DarculaName;
            editor.Save().Should().BeFalse("built-in names are reserved");

            editor.Name = "Midnight test";
            editor.Colors.Single(c => c.Key == "Accent").Value = "#ff8800";
            editor.Save().Should().BeTrue(editor.Status);

            var file = Path.Combine(ThemeService.Instance.Catalog.UserThemesDirectory, "midnight_test.json");
            File.Exists(file).Should().BeTrue();
            settings.Current.ThemeName.Should().Be("Midnight test");
            ThemeService.Instance.Catalog.Find("Midnight test")!.Colors["AppBg0"].Should().Be("#102030");

            // Re-applying the settings (e.g. at the next start) shows the saved theme.
            ThemeService.Instance.Apply(ThemeMode.Light);
            ThemeService.Instance.ApplySettings(settings.Current);
            Dispatcher.UIThread.RunJobs();
            PaletteColor("AppBg0Brush").Should().Be(Color.Parse("#102030"));
            PaletteColor("AccentBrush").Should().Be(Color.Parse("#ff8800"));

            var appearance = new AppearanceSettingsViewModel(settings.CreateEditableCopy());
            appearance.Themes.Select(t => t.ThemeName).Should().Contain(["VS2015 Blue", "Darcula", "Midnight test"]);
            appearance.SelectedTheme.ThemeName.Should().Be("Midnight test");
        }
        finally
        {
            ThemeService.Instance.Catalog.Delete("Midnight test");
            settings.Update(s => s.ThemeName = string.Empty);
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void OptionsPages_PersistStorageSettings_AndEncryptPasswords()
    {
        _ = TestHost.MainWindow;
        var settings = AppServices.GetRequired<AppSettingsService>();
        var before = settings.CreateEditableCopy();
        try
        {
            var options = AppServices.GetRequired<OptionsWindowViewModel>();
            options.Categories.Select(c => c.Key).Should().Contain(["saving", "sql", "logging"]);

            options.SelectCategory("sql");
            var sql = options.CurrentPage.Should().BeOfType<SqlServerSettingsViewModel>().Subject;
            sql.UseSqlServer = true;
            sql.SelectedServerType = sql.ServerTypes.Single(t => t.Value == DatabaseServerType.MySql);
            sql.Host = "db.example.com:3307";
            sql.DatabaseName = "mremoteng";
            sql.Username = "mrng";
            sql.Password = "sql-secret";
            sql.ReadOnly = true;
            sql.UpdateCheckInterval = 10;

            options.SelectCategory("saving");
            var saving = (SavingSettingsViewModel)options.CurrentPage!;
            saving.AutoSaveEveryMinutes = 5;
            saving.SaveConnectionsOnEdit = true;
            saving.SelectedBackupFrequency = saving.BackupFrequencies.Single(f => f.Value == BackupFrequency.OnExit);
            saving.BackupKeepCount = 3;
            saving.BackupDirectory = "/tmp/mrng-backups";
            saving.ExampleBackupName.Should().Contain("confCons.xml.").And.Contain(".backup").And.Contain("backup folder");

            options.SelectCategory("logging");
            var logging = (LoggingSettingsViewModel)options.CurrentPage!;
            logging.SelectedLevel = logging.Levels.Single(l => l.Value == LogFileLevel.Debug);

            options.Updates.UseProxy = true;
            options.Updates.ProxyAddress = "proxy.corp";
            options.Updates.ProxyPort = 3128;
            options.Updates.ProxyPassword = "proxy-secret";
            options.General.StartMinimized = true;

            options.OnApply().Should().BeTrue(options.ValidationMessage);

            // Reload from disk = next start.
            var reloaded = new AppSettingsService(settings.Provider);
            settings.Provider.Reload();
            reloaded.Load();
            var current = reloaded.Current;
            current.UseSqlServer.Should().BeTrue();
            current.SqlServerType.Should().Be(DatabaseServerType.MySql);
            current.SqlHost.Should().Be("db.example.com:3307");
            current.SqlReadOnly.Should().BeTrue();
            current.SqlUpdateCheckIntervalSeconds.Should().Be(10);
            current.AutoSaveEveryMinutes.Should().Be(5);
            current.SaveConnectionsOnEdit.Should().BeTrue();
            current.BackupFrequency.Should().Be(BackupFrequency.OnExit);
            current.BackupKeepCount.Should().Be(3);
            current.LogLevel.Should().Be(LogFileLevel.Debug);
            current.UpdateProxyAddress.Should().Be("proxy.corp");
            current.StartMinimized.Should().BeTrue();

            var crypto = AppServices.GetRequired<ICryptoProvider>();
            current.SqlPasswordProtected.Should().NotContain("sql-secret");
            crypto.Unprotect(current.SqlPasswordProtected).Should().Be("sql-secret");
            File.ReadAllText(settings.Provider.SettingsFilePath).Should().NotContain("sql-secret").And.NotContain("proxy-secret");

            var reopened = AppServices.GetRequired<OptionsWindowViewModel>();
            reopened.SqlServer.Password.Should().Be("sql-secret", "the page shows the decrypted password");
            reopened.Updates.ProxyPassword.Should().Be("proxy-secret");
        }
        finally
        {
            settings.Apply(before).Should().BeEmpty();
        }
    }

    [AvaloniaFact]
    public void StorageRuntime_AppliesBackupSettings_AndAutoSavesAfterAnEdit()
    {
        _ = TestHost.MainWindow;
        var settings = AppServices.GetRequired<AppSettingsService>();
        var connections = AppServices.GetRequired<ConnectionsService>();
        var tree = TestHost.ViewModel.ConnectionTree;
        var runtime = AppServices.GetRequired<StorageRuntime>();
        var before = settings.CreateEditableCopy();
        var dir = Path.Combine(TestHost.ConfigDirectory, "autosave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "confCons.xml");
        try
        {
            runtime.Attach();
            settings.Update(s =>
            {
                s.SaveConnectionsOnEdit = true;
                s.BackupFrequency = BackupFrequency.OnSave;
                s.BackupKeepCount = 2;
                s.BackupDirectory = Path.Combine(dir, "backups");
            });
            connections.BackupFrequency.Should().Be(BackupFrequency.OnSave);
            connections.BackupOptions.KeepCount.Should().Be(2);

            tree.CreateNewTree();
            tree.SaveToFile(file);
            tree.AddConnection("autosaved", Core.Connection.Protocol.ProtocolType.SSH2, "h1");
            tree.IsDirty.Should().BeTrue();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (tree.IsDirty && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
            }

            tree.IsDirty.Should().BeFalse("save-on-edit saved the change");
            File.ReadAllText(file).Should().Contain("autosaved");
            FileBackupCreator.GetBackups(file, connections.BackupOptions).Should().ContainSingle("the previous version was backed up");
        }
        finally
        {
            settings.Apply(before);
            tree.CreateNewTree();
        }
    }
}
