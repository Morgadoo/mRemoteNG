namespace mRemoteNG.Protocols.Abstractions;

/// <summary>
/// A graphical session that can redraw its whole display on demand (legacy "Refresh Screen"), e.g. after the
/// local copy got out of step with the remote desktop.
/// </summary>
public interface IRefreshableProtocol
{
    /// <summary>Asks the server for a complete (non-incremental) screen update. Does nothing while not connected.</summary>
    Task RefreshScreenAsync(CancellationToken ct = default);
}
