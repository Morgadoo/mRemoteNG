using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;

namespace mRemoteNG.Core.Localization;

/// <summary>A language offered in Options (<see cref="Name"/> is empty for "System default").</summary>
public sealed record LanguageOption(string Name, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Looks up user-facing strings for the current UI culture.
/// <para>
/// Two resource sets are searched: the cross-platform app's own <c>Localization/Strings.resx</c> (strings that
/// are new in this app, English only for now) and the WinForms app's <c>mRemoteNG/Language/Language*.resx</c>
/// (English plus 24 translations), which are linked into this assembly so both apps share one set of
/// translations. For each culture of the chain (e.g. de-AT, de) the app's set is tried first and then the
/// legacy one; when no culture has the key, the English (neutral) text is used. A key nobody defines
/// returns the key itself: a missing string never throws.
/// </para>
/// <para>
/// Strings may mark an access key with <c>&amp;</c> (the WinForms convention, <c>&amp;&amp;</c> for a literal
/// ampersand). <see cref="Get(string)"/> removes the marker; <see cref="Menu"/> converts it to Avalonia's
/// <c>_</c>.
/// </para>
/// </summary>
public static partial class Localizer
{
    private const string AppResources = "mRemoteNG.Core.Localization.Strings";
    private const string LegacyResources = "mRemoteNG.Core.Localization.Legacy.Language";

    private static readonly ResourceManager[] Sources =
    [
        new(AppResources, typeof(Localizer).Assembly),
        new(LegacyResources, typeof(Localizer).Assembly),
    ];

    /// <summary>The translations shipped with the app (the legacy app's list), English first.</summary>
    public static IReadOnlyList<string> SupportedCultureNames { get; } =
    [
        "en", "cs-CZ", "de", "el", "es", "es-AR", "fi-FI", "fr", "hu", "it", "ja-JP", "ko-KR", "lt", "nb-NO",
        "nl", "pl", "pt", "pt-BR", "ru", "sv-SE", "ta", "tr-TR", "uk", "zh-CN", "zh-TW",
    ];

    /// <summary>True when <paramref name="name"/> is one of <see cref="SupportedCultureNames"/> (case-insensitive).</summary>
    public static bool IsSupported(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && SupportedCultureNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "System default" followed by every supported language under its own (native) name, as offered in Options.
    /// </summary>
    public static IReadOnlyList<LanguageOption> GetLanguageOptions()
    {
        var options = new List<LanguageOption> { new(string.Empty, Get("LanguageSystemDefault")) };
        foreach (var name in SupportedCultureNames)
        {
            var culture = CultureInfo.GetCultureInfo(name);
            var native = culture.NativeName;
            if (native.Length > 0)
                native = culture.TextInfo.ToUpper(native[0]) + native[1..];
            options.Add(new LanguageOption(culture.Name, native));
        }
        return options;
    }

    /// <summary>
    /// Makes <paramref name="cultureName"/> the UI culture of this thread and of threads started later.
    /// Empty or unsupported names keep the operating system's language. Call before any window is created:
    /// strings are looked up when a view is loaded, so a change shows after a restart.
    /// </summary>
    public static CultureInfo ApplyUiCulture(string? cultureName)
    {
        if (IsSupported(cultureName))
        {
            var culture = CultureInfo.GetCultureInfo(cultureName!.Trim());
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        return CultureInfo.CurrentUICulture;
    }

    /// <summary>The text of <paramref name="key"/> in the current UI culture, without access-key markers.</summary>
    public static string Get(string key) => StripAccessKey(GetRaw(key, CultureInfo.CurrentUICulture));

    /// <summary>
    /// Like <see cref="Get(string)"/>, but <paramref name="english"/> replaces the neutral (English) text: the
    /// key's translation is used when the current culture has one, otherwise <paramref name="english"/>.
    /// Used where the cross-platform app words something differently in English but the legacy translation
    /// still fits (connection property names and descriptions, enum values).
    /// </summary>
    public static string Get(string key, string english) =>
        StripAccessKey(Translate(key, CultureInfo.CurrentUICulture, english) ?? english);

    /// <summary><see cref="Get(string)"/> for an explicit culture.</summary>
    public static string Get(string key, CultureInfo culture) => StripAccessKey(GetRaw(key, culture));

    /// <summary>The text of <paramref name="key"/> for a menu or button header: <c>&amp;</c> becomes Avalonia's <c>_</c>.</summary>
    public static string Menu(string key) => ToAccessKeyText(GetRaw(key, CultureInfo.CurrentUICulture));

    /// <summary>
    /// <see cref="string.Format(IFormatProvider, string, object[])"/> with the text of <paramref name="key"/>.
    /// A translation with broken placeholders falls back to the English text rather than throwing.
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            var english = StripAccessKey(GetRaw(key, CultureInfo.InvariantCulture));
            try
            {
                return string.Format(CultureInfo.CurrentCulture, english, args);
            }
            catch (FormatException)
            {
                return english;
            }
        }
    }

    /// <summary>
    /// <see cref="Format(string, object[])"/> with the translation of <paramref name="key"/> when the current
    /// culture has one, otherwise with <paramref name="english"/> (see <see cref="Get(string, string)"/>).
    /// </summary>
    public static string FormatOr(string key, string english, params object?[] args)
    {
        var template = Get(key, english);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, english, args);
        }
    }

    /// <summary>The stored text (access-key markers kept); the key itself when no resource defines it.</summary>
    public static string GetRaw(string key, CultureInfo culture) => Translate(key, culture, english: null) ?? key;

    /// <summary>True when the app's or the legacy English resources define <paramref name="key"/>.</summary>
    public static bool Exists(string key) => Lookup(key, CultureInfo.InvariantCulture, english: null) is not null;

    /// <summary>
    /// True when <paramref name="culture"/> (or a parent culture) has its own translation of <paramref name="key"/>,
    /// i.e. the English text is not used.
    /// </summary>
    public static bool IsTranslated(string key, CultureInfo culture) =>
        CultureChain(culture).Any(c => Sources.Any(source => TryGetString(source, c, key) is not null));

    /// <summary>
    /// Converts WinForms access-key markup to Avalonia's: <c>&amp;F</c> becomes <c>_F</c>, <c>&amp;&amp;</c> a literal
    /// <c>&amp;</c>, and a literal <c>_</c> is doubled so Avalonia does not take it as an access key.
    /// An <c>&amp;</c> that is not followed by a letter or digit (e.g. "Tabs &amp; Panels") stays as it is.
    /// </summary>
    public static string ToAccessKeyText(string text)
    {
        if (text.IndexOf('&') < 0 && text.IndexOf('_') < 0)
            return text;

        var result = new StringBuilder(text.Length + 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '_')
            {
                result.Append("__");
            }
            else if (c == '&' && i + 1 < text.Length && text[i + 1] == '&')
            {
                result.Append('&');
                i++;
            }
            else if (c == '&' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
            {
                result.Append('_');
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Removes WinForms access-key markers: <c>&amp;F</c> becomes <c>F</c>, <c>&amp;&amp;</c> a literal <c>&amp;</c>,
    /// and an appended "(&amp;F)" (the CJK convention) is removed.
    /// </summary>
    public static string StripAccessKey(string text)
    {
        if (text.IndexOf('&') < 0)
            return text;

        // CJK translations append the access key in parentheses ("ファイル(&F)"): drop it entirely.
        text = AppendedAccessKey().Replace(text, string.Empty);

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '&' && i + 1 < text.Length && text[i + 1] == '&')
            {
                result.Append('&');
                i++;
            }
            else if (c == '&' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
            {
                // Access-key marker: drop it.
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    private static string? Lookup(string key, CultureInfo culture, string? english) =>
        Lookup(key, culture, english, Sources);

    /// <summary>
    /// <see cref="Lookup(string, CultureInfo, string?)"/>, dropping a trailing colon that a translation added
    /// where the English text has none (some legacy translations of e.g. "SQL Server" are field labels).
    /// </summary>
    private static string? Translate(string key, CultureInfo culture, string? english)
    {
        var text = Lookup(key, culture, english);
        if (text is null || !EndsWithColon(text))
            return text;
        var neutral = english ?? Lookup(key, CultureInfo.InvariantCulture, english: null);
        return neutral is null || EndsWithColon(neutral) ? text : text.TrimEnd().TrimEnd(':', '：').TrimEnd();
    }

    private static bool EndsWithColon(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith(':') || trimmed.EndsWith('：');
    }

    /// <summary>The lookup order, with the resource sets passed in (for tests): app first, then legacy.</summary>
    internal static string? Lookup(string key, CultureInfo culture, string? english, IReadOnlyList<ResourceManager> sources)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        foreach (var c in CultureChain(culture))
        {
            foreach (var source in sources)
            {
                if (TryGetString(source, c, key) is { } text)
                    return text;
            }
        }

        if (english is not null)
            return english;

        foreach (var source in sources)
        {
            if (TryGetString(source, CultureInfo.InvariantCulture, key) is { } text)
                return text;
        }
        return null;
    }

    /// <summary>
    /// The specific-to-neutral cultures searched for <paramref name="culture"/> (never the invariant culture),
    /// plus the shipped culture of the same language when only a regional one exists (e.g. "ja" uses "ja-JP").
    /// Empty for English, the neutral language (no satellite assemblies).
    /// </summary>
    private static IEnumerable<CultureInfo> CultureChain(CultureInfo culture)
    {
        var language = culture.TwoLetterISOLanguageName;
        if (string.IsNullOrEmpty(culture.Name) || language == "en")
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (seen.Add(c.Name))
                yield return c;
        }

        foreach (var name in SupportedCultureNames)
        {
            if (!seen.Contains(name) && name.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
            {
                yield return CultureInfo.GetCultureInfo(name);
                yield break;
            }
        }
    }

    private static string? TryGetString(ResourceManager source, CultureInfo culture, string key)
    {
        try
        {
            var text = source.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is MissingManifestResourceException or MissingSatelliteAssemblyException
                                       or InvalidOperationException)
        {
            // No resources for this culture, or a non-string resource under the key.
            return null;
        }
    }

    [GeneratedRegex(@"\s?[(（]&[A-Za-z0-9][)）]")]
    private static partial Regex AppendedAccessKey();
}
