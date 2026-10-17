using System.Text;

namespace mRemoteNG.Protocols.Ssh.Terminal;

/// <summary>
/// Display width of a character in terminal cells, following Unicode Standard Annex #11
/// (East Asian Width) the way xterm and wcwidth do: Wide and Fullwidth characters take two
/// cells, everything else that the terminal prints takes one. Ambiguous characters stay one
/// cell (the usual Western-locale default).
/// </summary>
public static class DisplayWidth
{
    public static int Of(Rune rune)
    {
        var value = rune.Value;
        if (value < 0x20 || (value >= 0x7F && value < 0xA0))
            return 0;
        return IsWide(value) ? 2 : 1;
    }

    /// <summary>Wide (W) and Fullwidth (F) ranges, plus the emoji blocks terminals draw at double width.</summary>
    private static bool IsWide(int value) => value switch
    {
        >= 0x1100 and <= 0x115F => true, // Hangul Jamo leading consonants
        0x2329 or 0x232A => true,         // left/right-pointing angle brackets
        >= 0x2E80 and <= 0xA4CF when value != 0x303F => true, // CJK radicals through Yi
        >= 0xAC00 and <= 0xD7A3 => true, // Hangul syllables
        >= 0xF900 and <= 0xFAFF => true, // CJK compatibility ideographs
        >= 0xFE10 and <= 0xFE19 => true, // vertical forms
        >= 0xFE30 and <= 0xFE6F => true, // CJK compatibility forms
        >= 0xFF00 and <= 0xFF60 => true, // fullwidth ASCII
        >= 0xFFE0 and <= 0xFFE6 => true, // fullwidth symbols
        >= 0x1F300 and <= 0x1F64F => true, // miscellaneous symbols and emoticons
        >= 0x1F680 and <= 0x1F6FF => true, // transport and map symbols
        >= 0x1F900 and <= 0x1F9FF => true, // supplemental symbols
        >= 0x1FA00 and <= 0x1FAFF => true,
        >= 0x20000 and <= 0x3FFFD => true, // CJK extension B and later
        _ => false,
    };
}
