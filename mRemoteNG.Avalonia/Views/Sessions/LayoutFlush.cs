using Avalonia;
using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Moving a control from one window to another while the old window still has it queued for
/// layout makes Avalonia throw ("InvalidateArrange on wrong LayoutManager"). Running the old
/// window's pending layout pass right after the control was detached empties that queue.
/// </summary>
internal static class LayoutFlush
{
    /// <summary>The window <paramref name="visual"/> is in now (call before detaching it).</summary>
    public static TopLevel? RootOf(Visual? visual) => visual is null ? null : TopLevel.GetTopLevel(visual);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, TopLevel> LastRoots = new();

    /// <summary>
    /// Call right before <paramref name="view"/> is attached under <paramref name="newRoot"/>: when it was last
    /// shown in another window, that window's pending layout pass runs first.
    /// </summary>
    public static void BeforeAttach(Control view, TopLevel? newRoot)
    {
        // A closed (or never shown) window runs no more layout passes.
        if (LastRoots.TryGetValue(view, out var previous) && !ReferenceEquals(previous, newRoot) && previous.IsVisible)
            Run(previous);
        if (newRoot is not null)
            LastRoots.AddOrUpdate(view, newRoot);
    }

    /// <summary>Runs <paramref name="root"/>'s pending layout pass (after a control left it).</summary>
    public static void Run(TopLevel? root)
    {
        try
        {
            root?.UpdateLayout();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already inside a layout pass; the queue is processed by that pass.
        }
    }
}
