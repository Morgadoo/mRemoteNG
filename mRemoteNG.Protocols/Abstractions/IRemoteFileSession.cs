using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Abstractions;

/// <summary>
/// A connected remote file system (SFTP, UltraVNC file transfer) as browsed by the file transfer window.
/// Operations may be awaited from the UI thread; they do not run concurrently.
/// </summary>
public interface IRemoteFileSession : IDisposable
{
    bool IsConnected { get; }

    /// <summary>The directory shown first.</summary>
    string HomeDirectory { get; }

    /// <summary>Lists a directory: folders first, then files, by name; "." and ".." are omitted.</summary>
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);

    Task UploadFileAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Downloads a remote file; a partially written local file is removed on failure.</summary>
    Task DownloadFileAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);

    Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);

    Task DeleteAsync(RemoteFileEntry entry, CancellationToken ct = default);

    /// <summary>Joins a remote directory and a name.</summary>
    string Combine(string directory, string name);

    /// <summary>The parent of a remote path (the path itself at the top).</summary>
    string Parent(string path);

    /// <summary>True when <paramref name="name"/> can be a single path component.</summary>
    bool IsValidName(string name);
}
