using System.Globalization;
using System.Windows.Input;
using Material.Icons;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>A command offered by the palette: a menu command with its label, menu and shortcut.</summary>
public sealed record PaletteCommand(
    string Label,
    string Category,
    string? Shortcut,
    ICommand Command,
    object? Parameter = null,
    MaterialIconKind Icon = MaterialIconKind.ChevronRight);

public enum PaletteItemKind
{
    Connection,
    Command,
}

/// <summary>One row of the command palette: a connection or a command.</summary>
public sealed class PaletteItem
{
    private PaletteItem(PaletteItemKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public PaletteItemKind Kind { get; }

    public string Title { get; }

    /// <summary>Host (connections) — shown in mono.</summary>
    public string? Host { get; private init; }

    /// <summary>Folder path (connections) or menu (commands).</summary>
    public string? Detail { get; private init; }

    public string? Shortcut { get; private init; }

    public ConnectionInfo? Connection { get; private init; }

    public PaletteCommand? Command { get; private init; }

    public bool IsRecent { get; private init; }

    public bool IsFavorite { get; private init; }

    public bool IsConnection => Kind == PaletteItemKind.Connection;

    public bool IsCommand => Kind == PaletteItemKind.Command;

    public bool HasHost => !string.IsNullOrEmpty(Host);

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);

    /// <summary>The shortcut split into keycaps ("Ctrl", "Shift", "N").</summary>
    public IReadOnlyList<string> ShortcutKeys => Shortcut is { Length: > 0 } s ? SplitGesture(s) : [];

    public MaterialIconKind CommandIcon => Command?.Icon ?? MaterialIconKind.ChevronRight;

    public object? Protocol => Connection?.Protocol;

    public static PaletteItem ForConnection(ConnectionInfo connection, bool isRecent) => new(PaletteItemKind.Connection, connection.Name)
    {
        Connection = connection,
        Host = connection.Hostname,
        Detail = PaletteSearch.FolderPath(connection),
        IsRecent = isRecent,
        IsFavorite = connection.Favorite,
    };

    public static PaletteItem ForCommand(PaletteCommand command) => new(PaletteItemKind.Command, command.Label)
    {
        Command = command,
        Detail = command.Category,
        Shortcut = command.Shortcut,
    };

    private static IReadOnlyList<string> SplitGesture(string gesture)
    {
        // "Ctrl++" is Ctrl and Plus; otherwise '+' separates the keys.
        var keys = new List<string>();
        var start = 0;
        for (var i = 0; i < gesture.Length; i++)
        {
            if (gesture[i] == '+' && i > start)
            {
                keys.Add(gesture[start..i]);
                start = i + 1;
            }
        }
        if (start < gesture.Length)
            keys.Add(gesture[start..]);
        return keys;
    }

    public override string ToString() => Title;
}

/// <summary>
/// Matching and ranking for the command palette. A query is split into words; every word must match one of the
/// item's fields (name, host, folder, description for connections; label and menu for commands). A word scores by
/// how it matches — whole field, prefix, word start, substring, or (in names only) an in-order fuzzy match — times
/// the field's weight (name 3, host 2, others 1). Recent connections get a bonus that decreases with age,
/// favourites a small one; ties put connections before commands, then sort by name.
/// </summary>
public static class PaletteSearch
{
    public const int MaxResults = 60;

    private const int NameWeight = 3;
    private const int HostWeight = 2;
    private const int OtherWeight = 1;

    /// <summary>Folder names from the top of the tree down to the connection's parent, joined with " / ".</summary>
    public static string FolderPath(ConnectionInfo connection)
    {
        var names = new List<string>();
        for (var parent = connection.Parent; parent is not null and not RootNodeInfo; parent = parent.Parent)
            names.Add(parent.Name);
        names.Reverse();
        return string.Join(" / ", names);
    }

    /// <summary>
    /// The palette rows for <paramref name="query"/>. Empty query: recent connections, favourites, the other
    /// connections in tree order, then the commands in menu order.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Search(
        string? query,
        IEnumerable<ConnectionInfo> connections,
        IReadOnlyList<ConnectionInfo> recent,
        IEnumerable<PaletteCommand> commands)
    {
        var all = connections.Where(c => c is not ContainerInfo).Distinct().ToList();
        var recentRank = new Dictionary<ConnectionInfo, int>();
        for (var i = 0; i < recent.Count; i++)
            recentRank.TryAdd(recent[i], i);

        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            var ordered = recent.Where(all.Contains)
                .Concat(all.Where(c => c.Favorite && !recentRank.ContainsKey(c)))
                .Concat(all.Where(c => !c.Favorite && !recentRank.ContainsKey(c)))
                .Select(c => PaletteItem.ForConnection(c, recentRank.ContainsKey(c)))
                .Concat(commands.Select(PaletteItem.ForCommand));
            return ordered.Take(MaxResults).ToList();
        }

        var scored = new List<(PaletteItem Item, int Score, int Order)>();
        var order = 0;
        foreach (var connection in all)
        {
            var score = ScoreConnection(connection, words);
            if (score <= 0)
                continue;
            if (recentRank.TryGetValue(connection, out var rank))
                score += Math.Max(5, 40 - rank * 5);
            if (connection.Favorite)
                score += 10;
            scored.Add((PaletteItem.ForConnection(connection, recentRank.ContainsKey(connection)), score, order++));
        }
        foreach (var command in commands)
        {
            var score = ScoreCommand(command, words);
            if (score > 0)
                scored.Add((PaletteItem.ForCommand(command), score, order++));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Item.Kind)
            .ThenBy(s => s.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Order)
            .Take(MaxResults)
            .Select(s => s.Item)
            .ToList();
    }

    public static int ScoreConnection(ConnectionInfo connection, IReadOnlyList<string> words) =>
        ScoreFields(words,
            (connection.Name, NameWeight, true),
            (connection.Hostname, HostWeight, false),
            (FolderPath(connection), OtherWeight, false),
            (connection.Description, OtherWeight, false));

    public static int ScoreCommand(PaletteCommand command, IReadOnlyList<string> words) =>
        ScoreFields(words, (command.Label, NameWeight, true), (command.Category, OtherWeight, false));

    private static int ScoreFields(IReadOnlyList<string> words, params (string? Text, int Weight, bool Fuzzy)[] fields)
    {
        var total = 0;
        foreach (var word in words)
        {
            var best = 0;
            foreach (var (text, weight, fuzzy) in fields)
                best = Math.Max(best, Match(text, word, fuzzy) * weight);
            if (best == 0)
                return 0;
            total += best;
        }
        return total;
    }

    /// <summary>How well <paramref name="word"/> matches <paramref name="text"/>: 100 equal … 10 fuzzy, 0 none.</summary>
    public static int Match(string? text, string word, bool fuzzy = true)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
            return 0;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        if (compare.Compare(text, word, options) == 0)
            return 100;
        var index = compare.IndexOf(text, word, options);
        if (index == 0)
            return 80;
        if (index > 0)
        {
            // A later word start ("demo" in "SSH - demo server") beats a match inside a word.
            for (var i = index; i > 0; i = i + 1 < text.Length ? compare.IndexOf(text, word, i + 1, options) : -1)
            {
                if (IsWordBoundary(text[i - 1]))
                    return 60;
            }
            return 40;
        }
        return fuzzy && word.Length >= 2 && IsSubsequence(text, word) ? 10 : 0;
    }

    private static bool IsWordBoundary(char c) => !char.IsLetterOrDigit(c);

    private static bool IsSubsequence(string text, string word)
    {
        var j = 0;
        for (var i = 0; i < text.Length && j < word.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(word[j]))
                j++;
        }
        return j == word.Length;
    }
}

/// <summary>
/// The command palette (Ctrl+K / Ctrl+Shift+P, docs/design-system.md §8): one search box over connections and the
/// menu commands. Enter connects / runs, Ctrl+Enter connects with options; the view handles keys and focus.
/// </summary>
public sealed class CommandPaletteViewModel : ReactiveObject
{
    private readonly Func<IEnumerable<ConnectionInfo>> _connections;
    private readonly Func<IReadOnlyList<ConnectionInfo>> _recent;
    private bool _isOpen;
    private string _query = string.Empty;
    private IReadOnlyList<PaletteItem> _results = [];
    private PaletteItem? _selected;
    private IReadOnlyList<PaletteCommand> _commandSnapshot = [];

    public CommandPaletteViewModel(Func<IEnumerable<ConnectionInfo>> connections, Func<IReadOnlyList<ConnectionInfo>> recent)
    {
        _connections = connections;
        _recent = recent;
    }

    /// <summary>The commands offered (the main window lists its menu); read each time the palette opens.</summary>
    public Func<IEnumerable<PaletteCommand>> Commands { get; set; } = () => [];

    /// <summary>Opens a session for a connection; the flag asks for the connect-with-options dialog.</summary>
    public Func<ConnectionInfo, bool, Task> Connect { get; set; } = (_, _) => Task.CompletedTask;

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public string Query
    {
        get => _query;
        set
        {
            this.RaiseAndSetIfChanged(ref _query, value ?? string.Empty);
            Refresh();
        }
    }

    public IReadOnlyList<PaletteItem> Results
    {
        get => _results;
        private set
        {
            this.RaiseAndSetIfChanged(ref _results, value);
            this.RaisePropertyChanged(nameof(HasResults));
        }
    }

    public bool HasResults => _results.Count > 0;

    public PaletteItem? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public void Open()
    {
        _commandSnapshot = Commands().Where(c => c.Command.CanExecute(c.Parameter)).ToList();
        _query = string.Empty;
        this.RaisePropertyChanged(nameof(Query));
        Refresh();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    /// <summary>Moves the selection by <paramref name="delta"/> rows, wrapping around.</summary>
    public void MoveSelection(int delta)
    {
        if (_results.Count == 0)
            return;
        var index = _selected is null ? (delta > 0 ? -1 : 0) : IndexOf(_selected);
        index = ((index + delta) % _results.Count + _results.Count) % _results.Count;
        Selected = _results[index];
    }

    /// <summary>Connects to / runs <paramref name="item"/> (the selection when null) and closes the palette.</summary>
    public async Task AcceptAsync(PaletteItem? item = null, bool withOptions = false)
    {
        item ??= _selected;
        if (item is null)
            return;
        Close();
        if (item.Connection is { } connection)
        {
            await Connect(connection, withOptions);
        }
        else if (item.Command is { } command && command.Command.CanExecute(command.Parameter))
        {
            command.Command.Execute(command.Parameter);
        }
    }

    private int IndexOf(PaletteItem item)
    {
        for (var i = 0; i < _results.Count; i++)
        {
            if (ReferenceEquals(_results[i], item))
                return i;
        }
        return -1;
    }

    private void Refresh()
    {
        Results = PaletteSearch.Search(_query, _connections(), _recent(), _commandSnapshot);
        Selected = _results.FirstOrDefault();
    }
}
