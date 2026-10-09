using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>Handshake and message tests for <see cref="RfbClient"/> against an in-process scripted server.</summary>
public sealed class RfbClientTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] Challenge = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();

    private readonly FakeRfbServer _server = new();
    private readonly TcpClient _tcp = new();
    private RfbClient? _client;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _tcp.Dispose();
        await _server.DisposeAsync();
    }

    /// <summary>Runs the client handshake and the server script concurrently.</summary>
    private async Task<RfbClient> ConnectAsync(Func<Task> serverScript, string? password = null)
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        await _server.AcceptAsync();
        var clientTask = RfbClient.ConnectAsync(_tcp.GetStream(), new RfbClientOptions { Password = password });
        var serverTask = serverScript();
        try
        {
            _client = await clientTask.WaitAsync(Timeout);
        }
        finally
        {
            // Surface server-side assertion failures, but never mask the client's exception.
            if (serverTask.IsCompleted) await serverTask;
        }
        await serverTask.WaitAsync(Timeout);
        return _client;
    }

    private static readonly byte[] ExpectedSetPixelFormat =
    [
        0, 0, 0, 0,
        32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0,
    ];

    // ── Version and security negotiation ───────────────────────────────────

    [Fact]
    public async Task Rfb33_SecurityNone_CompletesInitialisation()
    {
        var chosenVersion = "";
        (byte Shared, byte[] SetPixelFormat, int[] Encodings) init = default;

        var client = await ConnectAsync(async () =>
        {
            chosenVersion = await _server.NegotiateVersionAsync("003.003");
            await _server.SendAsync(new RfbBytes().U32(RfbSecurityType.None));
            init = await _server.InitialiseAsync(800, 600, "test desktop");
        });

        chosenVersion.Should().Be("RFB 003.003\n");
        client.ProtocolVersion.Should().Be(new Version(3, 3));
        client.SecurityType.Should().Be(RfbSecurityType.None);
        client.DesktopName.Should().Be("test desktop");
        client.Framebuffer.Width.Should().Be(800);
        client.Framebuffer.Height.Should().Be(600);
        client.ServerPixelFormat.BitsPerPixel.Should().Be(16);
        init.Shared.Should().Be(1);
        init.SetPixelFormat.Should().Equal(ExpectedSetPixelFormat);
        init.Encodings.Should().Equal(RfbClientOptions.DefaultEncodings);
    }

    [Fact]
    public async Task Rfb33_VncAuthentication_SendsDesResponse()
    {
        byte[] response = [];

        var client = await ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.003");
            await _server.SendAsync(new RfbBytes().U32(RfbSecurityType.VncAuthentication).Raw(Challenge));
            response = await _server.ReceiveAsync(16);
            await _server.SendAsync(new RfbBytes().U32(0));
            await _server.InitialiseAsync(640, 480, "auth");
        }, password: "secret");

        response.Should().Equal(VncAuthentication.ComputeResponse("secret", Challenge));
        client.SecurityType.Should().Be(RfbSecurityType.VncAuthentication);
        client.Framebuffer.Width.Should().Be(640);
    }

    [Fact]
    public async Task Rfb33_VncAuthenticationFailure_ThrowsAuthenticationException()
    {
        var act = () => ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.003");
            await _server.SendAsync(new RfbBytes().U32(RfbSecurityType.VncAuthentication).Raw(Challenge));
            await _server.ReceiveAsync(16);
            await _server.SendAsync(new RfbBytes().U32(1));
            _server.CloseConnection();
        }, password: "wrong");

        (await act.Should().ThrowAsync<RfbAuthenticationException>())
            .WithMessage("VNC authentication failed*");
    }

    [Fact]
    public async Task Rfb33_ConnectionRefused_ReportsServerReason()
    {
        var act = () => ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.003");
            await _server.SendAsync(new RfbBytes().U32(RfbSecurityType.Invalid).String("Too many security failures"));
        });

        (await act.Should().ThrowAsync<RfbAuthenticationException>())
            .WithMessage("*Too many security failures*");
    }

    [Fact]
    public async Task Rfb38_SecurityNone_ReadsSecurityResult()
    {
        byte chosen = 0;

        var client = await ConnectAsync(async () =>
        {
            (await _server.NegotiateVersionAsync("003.008")).Should().Be("RFB 003.008\n");
            await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
            chosen = (await _server.ReceiveAsync(1))[0];
            await _server.SendAsync(new RfbBytes().U32(0));
            await _server.InitialiseAsync(1024, 768, "none");
        });

        chosen.Should().Be(RfbSecurityType.None);
        client.ProtocolVersion.Should().Be(new Version(3, 8));
        client.Framebuffer.Width.Should().Be(1024);
    }

    [Fact]
    public async Task Rfb38_PrefersNoneWhenBothOffered()
    {
        byte chosen = 0;

        await ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.008");
            await _server.SendAsync(new RfbBytes().U8(2).U8(RfbSecurityType.VncAuthentication).U8(RfbSecurityType.None));
            chosen = (await _server.ReceiveAsync(1))[0];
            await _server.SendAsync(new RfbBytes().U32(0));
            await _server.InitialiseAsync(10, 10, "x");
        }, password: "unused");

        chosen.Should().Be(RfbSecurityType.None);
    }

    [Fact]
    public async Task Rfb38_VncAuthentication_Succeeds()
    {
        byte chosen = 0;
        byte[] response = [];

        var client = await ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.008");
            await _server.SendAsync(new RfbBytes().U8(2).U8(16).U8(RfbSecurityType.VncAuthentication));
            chosen = (await _server.ReceiveAsync(1))[0];
            await _server.SendAsync(Challenge);
            response = await _server.ReceiveAsync(16);
            await _server.SendAsync(new RfbBytes().U32(0));
            await _server.InitialiseAsync(320, 200, "vnc auth 3.8");
        }, password: "password");

        chosen.Should().Be(RfbSecurityType.VncAuthentication);
        response.Should().Equal(VncAuthentication.ComputeResponse("password", Challenge));
        client.DesktopName.Should().Be("vnc auth 3.8");
    }

    [Fact]
    public async Task Rfb38_VncAuthenticationFailure_ReportsServerReason()
    {
        var act = () => ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.008");
            await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.VncAuthentication));
            await _server.ReceiveAsync(1);
            await _server.SendAsync(Challenge);
            await _server.ReceiveAsync(16);
            await _server.SendAsync(new RfbBytes().U32(1).String("Password check failed"));
        }, password: "wrong");

        (await act.Should().ThrowAsync<RfbAuthenticationException>())
            .WithMessage("VNC authentication failed: Password check failed");
    }

    [Fact]
    public async Task Rfb38_NoSecurityTypes_ReportsRefusalReason()
    {
        var act = () => ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.008");
            await _server.SendAsync(new RfbBytes().U8(0).String("Connection limit reached"));
        });

        (await act.Should().ThrowAsync<RfbAuthenticationException>())
            .WithMessage("*Connection limit reached*");
    }

    [Fact]
    public async Task Rfb38_OnlyUnsupportedSecurityTypes_ThrowsWithOfferedList()
    {
        var act = () => ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.008");
            await _server.SendAsync(new RfbBytes().U8(2).U8(18).U8(19));
        });

        (await act.Should().ThrowAsync<RfbProtocolException>())
            .WithMessage("*VeNCrypt*");
    }

    [Fact]
    public async Task VncAuthentication_WithoutPassword_FailsClearly()
    {
        var act = () => ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.008");
            await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.VncAuthentication));
            await _server.ReceiveAsync(1);
            await _server.SendAsync(Challenge);
        }, password: null);

        (await act.Should().ThrowAsync<RfbAuthenticationException>())
            .WithMessage("*requires a password*");
    }

    [Fact]
    public async Task Rfb37_SecurityNone_HasNoSecurityResult()
    {
        var client = await ConnectAsync(async () =>
        {
            (await _server.NegotiateVersionAsync("003.007")).Should().Be("RFB 003.007\n");
            await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
            await _server.ReceiveAsync(1);
            await _server.InitialiseAsync(50, 40, "3.7");
        });

        client.ProtocolVersion.Should().Be(new Version(3, 7));
        client.Framebuffer.Height.Should().Be(40);
    }

    [Theory]
    [InlineData("003.889", "RFB 003.008\n")] // Apple Remote Desktop
    [InlineData("004.001", "RFB 003.008\n")] // RealVNC 4.x+
    [InlineData("003.005", "RFB 003.003\n")] // unofficial minor
    public async Task VersionNegotiation_PicksHighestSupported(string serverVersion, string expected)
    {
        var chosen = "";
        await ConnectAsync(async () =>
        {
            chosen = await _server.NegotiateVersionAsync(serverVersion);
            if (expected.EndsWith("003.003\n"))
            {
                await _server.SendAsync(new RfbBytes().U32(RfbSecurityType.None));
            }
            else
            {
                await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
                await _server.ReceiveAsync(1);
                await _server.SendAsync(new RfbBytes().U32(0));
            }
            await _server.InitialiseAsync(10, 10, "v");
        });

        chosen.Should().Be(expected);
    }

    [Fact]
    public async Task NonVncServer_ThrowsProtocolException()
    {
        var act = () => ConnectAsync(async () =>
        {
            await _server.SendAsync(Encoding.ASCII.GetBytes("SSH-2.0-OpenSSH_9.6\r\n"));
        });

        (await act.Should().ThrowAsync<RfbProtocolException>())
            .WithMessage("*not send an RFB version banner*");
    }

    [Fact]
    public async Task Cancellation_DuringHandshake_ThrowsOperationCanceled()
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        await _server.AcceptAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // The server never sends its banner.
        var act = () => RfbClient.ConnectAsync(_tcp.GetStream(), new RfbClientOptions(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── After initialisation ───────────────────────────────────────────────

    private async Task<RfbClient> ConnectNoneAsync(int width, int height)
    {
        return await ConnectAsync(async () =>
        {
            await _server.NegotiateVersionAsync("003.008");
            await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
            await _server.ReceiveAsync(1);
            await _server.SendAsync(new RfbBytes().U32(0));
            await _server.InitialiseAsync(width, height, "session");
        });
    }

    [Fact]
    public async Task ServerMessages_UpdateFramebufferAndRaiseEvents()
    {
        var client = await ConnectNoneAsync(4, 2);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bell = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cutText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = 0;
        client.FramebufferUpdated += (_, _) =>
        {
            if (Interlocked.Increment(ref updates) == 2) updated.TrySetResult();
        };
        client.DesktopResized += (_, _) => resized.TrySetResult();
        client.BellReceived += (_, _) => bell.TrySetResult();
        client.ServerCutTextReceived += (_, text) => cutText.TrySetResult(text);
        client.Start();

        // The first request is non-incremental and covers the whole desktop.
        (await _server.ReceiveAsync(10)).Should().Equal(3, 0, 0, 0, 0, 0, 0, 4, 0, 2);

        // Update 1: a raw 2×1 rectangle.
        await _server.SendAsync(new RfbBytes().U8(0).U8(0).U16(1)
            .Rect(1, 1, 2, 1, RfbEncoding.Raw).Pixel(0xFF0000).Pixel(0x00FF00));
        // A palette message (ignored), a bell and clipboard text.
        await _server.SendAsync(new RfbBytes().U8(1).U8(0).U16(0).U16(2).Raw(new byte[12]));
        await _server.SendAsync(new RfbBytes().U8(2));
        await _server.SendAsync(new RfbBytes().U8(3).U8(0).U8(0).U8(0).String("héllo"));

        // After the first update the client asks for incremental changes.
        (await _server.ReceiveAsync(10)).Should().Equal(3, 1, 0, 0, 0, 0, 0, 4, 0, 2);
        await bell.Task.WaitAsync(Timeout);
        (await cutText.Task.WaitAsync(Timeout)).Should().Be("héllo");
        client.Framebuffer.GetPixel(1, 1).Should().Be(0xFFFF0000);
        client.Framebuffer.GetPixel(2, 1).Should().Be(0xFF00FF00);

        // Update 2: the desktop grows (DesktopSize pseudo-encoding) and is followed by a full request.
        await _server.SendAsync(new RfbBytes().U8(0).U8(0).U16(1).Rect(0, 0, 8, 6, RfbEncoding.DesktopSize));
        await resized.Task.WaitAsync(Timeout);
        await updated.Task.WaitAsync(Timeout);
        client.Framebuffer.Width.Should().Be(8);
        client.Framebuffer.Height.Should().Be(6);
        (await _server.ReceiveAsync(10)).Should().Equal(3, 0, 0, 0, 0, 0, 0, 8, 0, 6);
    }

    [Fact]
    public async Task ExtendedDesktopSize_ResizesOnSuccessStatusOnly()
    {
        var client = await ConnectNoneAsync(4, 2);
        var updates = new SemaphoreSlim(0);
        client.FramebufferUpdated += (_, _) => updates.Release();
        client.Start();
        await _server.ReceiveAsync(10);

        // Status 3 (invalid layout): no resize.
        await _server.SendAsync(new RfbBytes().U8(0).U8(0).U16(1)
            .Rect(1, 3, 100, 100, RfbEncoding.ExtendedDesktopSize).U8(1).U8(0).U8(0).U8(0).Raw(new byte[16]));
        (await updates.WaitAsync(Timeout)).Should().BeTrue();
        client.Framebuffer.Width.Should().Be(4);

        // Status 0 but the same size (TigerVNC repeats the layout after full requests): no resize and the
        // follow-up request stays incremental rather than re-requesting the whole screen forever.
        (await _server.ReceiveAsync(10))[1].Should().Be(1);
        await _server.SendAsync(new RfbBytes().U8(0).U8(0).U16(1)
            .Rect(0, 0, 4, 2, RfbEncoding.ExtendedDesktopSize).U8(1).U8(0).U8(0).U8(0).Raw(new byte[16]));
        (await updates.WaitAsync(Timeout)).Should().BeTrue();
        (await _server.ReceiveAsync(10)).Should().Equal(3, 1, 0, 0, 0, 0, 0, 4, 0, 2);

        // Status 0: resize to 16×9.
        await _server.SendAsync(new RfbBytes().U8(0).U8(0).U16(1)
            .Rect(0, 0, 16, 9, RfbEncoding.ExtendedDesktopSize).U8(1).U8(0).U8(0).U8(0).Raw(new byte[16]));
        (await updates.WaitAsync(Timeout)).Should().BeTrue();
        client.Framebuffer.Width.Should().Be(16);
        client.Framebuffer.Height.Should().Be(9);
        (await _server.ReceiveAsync(10)).Should().Equal(3, 0, 0, 0, 0, 0, 0, 16, 0, 9);
    }

    [Fact]
    public async Task CursorPseudoEncoding_RaisesCursorWithTransparency()
    {
        var client = await ConnectNoneAsync(4, 2);
        var cursor = new TaskCompletionSource<RfbCursor>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CursorChanged += (_, c) => cursor.TrySetResult(c);
        client.Start();
        await _server.ReceiveAsync(10);

        // 2×2 cursor, hotspot (1,0); mask shows the left column only.
        await _server.SendAsync(new RfbBytes().U8(0).U8(0).U16(1).Rect(1, 0, 2, 2, RfbEncoding.Cursor)
            .Pixel(0x111111).Pixel(0x222222).Pixel(0x333333).Pixel(0x444444)
            .U8(0b1000_0000).U8(0b1000_0000));

        var shape = await cursor.Task.WaitAsync(Timeout);
        shape.HotspotX.Should().Be(1);
        shape.Bgra.Should().Equal(0xFF111111, 0u, 0xFF333333, 0u);
    }

    [Fact]
    public async Task ClientMessages_AreEncodedPerSpecification()
    {
        var client = await ConnectNoneAsync(4, 2);
        client.Start();
        await _server.ReceiveAsync(10);

        client.SendKeyEvent(0xFF0D, down: true);
        client.SendPointerEvent(300, 2, RfbButtons.Left | RfbButtons.WheelDown);
        client.SendClientCutText("a\r\nb");

        (await _server.ReceiveAsync(8)).Should().Equal(4, 1, 0, 0, 0, 0, 0xFF, 0x0D);
        (await _server.ReceiveAsync(6)).Should().Equal(5, 17, 0x01, 0x2C, 0, 2);
        (await _server.ReceiveAsync(11)).Should().Equal(6, 0, 0, 0, 0, 0, 0, 3, (byte)'a', (byte)'\n', (byte)'b');
    }

    [Fact]
    public async Task ServerClosingConnection_ReportsDisconnectWithError()
    {
        var client = await ConnectNoneAsync(4, 2);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, error) => disconnected.TrySetResult(error);
        client.Start();
        await _server.ReceiveAsync(10);

        _server.CloseConnection();

        (await disconnected.Task.WaitAsync(Timeout)).Should().BeOfType<EndOfStreamException>();
    }

    [Fact]
    public async Task UnknownServerMessage_EndsSessionWithProtocolError()
    {
        var client = await ConnectNoneAsync(4, 2);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, error) => disconnected.TrySetResult(error);
        client.Start();
        await _server.ReceiveAsync(10);

        await _server.SendAsync([0x7A]);

        (await disconnected.Task.WaitAsync(Timeout)).Should().BeOfType<RfbProtocolException>();
    }

    [Fact]
    public async Task Dispose_EndsReceiveLoopWithoutError()
    {
        var client = await ConnectNoneAsync(4, 2);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, error) => disconnected.TrySetResult(error);
        client.Start();
        await _server.ReceiveAsync(10);

        client.Dispose();
        client.Dispose();

        (await disconnected.Task.WaitAsync(Timeout)).Should().BeNull();
        await client.Completion.WaitAsync(Timeout);
    }
}
