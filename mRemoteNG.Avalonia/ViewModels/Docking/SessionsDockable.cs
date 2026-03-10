using System.Collections.ObjectModel;
using Dock.Model.Mvvm.Controls;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Dock panel for active remote connection sessions (tabbed).</summary>
public sealed class SessionsDockable : Document
{
    private SessionTabViewModel? _activeSession;

    public SessionsDockable()
    {
        Id = "Sessions";
        Title = "Sessions";
    }

    public ObservableCollection<SessionTabViewModel> Sessions { get; } = [];

    public SessionTabViewModel? ActiveSession
    {
        get => _activeSession;
        set => this.RaiseAndSetIfChanged(ref _activeSession, value);
    }

    public void AddSession(SessionTabViewModel session)
    {
        Sessions.Add(session);
        ActiveSession = session;
    }

    public void CloseSession(SessionTabViewModel session)
    {
        Sessions.Remove(session);
        ActiveSession = Sessions.LastOrDefault();
    }
}

/// <summary>Represents a single active connection tab.</summary>
public sealed class SessionTabViewModel : ReactiveObject
{
    private string _title = "New Connection";
    private bool _isConnected;

    public string Id { get; } = Guid.NewGuid().ToString();
    public string Protocol { get; init; } = "SSH";
    public string Hostname { get; init; } = string.Empty;

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    /// <summary>The native window handle of the embedded connection process, if any.</summary>
    public IntPtr EmbeddedHandle { get; set; } = IntPtr.Zero;
}
