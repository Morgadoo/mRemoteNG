using System.Text.Json.Serialization;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Direction in which a session panel is split to create a sibling region.</summary>
public enum SplitSide
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// A node in the recursive split layout tree of the session area: either a leaf (<see cref="PanelName"/>)
/// showing one session panel, or a binary split (<see cref="First"/> and <see cref="Second"/>) divided
/// vertically (columns) or horizontally (rows) at <see cref="Ratio"/>.
/// </summary>
public sealed class SplitLayoutNode
{
    /// <summary>The panel shown by a leaf node; null on a split node.</summary>
    public string? PanelName { get; set; }

    /// <summary>True when <see cref="First"/> and <see cref="Second"/> are placed left and right; false for top and bottom.</summary>
    public bool Vertical { get; set; } = true;

    /// <summary>Share of the available width or height given to <see cref="First"/> (clamped to 0.1..0.9).</summary>
    public double Ratio { get; set; } = 0.5;

    public SplitLayoutNode? First { get; set; }

    public SplitLayoutNode? Second { get; set; }

    [JsonIgnore]
    public bool IsLeaf => First is null || Second is null;

    public static SplitLayoutNode Leaf(string panelName) => new() { PanelName = panelName };

    public static SplitLayoutNode Split(bool vertical, SplitLayoutNode first, SplitLayoutNode second, double ratio = 0.5) =>
        new()
        {
            Vertical = vertical,
            Ratio = ClampRatio(ratio),
            First = first,
            Second = second,
        };

    public static double ClampRatio(double ratio) =>
        double.IsFinite(ratio) ? Math.Clamp(ratio, 0.1, 0.9) : 0.5;

    public SplitLayoutNode Clone() => IsLeaf
        ? Leaf(PanelName ?? string.Empty)
        : Split(Vertical, First!.Clone(), Second!.Clone(), Ratio);

    /// <summary>Every panel name referenced by a leaf of this subtree, in left-to-right / top-to-bottom order.</summary>
    public IEnumerable<string> PanelNames()
    {
        if (IsLeaf)
        {
            if (!string.IsNullOrWhiteSpace(PanelName))
                yield return PanelName;
            yield break;
        }
        foreach (var name in First!.PanelNames())
            yield return name;
        foreach (var name in Second!.PanelNames())
            yield return name;
    }

    public bool ContainsPanel(string panelName) =>
        PanelNames().Any(n => string.Equals(n, panelName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces the leaf for <paramref name="targetPanel"/> with a split containing it and <paramref name="newPanel"/>.</summary>
    public SplitLayoutNode SplitLeaf(string targetPanel, string newPanel, SplitSide side, double ratio = 0.5)
    {
        if (IsLeaf)
        {
            if (!string.Equals(PanelName, targetPanel, StringComparison.OrdinalIgnoreCase))
                return Clone();
            var vertical = side is SplitSide.Left or SplitSide.Right;
            var newFirst = side is SplitSide.Left or SplitSide.Top;
            var existingLeaf = Leaf(PanelName!);
            var addedLeaf = Leaf(newPanel);
            return Split(vertical,
                newFirst ? addedLeaf : existingLeaf,
                newFirst ? existingLeaf : addedLeaf,
                ratio);
        }

        return Split(
            Vertical,
            First!.SplitLeaf(targetPanel, newPanel, side, ratio),
            Second!.SplitLeaf(targetPanel, newPanel, side, ratio),
            Ratio);
    }

    /// <summary>Updates the panel name when a user-named panel is renamed.</summary>
    public void RenameLeaf(string oldName, string newName)
    {
        if (IsLeaf)
        {
            if (string.Equals(PanelName, oldName, StringComparison.OrdinalIgnoreCase))
                PanelName = newName;
            return;
        }
        First!.RenameLeaf(oldName, newName);
        Second!.RenameLeaf(oldName, newName);
    }

    /// <summary>
    /// Drops leaves whose panel is not in <paramref name="validPanelNames"/> and collapses any split
    /// that loses a child. Returns null when no leaf survives.
    /// </summary>
    public SplitLayoutNode? Pruned(ISet<string> validPanelNames)
    {
        if (IsLeaf)
        {
            return !string.IsNullOrWhiteSpace(PanelName) && validPanelNames.Contains(PanelName.Trim())
                ? Leaf(PanelName.Trim())
                : null;
        }

        var first = First?.Pruned(validPanelNames);
        var second = Second?.Pruned(validPanelNames);
        if (first is null) return second;
        if (second is null) return first;
        return Split(Vertical, first, second, ClampRatio(Ratio));
    }

    /// <summary>Structural signature so the view knows when to rebuild its grid tree.</summary>
    public string Signature() =>
        IsLeaf
            ? $"L({PanelName})"
            : $"S({(Vertical ? 'V' : 'H')}:{First!.Signature()},{Second!.Signature()})";
}
