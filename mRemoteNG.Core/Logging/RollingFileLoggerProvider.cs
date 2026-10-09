using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace mRemoteNG.Core.Logging
{
    /// <summary>
    /// Writes log messages to a size-rolled text file, like the legacy log4net <c>RollingFileAppender</c>
    /// (<c>mRemoteNG.log</c>, 10 MB per file, 5 old files kept as <c>mRemoteNG.log.1</c> … <c>.5</c>; line format
    /// <c>%date [%thread] %-6level- %message</c>). The minimum level, the file and on/off can change at runtime.
    /// The file is opened shared, so it can be read while the app runs. Thread-safe.
    /// </summary>
    public sealed class RollingFileLoggerProvider : ILoggerProvider
    {
        public const long DefaultMaxFileSizeBytes = 10L * 1024 * 1024;
        public const int DefaultMaxRollBackups = 5;

        private readonly object _sync = new();
        private StreamWriter? _writer;
        private long _currentSize;
        private string _filePath;
        private bool _disposed;

        public RollingFileLoggerProvider(string filePath, LogLevel minimumLevel = LogLevel.Information,
            long maxFileSizeBytes = DefaultMaxFileSizeBytes, int maxRollBackups = DefaultMaxRollBackups)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            _filePath = Path.GetFullPath(filePath);
            MinimumLevel = minimumLevel;
            MaxFileSizeBytes = Math.Max(1024, maxFileSizeBytes);
            MaxRollBackups = Math.Max(0, maxRollBackups);
        }

        /// <summary>Messages below this level are dropped; <see cref="LogLevel.None"/> turns file logging off.</summary>
        public LogLevel MinimumLevel { get; set; }

        public long MaxFileSizeBytes { get; }

        public int MaxRollBackups { get; }

        public string FilePath
        {
            get
            {
                lock (_sync)
                    return _filePath;
            }
            set
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                var full = Path.GetFullPath(value);
                lock (_sync)
                {
                    if (string.Equals(full, _filePath, StringComparison.Ordinal))
                        return;
                    CloseWriter();
                    _filePath = full;
                }
            }
        }

        /// <summary>The last error writing the file (e.g. no permission); null when writing works.</summary>
        public Exception? LastError { get; private set; }

        public bool IsEnabled(LogLevel level) => level != LogLevel.None && MinimumLevel != LogLevel.None && level >= MinimumLevel;

        public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

        /// <summary>Writes one entry (used by the loggers and for messages from the in-app log panel).</summary>
        public void Write(LogLevel level, string category, string message, Exception? exception = null)
        {
            if (!IsEnabled(level))
                return;

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append("] ")
                .Append(LevelName(level).PadRight(6)).Append("- ");
            if (!string.IsNullOrEmpty(category))
                line.Append(category).Append(": ");
            line.Append(message);
            if (exception is not null)
                line.AppendLine().Append(exception);

            lock (_sync)
            {
                if (_disposed)
                    return;
                try
                {
                    var text = line.ToString();
                    var bytes = Encoding.UTF8.GetByteCount(text) + Environment.NewLine.Length;
                    EnsureWriter();
                    if (_currentSize > 0 && _currentSize + bytes > MaxFileSizeBytes)
                    {
                        CloseWriter();
                        Roll();
                        EnsureWriter();
                    }

                    _writer!.WriteLine(text);
                    _currentSize += bytes;
                    LastError = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LastError = ex;
                    CloseWriter();
                }
            }
        }

        /// <summary>Flushes and closes the file (it is reopened by the next write).</summary>
        public void Flush()
        {
            lock (_sync)
                _writer?.Flush();
        }

        private void EnsureWriter()
        {
            if (_writer is not null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _currentSize = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        private void CloseWriter()
        {
            _writer?.Dispose();
            _writer = null;
        }

        /// <summary>mRemoteNG.log → .1 → .2 … (the oldest beyond MaxRollBackups is deleted).</summary>
        private void Roll()
        {
            if (MaxRollBackups == 0)
            {
                File.Delete(_filePath);
                return;
            }

            var oldest = $"{_filePath}.{MaxRollBackups}";
            if (File.Exists(oldest))
                File.Delete(oldest);
            for (var i = MaxRollBackups - 1; i >= 1; i--)
            {
                var source = $"{_filePath}.{i}";
                if (File.Exists(source))
                    File.Move(source, $"{_filePath}.{i + 1}", overwrite: true);
            }

            if (File.Exists(_filePath))
                File.Move(_filePath, $"{_filePath}.1", overwrite: true);
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => level.ToString().ToUpperInvariant(),
        };

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                CloseWriter();
            }
        }

        private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!provider.IsEnabled(logLevel))
                    return;
                provider.Write(logLevel, ShortCategory(category), formatter(state, exception), exception);
            }

            private static string ShortCategory(string name)
            {
                var dot = name.LastIndexOf('.');
                return dot >= 0 ? name[(dot + 1)..] : name;
            }
        }
    }
}
