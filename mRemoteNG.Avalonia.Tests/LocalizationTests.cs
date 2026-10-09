using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using FluentAssertions;
using mRemoteNG.Avalonia.Localization;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.OptionsPages;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;
using Xunit;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>The UI in another language: strings come from the WinForms app's translations.</summary>
public class LocalizationTests
{
    [AvaloniaFact]
    public void MainWindow_StartedInGerman_ShowsGermanMenus()
    {
        _ = TestHost.MainWindow;
        var settings = AppServices.GetRequired<AppSettingsService>();
        var previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            settings.Update(s => s.Language = "de");
            // What App.OnFrameworkInitializationCompleted does before it creates the main window.
            Localizer.ApplyUiCulture(settings.Current.Language);

            // No DataContext and not shown: only the XAML (menus) is of interest, the shared view model stays untouched.
            var window = new MainWindow();
            var menu = window.GetLogicalDescendants().OfType<Menu>().First();
            var topLevel = menu.Items.OfType<MenuItem>().ToList();

            topLevel[0].Header.Should().Be("_Datei", "the legacy German \"&Datei\" with its access key");
            topLevel.Select(i => i.Header).Should().Contain(["_Ansicht", "E_xtras", "_Hilfe"]);
            topLevel[0].Items.OfType<MenuItem>().Select(i => i.Header).Should().Contain("Neue Verbindung...");
            window.FindControl<MenuItem>("ViewMenu")!.Items.OfType<MenuItem>().Select(i => i.Header as string)
                .Should().Contain(h => h != null && h.StartsWith("Alle Ordner", StringComparison.Ordinal),
                    "\"Expand All Folders\" uses the legacy translation");
        }
        finally
        {
            settings.Update(s => s.Language = string.Empty);
            CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [AvaloniaFact]
    public void MainWindow_InEnglish_KeepsItsOwnWording()
    {
        var window = new MainWindow();
        var file = window.GetLogicalDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().First();

        file.Header.Should().Be("_File");
        file.Items.OfType<MenuItem>().Select(i => i.Header).Should()
            .Contain(["_New Connection File", "New _Connection...", "Save Connection File _As...", "E_xit"]);
    }

    [AvaloniaFact]
    public void AppearancePage_OffersEveryLanguage_AndStoresTheChoice()
    {
        var working = new AppSettings();
        var vm = new AppearanceSettingsViewModel(working);
        var page = new AppearanceSettingsPage { DataContext = vm };

        page.FindControl<ComboBox>("LanguageCombo")!.ItemsSource.Should().BeSameAs(vm.Languages);
        vm.Languages.Should().HaveCount(26);
        vm.SelectedLanguage.Name.Should().BeEmpty("System default until a language is chosen");

        vm.SelectedLanguage = vm.Languages.Single(l => l.Name == "ja-JP");
        working.Language.Should().Be("ja-JP");
        vm.LanguageRestartNote.Should().Contain("restarted");
    }

    [AvaloniaFact]
    public void EnumLabels_UseTheLegacyTranslations()
    {
        EnumLabelConverter.ResourceKeys.Values.Where(key => !Localizer.Exists(key)).Should().BeEmpty();

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Convert(RDPSounds.DoNotPlay).Should().Be("Do not play");
            Convert(RDPResolutions.FitToWindow).Should().Be("Fit To Window", "English keeps the app's labels");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            Convert(RDPSounds.DoNotPlay).Should().Be(Localizer.Get("DoNotPlay")).And.NotBe("Do not play");
            TrExtension.Resolve("Options", "...").Should().Be("Optionen...");
            TrExtension.Resolve("Connections", upper: true).Should().Be("VERBINDUNGEN");
            TrExtension.Resolve("ExpandAllFolders", english: "Expand All Folders").Should().NotBe("Expand All Folders");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        static object? Convert(object value) =>
            EnumLabelConverter.Instance.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
    }
}
