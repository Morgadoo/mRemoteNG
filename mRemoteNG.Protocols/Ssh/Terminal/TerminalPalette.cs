namespace mRemoteNG.Protocols.Ssh.Terminal;

/// <summary>
/// Resolves <see cref="TerminalColor"/> values to 0xRRGGBB using the xterm 256-colour layout:
/// 16 ANSI colours, a 6×6×6 colour cube and a 24-step grey ramp.
/// </summary>
public static class TerminalPalette
{
    public const uint DefaultForeground = 0xD4D4D4;
    public const uint DefaultBackground = 0x1E1E1E;

    private static readonly uint[] Ansi16 =
    [
        0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF,
    ];

    private static readonly uint[] Table = BuildTable();

    /// <summary>Returns 0xRRGGBB for a palette index (0–255).</summary>
    public static uint FromIndex(int index) => Table[Math.Clamp(index, 0, 255)];

    public static uint Resolve(TerminalColor color, bool foreground) => color.Kind switch
    {
        TerminalColorKind.Indexed => FromIndex((int)color.Value),
        TerminalColorKind.Rgb => color.Value,
        _ => foreground ? DefaultForeground : DefaultBackground,
    };

    /// <summary>
    /// Effective foreground/background for a cell, applying inverse, hidden and xterm's
    /// "bold is bright" rule for the first eight colours.
    /// </summary>
    public static (uint Foreground, uint Background) ResolveCell(TerminalAttributes attributes)
    {
        var fgColor = attributes.Foreground;
        if (attributes.Has(TerminalStyle.Bold) && fgColor.Kind == TerminalColorKind.Indexed && fgColor.Value < 8)
            fgColor = TerminalColor.Indexed((int)fgColor.Value + 8);

        uint fg = Resolve(fgColor, foreground: true);
        uint bg = Resolve(attributes.Background, foreground: false);
        if (attributes.Has(TerminalStyle.Inverse))
            (fg, bg) = (bg, fg);
        if (attributes.Has(TerminalStyle.Hidden))
            fg = bg;
        return (fg, bg);
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        Ansi16.CopyTo(table, 0);

        int[] levels = [0x00, 0x5F, 0x87, 0xAF, 0xD7, 0xFF];
        for (int i = 0; i < 216; i++)
        {
            int r = levels[i / 36], g = levels[i / 6 % 6], b = levels[i % 6];
            table[16 + i] = (uint)((r << 16) | (g << 8) | b);
        }

        for (int i = 0; i < 24; i++)
        {
            int v = 8 + i * 10;
            table[232 + i] = (uint)((v << 16) | (v << 8) | v);
        }

        return table;
    }
}
