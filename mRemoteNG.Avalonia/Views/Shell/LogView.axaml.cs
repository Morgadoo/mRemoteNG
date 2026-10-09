using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels.Docking;

namespace mRemoteNG.Avalonia.Views.Shell;

/// <summary>
/// The rows of a log panel (<see cref="LogPanelDockable"/> or <see cref="DebugConsoleDockable"/>). New entries
/// scroll into view while the list is scrolled to its end; scrolling up to read stops the follow.
/// </summary>
public partial class LogView : UserControl
{
    private const double FollowTolerance = 24;
    private LogDockableBase? _log;

    public LogView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_log is not null)
            _log.Entries.CollectionChanged -= OnEntriesChanged;
        _log = DataContext as LogDockableBase;
        if (_log is not null)
            _log.Entries.CollectionChanged += OnEntriesChanged;
    }

    /// <summary>True while the view shows the last rows (new rows keep it there).</summary>
    public bool IsFollowing =>
        Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - FollowTolerance;

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !IsFollowing)
            return;
        // Twice: virtualized rows of varying height (wrapped messages) correct the extent after the first layout.
        Dispatcher.UIThread.Post(() =>
        {
            Scroller.ScrollToEnd();
            Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    /// <summary>All rows as text ("12:00:01 [Warning] message"), one per line.</summary>
    public static string Format(LogDockableBase log) =>
        string.Join(Environment.NewLine, log.Entries.Select(entry => entry.ToString()));
}
