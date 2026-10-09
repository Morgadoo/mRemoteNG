using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using Avalonia.Threading;
using Material.Icons;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Localization;
using ReactiveUI;

namespace mRemoteNG.Avalonia.Services;

/// <summary>Severity of a toast; picks its icon and colour (docs/design-system.md §6 Toasts).</summary>
public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One toast: icon by level, title, optional message and action, a close button.</summary>
public sealed class ToastItem : ReactiveObject
{
    internal IDisposable? Timer;

    public ToastItem(ToastLevel level, string title, string? message, string? actionText, Action? action, Action<ToastItem> dismiss)
    {
        Level = level;
        Title = title;
        Message = message;
        ActionText = actionText;
        DismissCommand = ReactiveCommand.Create(() => dismiss(this));
        ActionCommand = ReactiveCommand.Create(() =>
        {
            action?.Invoke();
            dismiss(this);
        });
    }

    public ToastLevel Level { get; }

    public string Title { get; }

    public string? Message { get; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>Label of the optional action link ("Show log"); null for none.</summary>
    public string? ActionText { get; }

    public bool HasAction => ActionText is not null;

    public MaterialIconKind Icon => Level switch
    {
        ToastLevel.Success => MaterialIconKind.CheckCircleOutline,
        ToastLevel.Warning => MaterialIconKind.AlertOutline,
        ToastLevel.Error => MaterialIconKind.AlertCircleOutline,
        _ => MaterialIconKind.InformationOutline,
    };

    public bool IsSuccess => Level == ToastLevel.Success;

    public bool IsWarning => Level == ToastLevel.Warning;

    public bool IsError => Level == ToastLevel.Error;

    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    public ReactiveCommand<Unit, Unit> ActionCommand { get; }
}

/// <summary>
/// In-app toasts shown bottom-right of the main window (docs/design-system.md §6): at most
/// <see cref="MaxVisible"/> stacked, newest at the bottom; they close by themselves after
/// <see cref="DefaultDuration"/> except errors, which stay until closed. The log panel keeps the full record:
/// <see cref="AttachLog"/> turns every error written to it into a toast, so any area that logs an error
/// (connection failures, failed saves…) gets one for free.
/// </summary>
public sealed class ToastService
{
    public const int MaxVisible = 3;
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private LogDockableBase? _log;
    private Action? _showLog;

    public ToastService()
        : this((delay, action) => DispatcherTimer.RunOnce(action, delay))
    {
    }

    /// <summary>For tests: <paramref name="schedule"/> runs an action after a delay and returns its cancellation.</summary>
    public ToastService(Func<TimeSpan, Action, IDisposable> schedule) => _schedule = schedule;

    /// <summary>The toasts on screen, oldest first.</summary>
    public ObservableCollection<ToastItem> Toasts { get; } = [];

    /// <summary>
    /// Shows a toast. <paramref name="duration"/> defaults to <see cref="DefaultDuration"/>, or no auto-dismiss for
    /// errors; <see cref="TimeSpan.Zero"/> also means "until closed". A toast with the same title and message as
    /// one on screen restarts that one instead of stacking a copy.
    /// </summary>
    public void Show(string title, string? message = null, ToastLevel level = ToastLevel.Info, TimeSpan? duration = null,
        string? actionText = null, Action? action = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(title, message, level, duration, actionText, action));
            return;
        }

        var lifetime = duration ?? (level == ToastLevel.Error ? TimeSpan.Zero : DefaultDuration);
        var existing = Toasts.FirstOrDefault(t => t.Title == title && t.Message == message && t.Level == level);
        if (existing is not null)
        {
            Toasts.Remove(existing);
            existing.Timer?.Dispose();
            existing.Timer = null;
        }

        var toast = existing ?? new ToastItem(level, title, message, actionText, action, Dismiss);
        Toasts.Add(toast);
        while (Toasts.Count > MaxVisible)
            Dismiss(Toasts[0]);
        if (lifetime > TimeSpan.Zero)
            toast.Timer = _schedule(lifetime, () => Dismiss(toast));
    }

    public void Dismiss(ToastItem toast)
    {
        toast.Timer?.Dispose();
        toast.Timer = null;
        Toasts.Remove(toast);
    }

    public void DismissAll()
    {
        foreach (var toast in Toasts.ToList())
            Dismiss(toast);
    }

    /// <summary>
    /// Shows a toast for every <see cref="LogLevel.Error"/> entry written to <paramref name="log"/>, with a
    /// "Show log" action running <paramref name="showLog"/> when given.
    /// </summary>
    public void AttachLog(LogDockableBase log, Action? showLog = null)
    {
        _showLog = showLog;
        if (ReferenceEquals(_log, log))
            return;
        if (_log is not null)
            _log.Entries.CollectionChanged -= OnLogEntriesChanged;
        _log = log;
        log.Entries.CollectionChanged += OnLogEntriesChanged;
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
            return;
        foreach (LogEntry entry in e.NewItems)
        {
            if (entry.Level != LogLevel.Error)
                continue;
            Show(Localizer.Get("ShellToastErrorTitle"), entry.Message, ToastLevel.Error,
                actionText: _showLog is null ? null : Localizer.Get("ShellShowLog"), action: _showLog);
        }
    }
}
