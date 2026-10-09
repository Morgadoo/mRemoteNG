using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace mRemoteNG.Core.Config.Connections
{
    /// <summary>What <see cref="ConnectionsAutoSaver"/> saves: usually the connection tree view model.</summary>
    public interface IAutoSaveTarget
    {
        /// <summary>True when the tree has unsaved changes.</summary>
        bool IsDirty { get; }

        /// <summary>True when there is a file (or writable database) to save to.</summary>
        bool CanSave { get; }

        /// <summary>Saves and marks the tree clean.</summary>
        void Save();
    }

    public sealed class AutoSaveEventArgs(string reason, Exception? error = null) : EventArgs
    {
        /// <summary>"interval" or "edit".</summary>
        public string Reason { get; } = reason;

        public Exception? Error { get; } = error;
    }

    /// <summary>
    /// Saves the connections automatically (legacy <c>AutoSaveEveryMinutes</c> timer and
    /// <c>SaveConnectionsOnEdit</c>): every N minutes, and/or shortly after each edit. Only saves when the
    /// target has unsaved changes and somewhere to save to. Saves run through <c>dispatch</c>
    /// (e.g. on the UI thread); timers never overlap a save with another.
    /// </summary>
    public sealed class ConnectionsAutoSaver : IDisposable
    {
        public const string ReasonInterval = "interval";
        public const string ReasonEdit = "edit";

        /// <summary>Edits within this window are saved together.</summary>
        public static readonly TimeSpan DefaultEditDelay = TimeSpan.FromSeconds(2);

        private readonly Action<Action> _dispatch;
        private readonly ILogger _logger;
        private readonly TimeSpan _editDelay;
        private readonly object _sync = new();
        private Timer? _intervalTimer;
        private Timer? _editTimer;
        private IAutoSaveTarget? _target;
        private TimeSpan _interval;
        private bool _saveOnEdit;
        private bool _disposed;
        private int _saving;

        /// <param name="dispatch">Runs a save on the thread that owns the target (null: run inline).</param>
        /// <param name="editDelay">Delay between an edit and the save it triggers (null: <see cref="DefaultEditDelay"/>).</param>
        public ConnectionsAutoSaver(Action<Action>? dispatch = null, ILogger? logger = null, TimeSpan? editDelay = null)
        {
            _dispatch = dispatch ?? (action => action());
            _logger = logger ?? NullLogger.Instance;
            _editDelay = editDelay ?? DefaultEditDelay;
        }

        /// <summary>Raised after an automatic save (also when it failed: see <see cref="AutoSaveEventArgs.Error"/>).</summary>
        public event EventHandler<AutoSaveEventArgs>? AutoSaved;

        public TimeSpan Interval
        {
            get
            {
                lock (_sync)
                    return _interval;
            }
        }

        public bool SaveOnEdit
        {
            get
            {
                lock (_sync)
                    return _saveOnEdit;
            }
        }

        public void Attach(IAutoSaveTarget target)
        {
            lock (_sync)
                _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>Applies the settings: save every <paramref name="intervalMinutes"/> minutes (0 = off) and/or after edits.</summary>
        public void Configure(int intervalMinutes, bool saveOnEdit) =>
            Configure(TimeSpan.FromMinutes(Math.Max(0, intervalMinutes)), saveOnEdit);

        public void Configure(TimeSpan interval, bool saveOnEdit)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _saveOnEdit = saveOnEdit;
                if (!saveOnEdit)
                    _editTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                if (interval == _interval && (_intervalTimer is not null) == (interval > TimeSpan.Zero))
                    return;

                _interval = interval;
                _intervalTimer?.Dispose();
                _intervalTimer = interval > TimeSpan.Zero
                    ? new Timer(_ => Trigger(ReasonInterval), null, interval, interval)
                    : null;
            }
        }

        /// <summary>Call whenever the tree was edited; schedules a save when "save on edit" is on.</summary>
        public void NotifyEdited()
        {
            lock (_sync)
            {
                if (_disposed || !_saveOnEdit)
                    return;
                _editTimer ??= new Timer(_ => Trigger(ReasonEdit), null, Timeout.Infinite, Timeout.Infinite);
                _editTimer.Change(_editDelay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Saves now when the target is dirty and can be saved. Must run on the target's thread.
        /// Returns true when it saved.
        /// </summary>
        public bool SaveIfNeeded(string reason)
        {
            IAutoSaveTarget? target;
            lock (_sync)
                target = _disposed ? null : _target;

            if (target is null || !target.IsDirty || !target.CanSave)
                return false;

            if (Interlocked.Exchange(ref _saving, 1) == 1)
                return false;

            try
            {
                target.Save();
                _logger.LogInformation("Connections saved automatically ({Reason})", reason);
                AutoSaved?.Invoke(this, new AutoSaveEventArgs(reason));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic save of the connections failed");
                AutoSaved?.Invoke(this, new AutoSaveEventArgs(reason, ex));
                return false;
            }
            finally
            {
                Volatile.Write(ref _saving, 0);
            }
        }

        private void Trigger(string reason)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
            }

            try
            {
                _dispatch(() => SaveIfNeeded(reason));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not schedule the automatic save");
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _intervalTimer?.Dispose();
                _intervalTimer = null;
                _editTimer?.Dispose();
                _editTimer = null;
            }
        }
    }
}
