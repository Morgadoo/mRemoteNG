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
        LogLevel.Warning => "\u26a0",
        LogLevel.Error => "\u2716",
        LogLevel.Debug => "\u2699",
        _ => "\u2139",
    };

    public string LevelColor => Level switch
    {
        LogLevel.Warning => "#ffc107",
        LogLevel.Error => "#f44336",
        LogLevel.Debug => "#888888",
        _ => "#569cd6",
    };
}

/// <summary>Base class for dockable log panels with capped entry collections.</summary>
public abstract class LogDockableBase : Tool
{
    private const int MaxEntries = 2000;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    protected LogLevel DefaultLevel { get; init; } = LogLevel.Info;

    public void Log(string message, LogLevel level)
    {
        void AddEntry()
        {
            if (Entries.Count >= MaxEntries)
                Entries.RemoveAt(0);
            Entries.Add(new LogEntry(level, message, DateTime.Now));
        }

        if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            global::Avalonia.Threading.Dispatcher.UIThread.Post(AddEntry);
        else
            AddEntry();
    }

    public void Log(string message) => Log(message, DefaultLevel);

    public void Clear() => Entries.Clear();
}

/// <summary>Dock panel showing application log / error messages.</summary>
public sealed class LogPanelDockable : LogDockableBase
{
    public LogPanelDockable()
    {
        Id = "LogPanel";
        Title = "Log";
        DefaultLevel = LogLevel.Info;
    }
}

/// <summary>Debug console that captures Trace/Debug output.</summary>
public sealed class DebugConsoleDockable : LogDockableBase
{
    public DebugConsoleDockable()
    {
        Id = "DebugConsole";
        Title = "Debug Console";
        DefaultLevel = LogLevel.Debug;
    }
}
