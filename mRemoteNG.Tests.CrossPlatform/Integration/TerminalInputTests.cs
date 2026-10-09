using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Shell;
using mRemoteNG.Protocols.Ssh;
using mRemoteNG.Protocols.Telnet;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

/// <summary>
/// <see cref="ITerminalProtocol"/> (programmatic input, used by Multi-SSH) on every terminal protocol.
/// SSH is covered in <see cref="SshTunnelIntegrationTests"/> against a real server.
/// </summary>
public sealed class TerminalInputTests
{
    [Theory]
    [InlineData(ProtocolType.Ssh)]
    [InlineData(ProtocolType.Telnet)]
    [InlineData(ProtocolType.Rlogin)]
    [InlineData(ProtocolType.Raw)]
    [InlineData(ProtocolType.Serial)]
    [InlineData(ProtocolType.PowerShell)]
    [InlineData(ProtocolType.LocalShell)]
    public void EveryTerminalProtocol_AcceptsProgrammaticInput(ProtocolType type)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ProtocolFactory.Register(services);
        using var provider = services.BuildServiceProvider();

        using var protocol = provider.GetRequiredService<IProtocolFactory>().Create(type);

        protocol.Should().BeAssignableTo<ITerminalProtocol>();
    }

    [Theory]
    [InlineData(ProtocolType.Telnet)]
    [InlineData(ProtocolType.Rlogin)]
    [InlineData(ProtocolType.Raw)]
    [InlineData(ProtocolType.LocalShell)]
    public async Task InputBeforeConnecting_IsIgnored(ProtocolType type)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ProtocolFactory.Register(services);
        using var provider = services.BuildServiceProvider();
        using var protocol = provider.GetRequiredService<IProtocolFactory>().Create(type);

        var send = () => ((ITerminalProtocol)protocol).SendInputAsync("x\r"u8.ToArray());

        await send.Should().NotThrowAsync();
    }

    /// <summary>Accepts one client and returns everything it sends until <paramref name="expectedLength"/> bytes arrived.</summary>
    private static (int Port, Task<byte[]> Received) StartCapturingServer(int expectedLength, byte[]? greeting = null, int skip = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                if (skip > 0)
                    await stream.ReadExactlyAsync(new byte[skip]);
                if (greeting is not null)
                    await stream.WriteAsync(greeting);
                var buffer = new byte[expectedLength];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await stream.ReadExactlyAsync(buffer, timeout.Token);
                return buffer;
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, received);
    }

    [Fact]
    public async Task Telnet_SendsInputTelnetEncoded()
    {
        // Telnet sends CR as CR NUL and doubles 0xFF; ignore the NAWS offer (IAC WILL NAWS) first.
        var (port, received) = StartCapturingServer(expectedLength: 6, skip: 3);
        using var telnet = new TelnetProtocol(NullLogger<TelnetProtocol>.Instance);
        await telnet.ConnectAsync(new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Telnet });

        await ((ITerminalProtocol)telnet).SendInputAsync([(byte)'l', (byte)'s', (byte)'\r', 0xFF]);

        (await received).Should().Equal((byte)'l', (byte)'s', (byte)'\r', 0, 0xFF, 0xFF);
    }

    [Fact]
    public async Task Rlogin_SendsInputUnchanged()
    {
        var handshakeLength = RloginProtocol.BuildHandshake(Environment.UserName, "bob").Length;
        var (port, received) = StartCapturingServer(expectedLength: 4, greeting: [0], skip: handshakeLength);
        using var rlogin = new RloginProtocol(NullLogger<RloginProtocol>.Instance);
        await rlogin.ConnectAsync(new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Rlogin, Username = "bob" });

        await ((ITerminalProtocol)rlogin).SendInputAsync("pwd\r"u8.ToArray());

        (await received).Should().Equal("pwd\r"u8.ToArray());
    }

    [Fact]
    public async Task Raw_SendsLinesThroughTheLineEditor()
    {
        var (port, received) = StartCapturingServer(expectedLength: 7);
        using var raw = new RawSocketProtocol(NullLogger<RawSocketProtocol>.Instance);
        var view = (TerminalView)raw.CreateView();
        await raw.ConnectAsync(new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Raw });

        var terminal = (ITerminalProtocol)raw;
        await terminal.SendInputAsync("helo"u8.ToArray());
        await terminal.SendInputAsync([0x7f]); // Backspace edits the pending line
        await terminal.SendInputAsync("lo\r"u8.ToArray());

        Encoding.ASCII.GetString(await received).Should().Be("hello\r\n");
        view.GetScreenText().Should().Contain("hello", "input is echoed locally, as when typed");
    }

    [SkippableFact]
    public async Task LocalShell_RunsSentCommands()
    {
        Skip.IfNot(OperatingSystem.IsLinux() && File.Exists("/usr/bin/script"), "needs script(1) on Linux");
        using var shell = new LocalShellProtocol(NullLogger<LocalShellProtocol>.Instance);
        var view = (TerminalView)shell.CreateView();
        await shell.ConnectAsync(new ConnectionParameters
        {
            Hostname = "",
            Port = 0,
            Protocol = ProtocolType.LocalShell,
            Extras = new Dictionary<string, string> { [ConnectionParametersFactory.Keys.LocalShellMode] = "terminal" },
        });

        await ((ITerminalProtocol)shell).SendInputAsync("echo local-$((2+3))\r"u8.ToArray());

        await WaitForScreenAsync(view, "local-5");
        await shell.DisconnectAsync();
    }

    [SkippableFact]
    public async Task Serial_WritesSentInputToThePort()
    {
        Skip.IfNot(OperatingSystem.IsLinux() && File.Exists("/usr/bin/socat"), "needs socat on Linux");
        var directory = Path.Combine(Path.GetTempPath(), "serial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var device = Path.Combine(directory, "ttyA");
        var capture = Path.Combine(directory, "capture");

        // A pseudo-terminal pair: the protocol opens one end, socat copies the other end to a file.
        using var socat = Process.Start(new ProcessStartInfo("/usr/bin/socat",
            $"-u PTY,link={device},raw,echo=0 OPEN:{capture},creat,append")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(device) && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            using var serial = new SerialProtocol(NullLogger<SerialProtocol>.Instance);
            serial.CreateView();
            await serial.ConnectAsync(new ConnectionParameters { Hostname = device, Port = 0, Protocol = ProtocolType.Serial });
            Skip.If(serial.State != ConnectionState.Connected, "the pseudo-terminal could not be opened as a serial port");

            await ((ITerminalProtocol)serial).SendInputAsync("AT\r"u8.ToArray());

            deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!File.Exists(capture) || new FileInfo(capture).Length < 3) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            (await File.ReadAllTextAsync(capture)).Should().Be("AT\r");
            await serial.DisconnectAsync();
        }
        finally
        {
            try { socat.Kill(); } catch (InvalidOperationException) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForScreenAsync(TerminalView view, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!view.GetScreenText().Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"'{text}' did not appear. Screen:\n{view.GetScreenText()}");
            await Task.Delay(50);
        }
    }
}
