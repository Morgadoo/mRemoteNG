using Avalonia.Media;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Builds tab titles and colours the way the legacy connection tabs did.</summary>
public static class SessionTabAppearance
{
    /// <summary>Width of the coloured frame drawn around a session (legacy: 4 px).</summary>
    public const double FrameWidth = 4;

    /// <summary>
    /// The tab text: the connection name (or host), optionally "Quick: " for quick connects, the
    /// protocol in front ("SSH2: name") and the logon behind ("name (DOMAIN\user)").
    /// </summary>
    public static string FormatTitle(ConnectionInfo? connection, ConnectionParameters parameters, AppSettings? settings)
    {
        var name = connection is { Name.Length: > 0 } ? connection.Name : parameters.Hostname;
        if (connection is { IsQuickConnect: true } && settings?.IdentifyQuickConnectTabs == true)
            name = $"Quick: {name}";

        var title = settings?.ShowProtocolOnTabs == true
            ? $"{ProtocolDisplayName(connection, parameters)}: {name}"
            : name;

        if (settings?.ShowLogonInfoOnTabs == true)
        {
            var domain = connection?.Domain ?? parameters.Domain ?? string.Empty;
            var user = connection?.Username ?? parameters.Username ?? string.Empty;
            var logon = domain.Length > 0 && user.Length > 0 ? $"{domain}\\{user}" : domain + user;
            if (logon.Length > 0)
                title += $" ({logon})";
        }

        return title;
    }

    /// <summary>Protocol name as the legacy app showed it (the connection's protocol, e.g. "SSH2").</summary>
    public static string ProtocolDisplayName(ConnectionInfo? connection, ConnectionParameters parameters) =>
        connection is not null && !connection.IsContainer
            ? connection.Protocol.ToString()
            : parameters.Protocol.ToString().ToUpperInvariant();

    /// <summary>Icon key for <see cref="Services.IconService.GetProtocolIcon"/>.</summary>
    public static string ProtocolIconKey(ProtocolType protocol) => protocol switch
    {
        ProtocolType.Ssh or ProtocolType.SshSftp => "SSH",
        ProtocolType.Rdp => "RDP",
        ProtocolType.Vnc => "VNC",
        ProtocolType.Telnet => "Telnet",
        ProtocolType.Rlogin => "Rlogin",
        ProtocolType.Raw => "RAW",
        ProtocolType.Http => "HTTP",
        ProtocolType.Https => "HTTPS",
        ProtocolType.PowerShell => "PowerShell",
        ProtocolType.Serial => "Serial",
        ProtocolType.LocalShell => "Terminal",
        _ => "mRemoteNG",
    };

    /// <summary>
    /// Parses the connection's TabColor: a colour name ("Red", "DarkOrange") or hex ("#FF8000", "#80FF8000").
    /// Returns null for empty or unreadable values (the legacy app ignored those too).
    /// </summary>
    public static Color? ParseTabColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (Color.TryParse(text, out var color))
            return color;
        // Legacy files written by System.Drawing may hold "Color [Red]" or "Color [A=255, R=…]".
        if (text.StartsWith("Color [", StringComparison.OrdinalIgnoreCase) && text.EndsWith(']'))
        {
            var inner = text[7..^1];
            if (Color.TryParse(inner, out color))
                return color;
            var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim());
            if (parts.TryGetValue("R", out var r) && parts.TryGetValue("G", out var g) && parts.TryGetValue("B", out var b)
                && byte.TryParse(r, out var rb) && byte.TryParse(g, out var gb) && byte.TryParse(b, out var bb))
            {
                var ab = parts.TryGetValue("A", out var a) && byte.TryParse(a, out var parsedA) ? parsedA : (byte)255;
                return Color.FromArgb(ab, rb, gb, bb);
            }
        }
        return null;
    }

    /// <summary>
    /// The legacy frame colours (InterfaceControl.GetFrameColor). They are user-chosen identifiers ("the red frame is
    /// production"), so they keep their hues in both themes; mid-tone values that read on dark and light backgrounds.
    /// </summary>
    public static Color? FrameColor(ConnectionFrameColor frame) => frame switch
    {
        ConnectionFrameColor.Red => Color.FromRgb(220, 53, 69),
        ConnectionFrameColor.Yellow => Color.FromRgb(255, 193, 7),
        ConnectionFrameColor.Green => Color.FromRgb(40, 167, 69),
        ConnectionFrameColor.Blue => Color.FromRgb(0, 123, 255),
        ConnectionFrameColor.Purple => Color.FromRgb(111, 66, 193),
        _ => null,
    };

    /// <summary>Splits the EnvironmentTags property ("prod, eu-west") into tags.</summary>
    public static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The kind of an environment tag, which picks its badge colour from the palette: production → Danger,
    /// staging/test → Warning, development → Success, anything else neutral.
    /// </summary>
    public static EnvironmentTagKind TagKind(string tag)
    {
        var t = tag.ToLowerInvariant();
        if (t.StartsWith("prod", StringComparison.Ordinal) || t is "prd" or "live")
            return EnvironmentTagKind.Production;
        if (t.StartsWith("stag", StringComparison.Ordinal) || t.StartsWith("test", StringComparison.Ordinal)
            || t is "uat" or "qa" or "preprod" or "pre-prod")
            return EnvironmentTagKind.Staging;
        if (t.StartsWith("dev", StringComparison.Ordinal) || t is "lab" or "local" or "sandbox")
            return EnvironmentTagKind.Development;
        return EnvironmentTagKind.Other;
    }
}

/// <summary>Environment tag categories (badge colours: Danger, Warning, Success, neutral).</summary>
public enum EnvironmentTagKind
{
    Other,
    Production,
    Staging,
    Development,
}

/// <summary>One environment tag shown as a badge on a tab; the view colours it by <see cref="Kind"/>.</summary>
public sealed record EnvironmentTagBadge(string Text, EnvironmentTagKind Kind)
{
    public bool IsProduction => Kind == EnvironmentTagKind.Production;
    public bool IsStaging => Kind == EnvironmentTagKind.Staging;
    public bool IsDevelopment => Kind == EnvironmentTagKind.Development;
}
