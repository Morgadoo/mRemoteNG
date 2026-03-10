using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using NSubstitute;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols;

/// <summary>
/// Unit tests for <see cref="ProtocolFactory"/> and protocol abstractions.
/// No network connections are made — protocols are only instantiated.
/// </summary>
public sealed class ProtocolFactoryTests
{
    private readonly IServiceProvider _services;

    public ProtocolFactoryTests()
    {
        var collection = new ServiceCollection();
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        ProtocolFactory.Register(collection);
        _services = collection.BuildServiceProvider();
    }

    [Theory]
    [InlineData(ProtocolType.Ssh)]
    [InlineData(ProtocolType.SshSftp)]
    [InlineData(ProtocolType.Telnet)]
    [InlineData(ProtocolType.Rlogin)]
    [InlineData(ProtocolType.Rdp)]
    [InlineData(ProtocolType.Vnc)]
    [InlineData(ProtocolType.Http)]
    [InlineData(ProtocolType.Https)]
    [InlineData(ProtocolType.PowerShell)]
    [InlineData(ProtocolType.Serial)]
    [InlineData(ProtocolType.ExternalApp)]
    public void Create_ShouldReturnProtocol_ForAllTypes(ProtocolType type)
    {
        var factory = _services.GetRequiredService<IProtocolFactory>();

        var protocol = factory.Create(type);

        protocol.Should().NotBeNull();
        protocol.State.Should().Be(ConnectionState.Disconnected);
        protocol.Dispose();
    }

    [Fact]
    public void Create_ShouldReturnVisualProtocol_ForSsh()
    {
        var factory = _services.GetRequiredService<IProtocolFactory>();
        var protocol = factory.Create(ProtocolType.Ssh);

        protocol.Should().BeAssignableTo<IVisualProtocol>();
        var visual = (IVisualProtocol)protocol;
        var view = visual.CreateView();
        view.Should().NotBeNull();

        protocol.Dispose();
    }

    [Fact]
    public void Create_ShouldReturnVisualProtocol_ForVnc()
    {
        var factory = _services.GetRequiredService<IProtocolFactory>();
        var protocol = factory.Create(ProtocolType.Vnc);

        protocol.Should().BeAssignableTo<IVisualProtocol>();
        protocol.Dispose();
    }

    [Fact]
    public void Create_ShouldReturnNewInstance_EachCall()
    {
        var factory = _services.GetRequiredService<IProtocolFactory>();
        var p1 = factory.Create(ProtocolType.Ssh);
        var p2 = factory.Create(ProtocolType.Ssh);

        // Transient — should not be the same object
        p1.Should().NotBeSameAs(p2, "protocols are registered as transient");

        p1.Dispose();
        p2.Dispose();
    }

    [Fact]
    public void StateChanged_ShouldFireOnConnect_WhenImplemented()
    {
        // Use ExternalAppProtocol (no network needed) to test state machine
        var factory = _services.GetRequiredService<IProtocolFactory>();
        var protocol = factory.Create(ProtocolType.ExternalApp);
        var states = new List<ConnectionState>();

        protocol.StateChanged += (_, s) => states.Add(s);

        // No command configured — should reach Error state
        var parameters = new ConnectionParameters
        {
            Hostname = "localhost",
            Port = 22,
            Protocol = ProtocolType.ExternalApp,
        };

        // Connect fires and reaches Error (no command set)
        protocol.ConnectAsync(parameters).GetAwaiter().GetResult();

        states.Should().Contain(ConnectionState.Error, "ExternalApp without command should fail");
        protocol.Dispose();
    }
}
