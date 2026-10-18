using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using mRemoteNG.Protocols.Ssh;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>UltraVNC file transfer and text chat against an in-process scripted server.</summary>
public sealed class UltraVncFileTransferTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeRfbServer _server = new();
    private readonly TcpClient _tcp = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mrng-ultravnc-ft-" + Guid.NewGuid().ToString("N"));
    private RfbClient _client = null!;

    public async Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(_directory);
        await _tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        await _server.AcceptAsync();
        var clientTask = RfbClient.ConnectAsync(_tcp.GetStream(), new RfbClientOptions());
        await _server.NegotiateVersionAsync("003.008");
        await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
        await _server.ReceiveAsync(1);
        await _server.SendAsync(new RfbBytes().U32(0));
        await _server.InitialiseAsync(4, 2, "ultra");
        _client = await clientTask.WaitAsync(Timeout);
        _client.Start();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _tcp.Dispose();
        await _server.DisposeAsync();
        try { System.IO.Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    // ── Access ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_AsksForAccessWithTheFileTransferVersion()
    {
        var session = await OpenAsync();

        session.IsConnected.Should().BeTrue();
        session.HomeDirectory.Should().Be(@"C:\");
    }

    [Fact]
    public async Task Open_WhenServerRefuses_ThrowsUnauthorizedAccess()
    {
        var open = UltraVncFileTransfer.OpenAsync(_client);
        var request = await ReadFileTransferAsync();
        request.Type.Should().Be(RfbFileTransfer.AbortFileTransfer);
        await SendFileTransferAsync(RfbFileTransfer.FileTransferAccess, 0, RfbFileTransfer.Error);

        var act = () => open.WaitAsync(Timeout);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Open_WhenServerDoesNotAnswer_TimesOut()
    {
        var act = () => UltraVncFileTransfer.OpenAsync(_client, replyTimeout: TimeSpan.FromMilliseconds(200)).WaitAsync(Timeout);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Open_WhenConnectionCloses_FailsAndReportsDisconnected()
    {
        var open = UltraVncFileTransfer.OpenAsync(_client);
        await ReadFileTransferAsync();
        _server.CloseConnection();

        var act = () => open.WaitAsync(Timeout);

        await act.Should().ThrowAsync<IOException>();
    }

    // ── Listing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListDirectory_ParsesEntries_SkipsDotEntries_AndSortsFoldersFirst()
    {
        var session = await OpenAsync();
        var modified = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        var list = session.ListDirectoryAsync(@"C:\home\user");
        var request = await ReadFileTransferAsync();
        request.Type.Should().Be(RfbFileTransfer.DirContentRequest);
        request.Param.Should().Be(RfbFileTransfer.RequestDirContent);
        request.Text.Should().Be(@"C:\home\user\");

        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0, Encoding.ASCII.GetBytes(@"C:\home\user\"));
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0, FindData(".", true, 0, 0, 0));
        // A Windows server: FILETIME and a size above 4 GiB.
        var fileTime = modified.ToFileTimeUtc();
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0,
            FindData("big.iso", false, 5_000_000_000, (uint)fileTime, (uint)(fileTime >> 32)));
        // libvncserver: Unix seconds in the low word.
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0,
            FindData("Docs", true, 0, (uint)new DateTimeOffset(modified).ToUnixTimeSeconds(), 0));
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0, FindData("a.txt", false, 12, 0, 0));
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, 0, 0);

        var entries = await list.WaitAsync(Timeout);

        entries.Select(e => e.Name).Should().Equal("Docs", "a.txt", "big.iso");
        entries[0].Should().BeEquivalentTo(new RemoteFileEntry("Docs", @"C:\home\user\Docs", true, false, 0, modified.ToLocalTime()));
        entries[1].Length.Should().Be(12);
        entries[2].Length.Should().Be(5_000_000_000);
        entries[2].LastWriteTime.Should().Be(modified.ToLocalTime());
        entries[2].FullPath.Should().Be(@"C:\home\user\big.iso");
    }

    [Fact]
    public async Task ListDirectory_WhenServerCannotOpenIt_ThrowsDirectoryNotFound()
    {
        var session = await OpenAsync();

        var list = session.ListDirectoryAsync(@"C:\missing");
        await ReadFileTransferAsync();
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0);
        var act = () => list.WaitAsync(Timeout);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task ListDirectory_IgnoresLateRepliesToAnEarlierRequest()
    {
        var session = await OpenAsync();
        await SendFileTransferAsync(RfbFileTransfer.CommandReturn, RfbFileTransfer.AnswerDirCreate, 0);
        await SendFileTransferAsync(RfbFileTransfer.FileChecksums, 0, 0, new byte[8]);

        var list = session.ListDirectoryAsync(@"C:\");
        (await ReadFileTransferAsync()).Text.Should().Be(@"C:\");
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0, Encoding.ASCII.GetBytes(@"C:\"));
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0, FindData("etc", true, 0, 0, 0));
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, 0, 0);

        (await list.WaitAsync(Timeout)).Select(e => e.FullPath).Should().Equal(@"C:\etc");
    }

    // ── Download ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_AcceptsCompressedAndRawPackets()
    {
        var session = await OpenAsync();
        var target = Path.Combine(_directory, "out.bin");
        var content = Enumerable.Range(0, 20_000).Select(i => (byte)(i % 251)).ToArray();
        var reports = new List<TransferProgress>();

        var download = session.DownloadFileAsync(@"C:\data\in.bin", target, new SyncProgress(reports));
        var request = await ReadFileTransferAsync();
        request.Type.Should().Be(RfbFileTransfer.FileTransferRequest);
        request.Size.Should().Be(1u, "the client asks for compression");
        request.Text.Should().Be(@"C:\data\in.bin");

        await _server.SendAsync(new RfbBytes()
            .U8(RfbClientMessage.FileTransfer).U8(RfbFileTransfer.FileHeader).U8(0).U8(0)
            .U32((uint)content.Length).String("in.bin,01/02/2024 10:20").U32(0));
        var start = await ReadFileTransferAsync();
        start.Type.Should().Be(RfbFileTransfer.FileHeader);
        start.Size.Should().NotBe(RfbFileTransfer.Error);

        await SendFileTransferAsync(RfbFileTransfer.FilePacket, 0, 1, Compress(content.AsSpan(0, 8192)));
        await SendFileTransferAsync(RfbFileTransfer.FilePacket, 0, 0, content[8192..16384]);
        await SendFileTransferAsync(RfbFileTransfer.FilePacket, 0, 1, Compress(content.AsSpan(16384)));
        await SendFileTransferAsync(RfbFileTransfer.EndOfFile, 0, 0);
        await download.WaitAsync(Timeout);

        (await File.ReadAllBytesAsync(target)).Should().Equal(content);
        reports.Should().Contain(new TransferProgress(8192, content.Length));
        reports[^1].Should().Be(new TransferProgress(content.Length, content.Length));
    }

    [Fact]
    public async Task Download_WhenServerCannotOpenTheFile_ThrowsFileNotFound()
    {
        var session = await OpenAsync();
        var target = Path.Combine(_directory, "never.bin");

        var download = session.DownloadFileAsync(@"C:\nope", target);
        await ReadFileTransferAsync();
        // A failed header has no high size word.
        await SendFileTransferAsync(RfbFileTransfer.FileHeader, 0, RfbFileTransfer.Error, Encoding.ASCII.GetBytes("nope"));
        var act = () => download.WaitAsync(Timeout);

        await act.Should().ThrowAsync<FileNotFoundException>();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Download_WhenServerAborts_RemovesThePartialFile()
    {
        var session = await OpenAsync();
        var target = Path.Combine(_directory, "partial.bin");

        var download = session.DownloadFileAsync(@"C:\big", target);
        await ReadFileTransferAsync();
        await _server.SendAsync(new RfbBytes()
            .U8(RfbClientMessage.FileTransfer).U8(RfbFileTransfer.FileHeader).U8(0).U8(0)
            .U32(100).String("big,01/02/2024 10:20").U32(0));
        await ReadFileTransferAsync();
        await SendFileTransferAsync(RfbFileTransfer.FilePacket, 0, 0, new byte[10]);
        await SendFileTransferAsync(RfbFileTransfer.AbortFileTransfer, 0, 0, Encoding.ASCII.GetBytes("read error"));
        var act = () => download.WaitAsync(Timeout);

        (await act.Should().ThrowAsync<IOException>()).WithMessage("*read error*");
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Download_WhenCancelled_AbortsOnTheServer()
    {
        var session = await OpenAsync();
        var target = Path.Combine(_directory, "cancel.bin");
        using var cts = new CancellationTokenSource();

        var download = session.DownloadFileAsync(@"C:\big", target, ct: cts.Token);
        await ReadFileTransferAsync();
        await _server.SendAsync(new RfbBytes()
            .U8(RfbClientMessage.FileTransfer).U8(RfbFileTransfer.FileHeader).U8(0).U8(0)
            .U32(100).String("big,01/02/2024 10:20").U32(0));
        await ReadFileTransferAsync();
        cts.Cancel();
        var act = () => download.WaitAsync(Timeout);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadFileTransferAsync()).Type.Should().Be(RfbFileTransfer.AbortFileTransfer);
        File.Exists(target).Should().BeFalse();
    }

    // ── Upload ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_OffersTheFile_ThenSendsBlocksAndEndOfFile()
    {
        var session = await OpenAsync();
        var source = Path.Combine(_directory, "up.bin");
        var content = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7)).ToArray();
        await File.WriteAllBytesAsync(source, content);
        File.SetLastWriteTime(source, new DateTime(2023, 12, 31, 23, 59, 0));

        var upload = session.UploadFileAsync(source, @"C:\tmp\up.bin");
        var offer = await ReadFileTransferAsync();
        offer.Type.Should().Be(RfbFileTransfer.FileTransferOffer);
        offer.Size.Should().Be((uint)content.Length);
        offer.Text.Should().Be(@"C:\tmp\up.bin,12/31/2023 23:59");
        offer.SizeHigh.Should().Be(0u);
        await SendFileTransferAsync(RfbFileTransfer.FileAcceptHeader, 0, 0, offer.Data);

        var received = new List<byte>();
        var blocks = new List<int>();
        while (true)
        {
            var message = await ReadFileTransferAsync();
            if (message.Type == RfbFileTransfer.EndOfFile)
                break;
            message.Type.Should().Be(RfbFileTransfer.FilePacket);
            message.Size.Should().Be(0u, "blocks are sent uncompressed");
            blocks.Add(message.Data.Length);
            received.AddRange(message.Data);
        }
        await upload.WaitAsync(Timeout);

        blocks.Should().Equal(8192, 8192, 20_000 - 16384);
        received.Should().Equal(content);
    }

    [Fact]
    public async Task Upload_WhenServerRejectsTheOffer_Throws()
    {
        var session = await OpenAsync();
        var source = Path.Combine(_directory, "denied.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        var upload = session.UploadFileAsync(source, @"C:\readonly\denied.bin");
        await ReadFileTransferAsync();
        await SendFileTransferAsync(RfbFileTransfer.FileAcceptHeader, 0, RfbFileTransfer.Error);
        var act = () => upload.WaitAsync(Timeout);

        await act.Should().ThrowAsync<IOException>().WithMessage("*could not create*");
    }

    // ── Commands ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDirectory_SendsTheCommand_AndReportsFailure()
    {
        var session = await OpenAsync();

        var ok = session.CreateDirectoryAsync(@"C:\tmp\new");
        var command = await ReadFileTransferAsync();
        command.Type.Should().Be(RfbFileTransfer.Command);
        command.Param.Should().Be(RfbFileTransfer.CommandDirCreate);
        command.Text.Should().Be(@"C:\tmp\new");
        await SendFileTransferAsync(RfbFileTransfer.CommandReturn, RfbFileTransfer.AnswerDirCreate, 0, command.Data);
        await ok.WaitAsync(Timeout);

        var failing = session.CreateDirectoryAsync(@"C:\tmp\new");
        await ReadFileTransferAsync();
        await SendFileTransferAsync(RfbFileTransfer.CommandReturn, RfbFileTransfer.AnswerDirCreate, RfbFileTransfer.Error);
        var act = () => failing.WaitAsync(Timeout);
        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Delete_Directory_DeletesContentsFirst_AndFallsBackToTheDirectoryCommand()
    {
        var session = await OpenAsync();

        var delete = session.DeleteAsync(new RemoteFileEntry("old", @"C:\tmp\old", true, false, 0, DateTime.MinValue));
        (await ReadFileTransferAsync()).Text.Should().Be(@"C:\tmp\old\");
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0, Encoding.ASCII.GetBytes(@"C:\tmp\old\"));
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, RfbFileTransfer.AnswerDirectory, 0, FindData("f.txt", false, 1, 0, 0));
        await SendFileTransferAsync(RfbFileTransfer.DirPacket, 0, 0);

        var deleteFile = await ReadFileTransferAsync();
        (deleteFile.Param, deleteFile.Text).Should().Be((RfbFileTransfer.CommandFileDelete, @"C:\tmp\old\f.txt"));
        await SendFileTransferAsync(RfbFileTransfer.CommandReturn, RfbFileTransfer.AnswerFileDelete, 0);

        var deleteAsFile = await ReadFileTransferAsync();
        (deleteAsFile.Param, deleteAsFile.Text).Should().Be((RfbFileTransfer.CommandFileDelete, @"C:\tmp\old"));
        await SendFileTransferAsync(RfbFileTransfer.CommandReturn, RfbFileTransfer.AnswerFileDelete, RfbFileTransfer.Error);

        var deleteDirectory = await ReadFileTransferAsync();
        (deleteDirectory.Param, deleteDirectory.Text).Should().Be((RfbFileTransfer.CommandDirDelete, @"C:\tmp\old"));
        await SendFileTransferAsync(RfbFileTransfer.CommandReturn, RfbFileTransfer.AnswerDirDelete, 0);

        await delete.WaitAsync(Timeout);
    }

    // ── Paths ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\", "a", @"C:\a")]
    [InlineData(@"C:\dir", "a", @"C:\dir\a")]
    [InlineData(@"C:\dir\", "a", @"C:\dir\a")]
    public async Task Combine_JoinsWithBackslash(string directory, string name, string expected)
    {
        var session = await OpenAsync();
        session.Combine(directory, name).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"C:\dir", @"C:\")]
    [InlineData(@"C:\dir\", @"C:\")]
    [InlineData(@"C:\dir\sub", @"C:\dir")]
    [InlineData(@"D:\x\y\", @"D:\x")]
    public async Task Parent_StopsAtTheDriveRoot(string path, string expected)
    {
        var session = await OpenAsync();
        session.Parent(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("report.txt", true)]
    [InlineData("a b,c", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("a?", false)]
    public async Task IsValidName_RejectsWindowsSeparatorsAndWildcards(string name, bool expected)
    {
        var session = await OpenAsync();
        session.IsValidName(name).Should().Be(expected);
    }

    // ── Text chat ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TextChat_SendsControlAndTextMessages()
    {
        _client.SendTextChatControl(RfbTextChat.Open);
        _client.SendTextChat("héllo");
        _client.SendTextChat(new string('x', RfbTextChat.MaxLength + 10));
        _client.SendTextChatControl(RfbTextChat.Close);

        (await ReadTextChatAsync()).Should().Be((RfbTextChat.Open, ""));
        (await ReadTextChatAsync()).Should().Be((5u + 1, "héllo"));
        var (length, text) = await ReadTextChatAsync();
        length.Should().Be((uint)RfbTextChat.MaxLength);
        text.Length.Should().Be(RfbTextChat.MaxLength);
        (await ReadTextChatAsync()).Should().Be((RfbTextChat.Close, ""));

        var invalid = () => _client.SendTextChatControl(5);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TextChat_RaisesEventsForServerMessages()
    {
        var received = new List<RfbTextChatMessage>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.TextChatReceived += (_, message) =>
        {
            lock (received)
            {
                received.Add(message);
                if (received.Count == 5) done.TrySetResult();
            }
        };

        await _server.SendAsync(TextChat(RfbTextChat.Open));
        await _server.SendAsync(TextChat("hi there\0garbage"u8.ToArray()));
        // Older UltraVNC versions send ANSI text.
        await _server.SendAsync(TextChat([0x63, 0x61, 0x66, 0xE9]));
        await _server.SendAsync(TextChat(RfbTextChat.Close));
        await _server.SendAsync(TextChat(RfbTextChat.Finished));
        await done.Task.WaitAsync(Timeout);

        received.Should().Equal(
            new RfbTextChatMessage(RfbTextChatKind.Opened),
            new RfbTextChatMessage(RfbTextChatKind.Text, "hi there"),
            new RfbTextChatMessage(RfbTextChatKind.Text, "café"),
            new RfbTextChatMessage(RfbTextChatKind.Closed),
            new RfbTextChatMessage(RfbTextChatKind.Finished));
    }

    [Fact]
    public async Task TextChat_OversizedMessage_EndsTheConnection()
    {
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Disconnected += (_, error) => disconnected.TrySetResult(error);

        await _server.SendAsync(new RfbBytes().U8(RfbClientMessage.TextChat).U8(0).U8(0).U8(0).U32(10_000_000));

        (await disconnected.Task.WaitAsync(Timeout)).Should().BeOfType<RfbProtocolException>();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<UltraVncFileTransfer> OpenAsync()
    {
        var open = UltraVncFileTransfer.OpenAsync(_client);
        var request = await ReadFileTransferAsync();
        request.Type.Should().Be(RfbFileTransfer.AbortFileTransfer);
        request.Param.Should().Be(RfbFileTransfer.ProtocolVersion);
        await SendFileTransferAsync(RfbFileTransfer.FileTransferAccess, 0, 1);
        return await open.WaitAsync(Timeout);
    }

    private sealed record ClientFileTransfer(byte Type, byte Param, uint Size, byte[] Data, uint SizeHigh)
    {
        public string Text => Encoding.UTF8.GetString(Data);
    }

    /// <summary>Reads client messages until a file transfer message, skipping update requests and input.</summary>
    private async Task<ClientFileTransfer> ReadFileTransferAsync()
    {
        while (true)
        {
            var type = (await _server.ReceiveAsync(1))[0];
            switch (type)
            {
                case RfbClientMessage.FileTransfer:
                    var header = await _server.ReceiveAsync(11);
                    var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3));
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(7));
                    var data = length > 0 ? await _server.ReceiveAsync(length) : [];
                    uint sizeHigh = 0;
                    if (header[0] == RfbFileTransfer.FileTransferOffer)
                        sizeHigh = BinaryPrimitives.ReadUInt32BigEndian(await _server.ReceiveAsync(4));
                    return new ClientFileTransfer(header[0], header[1], size, data, sizeHigh);
                case 3:
                    await _server.ReceiveAsync(9);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected client message {type}.");
            }
        }
    }

    private async Task<(uint Length, string Text)> ReadTextChatAsync()
    {
        while (true)
        {
            var type = (await _server.ReceiveAsync(1))[0];
            if (type == 3)
            {
                await _server.ReceiveAsync(9);
                continue;
            }
            type.Should().Be(RfbClientMessage.TextChat);
            var header = await _server.ReceiveAsync(7);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3));
            if (length is RfbTextChat.Open or RfbTextChat.Close or RfbTextChat.Finished)
                return (length, "");
            return (length, Encoding.UTF8.GetString(await _server.ReceiveAsync((int)length)));
        }
    }

    private Task SendFileTransferAsync(byte type, byte param, uint size, byte[]? data = null)
    {
        data ??= [];
        return _server.SendAsync(new RfbBytes()
            .U8(RfbClientMessage.FileTransfer).U8(type).U8(param).U8(0).U32(size).U32((uint)data.Length).Raw(data));
    }

    private static byte[] TextChat(uint control) =>
        new RfbBytes().U8(RfbClientMessage.TextChat).U8(0).U8(0).U8(0).U32(control);

    private static byte[] TextChat(byte[] text) =>
        new RfbBytes().U8(RfbClientMessage.TextChat).U8(0).U8(0).U8(0).U32((uint)text.Length).Raw(text);

    /// <summary>An RFB_FIND_DATA entry as libvncserver sends it (name not NUL-terminated).</summary>
    private static byte[] FindData(string name, bool directory, long size, uint writeLow, uint writeHigh)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[44 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), directory ? 0x10u : 0x80u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), writeLow);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), writeHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), (uint)(size >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), (uint)size);
        nameBytes.CopyTo(data, 44);
        return data;
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    private sealed class SyncProgress(List<TransferProgress> reports) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
