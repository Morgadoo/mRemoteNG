using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols;

public class ConnectionPreparerTests
{
    private sealed class Step(int order, Func<PreparationContext, ConnectionParameters, ConnectionParameters> apply) : IConnectionPreparationStep
    {
        public int Order { get; } = order;

        public Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct) =>
            Task.FromResult(apply(context, parameters));
    }

    private sealed class Resource(List<string> log, string name) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            log.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    private static ConnectionInfo Ssh() => new() { Name = "n", Protocol = CoreProtocol.SSH2, Hostname = "host", Username = "u", Password = "p" };

    [Fact]
    public async Task Steps_RunInOrder_AndCanRewriteParameters()
    {
        var preparer = new ConnectionPreparer(
        [
            new Step(300, (_, p) => p with { Hostname = p.Hostname + "-tunnel" }),
            new Step(100, (_, p) => p with { Hostname = p.Hostname + "-resolved" }),
        ]);

        var prepared = await preparer.PrepareAsync(Ssh());

        prepared.Parameters.Hostname.Should().Be("host-resolved-tunnel");
    }

    [Fact]
    public async Task Resources_AreReleasedInReverseOrder_WhenTheSessionEnds()
    {
        var log = new List<string>();
        var preparer = new ConnectionPreparer(
        [
            new Step(1, (c, p) => { c.Resources.Add(new Resource(log, "first")); return p; }),
            new Step(2, (c, p) => { c.Resources.Add(new Resource(log, "second")); return p; }),
        ]);

        var prepared = await preparer.PrepareAsync(Ssh());
        await prepared.DisposeAsync();
        await prepared.DisposeAsync();

        log.Should().Equal("second", "first");
    }

    [Fact]
    public async Task FailingStep_ReleasesResourcesAcquiredSoFar()
    {
        var log = new List<string>();
        var preparer = new ConnectionPreparer(
        [
            new Step(1, (c, p) => { c.Resources.Add(new Resource(log, "tunnel")); return p; }),
            new Step(2, (_, _) => throw new InvalidOperationException("vault unreachable")),
        ]);

        var act = () => preparer.PrepareAsync(Ssh());

        await act.Should().ThrowAsync<InvalidOperationException>();
        log.Should().Equal("tunnel");
    }

    [Fact]
    public async Task ConnectOptions_StripCredentialsAndOverrideConsoleAndViewOnly()
    {
        var preparer = new ConnectionPreparer([]);
        var rdp = new ConnectionInfo { Name = "r", Protocol = CoreProtocol.RDP, Hostname = "h", Username = "u", Password = "p", UseConsoleSession = false };

        var prepared = await preparer.PrepareAsync(rdp, new ConnectOptions { NoCredentials = true, ConsoleSession = true, ViewOnly = true });

        prepared.Parameters.Username.Should().BeNull();
        prepared.Parameters.Password.Should().BeNull();
        prepared.Parameters.Extras[ConnectionParametersFactory.Keys.RdpConsole].Should().Be("true");
        prepared.Parameters.Extras[ConnectionParametersFactory.Keys.VncViewOnly].Should().Be("true");
    }

    [Fact]
    public async Task ConnectOptions_Fullscreen_OverridesRdpResolutionOnly()
    {
        var preparer = new ConnectionPreparer([]);
        var rdp = new ConnectionInfo { Name = "r", Protocol = CoreProtocol.RDP, Hostname = "h" };

        var rdpPrepared = await preparer.PrepareAsync(rdp, new ConnectOptions { Fullscreen = true });
        var sshPrepared = await preparer.PrepareAsync(Ssh(), new ConnectOptions { Fullscreen = true });

        rdpPrepared.Parameters.Extras[ConnectionParametersFactory.Keys.RdpResolution].Should().Be("fullscreen");
        sshPrepared.Parameters.Extras.Should().NotContainKey(ConnectionParametersFactory.Keys.RdpResolution);
    }
}
