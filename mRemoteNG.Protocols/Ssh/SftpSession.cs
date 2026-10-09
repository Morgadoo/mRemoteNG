using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>An entry in a remote directory listing.</summary>
public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymbolicLink,
    long Length,
    DateTime LastWriteTime);

/// <summary>Bytes transferred so far out of the total (total is 0 when unknown).</summary>
public readonly record struct TransferProgress(long BytesTransferred, long TotalBytes)
{
    public double Percent => TotalBytes > 0 ? Math.Min(100.0, BytesTransferred * 100.0 / TotalBytes) : 0;
}

/// <summary>
/// An SFTP connection with host key verification, used by the SSH file transfer dialog.
/// All operations run on the thread pool and may be awaited from the UI thread.
/// </summary>
public sealed class SftpSession : IDisposable
{
    private readonly SshConnector _connector;
    private readonly ILogger<SftpSession> _logger;
    private SftpClient? _client;

    public SftpSession(IHostKeyVerifier hostKeyVerifier, ISshUserPrompt userPrompt, ILogger<SftpSession>? logger = null)
    {
        _logger = logger ?? NullLogger<SftpSession>.Instance;
        _connector = new SshConnector(hostKeyVerifier, userPrompt, _logger);
    }

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>The remote working directory after login (normally the user's home).</summary>
    public string HomeDirectory { get; private set; } = "/";

    public async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        await DisconnectAsync();
        _client = await _connector.ConnectAsync(parameters, info => new SftpClient(info), ct);
        HomeDirectory = _client.WorkingDirectory;
        _logger.LogInformation("SFTP connected to {Host}", parameters.DisplayName);
    }

    /// <summary>Lists a directory: folders first, then files, by name; "." and ".." are omitted.</summary>
    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        var entries = new List<RemoteFileEntry>();
        await foreach (var file in client.ListDirectoryAsync(path, ct))
        {
            if (file.Name is "." or "..")
                continue;
            bool isDirectory = file.IsDirectory;
            if (file.IsSymbolicLink)
                isDirectory = await IsDirectoryTargetAsync(client, file.FullName, ct);
            entries.Add(new RemoteFileEntry(file.Name, file.FullName, isDirectory, file.IsSymbolicLink,
                file.Length, file.LastWriteTime));
        }

        return entries
            .OrderBy(e => !e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Uploads a local file, replacing an existing remote file.</summary>
    public async Task UploadFileAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        var client = RequireClient();
        await using var source = File.OpenRead(localPath);
        long total = source.Length;
        progress?.Report(new TransferProgress(0, total));
        await client.UploadFileAsync(source, remotePath, true,
            new SyncProgress<UploadFileProgressReport>(r => progress?.Report(new TransferProgress((long)r.TotalBytesUploaded, total))),
            ct);
        progress?.Report(new TransferProgress(total, total));
    }

    /// <summary>Downloads a remote file; a partially written local file is removed on failure.</summary>
    public async Task DownloadFileAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        var client = RequireClient();
        var attributes = await client.GetAttributesAsync(remotePath, ct);
        long total = attributes.Size;
        progress?.Report(new TransferProgress(0, total));
        try
        {
            await using (var target = File.Create(localPath))
            {
                await client.DownloadFileAsync(remotePath, target,
                    new SyncProgress<DownloadFileProgressReport>(r => progress?.Report(new TransferProgress((long)r.TotalBytesDownloaded, total))),
                    ct);
            }
            progress?.Report(new TransferProgress(total, total));
        }
        catch
        {
            TryDeleteLocal(localPath);
            throw;
        }
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default) =>
        await RequireClient().CreateDirectoryAsync(remotePath, ct);

    /// <summary>Deletes a file, or a directory together with its contents.</summary>
    public async Task DeleteAsync(RemoteFileEntry entry, CancellationToken ct = default)
    {
        var client = RequireClient();
        if (!entry.IsDirectory || entry.IsSymbolicLink)
        {
            await client.DeleteFileAsync(entry.FullPath, ct);
            return;
        }

        foreach (var child in await ListDirectoryAsync(entry.FullPath, ct))
            await DeleteAsync(child, ct);
        await client.DeleteDirectoryAsync(entry.FullPath, ct);
    }

    public Task DisconnectAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
            return Task.CompletedTask;
        return Task.Run(() =>
        {
            try { client.Disconnect(); }
            catch (Exception ex) { _logger.LogDebug(ex, "SFTP disconnect failed"); }
            client.Dispose();
        });
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    /// <summary>Joins a remote directory and a name with '/'.</summary>
    public static string CombinePath(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    /// <summary>The parent of a remote path ("/" for top-level paths).</summary>
    public static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        int index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private SftpClient RequireClient() =>
        _client is { IsConnected: true } client
            ? client
            : throw new InvalidOperationException("The SFTP session is not connected.");

    private static async Task<bool> IsDirectoryTargetAsync(SftpClient client, string path, CancellationToken ct)
    {
        try
        {
            return (await client.GetAttributesAsync(path, ct)).IsDirectory;
        }
        catch (Exception)
        {
            return false; // dangling link or no permission
        }
    }

    private void TryDeleteLocal(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not remove partial download {Path}", path); }
    }

    /// <summary>Reports synchronously; the caller's <see cref="IProgress{T}"/> decides where to marshal.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
