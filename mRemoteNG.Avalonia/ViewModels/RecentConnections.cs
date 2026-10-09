using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// The connections opened most recently (most recent first), by their constant id so the list survives reloading
/// the file. Feeds the empty session area's cards and the command palette; persisted in the settings as one id
/// per line (<see cref="Serialize"/> / <see cref="Load"/>).
/// </summary>
public sealed class RecentConnections
{
    public const int Capacity = 12;

    private readonly List<string> _ids = [];

    public event EventHandler? Changed;

    /// <summary>The ids, most recent first.</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>Moves <paramref name="connection"/> to the front. Folders and quick connects are not remembered.</summary>
    public void Add(ConnectionInfo connection)
    {
        if (connection is ContainerInfo || connection.IsQuickConnect || string.IsNullOrEmpty(connection.ConstantID))
            return;
        if (_ids.Count > 0 && _ids[0] == connection.ConstantID)
            return;
        _ids.Remove(connection.ConstantID);
        _ids.Insert(0, connection.ConstantID);
        if (_ids.Count > Capacity)
            _ids.RemoveRange(Capacity, _ids.Count - Capacity);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The remembered connections that exist below <paramref name="root"/>, most recent first.</summary>
    public IReadOnlyList<ConnectionInfo> Resolve(ConnectionInfo? root)
    {
        if (root is not ContainerInfo container || _ids.Count == 0)
            return [];
        var byId = new Dictionary<string, ConnectionInfo>(StringComparer.Ordinal);
        foreach (var connection in container.GetRecursiveChildList())
        {
            if (connection is not ContainerInfo)
                byId.TryAdd(connection.ConstantID, connection);
        }
        return _ids.Select(id => byId.GetValueOrDefault(id)).OfType<ConnectionInfo>().ToList();
    }

    public string Serialize() => string.Join('\n', _ids);

    public void Load(string? serialized)
    {
        _ids.Clear();
        if (string.IsNullOrWhiteSpace(serialized))
            return;
        foreach (var id in serialized.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!_ids.Contains(id) && _ids.Count < Capacity)
                _ids.Add(id);
        }
    }
}
