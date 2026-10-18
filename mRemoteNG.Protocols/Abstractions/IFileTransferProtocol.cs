namespace mRemoteNG.Protocols.Abstractions;

/// <summary>A session that can browse and transfer files over its own connection (UltraVNC file transfer).</summary>
public interface IFileTransferProtocol
{
    /// <summary>
    /// Asks the server for file access over the current connection. Throws <see cref="UnauthorizedAccessException"/>
    /// when the server refuses, <see cref="InvalidOperationException"/> while not connected.
    /// </summary>
    Task<IRemoteFileSession> OpenFileTransferAsync(CancellationToken ct = default);
}
