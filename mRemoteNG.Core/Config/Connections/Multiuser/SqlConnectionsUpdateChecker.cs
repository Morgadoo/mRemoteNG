using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace mRemoteNG.Core.Config.Connections.Multiuser
{
    /// <summary>Raised when another client saved the connection database.</summary>
    public sealed class ConnectionsUpdateAvailableEventArgs(DateTime updateTime) : EventArgs
    {
        /// <summary>The database's <c>tblUpdate.LastUpdate</c>.</summary>
        public DateTime UpdateTime { get; } = updateTime;
    }

    /// <summary>
    /// Multi-user support (legacy <c>SqlConnectionsUpdateChecker</c> + <c>RemoteConnectionsSyncronizer</c>):
    /// polls <c>tblUpdate.LastUpdate</c> at an interval and raises <see cref="UpdateAvailable"/> once per
    /// timestamp that differs from the one this client last loaded or saved.
    /// Checks never overlap; the next one is scheduled when the previous finished.
    /// Events are raised on a thread-pool thread.
    /// </summary>
    public sealed class SqlConnectionsUpdateChecker : IDisposable
    {
        private readonly Func<DateTime?> _readDatabaseTimestamp;
        private readonly Func<DateTime?> _knownTimestamp;
        private readonly ILogger _logger;
        private readonly object _sync = new();
        private Timer? _timer;
        private TimeSpan _interval;
        private DateTime? _lastNotified;
        private bool _disposed;
        private int _checking;
        private bool _lastCheckFailed;

        /// <param name="readDatabaseTimestamp">Reads <c>tblUpdate.LastUpdate</c> (e.g. <see cref="Sql.SqlConnectionsStore.GetLastUpdate"/>).</param>
        /// <param name="knownTimestamp">The timestamp this client last loaded or saved.</param>
        public SqlConnectionsUpdateChecker(Func<DateTime?> readDatabaseTimestamp, Func<DateTime?> knownTimestamp, ILogger? logger = null)
        {
            _readDatabaseTimestamp = readDatabaseTimestamp ?? throw new ArgumentNullException(nameof(readDatabaseTimestamp));
            _knownTimestamp = knownTimestamp ?? throw new ArgumentNullException(nameof(knownTimestamp));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>Raised when the database holds a newer tree than this client.</summary>
        public event EventHandler<ConnectionsUpdateAvailableEventArgs>? UpdateAvailable;

        /// <summary>Raised when a check could not reach the database (once until a check succeeds again).</summary>
        public event EventHandler<Exception>? CheckFailed;

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                    return _timer is not null;
            }
        }

        public TimeSpan Interval
        {
            get
            {
                lock (_sync)
                    return _interval;
            }
        }

        /// <summary>Starts polling every <paramref name="interval"/> (minimum one second).</summary>
        public void Start(TimeSpan interval)
        {
            if (interval < TimeSpan.FromSeconds(1))
                interval = TimeSpan.FromSeconds(1);

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _interval = interval;
                _timer ??= new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(interval, Timeout.InfiniteTimeSpan);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>Forgets which update was already reported (call after loading or saving).</summary>
        public void Reset()
        {
            lock (_sync)
                _lastNotified = null;
        }

        /// <summary>
        /// Checks once. Returns true when the database changed since <c>knownTimestamp</c> and raises
        /// <see cref="UpdateAvailable"/> if this change was not reported before.
        /// </summary>
        public bool CheckNow()
        {
            if (Interlocked.Exchange(ref _checking, 1) == 1)
                return false;

            try
            {
                var database = _readDatabaseTimestamp();
                if (_lastCheckFailed)
                {
                    _lastCheckFailed = false;
                    _logger.LogInformation("The connection database is reachable again");
                }

                if (database is null)
                    return false;

                var known = _knownTimestamp();
                if (known is not null && !HasChanged(database.Value, known.Value))
                    return false;

                bool notify;
                lock (_sync)
                {
                    notify = _lastNotified != database;
                    _lastNotified = database;
                }

                if (notify)
                {
                    _logger.LogInformation("The connection database was updated by another client at {Time}", database);
                    UpdateAvailable?.Invoke(this, new ConnectionsUpdateAvailableEventArgs(database.Value));
                }

                return true;
            }
            catch (Exception ex)
            {
                if (!_lastCheckFailed)
                {
                    _lastCheckFailed = true;
                    _logger.LogWarning(ex, "Could not check the connection database for updates");
                    CheckFailed?.Invoke(this, ex);
                }

                return false;
            }
            finally
            {
                Volatile.Write(ref _checking, 0);
            }
        }

        /// <summary>
        /// True when the database timestamp differs from the one this client knows. Each client stamps the
        /// save with its own clock, so "different" (not "later") is what reveals another client's save.
        /// Differences below 5 ms are ignored (SQL Server's datetime rounds to 1/300 s).
        /// </summary>
        public static bool HasChanged(DateTime database, DateTime known) =>
            (database - known).Duration() > TimeSpan.FromMilliseconds(5);

        private void OnTimer()
        {
            CheckNow();
            lock (_sync)
            {
                if (!_disposed)
                    _timer?.Change(_interval, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
