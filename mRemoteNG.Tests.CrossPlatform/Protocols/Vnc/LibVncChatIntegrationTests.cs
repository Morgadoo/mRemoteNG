using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>
/// A LibVNCServer server on port 5967 whose text chat answers "echo: " + the message and returns open and close
/// requests; compiled for the run (needs gcc and libvncserver-dev).
/// </summary>
public sealed class LibVncChatEchoFixture : IDisposable
{
    public const int Port = 5967;

    private const string Source = """
        #include <stdio.h>
        #include <stdlib.h>
        #include <rfb/rfb.h>

        static void chat(rfbClientPtr cl, int length, char *text)
        {
            char reply[rfbTextMaxSize];
            int n;
            if ((uint32_t)length == rfbTextChatOpen || (uint32_t)length == rfbTextChatClose) {
                rfbSendTextChatMessage(cl, (uint32_t)length, "");
                return;
            }
            if ((uint32_t)length == rfbTextChatFinished)
                return;
            n = snprintf(reply, sizeof reply, "echo: %.*s", length, text);
            if (n >= (int)sizeof reply)
                n = sizeof reply - 1;
            rfbSendTextChatMessage(cl, n, reply);
        }

        int main(int argc, char **argv)
        {
            rfbScreenInfoPtr screen = rfbGetScreen(&argc, argv, 64, 48, 8, 3, 4);
            if (!screen)
                return 1;
            screen->frameBuffer = calloc(64 * 48 * 4, 1);
            screen->setTextChat = chat;
            screen->alwaysShared = TRUE;
            rfbInitServer(screen);
            rfbRunEventLoop(screen, 10000, FALSE);
            return 0;
        }
        """;

    private readonly Process? _server;
    private readonly StringBuilder _log = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mremoteng-libvnc-chat-{Guid.NewGuid():N}");

    public LibVncChatEchoFixture()
    {
        var gcc = ExternalProcess.FindExecutable("gcc");
        if (gcc is null || !File.Exists("/usr/include/rfb/rfb.h"))
        {
            SkipReason = "gcc / libvncserver-dev not installed";
            return;
        }
        if (ExternalProcess.IsListening(Port))
        {
            SkipReason = $"Port {Port} is already in use";
            return;
        }
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "chat-echo.c");
        var binary = Path.Combine(_directory, "chat-echo");
        File.WriteAllText(source, Source);
        using (var compile = Process.Start(new ProcessStartInfo(gcc, [source, "-o", binary, "-lvncserver"])
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!)
        {
            var errors = compile.StandardError.ReadToEnd();
            compile.WaitForExit();
            if (compile.ExitCode != 0)
            {
                SkipReason = $"Could not compile the chat echo server: {errors}";
                return;
            }
        }
        _server = ExternalProcess.Start(binary, ["-rfbport", $"{Port}", "-listen", "127.0.0.1"], _log);
        if (!ExternalProcess.WaitUntil(() => ExternalProcess.IsListening(Port), TimeSpan.FromSeconds(10), _server))
        {
            lock (_log) SkipReason = $"The chat echo server did not start: {_log}";
        }
    }

    public string? SkipReason { get; }

    public void Dispose()
    {
        ExternalProcess.Stop(_server);
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

[Trait("Category", "Integration")]
public sealed class LibVncChatIntegrationTests : IClassFixture<LibVncChatEchoFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly LibVncChatEchoFixture _server;

    public LibVncChatIntegrationTests(LibVncChatEchoFixture server) => _server = server;

    [SkippableFact]
    public async Task TextChat_RoundTripsThroughLibVncServer()
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        using var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance);
        var events = new List<TextChatEvent>();
        var gotFour = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.TextChatReceived += (_, e) =>
        {
            lock (events)
            {
                events.Add(e);
                if (events.Count == 4) gotFour.TrySetResult();
            }
        };
        await protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = LibVncChatEchoFixture.Port,
            Protocol = ProtocolType.Vnc,
        }).WaitAsync(Timeout);

        protocol.OpenTextChat();
        protocol.SendTextChat("héllo wörld").Should().BeTrue();
        protocol.SendTextChat(new string('x', 5000)).Should().BeTrue("long messages are cut, not refused");
        protocol.CloseTextChat();
        await gotFour.Task.WaitAsync(Timeout);

        events[0].Should().Be(new TextChatEvent(TextChatEventKind.Opened));
        events[1].Should().Be(new TextChatEvent(TextChatEventKind.Message, "echo: héllo wörld"));
        events[2].Kind.Should().Be(TextChatEventKind.Message);
        events[2].Text.Should().StartWith("echo: xxx");
        events[3].Should().Be(new TextChatEvent(TextChatEventKind.Closed));
        protocol.State.Should().Be(ConnectionState.Connected, "the server kept the session after the 5000-character message");
    }
}
