using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>
/// Starts a private TigerVNC <c>Xvnc</c> (display :61, port 5961, VNC authentication) for the integration tests,
/// or records why it could not.
/// </summary>
public sealed class XvncFixture : IDisposable
{
    public const int DisplayNumber = 61;
    public const int Port = 5961;
    public const string Password = "secret12";
    public const int Width = 800;
    public const int Height = 600;

    private readonly Process? _xvnc;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mremoteng-xvnc-{Guid.NewGuid():N}");
    private readonly StringBuilder _log = new();

    public XvncFixture()
    {
        var xvnc = FindExecutable("Xvnc");
        var vncpasswd = FindExecutable("vncpasswd");
        if (xvnc is null || vncpasswd is null)
        {
            SkipReason = "Xvnc / vncpasswd (TigerVNC) not installed";
            return;
        }
        if (IsListening() || !ExternalProcess.ClaimDisplay(DisplayNumber))
        {
            SkipReason = $"Port {Port} or display :{DisplayNumber} is already in use";
            return;
        }

        Directory.CreateDirectory(_directory);
        var passwordFile = Path.Combine(_directory, "passwd");
        using (var passwd = Process.Start(new ProcessStartInfo(vncpasswd, "-f")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!)
        {
            passwd.StandardInput.WriteLine(Password);
            passwd.StandardInput.Close();
            using var file = File.Create(passwordFile);
            passwd.StandardOutput.BaseStream.CopyTo(file);
            passwd.WaitForExit();
        }

        var start = new ProcessStartInfo(xvnc)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            $":{DisplayNumber}", "-rfbport", $"{Port}", "-SecurityTypes", "VncAuth", "-PasswordFile", passwordFile,
            "-geometry", $"{Width}x{Height}", "-depth", "24", "-localhost", "-nolisten", "tcp",
        })
        {
            start.ArgumentList.Add(arg);
        }

        _xvnc = Process.Start(start)!;
        _xvnc.ErrorDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
        _xvnc.OutputDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
        _xvnc.BeginErrorReadLine();
        _xvnc.BeginOutputReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!IsListening())
        {
            if (_xvnc.HasExited || DateTime.UtcNow > deadline)
            {
                lock (_log) SkipReason = $"Xvnc did not start: {_log}";
                return;
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>Null when the server is running.</summary>
    public string? SkipReason { get; }

    /// <summary>Runs an X client against the test display; returns null when the tool is not installed.</summary>
    public string? RunX(string tool, params string[] args)
    {
        var path = FindExecutable(tool);
        if (path is null) return null;
        var info = new ProcessStartInfo(path) { RedirectStandardOutput = true, UseShellExecute = false };
        info.Environment["DISPLAY"] = $":{DisplayNumber}";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static bool IsListening()
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string? FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    public void Dispose()
    {
        // Also removes the lock file and socket the killed server leaves behind.
        ExternalProcess.Stop(_xvnc, DisplayNumber);
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

[Trait("Category", "Integration")]
public sealed class XvncIntegrationTests : IClassFixture<XvncFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly XvncFixture _xvnc;

    public XvncIntegrationTests(XvncFixture xvnc) => _xvnc = xvnc;

    private async Task<(TcpClient Tcp, RfbClient Client)> ConnectAsync(string password, IReadOnlyList<int>? encodings = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, XvncFixture.Port);
        var options = new RfbClientOptions { Password = password, Encodings = encodings ?? RfbClientOptions.DefaultEncodings };
        try
        {
            return (tcp, await RfbClient.ConnectAsync(tcp.GetStream(), options).WaitAsync(Timeout));
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="trigger"/> (starting the client or requesting a full update) and waits until decoded
    /// rectangles have covered the whole desktop, possibly over several FramebufferUpdate messages.
    /// </summary>
    private static async Task WaitForFullCoverageAsync(RfbClient client, Action trigger)
    {
        var full = new RfbRect(0, 0, client.Framebuffer.Width, client.Framebuffer.Height);
        client.Framebuffer.TakeDirty(); // discard earlier marks, e.g. "everything is new" from initialisation
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

    [SkippableFact]
    public async Task Connect_ReceivesFullFramebuffer_AndAcceptsPointerAndKeyEvents()
    {
        Skip.If(_xvnc.SkipReason is not null, _xvnc.SkipReason);

        var (tcp, client) = await ConnectAsync(XvncFixture.Password);
        using (tcp)
        using (client)
        {
            client.ProtocolVersion.Should().Be(new Version(3, 8));
            client.SecurityType.Should().Be(RfbSecurityType.VncAuthentication);
            client.Framebuffer.Width.Should().Be(XvncFixture.Width);
            client.Framebuffer.Height.Should().Be(XvncFixture.Height);

            await WaitForFullCoverageAsync(client, client.Start);
            client.Framebuffer.Pixels.Should().OnlyContain(p => (p & 0xFF000000) == 0xFF000000,
                "every pixel has been decoded (decoded pixels are opaque, untouched ones are 0)");

            Exception? disconnectError = null;
            client.Disconnected += (_, error) => disconnectError = error ?? new Exception("closed");

            client.SendPointerEvent(123, 45, RfbButtons.None);
            client.SendKeyEvent(X11KeySymbols.ShiftL, down: true);
            client.SendKeyEvent('a', down: true);
            client.SendKeyEvent('a', down: false);
            client.SendKeyEvent(X11KeySymbols.ShiftL, down: false);
            client.SendClientCutText("clipboard from test");

            // If xdotool is available, confirm the X server really moved its pointer.
            if (_xvnc.RunX("xdotool", "getmouselocation") is not null)
            {
                var deadline = DateTime.UtcNow + Timeout;
                string? location;
                do
                {
                    location = _xvnc.RunX("xdotool", "getmouselocation");
                    if (location!.StartsWith("x:123 y:45 ", StringComparison.Ordinal)) break;
                    await Task.Delay(50);
                } while (DateTime.UtcNow < deadline);
                location.Should().StartWith("x:123 y:45 ");
            }

            // The session is still healthy: another full update round-trips.
            var again = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.FramebufferUpdated += (_, _) => again.TrySetResult();
            client.RequestUpdate(incremental: false);
            await again.Task.WaitAsync(Timeout);
            disconnectError.Should().BeNull();
        }
    }

    [SkippableFact]
    public async Task WrongPassword_IsRejected()
    {
        Skip.If(_xvnc.SkipReason is not null, _xvnc.SkipReason);

        var act = () => ConnectAsync("not-the-password");

        await act.Should().ThrowAsync<RfbAuthenticationException>();
    }

    public static TheoryData<string, int> Encodings => new()
    {
        { "ZRLE", RfbEncoding.Zrle },
        { "Hextile", RfbEncoding.Hextile },
        { "RRE", RfbEncoding.Rre },
    };

    [SkippableTheory]
    [MemberData(nameof(Encodings))]
    public async Task EveryEncoding_DecodesTheSameDesktopAsRaw(string name, int encoding)
    {
        Skip.If(_xvnc.SkipReason is not null, _xvnc.SkipReason);
        // A plaid root window gives every encoding something non-trivial to compress.
        _xvnc.RunX("xsetroot", "-mod", "7", "5", "-fg", "#d04020", "-bg", "#2040a0");

        // Cursor is requested in both runs so the server never paints the pointer into the framebuffer.
        var (raw, _) = await CaptureAsync([RfbEncoding.Raw, RfbEncoding.Cursor]);
        var (decoded, counts) = await CaptureAsync([encoding, RfbEncoding.Raw, RfbEncoding.Cursor]);

        counts.Should().ContainKey(encoding, $"the server should have used {name}");
        decoded.Should().Equal(raw, $"{name} must decode to exactly the pixels Raw delivers");
        raw.Distinct().Count().Should().BeGreaterThan(1, "the test pattern is not uniform");
    }

    private async Task<(uint[] Pixels, IReadOnlyDictionary<int, long> RectangleCounts)> CaptureAsync(IReadOnlyList<int> encodings)
    {
        var (tcp, client) = await ConnectAsync(XvncFixture.Password, encodings);
        using (tcp)
        using (client)
        {
            await WaitForFullCoverageAsync(client, client.Start);

            // Wipe and ask for the whole screen again: for ZRLE this continues the same zlib stream.
            // (The desktop is static, so the receive loop is idle while the array is cleared.)
            Array.Clear(client.Framebuffer.Pixels);
            await WaitForFullCoverageAsync(client, () => client.RequestUpdate(incremental: false));

            return ((uint[])client.Framebuffer.Pixels.Clone(), new Dictionary<int, long>(client.RectangleCounts));
        }
    }

    [SkippableFact]
    public async Task VncProtocol_ConnectsAndDisconnects()
    {
        Skip.If(_xvnc.SkipReason is not null, _xvnc.SkipReason);

        using var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance);
        await protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = XvncFixture.Port,
            Protocol = ProtocolType.Vnc,
            Password = XvncFixture.Password,
        }).WaitAsync(Timeout);

        protocol.State.Should().Be(ConnectionState.Connected);
        protocol.Framebuffer!.Width.Should().Be(XvncFixture.Width);

        await protocol.DisconnectAsync().WaitAsync(Timeout);
        protocol.State.Should().Be(ConnectionState.Disconnected);
    }
}
