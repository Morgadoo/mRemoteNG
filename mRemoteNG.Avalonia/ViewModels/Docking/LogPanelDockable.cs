using System.Collections.ObjectModel;
using Dock.Model.Mvvm.Controls;
using Material.Icons;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

public enum LogLevel { Info, Warning, Error, Debug }

public sealed class LogEntry(LogLevel level, string message, DateTime timestamp)
{
    public LogLevel Level { get; } = level;
    public string Message { get; } = message;
    public DateTime Timestamp { get; } = timestamp;
    public string FormattedTime => Timestamp.ToString("HH:mm:ss");

    /// <summary>Level glyph (docs/design-system.md §6 Panels): info, warning, error, debug.</summary>
    public MaterialIconKind IconKind => Level switch
    {
        LogLevel.Warning => MaterialIconKind.AlertOutline,
        LogLevel.Error => MaterialIconKind.AlertCircleOutline,
        LogLevel.Debug => MaterialIconKind.BugOutline,
        _ => MaterialIconKind.InformationOutline,
    };

    public bool IsWarning => Level == LogLevel.Warning;

    public bool IsError => Level == LogLevel.Error;

    /// <summary>The line as copied to the clipboard.</summary>
    public override string ToString() => $"{FormattedTime} [{Level}] {Message}";
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
