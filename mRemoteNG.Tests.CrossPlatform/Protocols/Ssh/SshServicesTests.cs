using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

public class SshServicesTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ProtocolFactory.Register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Register_AddsThePreparationStepsAndPuttyServices()
    {
        using var provider = BuildProvider();

        provider.GetServices<IConnectionPreparationStep>().Select(s => s.GetType()).Should()
            .Contain([typeof(SshSettingsPreparationStep), typeof(SshTunnelPreparationStep)]);
        provider.GetRequiredService<PuttySessionsTree>().Root.Should().BeOfType<RootPuttySessionsNodeInfo>();
        provider.GetRequiredService<ConnectionPreparer>().Should().NotBeNull();
    }

    [Fact]
    public async Task Preparer_FromDI_RunsTheTunnelStep()
    {
        using var provider = BuildProvider();
        var root = new RootNodeInfo(RootNodeType.Connection);
        var target = new ConnectionInfo { Name = "t", Protocol = CoreProtocol.SSH2, Hostname = "h", Port = 22, SSHTunnelConnectionName = "jump" };
        root.AddChild(target);

        var act = () => provider.GetRequiredService<ConnectionPreparer>().PrepareAsync(target);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*\"jump\"*was not found*");
    }
}
