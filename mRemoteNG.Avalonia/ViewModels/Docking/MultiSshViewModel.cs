using System.Text;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Which sessions the Multi-SSH toolbar types into.</summary>
public enum MultiSshScope
{
    /// <summary>Every open terminal session in every panel (legacy behaviour).</summary>
    AllSessions,

    /// <summary>Only the terminal sessions of the active panel.</summary>
    ActivePanel,
}

/// <summary>
/// The Multi-SSH toolbar (legacy MultiSshToolStrip): what is typed is sent, followed by Enter, to every
/// connected terminal session (<see cref="ITerminalProtocol"/>) that is included as a target.
/// Keeps a command history for Up/Down, and forwards Ctrl+letter as control characters.
/// </summary>
public sealed class MultiSshViewModel : ReactiveObject
{
    private const int HistoryLength = 100;

    private readonly SessionsDockable _sessions;
    private readonly List<string> _history = [];
    private int _historyIndex;
    private string _commandText = string.Empty;
    private MultiSshScope _scope = MultiSshScope.AllSessions;

    public MultiSshViewModel(SessionsDockable sessions)
    {
        _sessions = sessions;
        _sessions.Sessions.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(TargetSummary));
    }

    public string CommandText
    {
        get => _commandText;
        set => this.RaiseAndSetIfChanged(ref _commandText, value ?? string.Empty);
    }

    public MultiSshScope Scope
    {
        get => _scope;
        set
        {
            this.RaiseAndSetIfChanged(ref _scope, value);
            this.RaisePropertyChanged(nameof(TargetSummary));
        }
    }

    public IReadOnlyList<Choice<MultiSshScope>> ScopeChoices { get; } =
    [
        new(MultiSshScope.AllSessions, "All sessions"),
        new(MultiSshScope.ActivePanel, "Active panel"),
    ];

    public Choice<MultiSshScope> SelectedScope
    {
        get => ScopeChoices.First(c => c.Value == Scope);
        set
        {
            if (value is not null) Scope = value.Value;
        }
    }

    public IReadOnlyList<string> History => _history;

    /// <summary>"Sends to 3 sessions" — refreshed when sessions open/close (and by <see cref="RefreshTargets"/>).</summary>
    public string TargetSummary
    {
        get
        {
            var count = GetTargets().Count;
            return count == 1 ? "1 session" : $"{count} sessions";
        }
    }

    public void RefreshTargets() => this.RaisePropertyChanged(nameof(TargetSummary));

    /// <summary>The connected terminal sessions that receive input.</summary>
    public IReadOnlyList<SessionTabViewModel> GetTargets()
    {
        IEnumerable<SessionTabViewModel> candidates = Scope == MultiSshScope.ActivePanel
            ? _sessions.ActivePanel?.Sessions ?? Enumerable.Empty<SessionTabViewModel>()
            : _sessions.Sessions;
        return candidates
            .Where(s => s.IsMultiSshTarget && s.Protocol is ITerminalProtocol && s.Protocol.State == ConnectionState.Connected)
            .ToList();
    }

    /// <summary>Enter: sends the text plus Enter ("\r") to every target, records it and clears the box.</summary>
    public async Task<int> SendCommandAsync()
    {
        var text = CommandText;
        var sent = await SendAsync(Encoding.UTF8.GetBytes(text + "\r"));
        if (!string.IsNullOrWhiteSpace(text))
        {
            _history.Add(text.Trim());
            if (_history.Count > HistoryLength)
                _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
        CommandText = string.Empty;
        return sent;
    }

    /// <summary>Ctrl+letter: sends the control character (Ctrl+C → 0x03) to every target.</summary>
    public Task<int> SendControlAsync(char letter)
    {
        var upper = char.ToUpperInvariant(letter);
        if (upper is < '@' or > '_')
            return Task.FromResult(0);
        return SendAsync([(byte)(upper - '@')]);
    }

    /// <summary>Sends raw input to every target; returns how many sessions accepted it.</summary>
    public async Task<int> SendAsync(byte[] data)
    {
        var targets = GetTargets();
        var results = await Task.WhenAll(targets.Select(async session =>
        {
            try
            {
                await ((ITerminalProtocol)session.Protocol).SendInputAsync(data);
                return true;
            }
            catch (Exception ex)
            {
                _sessions.ReportError($"Multi-SSH: sending to \"{session.DisplayTitle}\" failed: {ex.Message}");
                return false;
            }
        }));
        return results.Count(r => r);
    }

    /// <summary>Up (-1) / Down (+1) through the command history; returns false at either end.</summary>
    public bool NavigateHistory(int direction)
    {
        if (_history.Count == 0) return false;
        var next = _historyIndex + direction;
        if (next < 0 || next > _history.Count) return false;
        _historyIndex = next;
        CommandText = next == _history.Count ? string.Empty : _history[next];
        return true;
    }
}
