using System.Collections;
using System.Globalization;
using System.Resources;
using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Localization;

public class LocalizerTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    [Fact]
    public void Get_ReturnsTheEnglishText_ForTheInvariantCulture()
    {
        Localizer.Get("NewConnection", CultureInfo.InvariantCulture).Should().Be("New Connection");
        Localizer.Get("LanguageSystemDefault", CultureInfo.InvariantCulture).Should().Be("System default");
    }

    [Fact]
    public void Get_ReturnsTheLegacyTranslation()
    {
        Localizer.Get("NewConnection", German).Should().Be("Neue Verbindung");
        Localizer.Get("Options", Japanese).Should().Be("オプション");
    }

    [Fact]
    public void Get_UsesTheParentCulture_AndTheShippedRegionalCultureOfALanguage()
    {
        Localizer.Get("NewConnection", CultureInfo.GetCultureInfo("de-AT")).Should().Be("Neue Verbindung");
        Localizer.Get("Options", CultureInfo.GetCultureInfo("ja")).Should().Be("オプション", "only ja-JP ships; it serves plain ja");
    }

    [Fact]
    public void Get_FallsBackToEnglish_WhenTheCultureHasNoTranslation()
    {
        // fi-FI translates only a handful of strings; the app's own strings exist in English only.
        Localizer.IsTranslated("NewConnection", CultureInfo.GetCultureInfo("fi-FI")).Should().BeFalse();
        Localizer.Get("NewConnection", CultureInfo.GetCultureInfo("fi-FI")).Should().Be("New Connection");
        Localizer.Get("LanguageSystemDefault", German).Should().Be("System default");
    }

    [Fact]
    public void Lookup_TriesTheAppStringsBeforeTheLegacyOnes_ForEachCulture()
    {
        var app = new DictionaryResourceManager(new()
        {
            [""] = new() { ["Key"] = "app-en", ["AppOnly"] = "app-only-en" },
            ["de"] = new() { ["Key"] = "app-de" },
        });
        var legacy = new DictionaryResourceManager(new()
        {
            [""] = new() { ["Key"] = "legacy-en", ["LegacyOnly"] = "legacy-only-en" },
            ["de"] = new() { ["Key"] = "legacy-de", ["LegacyOnly"] = "legacy-only-de" },
            ["de-AT"] = new() { ["Key"] = "legacy-de-AT" },
        });
        ResourceManager[] sources = [app, legacy];

        Localizer.Lookup("Key", German, null, sources).Should().Be("app-de", "the app's strings come first");
        Localizer.Lookup("Key", CultureInfo.GetCultureInfo("de-AT"), null, sources).Should()
            .Be("legacy-de-AT", "a more specific culture wins, whichever set has it");
        Localizer.Lookup("LegacyOnly", German, null, sources).Should().Be("legacy-only-de");
        Localizer.Lookup("AppOnly", German, null, sources).Should().Be("app-only-en", "untranslated: English");
        Localizer.Lookup("Key", CultureInfo.GetCultureInfo("fr"), null, sources).Should().Be("app-en");
        Localizer.Lookup("Key", CultureInfo.GetCultureInfo("fr"), "own English", sources).Should().Be("own English");
        Localizer.Lookup("Key", German, "own English", sources).Should().Be("app-de", "a translation beats the English override");
        Localizer.Lookup("Missing", German, null, sources).Should().BeNull();
    }

    [Fact]
    public void Get_DropsATrailingColon_ThatOnlyTheTranslationHas()
    {
        // German "SQL-Server:" (a field label in the WinForms app) for the English "SQL Server".
        Localizer.Get("SQLServer", German).Should().Be("SQL-Server");
        Localizer.Get("Hostname", German).Should().EndWith(":", "the English \"Hostname:\" has a colon too");
    }

    [Fact]
    public void MissingKeys_ReturnTheKey_AndNeverThrow()
    {
        Localizer.Exists("NoSuchKey_42").Should().BeFalse();
        Localizer.Get("NoSuchKey_42", German).Should().Be("NoSuchKey_42");
        Localizer.Get(string.Empty, German).Should().BeEmpty();
        Localizer.Format("NoSuchKey_42", 1).Should().Be("NoSuchKey_42");
        Localizer.Get("NoSuchKey_42", "English text").Should().Be("English text");
    }

    [Fact]
    public void GetWithEnglish_KeepsTheAppsEnglishWording_ButUsesTranslations()
    {
        WithUiCulture(CultureInfo.InvariantCulture,
            () => Localizer.Get("CollapseAllFolders", "Collapse All Folders").Should().Be("Collapse All Folders"));
        WithUiCulture(German,
            () => Localizer.Get("CollapseAllFolders", "Collapse All Folders").Should().NotBe("Collapse All Folders"));
    }

    [Fact]
    public void Get_RemovesAccessKeys_AndMenu_ConvertsThemToAvalonia()
    {
        WithUiCulture(German, () =>
        {
            Localizer.Get("_File").Should().Be("Datei");
            Localizer.Menu("_File").Should().Be("_Datei");
        });
        WithUiCulture(Japanese, () => Localizer.Menu("_File").Should().Be("ファイル(_F)"));
        WithUiCulture(CultureInfo.InvariantCulture, () =>
        {
            Localizer.Menu("_File").Should().Be("_File");
            Localizer.Get("TabsAndPanels").Should().Be("Tabs & Panels", "the legacy text is \"Tabs && Panels\"");
        });
    }

    [Theory]
    [InlineData("&File", "_File")]
    [InlineData("E&xit", "E_xit")]
    [InlineData("Tabs && Panels", "Tabs & Panels")]
    [InlineData("Tabs & Panels", "Tabs & Panels")]
    [InlineData("Save_As", "Save__As")]
    [InlineData("ファイル(&F)", "ファイル(_F)")]
    [InlineData("Trailing &", "Trailing &")]
    [InlineData("Plain", "Plain")]
    public void ToAccessKeyText_ConvertsWinFormsMarkup(string text, string expected) =>
        Localizer.ToAccessKeyText(text).Should().Be(expected);

    [Theory]
    [InlineData("&File", "File")]
    [InlineData("Tabs && Panels", "Tabs & Panels")]
    [InlineData("Tabs & Panels", "Tabs & Panels")]
    [InlineData("Save_As", "Save_As")]
    [InlineData("ファイル(&F)", "ファイル")]
    [InlineData("New (&N)", "New")]
    public void StripAccessKey_RemovesTheMarker(string text, string expected) =>
        Localizer.StripAccessKey(text).Should().Be(expected);

    [Fact]
    public void Format_FillsPlaceholders_InTheCurrentLanguage()
    {
        WithUiCulture(German, () =>
            Localizer.Format("LanguageRestartRequired", "mRemoteNG").Should().StartWith("mRemoteNG muss neu gestartet werden"));
        WithUiCulture(CultureInfo.InvariantCulture, () =>
        {
            Localizer.Format("ActiveConnectionsFormat", 3).Should().Be("Active connections: 3");
            Localizer.FormatOr("ConfirmDeleteNodeConnection", "Delete the connection \"{0}\"?", "web").Should()
                .Be("Delete the connection \"web\"?");
        });
    }

    [Fact]
    public void EverySupportedCulture_HasASatelliteAssembly()
    {
        var legacy = new ResourceManager("mRemoteNG.Core.Localization.Legacy.Language", typeof(Localizer).Assembly);
        foreach (var name in Localizer.SupportedCultureNames.Where(n => n != "en"))
        {
            legacy.GetResourceSet(CultureInfo.GetCultureInfo(name), createIfNotExists: true, tryParents: false)
                .Should().NotBeNull($"the {name} translation is built as a satellite assembly");
        }
        Localizer.SupportedCultureNames.Should().HaveCount(25, "English plus the 24 translations of the WinForms app");
    }

    [Fact]
    public void LanguageOptions_StartWithSystemDefault_AndUseNativeNames()
    {
        var options = WithUiCulture(CultureInfo.InvariantCulture, Localizer.GetLanguageOptions);

        options.Should().HaveCount(26);
        options[0].Should().Be(new LanguageOption(string.Empty, "System default"));
        options.Should().Contain(new LanguageOption("de", "Deutsch"));
        options.Should().Contain(o => o.Name == "ja-JP" && o.DisplayName.StartsWith("日本語"));
        options.Select(o => o.Name).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("DE", true)]
    [InlineData("zh-TW", true)]
    [InlineData("", false)]
    [InlineData("xx-YY", false)]
    [InlineData(null, false)]
    public void IsSupported_AcceptsTheShippedCultures(string? name, bool expected) =>
        Localizer.IsSupported(name).Should().Be(expected);

    [Fact]
    public void ConnectionPropertyLabels_AreTranslated_ButKeepTheirEnglishWording()
    {
        var hostname = ConnectionPropertyCatalog.Get(nameof(ConnectionInfo.Hostname));

        WithUiCulture(CultureInfo.InvariantCulture, () =>
        {
            hostname.DisplayName.Should().Be("Host name / IP");
            ConnectionPropertyCategories.GetDisplayName("RD Gateway").Should().Be("RD Gateway");
        });
        WithUiCulture(German, () =>
        {
            hostname.DisplayName.Should().Be(Localizer.Get("HostnameIp", German));
            hostname.Description.Should().Be(Localizer.Get("PropertyDescriptionHostnameIp", German));
            ConnectionPropertyCategories.GetDisplayName(ConnectionPropertyCategories.Display).Should().Be("Anzeige");
        });
    }

    /// <summary>In-memory resources: culture name ("" for neutral) → key → text.</summary>
    private sealed class DictionaryResourceManager(Dictionary<string, Dictionary<string, string>> cultures) : ResourceManager
    {
        public override ResourceSet? GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents) =>
            cultures.TryGetValue(culture.Name, out var strings) ? new ResourceSet(new Reader(strings)) : null;

        private sealed class Reader(Dictionary<string, string> strings) : IResourceReader
        {
            public IDictionaryEnumerator GetEnumerator() => new System.Collections.Hashtable(strings).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Close()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    internal static void WithUiCulture(CultureInfo culture, Action action) =>
        WithUiCulture(culture, () =>
        {
            action();
            return 0;
        });

    internal static T WithUiCulture<T>(CultureInfo culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}

/// <summary>Tests that change the process-wide default UI culture; they must not run alongside others.</summary>
[CollectionDefinition(nameof(UiCultureCollection), DisableParallelization = true)]
public sealed class UiCultureCollection;

[Collection(nameof(UiCultureCollection))]
public class LocalizerApplyUiCultureTests
{
    [Fact]
    public void ApplyUiCulture_SetsTheUiCulture_AndIgnoresUnknownNames()
    {
        var previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            Localizer.ApplyUiCulture("de").Name.Should().Be("de");
            CultureInfo.DefaultThreadCurrentUICulture!.Name.Should().Be("de");
            Localizer.Get("NewConnection").Should().Be("Neue Verbindung");

            Localizer.ApplyUiCulture("xx-YY").Name.Should().Be("de", "an unknown name keeps the current language");
            Localizer.ApplyUiCulture(string.Empty).Name.Should().Be("de", "empty means the system language: nothing to change");
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
