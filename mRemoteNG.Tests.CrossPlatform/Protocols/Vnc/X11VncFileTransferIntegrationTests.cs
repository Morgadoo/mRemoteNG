using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using mRemoteNG.Protocols.Vnc;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary><c>Xvfb</c> on display :136 served by <c>x11vnc -ultrafilexfer</c> (LibVNCServer) on port 5966.</summary>
public sealed class X11VncFileTransferFixture : IDisposable
{
    public const int DisplayNumber = 136;
    public const int Port = 5966;

    private readonly Process? _xvfb;
    private readonly Process? _x11vnc;
    private readonly StringBuilder _log = new();

    public X11VncFileTransferFixture()
    {
        var xvfb = ExternalProcess.FindExecutable("Xvfb");
        var x11vnc = ExternalProcess.FindExecutable("x11vnc");
        if (xvfb is null || x11vnc is null)
        {
            SkipReason = "Xvfb / x11vnc not installed";
            return;
        }
        if (ExternalProcess.IsListening(Port) || !ExternalProcess.ClaimDisplay(DisplayNumber))
        {
            SkipReason = $"Port {Port} or display :{DisplayNumber} is already in use";
            return;
        }
        System.IO.Directory.CreateDirectory(Directory);
        _xvfb = ExternalProcess.Start(xvfb, [$":{DisplayNumber}", "-screen", "0", "320x200x24", "-nolisten", "tcp", "-noreset"], _log);
        if (!ExternalProcess.WaitUntil(() => File.Exists($"/tmp/.X11-unix/X{DisplayNumber}"), TimeSpan.FromSeconds(10), _xvfb))
        {
            lock (_log) SkipReason = $"Xvfb did not start: {_log}";
            return;
        }
        _x11vnc = ExternalProcess.Start(x11vnc,
        [
            "-display", $":{DisplayNumber}", "-rfbport", $"{Port}", "-localhost", "-nopw", "-forever", "-shared",
            "-ultrafilexfer", "-noxdamage", "-q",
        ], _log);
        if (!ExternalProcess.WaitUntil(() => ExternalProcess.IsListening(Port), TimeSpan.FromSeconds(15), _x11vnc))
        {
            lock (_log) SkipReason = $"x11vnc did not start: {_log}";
        }
    }

    /// <summary>A scratch directory on the server's file system (the server and the tests share the machine).</summary>
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"mremoteng-x11vnc-ft-{Guid.NewGuid():N}");

    public string? SkipReason { get; }

    public void Dispose()
    {
        ExternalProcess.Stop(_x11vnc);
        ExternalProcess.Stop(_xvfb, DisplayNumber);
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

[Trait("Category", "Integration")]
public sealed class X11VncFileTransferIntegrationTests : IClassFixture<X11VncFileTransferFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly X11VncFileTransferFixture _server;

    public X11VncFileTransferIntegrationTests(X11VncFileTransferFixture server) => _server = server;

    /// <summary>LibVNCServer maps "C:\a\b" to "/a/b".</summary>
    private static string RemotePath(string localPath) => @"C:" + localPath.Replace('/', '\\');

    [SkippableFact]
    public async Task FileTransfer_ListsUploadsDownloadsCreatesAndDeletes()
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        var root = Path.Combine(_server.Directory, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(root, "existing"));
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello from the server");
        var content = new byte[300_000];
        new Random(42).NextBytes(content.AsSpan(0, 150_000)); // half random, half zeros: compressed and raw blocks
        var local = Path.Combine(_server.Directory, $"local-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(local, content);

        using var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance);
        await protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = X11VncFileTransferFixture.Port,
            Protocol = ProtocolType.Vnc,
        }).WaitAsync(Timeout);
        using var session = await protocol.OpenFileTransferAsync().WaitAsync(Timeout);
        var remoteRoot = RemotePath(root);

        var listing = await session.ListDirectoryAsync(remoteRoot).WaitAsync(Timeout);
        listing.Select(e => (e.Name, e.IsDirectory)).Should().Equal(("existing", true), ("hello.txt", false));
        listing[1].Length.Should().Be("hello from the server".Length);
        listing[1].LastWriteTime.Should().BeCloseTo(File.GetLastWriteTime(Path.Combine(root, "hello.txt")), TimeSpan.FromSeconds(2));

        var uploaded = session.Combine(remoteRoot, "upload.bin");
        var uploadProgress = new List<TransferProgress>();
        await session.UploadFileAsync(local, uploaded, new Recorder(uploadProgress)).WaitAsync(Timeout);
        uploadProgress[^1].Should().Be(new TransferProgress(content.Length, content.Length));
        ExternalProcess.WaitUntil(() => File.Exists(Path.Combine(root, "upload.bin"))
                                        && new FileInfo(Path.Combine(root, "upload.bin")).Length == content.Length,
            TimeSpan.FromSeconds(10)).Should().BeTrue("the server writes the uploaded file");

        var downloaded = Path.Combine(_server.Directory, $"down-{Guid.NewGuid():N}.bin");
        await session.DownloadFileAsync(uploaded, downloaded).WaitAsync(Timeout);
        File.ReadAllBytes(downloaded).Should().Equal(content);
        File.ReadAllBytes(Path.Combine(root, "upload.bin")).Should().Equal(content);

        var missing = () => session.DownloadFileAsync(session.Combine(remoteRoot, "missing"), downloaded + ".x").WaitAsync(Timeout);
        await missing.Should().ThrowAsync<FileNotFoundException>();

        var created = session.Combine(remoteRoot, "made");
        await session.CreateDirectoryAsync(created).WaitAsync(Timeout);
        System.IO.Directory.Exists(Path.Combine(root, "made")).Should().BeTrue();

        File.WriteAllText(Path.Combine(root, "existing", "inner.txt"), "x");
        var existing = (await session.ListDirectoryAsync(remoteRoot).WaitAsync(Timeout)).Single(e => e.Name == "existing");
        await session.DeleteAsync(existing).WaitAsync(Timeout);
        System.IO.Directory.Exists(Path.Combine(root, "existing")).Should().BeFalse();

        var after = await session.ListDirectoryAsync(remoteRoot).WaitAsync(Timeout);
        after.Select(e => e.Name).Should().Equal("made", "hello.txt", "upload.bin");
        protocol.State.Should().Be(ConnectionState.Connected, "file transfer leaves the desktop session running");
    }

    [SkippableFact]
    public async Task ListDirectory_OfAMissingDirectory_Throws()
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        using var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance);
        await protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = X11VncFileTransferFixture.Port,
            Protocol = ProtocolType.Vnc,
        }).WaitAsync(Timeout);
        using var session = await protocol.OpenFileTransferAsync().WaitAsync(Timeout);

        var act = () => session.ListDirectoryAsync(RemotePath(Path.Combine(_server.Directory, "does-not-exist"))).WaitAsync(Timeout);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    private sealed class Recorder(List<TransferProgress> reports) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
