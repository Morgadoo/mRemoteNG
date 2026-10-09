using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Localization;

/// <summary>
/// XAML markup extension for a localized string: <c>{l:Tr NewConnection}</c> or
/// <c>{l:Tr Key=Options, Suffix='...'}</c>, with <c>xmlns:l="using:mRemoteNG.Avalonia.Localization"</c>.
/// The text is resolved once, when the view loads, in the current UI culture (see <see cref="Localizer"/>);
/// a language change takes effect after a restart.
/// <para>
/// On a <see cref="MenuItem"/> header the resource's access key (<c>&amp;</c>) becomes Avalonia's <c>_</c>;
/// elsewhere it is removed unless <see cref="AccessKey"/> is set (buttons, check boxes).
/// </para>
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    /// <summary>Resource key (in mRemoteNG.Core/Localization/Strings.resx or the legacy Language.resx).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Text appended after the translation, e.g. "..." or ":".</summary>
    public string? Suffix { get; set; }

    /// <summary>
    /// The English wording of this app where it differs from the resource's English text (e.g. title case,
    /// another access key), written like any Avalonia header (<c>_</c> marks the access key). Used when the
    /// current language has no translation of <see cref="Key"/>; a translation always wins.
    /// </summary>
    public string? English { get; set; }

    /// <summary>Upper-case the text (in the current culture), for headings shown in capitals.</summary>
    public bool Upper { get; set; }

    /// <summary>
    /// True to keep the access key (as <c>_</c>), false to remove it; unset keeps it only for menu items.
    /// </summary>
    public bool? AccessKey { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Resolve(Key, Suffix, AccessKey ?? IsMenuHeader(serviceProvider), English, Upper);

    /// <summary>The text <see cref="ProvideValue"/> produces, for code-behind and tests.</summary>
    public static string Resolve(string key, string? suffix = null, bool accessKey = false, string? english = null,
        bool upper = false)
    {
        string text;
        if (english is not null && !Localizer.IsTranslated(key, CultureInfo.CurrentUICulture))
            text = accessKey ? english : StripUnderscoreAccessKey(english);
        else
            text = accessKey ? Localizer.Menu(key) : Localizer.Get(key);

        if (!string.IsNullOrEmpty(suffix))
            text += suffix;
        return upper ? text.ToUpper(CultureInfo.CurrentUICulture) : text;
    }

    /// <summary>Removes Avalonia access-key markers: <c>_F</c> becomes <c>F</c>, <c>__</c> a literal <c>_</c>.</summary>
    public static string StripUnderscoreAccessKey(string text)
    {
        if (text.IndexOf('_') < 0)
            return text;

        var result = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '_')
                result.Append(text[i]);
            else if (i + 1 < text.Length && text[i + 1] == '_')
                result.Append(text[++i]);
        }
        return result.ToString();
    }

    private static bool IsMenuHeader(IServiceProvider serviceProvider)
    {
        try
        {
            return serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget
            {
                TargetObject: MenuItem,
            } target && target.TargetProperty is global::Avalonia.AvaloniaProperty { Name: "Header" };
        }
        catch (Exception)
        {
            // Designer or an unusual host: plain text.
            return false;
        }
    }
}
