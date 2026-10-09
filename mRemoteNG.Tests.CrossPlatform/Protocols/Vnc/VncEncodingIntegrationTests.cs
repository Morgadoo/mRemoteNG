using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;
using Xunit.Abstractions;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>Captures whole desktops from a live server with a chosen encoding list and pixel format.</summary>
internal static class DesktopCapture
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public sealed record Result(uint[] Pixels, IReadOnlyDictionary<int, long> RectangleCounts, string TightStatistics);

    public static async Task<Result> CaptureAsync(int port, IReadOnlyList<int> encodings, PixelFormat format)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var client = await RfbClient.ConnectAsync(tcp.GetStream(),
            new RfbClientOptions { Encodings = encodings, PixelFormat = format }).WaitAsync(Timeout);

        await WaitForFullCoverageAsync(client, client.Start);
        // Wipe and request everything again: persistent zlib streams must carry over between updates.
        Array.Clear(client.Framebuffer.Pixels);
        await WaitForFullCoverageAsync(client, () => client.RequestUpdate(incremental: false));

        return new Result((uint[])client.Framebuffer.Pixels.Clone(), new Dictionary<int, long>(client.RectangleCounts),
            client.TightStatistics.ToString());
    }

    private static async Task WaitForFullCoverageAsync(RfbClient client, Action trigger)
    {
        var full = new RfbRect(0, 0, client.Framebuffer.Width, client.Framebuffer.Height);
        client.Framebuffer.TakeDirty();
        var covered = default(RfbRect);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onUpdate = (_, _) =>
        {
            covered = covered.Union(client.Framebuffer.TakeDirty());
            if (covered == full) done.TrySetResult();
        };
        EventHandler<Exception?> onDisconnect = (_, error) => failed.TrySetResult(error);
        client.FramebufferUpdated += onUpdate;
        client.Disconnected += onDisconnect;
        try
        {
            trigger();
            var first = await Task.WhenAny(done.Task, failed.Task).WaitAsync(Timeout);
            if (first == failed.Task)
                throw new Xunit.Sdk.XunitException($"Disconnected before the full update: {failed.Task.Result}");
        }
        finally
        {
            client.FramebufferUpdated -= onUpdate;
            client.Disconnected -= onDisconnect;
        }
    }

    public static PixelFormat Format(int bpp) => bpp switch
    {
        8 => PixelFormat.Bgr233,
        16 => PixelFormat.Rgb565,
        _ => PixelFormat.Bgra32,
    };
}

/// <summary>Every encoding TigerVNC sends must decode to exactly what Raw delivers, at 32, 16 and 8 bpp.</summary>
[Trait("Category", "Integration")]
[Collection(TigerVncCollection.Name)]
public sealed class TigerVncEncodingTests
{
    private readonly TigerVncFixture _server;
    private readonly ITestOutputHelper _output;

    public TigerVncEncodingTests(TigerVncFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    public static TheoryData<string, int, int, int?> Cases => new()
    {
        // name, encoding, bpp, compression level
        { "Tight", RfbEncoding.Tight, 32, null },
        { "Tight level 0", RfbEncoding.Tight, 32, 0 },
        { "Tight level 9", RfbEncoding.Tight, 32, 9 },
        { "ZRLE level 1", RfbEncoding.Zrle, 32, 1 },
        { "Tight", RfbEncoding.Tight, 16, null },
        { "ZRLE", RfbEncoding.Zrle, 16, null },
        { "Hextile", RfbEncoding.Hextile, 16, null },
        { "RRE", RfbEncoding.Rre, 16, null },
        { "Tight", RfbEncoding.Tight, 8, null },
        { "ZRLE", RfbEncoding.Zrle, 8, null },
        { "Hextile", RfbEncoding.Hextile, 8, null },
    };

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task Encoding_DecodesTheSameDesktopAsRaw(string name, int encoding, int bpp, int? compression)
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        var format = DesktopCapture.Format(bpp);

        // Cursor is requested in both runs so the server never paints the pointer into the framebuffer.
        var raw = await DesktopCapture.CaptureAsync(TigerVncFixture.Port, [RfbEncoding.Raw, RfbEncoding.Cursor], format);
        List<int> encodings = [encoding, RfbEncoding.Raw, RfbEncoding.Cursor];
        if (compression is { } level) encodings.Add(RfbEncoding.CompressLevel(level));
        var decoded = await DesktopCapture.CaptureAsync(TigerVncFixture.Port, encodings, format);
        _output.WriteLine($"{name} {bpp}bpp: rectangles {string.Join(", ", decoded.RectangleCounts.Select(kv => $"{kv.Key}={kv.Value}"))}; tight {decoded.TightStatistics}");

        decoded.RectangleCounts.Should().ContainKey(encoding, $"the server should have used {name}");
        decoded.Pixels.Should().Equal(raw.Pixels, $"{name} at {bpp} bpp must decode to exactly the pixels Raw delivers");
        raw.Pixels.Distinct().Count().Should().BeGreaterThan(bpp == 8 ? 4 : 50, "the test pattern has many colours");
    }

    [SkippableTheory]
    [InlineData(32)]
    [InlineData(16)]
    public async Task TightJpeg_IsCloseToRaw(int bpp)
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        var format = DesktopCapture.Format(bpp);

        var raw = await DesktopCapture.CaptureAsync(TigerVncFixture.Port, [RfbEncoding.Raw, RfbEncoding.Cursor], format);
        var jpeg = await DesktopCapture.CaptureAsync(TigerVncFixture.Port,
            [RfbEncoding.Tight, RfbEncoding.Raw, RfbEncoding.Cursor, RfbEncoding.QualityLevel(9)], format);
        _output.WriteLine($"tight {jpeg.TightStatistics}");

        jpeg.TightStatistics.Should().NotContain("jpeg 0,", "a quality level lets TigerVNC send the smooth area as JPEG");
        double total = 0;
        for (var i = 0; i < raw.Pixels.Length; i++)
        {
            uint a = raw.Pixels[i], b = jpeg.Pixels[i];
            total += Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF))
                + Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF))
                + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
        }
        var meanError = total / (raw.Pixels.Length * 3.0);
        _output.WriteLine($"mean absolute error per channel: {meanError:F2}");
        jpeg.Pixels.Should().OnlyContain(p => (p & 0xFF000000u) == 0xFF000000u);
        meanError.Should().BeLessThan(4, "JPEG at quality 9 is visually lossless");
    }
}

/// <summary>LibVNCServer (x11vnc) encodings, including Zlib and CoRRE, against Raw.</summary>
[Trait("Category", "Integration")]
[Collection(X11VncCollection.Name)]
public sealed class X11VncEncodingTests
{
    private readonly X11VncFixture _server;
    private readonly ITestOutputHelper _output;

    public X11VncEncodingTests(X11VncFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    public static TheoryData<string, int, int> Cases => new()
    {
        { "Zlib", RfbEncoding.Zlib, 32 },
        { "CoRRE", RfbEncoding.CoRre, 32 },
        { "Tight", RfbEncoding.Tight, 32 },
        { "ZRLE", RfbEncoding.Zrle, 32 },
        { "Hextile", RfbEncoding.Hextile, 32 },
        { "Zlib", RfbEncoding.Zlib, 16 },
        { "Tight", RfbEncoding.Tight, 16 },
        { "CoRRE", RfbEncoding.CoRre, 8 },
    };

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task Encoding_DecodesTheSameDesktopAsRaw(string name, int encoding, int bpp)
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        var format = DesktopCapture.Format(bpp);

        var raw = await DesktopCapture.CaptureAsync(X11VncFixture.Port, [RfbEncoding.Raw], format);
        var decoded = await DesktopCapture.CaptureAsync(X11VncFixture.Port, [encoding, RfbEncoding.Raw], format);
        _output.WriteLine($"{name} {bpp}bpp: rectangles {string.Join(", ", decoded.RectangleCounts.Select(kv => $"{kv.Key}={kv.Value}"))}; tight {decoded.TightStatistics}");

        decoded.RectangleCounts.Should().ContainKey(encoding, $"the server should have used {name}");
        decoded.Pixels.Should().Equal(raw.Pixels, $"{name} at {bpp} bpp must decode to exactly the pixels Raw delivers");
    }
}
