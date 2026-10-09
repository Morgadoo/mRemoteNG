using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.External;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.External;

/// <summary>"External tool before/after" (PreExtApp/PostExtApp) through the real connection preparer and real processes.</summary>
public sealed class ExternalToolPreparationStepTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mrng-prepost-").FullName;
    private readonly ExternalToolsService _tools;
    private readonly List<string> _messages = [];

    public ExternalToolPreparationStepTests()
    {
        _tools = new ExternalToolsService(
            new ExternalToolsRepository(Path.Combine(_directory, ExternalToolsRepository.FileName)), new ExternalToolLauncher());
        _tools.Message += (_, e) => _messages.Add(e.Message);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ConnectionPreparer Preparer() => new([new ExternalToolPreparationStep(_tools)]);

    private string Marker(string name) => Path.Combine(_directory, name);

    /// <summary>A tool that sleeps, then writes the connection's host name into <paramref name="marker"/>.</summary>
    private static ExternalTool WriterTool(string name, string marker, bool waitForExit, double sleepSeconds = 0.5) =>
        new(name, "sh", $"-c 'sleep {sleepSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}; echo \"$1\" > \"$2\"' sh %HOSTNAME% \"{marker}\"")
        {
            WaitForExit = waitForExit,
        };

    private static ConnectionInfo Ssh(string pre = "", string post = "") => new()
    {
        Name = "web01", Protocol = CoreProtocol.SSH2, Hostname = "web01.example", PreExtApp = pre, PostExtApp = post,
    };

    [Fact]
    public void Step_IsRegisteredWithOrder400()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ProtocolFactory.Register(services);

        var steps = services.BuildServiceProvider().GetServices<IConnectionPreparationStep>().ToList();

        steps.OfType<ExternalToolPreparationStep>().Should().ContainSingle().Which.Order.Should().Be(400);
    }

    [SkippableFact]
    public async Task PreApp_WithWaitForExit_FinishesBeforeTheConnectionIsReady()
    {
        Skip.If(OperatingSystem.IsWindows());
        string marker = Marker("pre");
        _tools.ReplaceAll([WriterTool("before", marker, waitForExit: true)]);

        await using var prepared = await Preparer().PrepareAsync(Ssh(pre: "before"));

        File.Exists(marker).Should().BeTrue("the connection waited for the tool to exit");
        File.ReadAllText(marker).Trim().Should().Be("web01.example");
        prepared.Parameters.Hostname.Should().Be("web01.example");
    }

    [SkippableFact]
    public async Task PreApp_WithoutWaitForExit_DoesNotDelayTheConnection()
    {
        Skip.If(OperatingSystem.IsWindows());
        string marker = Marker("pre-nowait");
        _tools.ReplaceAll([WriterTool("before", marker, waitForExit: false, sleepSeconds: 1.5)]);

        await using var prepared = await Preparer().PrepareAsync(Ssh(pre: "before"));

        File.Exists(marker).Should().BeFalse("the tool is still running");
    }

    [Fact]
    public async Task MissingOrFailingPreApp_DoesNotPreventTheConnection_AsLegacy()
    {
        _tools.ReplaceAll([new ExternalTool("broken", "mrng-no-such-program-" + Guid.NewGuid().ToString("N"))]);

        await using var missing = await Preparer().PrepareAsync(Ssh(pre: "not there"));
        await using var failing = await Preparer().PrepareAsync(Ssh(pre: "broken"));

        missing.Parameters.Hostname.Should().Be("web01.example");
        failing.Parameters.Hostname.Should().Be("web01.example");
        _messages.Should().Contain(m => m.Contains("not there")).And.Contain(m => m.Contains("broken"));
    }

    [SkippableFact]
    public async Task PostApp_RunsWhenTheSessionResourcesAreReleased()
    {
        Skip.If(OperatingSystem.IsWindows());
        string marker = Marker("post");
        _tools.ReplaceAll([WriterTool("after", marker, waitForExit: true, sleepSeconds: 0)]);
        var connection = Ssh(post: "after");

        var prepared = await Preparer().PrepareAsync(connection);
        connection.Hostname = "changed-later";
        await Task.Delay(200);
        File.Exists(marker).Should().BeFalse("the session is still open");

        await prepared.DisposeAsync();
        await prepared.DisposeAsync();

        File.ReadAllLines(marker).Should().Equal(["web01.example"], "it ran once, with the connection as it was when the session opened");
    }

    [Fact]
    public async Task NoPreOrPostApp_AddsNoResources()
    {
        var prepared = await Preparer().PrepareAsync(Ssh());

        prepared.Resources.Should().BeEmpty();
    }

    [Fact]
    public async Task IntegratedPreApp_OpensItsTabWithoutRecursing()
    {
        var opened = new List<ConnectionInfo>();
        _tools.ReplaceAll([new ExternalTool("term", "xterm") { TryIntegrate = true }]);
        _tools.OpenIntegratedSession = async c =>
        {
            opened.Add(c);
            // What the sessions dock would do: prepare the IntApp tab's connection as well.
            await using var _ = await Preparer().PrepareAsync(c);
        };

        await using var prepared = await Preparer().PrepareAsync(Ssh(pre: "term"));

        opened.Should().ContainSingle().Which.Protocol.Should().Be(CoreProtocol.IntApp);
    }
}
