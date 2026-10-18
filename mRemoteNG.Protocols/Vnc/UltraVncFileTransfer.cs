using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using mRemoteNG.Protocols.Vnc.Rfb;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>
/// UltraVNC file transfer (rfbFileTransfer messages, as served by UltraVNC and by libvncserver servers such as
/// x11vnc -ultrafilexfer) over a running <see cref="RfbClient"/>. Paths are Windows-style ("C:\dir\file");
/// libvncserver maps them to "/dir/file". One operation runs at a time; every reply has a timeout because
/// servers ignore requests they do not support.
/// </summary>
public sealed class UltraVncFileTransfer : IRemoteFileSession
{
    public const string Root = @"C:\";

    private static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(30);

    // RFB_FIND_DATA (Win32 WIN32_FIND_DATA, little-endian) offsets
    private const int FindAttributes = 0;
    private const int FindLastWriteLow = 20;
    private const int FindLastWriteHigh = 24;
    private const int FindSizeHigh = 28;
    private const int FindSizeLow = 32;
    private const int FindName = 44;
    private const uint DirectoryAttribute = 0x10;

    private readonly RfbClient _client;
    private readonly ILogger _logger;
    private readonly Channel<RfbFileTransferMessage> _replies =
        Channel.CreateUnbounded<RfbFileTransferMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;

    private UltraVncFileTransfer(RfbClient client, ILogger? logger)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
        _client.FileTransferReceived += OnFileTransferReceived;
        _client.Disconnected += OnDisconnected;
        if (_client.Completion.IsCompleted)
            _replies.Writer.TryComplete();
    }

    /// <summary>How long to wait for each reply from the server.</summary>
    internal TimeSpan ReplyTimeout { get; private init; } = DefaultReplyTimeout;

    public bool IsConnected => !_disposed && !_client.Completion.IsCompleted;

    public string HomeDirectory => Root;

    /// <summary>
    /// Asks the server for file access. Throws <see cref="UnauthorizedAccessException"/> when it refuses. Note that
    /// libvncserver servers without file transfer enabled close the whole connection instead of refusing.
    /// </summary>
    internal static async Task<UltraVncFileTransfer> OpenAsync(RfbClient client, ILogger? logger = null,
        TimeSpan? replyTimeout = null, CancellationToken ct = default)
    {
        var session = new UltraVncFileTransfer(client, logger) { ReplyTimeout = replyTimeout ?? DefaultReplyTimeout };
        try
        {
            await session.RunAsync(async () =>
            {
                await session.SendAsync(RfbFileTransfer.AbortFileTransfer, RfbFileTransfer.ProtocolVersion, 0, []).ConfigureAwait(false);
                var reply = await session.ReceiveAsync(m => m.ContentType == RfbFileTransfer.FileTransferAccess, ct).ConfigureAwait(false);
                if (reply.Size == RfbFileTransfer.Error)
                    throw new UnauthorizedAccessException("The VNC server does not allow file transfer.");
                return true;
            }, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default) =>
        RunAsync(() => ListCoreAsync(path, ct), ct);

    private async Task<IReadOnlyList<RemoteFileEntry>> ListCoreAsync(string path, CancellationToken ct)
    {
        var directory = WithSeparator(path);
        await SendAsync(RfbFileTransfer.DirContentRequest, RfbFileTransfer.RequestDirContent, 0, Encode(directory)).ConfigureAwait(false);

        var entries = new List<RemoteFileEntry>();
        var first = true;
        while (true)
        {
            var packet = await ReceiveAsync(m => m.ContentType == RfbFileTransfer.DirPacket, ct).ConfigureAwait(false);
            if (packet.ContentParam == 0)
                break; // end of listing
            if (first)
            {
                // The server first echoes the directory; an empty echo means it could not be opened.
                first = false;
                if (packet.ContentParam == RfbFileTransfer.AnswerDirectory && packet.Data.Length == 0)
                    throw new DirectoryNotFoundException($"The VNC server could not open {path}.");
                continue;
            }
            if (packet.ContentParam is RfbFileTransfer.AnswerDirectory or RfbFileTransfer.AnswerFile
                && ParseFindData(packet.Data, directory) is { } entry)
                entries.Add(entry);
        }

        return entries
            .OrderBy(e => !e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task UploadFileAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            await using var source = File.OpenRead(localPath);
            long total = source.Length;
            var modified = File.GetLastWriteTime(localPath).ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture);
            await SendAsync(RfbFileTransfer.FileTransferOffer, 0, (uint)total, Encode(remotePath + "," + modified),
                (uint)(total >> 32)).ConfigureAwait(false);
            var accept = await ReceiveAsync(m => m.ContentType == RfbFileTransfer.FileAcceptHeader, ct).ConfigureAwait(false);
            if (accept.Size == RfbFileTransfer.Error)
                throw new IOException($"The VNC server could not create {remotePath}.");

            progress?.Report(new TransferProgress(0, total));
            var buffer = new byte[RfbFileTransfer.BlockSize];
            long sent = 0;
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ThrowIfServerAborted();
                    await SendAsync(RfbFileTransfer.FilePacket, 0, 0, buffer.AsSpan(0, read)).ConfigureAwait(false);
                    sent += read;
                    progress?.Report(new TransferProgress(sent, total));
                }
                await SendAsync(RfbFileTransfer.EndOfFile, 0, 0, []).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ServerAbortedException)
            {
                await TryAbortAsync().ConfigureAwait(false);
                throw;
            }
            progress?.Report(new TransferProgress(total, total));
            return true;
        }, ct);

    public Task DownloadFileAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            // Size 1 asks for zlib-compressed packets; servers that cannot compress send them raw.
            await SendAsync(RfbFileTransfer.FileTransferRequest, 0, 1, Encode(remotePath)).ConfigureAwait(false);
            var header = await ReceiveAsync(m => m.ContentType == RfbFileTransfer.FileHeader, ct).ConfigureAwait(false);
            if (header.Size == RfbFileTransfer.Error)
                throw new FileNotFoundException($"The VNC server could not open {remotePath}.", remotePath);

            long total = ((long)header.SizeHigh << 32) | header.Size;
            progress?.Report(new TransferProgress(0, total));
            try
            {
                long received;
                await using (var target = File.Create(localPath))
                {
                    await SendAsync(RfbFileTransfer.FileHeader, 0, 0, []).ConfigureAwait(false);
                    while (true)
                    {
                        var packet = await ReceiveAsync(
                            m => m.ContentType is RfbFileTransfer.FilePacket or RfbFileTransfer.EndOfFile, ct).ConfigureAwait(false);
                        if (packet.ContentType == RfbFileTransfer.EndOfFile)
                            break;
                        if (packet.Size == 1)
                            await Inflate(packet.Data, target, ct).ConfigureAwait(false);
                        else
                            await target.WriteAsync(packet.Data, ct).ConfigureAwait(false);
                        progress?.Report(new TransferProgress(target.Position, total));
                    }
                    received = target.Position;
                }
                if (received != total)
                    _logger.LogWarning("UltraVNC download of {Path}: received {Received} of {Total} bytes", remotePath, received, total);
                progress?.Report(new TransferProgress(received, total));
            }
            catch (Exception ex)
            {
                if (ex is not ServerAbortedException)
                    await TryAbortAsync().ConfigureAwait(false);
                TryDeleteLocal(localPath);
                throw;
            }
            return true;
        }, ct);

    public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            if (!await CommandAsync(RfbFileTransfer.CommandDirCreate, RfbFileTransfer.AnswerDirCreate, remotePath, ct).ConfigureAwait(false))
                throw new IOException($"The VNC server could not create {remotePath}.");
            return true;
        }, ct);

    /// <summary>Deletes a file, or a directory together with its contents.</summary>
    public Task DeleteAsync(RemoteFileEntry entry, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            await DeleteCoreAsync(entry, ct).ConfigureAwait(false);
            return true;
        }, ct);

    private async Task DeleteCoreAsync(RemoteFileEntry entry, CancellationToken ct)
    {
        if (!entry.IsDirectory)
        {
            if (!await CommandAsync(RfbFileTransfer.CommandFileDelete, RfbFileTransfer.AnswerFileDelete, entry.FullPath, ct).ConfigureAwait(false))
                throw new IOException($"The VNC server could not delete {entry.FullPath}.");
            return;
        }

        foreach (var child in await ListCoreAsync(entry.FullPath, ct).ConfigureAwait(false))
            await DeleteCoreAsync(child, ct).ConfigureAwait(false);
        // libvncserver removes (empty) directories with the file delete command and ignores the directory delete
        // command; UltraVNC needs the directory delete command.
        if (await CommandAsync(RfbFileTransfer.CommandFileDelete, RfbFileTransfer.AnswerFileDelete, entry.FullPath, ct).ConfigureAwait(false)
            || await CommandAsync(RfbFileTransfer.CommandDirDelete, RfbFileTransfer.AnswerDirDelete, entry.FullPath, ct).ConfigureAwait(false))
            return;
        throw new IOException($"The VNC server could not delete {entry.FullPath}.");
    }

    /// <summary>Runs a file command; true when the server reports success.</summary>
    private async Task<bool> CommandAsync(byte command, byte answer, string path, CancellationToken ct)
    {
        await SendAsync(RfbFileTransfer.Command, command, 0, Encode(path)).ConfigureAwait(false);
        var reply = await ReceiveAsync(m => m.ContentType == RfbFileTransfer.CommandReturn && m.ContentParam == answer, ct).ConfigureAwait(false);
        return reply.Size != RfbFileTransfer.Error;
    }

    public string Combine(string directory, string name) => WithSeparator(directory) + name;

    public string Parent(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var index = trimmed.LastIndexOf('\\');
        if (index < 0)
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : Root;
        return index <= 2 && trimmed.Length > 1 && trimmed[1] == ':' ? trimmed[..(index + 1)] : trimmed[..index];
    }

    public bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && name.IndexOfAny(InvalidNameChars) < 0;

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0'];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.FileTransferReceived -= OnFileTransferReceived;
        _client.Disconnected -= OnDisconnected;
        _replies.Writer.TryComplete();
    }

    // ── Plumbing ──────────────────────────────────────────────────────────

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
                throw new IOException("The VNC connection is closed.");
            // Late replies to an operation that timed out or was cancelled.
            while (_replies.Reader.TryRead(out _)) { }
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task SendAsync(byte contentType, byte contentParam, uint size, ReadOnlySpan<byte> data, uint? sizeHigh = null) =>
        _client.SendFileTransferAsync(contentType, contentParam, size, data, sizeHigh);

    private async Task<RfbFileTransferMessage> ReceiveAsync(Func<RfbFileTransferMessage, bool> match, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        try
        {
            while (true)
            {
                var message = await _replies.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (match(message))
                    return message;
                if (message.ContentType == RfbFileTransfer.AbortFileTransfer)
                    throw new ServerAbortedException(message);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The VNC server did not answer the file transfer request.");
        }
        catch (ChannelClosedException)
        {
            throw new IOException("The VNC connection is closed.");
        }
    }

    private void ThrowIfServerAborted()
    {
        while (_replies.Reader.TryRead(out var message))
        {
            if (message.ContentType == RfbFileTransfer.AbortFileTransfer)
                throw new ServerAbortedException(message);
        }
    }

    private async Task TryAbortAsync()
    {
        try
        {
            if (IsConnected)
                await SendAsync(RfbFileTransfer.AbortFileTransfer, 0, 0, []).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not abort the UltraVNC file transfer");
        }
    }

    private void OnFileTransferReceived(object? sender, RfbFileTransferMessage message) => _replies.Writer.TryWrite(message);

    private void OnDisconnected(object? sender, Exception? error) => _replies.Writer.TryComplete();

    private static async Task Inflate(byte[] data, Stream target, CancellationToken ct)
    {
        try
        {
            await using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            await zlib.CopyToAsync(target, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new IOException("The VNC server sent corrupt compressed file data.", ex);
        }
    }

    private void TryDeleteLocal(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not remove partial download {Path}", path); }
    }

    private static string WithSeparator(string path) => path.EndsWith('\\') ? path : path + "\\";

    private static byte[] Encode(string path) => Encoding.UTF8.GetBytes(path);

    /// <summary>Parses an RFB_FIND_DATA directory entry; null for "." and "..".</summary>
    internal static RemoteFileEntry? ParseFindData(ReadOnlySpan<byte> data, string directory)
    {
        if (data.Length <= FindName)
            return null;
        var name = RfbClient.DecodeText(data[FindName..]);
        if (name is "" or "." or "..")
            return null;
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[FindAttributes..]);
        var isDirectory = (attributes & DirectoryAttribute) != 0;
        long length = isDirectory
            ? 0
            : ((long)BinaryPrimitives.ReadUInt32LittleEndian(data[FindSizeHigh..]) << 32)
              | BinaryPrimitives.ReadUInt32LittleEndian(data[FindSizeLow..]);
        var modified = LastWriteTime(
            BinaryPrimitives.ReadUInt32LittleEndian(data[FindLastWriteLow..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[FindLastWriteHigh..]));
        return new RemoteFileEntry(name, WithSeparator(directory) + name, isDirectory, false, length, modified);
    }

    /// <summary>
    /// UltraVNC sends a Windows FILETIME; libvncserver puts Unix seconds in the low word and leaves the high word 0.
    /// </summary>
    private static DateTime LastWriteTime(uint low, uint high)
    {
        try
        {
            return high == 0
                ? DateTimeOffset.FromUnixTimeSeconds(low).LocalDateTime
                : DateTime.FromFileTime(((long)high << 32) | low);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private sealed class ServerAbortedException(RfbFileTransferMessage message)
        : IOException(message.Data.Length > 0
            ? $"The VNC server aborted the file transfer: {RfbClient.DecodeText(message.Data)}"
            : "The VNC server aborted the file transfer.");
}
