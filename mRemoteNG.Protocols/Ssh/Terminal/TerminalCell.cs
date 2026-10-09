using System.Text;

namespace mRemoteNG.Protocols.Ssh.Terminal;

public enum TerminalColorKind : byte
{
    Default,
    Indexed,
    Rgb,
}

/// <summary>A terminal colour: the default, a 256-colour palette index, or 24-bit RGB.</summary>
public readonly record struct TerminalColor(TerminalColorKind Kind, uint Value)
{
    public static TerminalColor Default => default;

    public static TerminalColor Indexed(int index) => new(TerminalColorKind.Indexed, (uint)Math.Clamp(index, 0, 255));

    public static TerminalColor Rgb(int r, int g, int b) =>
        new(TerminalColorKind.Rgb, (uint)((Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255)));

    public override string ToString() => Kind switch
    {
        TerminalColorKind.Indexed => $"#{Value}",
        TerminalColorKind.Rgb => $"rgb({Value >> 16},{(Value >> 8) & 0xFF},{Value & 0xFF})",
        _ => "default",
    };
}

[Flags]
public enum TerminalStyle : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,
}

/// <summary>Graphic rendition (SGR) state applied to a cell.</summary>
public readonly record struct TerminalAttributes(TerminalColor Foreground, TerminalColor Background, TerminalStyle Style)
{
    public static TerminalAttributes Default => default;

    public bool Has(TerminalStyle style) => (Style & style) == style;

    public TerminalAttributes With(TerminalStyle style, bool on) =>
        this with { Style = on ? Style | style : Style & ~style };
}

/// <summary>One character cell on the screen.</summary>
public readonly record struct TerminalCell(Rune Rune, TerminalAttributes Attributes)
{
    public static readonly Rune Space = new(' ');

    public static TerminalCell Blank(TerminalAttributes attributes) => new(Space, attributes);
}
