using System.Text.Json;
using System.Text.Json.Serialization;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>
/// The persisted main-window layout: window placement, which side panels are visible and their sizes,
/// the session panels (order, docked or floating, splitter weights) and the toolbars.
/// Stored as JSON in <see cref="Core.Settings.AppSettings.WindowLayout"/>.
/// </summary>
public sealed class WindowLayoutState
{
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 800;
    public const double DefaultTreeWidth = 280;
    public const double DefaultBottomHeight = 160;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Version { get; set; } = 1;

    /// <summary>Window position (null: centre on screen).</summary>
    public int? X { get; set; }

    public int? Y { get; set; }

    /// <summary>Size of the window when not maximised.</summary>
    public double Width { get; set; } = DefaultWidth;

    public double Height { get; set; } = DefaultHeight;

    /// <summary>"Normal" or "Maximized" (minimised and full screen are restored as normal).</summary>
    public string WindowState { get; set; } = "Normal";

    public bool TreeVisible { get; set; } = true;

    public bool LogVisible { get; set; } = true;

    public double TreeWidth { get; set; } = DefaultTreeWidth;

    public double BottomHeight { get; set; } = DefaultBottomHeight;

    public bool MultiSshToolbarVisible { get; set; }

    public PanelArrangement Arrangement { get; set; } = PanelArrangement.Tabbed;

    public string? ActivePanel { get; set; }

    public List<PanelLayoutState> Panels { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a stored layout; null when empty or unreadable (the default layout is used then).</summary>
    public static WindowLayoutState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<WindowLayoutState>(json, JsonOptions);
            return state?.Sanitized();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces out-of-range values with defaults (hand-edited or corrupted settings).</summary>
    private WindowLayoutState Sanitized()
    {
        static double Size(double value, double fallback, double min) =>
            double.IsFinite(value) && value >= min && value <= 20000 ? value : fallback;

        Width = Size(Width, DefaultWidth, 400);
        Height = Size(Height, DefaultHeight, 300);
        TreeWidth = Size(TreeWidth, DefaultTreeWidth, 80);
        BottomHeight = Size(BottomHeight, DefaultBottomHeight, 40);
        if (WindowState is not ("Normal" or "Maximized"))
            WindowState = "Normal";
        if (!Enum.IsDefined(Arrangement))
            Arrangement = PanelArrangement.Tabbed;
        Panels = (Panels ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p?.Name))
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        foreach (var panel in Panels)
        {
            panel.Name = panel.Name.Trim();
            if (!double.IsFinite(panel.Weight) || panel.Weight <= 0.01)
                panel.Weight = 1;
        }
        return this;
    }
}

public sealed class PanelLayoutState
{
    public string Name { get; set; } = string.Empty;

    public bool IsFloating { get; set; }

    public double Weight { get; set; } = 1;

    public FloatingBounds? Floating { get; set; }
}
