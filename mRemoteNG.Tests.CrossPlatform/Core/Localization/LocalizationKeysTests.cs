using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Localization;

/// <summary>
/// Every resource key the cross-platform app refers to must exist in English (Localization/Strings.resx or the
/// WinForms app's Language.resx). Scans the Avalonia views for {l:Tr ...} and the C# sources for Localizer calls.
/// </summary>
public partial class LocalizationKeysTests
{
    // {l:Tr Key...} or {l:Tr Key=Key...}
    [GeneratedRegex(@"\{l:Tr\s+(?:Key\s*=\s*)?([A-Za-z0-9_]+)")]
    private static partial Regex XamlKey();

    // Localizer.Get("Key" / Menu / Format / FormatOr, TrExtension.Resolve("Key"
    [GeneratedRegex(@"(?:Localizer\.(?:Get|Menu|Format|FormatOr|IsTranslated|GetRaw)|TrExtension\.Resolve)\(\s*""([A-Za-z0-9_]+)""")]
    private static partial Regex CodeKey();

    // Localizer.Format(condition ? "KeyA" : "KeyB", ...)
    [GeneratedRegex(@"Localizer\.(?:Get|Menu|Format)\([^;]*?\?\s*""([A-Za-z0-9_]+)""\s*:\s*""([A-Za-z0-9_]+)""")]
    private static partial Regex ConditionalCodeKey();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mRemoteNG.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> SourceFiles(string project, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), project), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .Where(d => d.Attribute("type") is null)
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")?.Value ?? string.Empty);

    [Fact]
    public void EveryKeyUsedInTheViews_Exists()
    {
        var used = SourceFiles("mRemoteNG.Avalonia", "*.axaml")
            .SelectMany(file => XamlKey().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Key: m.Groups[1].Value)))
            .ToList();

        used.Should().HaveCountGreaterThan(400, "the views use localized strings throughout");
        used.Where(u => !Localizer.Exists(u.Key)).Select(u => $"{u.File}: {u.Key}").Should().BeEmpty();
    }

    [Fact]
    public void EveryKeyUsedInCode_Exists()
    {
        var used = SourceFiles("mRemoteNG.Avalonia", "*.cs").Concat(SourceFiles("mRemoteNG.Core", "*.cs"))
            .SelectMany(file =>
            {
                var text = File.ReadAllText(file);
                return CodeKey().Matches(text).Select(m => m.Groups[1].Value)
                    .Concat(ConditionalCodeKey().Matches(text).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }))
                    .Select(key => (File: Path.GetFileName(file), Key: key));
            })
            .ToList();

        used.Should().HaveCountGreaterThan(200);
        used.Where(u => !Localizer.Exists(u.Key)).Select(u => $"{u.File}: {u.Key}").Should().BeEmpty();
    }

    [Fact]
    public void ConnectionPropertyKeys_Exist()
    {
        foreach (var descriptor in ConnectionPropertyCatalog.All)
        {
            if (descriptor.DisplayNameKey is { } name)
                Localizer.Exists(name).Should().BeTrue($"{descriptor.Name} label key {name}");
            if (descriptor.DescriptionKey is { } description)
                Localizer.Exists(description).Should().BeTrue($"{descriptor.Name} description key {description}");
            ConnectionPropertyCategories.GetDisplayName(descriptor.Section).Should().NotBeNullOrWhiteSpace();
        }

        var german = CultureInfo.GetCultureInfo("de");
        ConnectionPropertyCatalog.All.Count(d => d.DisplayNameKey is { } key && Localizer.IsTranslated(key, german))
            .Should().BeGreaterThan(60, "most property names have a legacy German translation");
    }

    [Fact]
    public void AppStrings_DoNotReuseLegacyKeys_AndAreWellFormed()
    {
        var root = RepoRoot();
        var app = ReadResx(Path.Combine(root, "mRemoteNG.Core", "Localization", "Strings.resx"));
        var legacy = ReadResx(Path.Combine(root, "mRemoteNG", "Language", "Language.resx"));

        app.Keys.Intersect(legacy.Keys).Should().BeEmpty(
            "the app's strings are searched first and would hide the legacy translations of that key");
        app.Values.Should().NotContain(string.Empty);

        // Format strings must format (balanced braces), so Localizer.Format never falls into its error path.
        foreach (var (key, value) in app.Where(p => p.Key.EndsWith("Format", StringComparison.Ordinal)))
        {
            var format = () => string.Format(CultureInfo.InvariantCulture, value, 1, 2, 3, 4);
            format.Should().NotThrow($"{key} is a format string");
        }
    }

    [Fact]
    public void LegacyResources_AreLinked_NotCopied()
    {
        var root = RepoRoot();
        Directory.EnumerateFiles(Path.Combine(root, "mRemoteNG.Core"), "Language*.resx", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Should().BeEmpty("the WinForms app's translations are linked from mRemoteNG/Language");
        File.ReadAllText(Path.Combine(root, "mRemoteNG.Core", "mRemoteNG.Core.csproj"))
            .Should().Contain(@"..\mRemoteNG\Language\Language*.resx");
    }
}
