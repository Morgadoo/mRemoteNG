using System.Collections.ObjectModel;
using Dock.Model.Mvvm.Controls;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

public enum LogLevel { Info, Warning, Error, Debug }

public sealed class LogEntry(LogLevel level, string message, DateTime timestamp)
{
    public LogLevel Level { get; } = level;
    public string Message { get; } = message;
    public DateTime Timestamp { get; } = timestamp;
    public string FormattedTime => Timestamp.ToString("HH:mm:ss");

    public string LevelIcon => Level switch
    {
        LogLevel.Warning => "⚠",
        LogLevel.Error => "✖",
        LogLevel.Debug => "⚙",
        _ => "ℹ",
    };

    public string LevelColor => Level switch
    {
        LogLevel.Warning => "#ffc107",
        LogLevel.Error => "#f44336",
        LogLevel.Debug => "#888888",
        _ => "#569cd6",
    };
}

/// <summary>Dock panel showing application log / error messages.</summary>
public sealed class LogPanelDockable : Tool
{
    public LogPanelDockable()
    {
        Id = "LogPanel";
        Title = "Log";
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        // Ensure UI thread
        if (System.Threading.Thread.CurrentThread.IsBackground)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                Entries.Add(new LogEntry(level, message, DateTime.Now)));
        }
        else
        {
            Entries.Add(new LogEntry(level, message, DateTime.Now));
        }
    }

    public void Clear() => Entries.Clear();
}
