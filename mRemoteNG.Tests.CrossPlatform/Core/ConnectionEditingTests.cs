using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Core;

/// <summary>Helpers behind the connection editor: inheritance access, defaults and validation.</summary>
public class ConnectionEditingTests
{
    private readonly RootNodeInfo _root = new(RootNodeType.Connection);
    private readonly ContainerInfo _folder = new() { Name = "folder", Username = "folder-user" };
    private readonly ContainerInfo _subFolder = new() { Name = "sub", Username = "sub-user" };
    private readonly ConnectionInfo _node = new() { Name = "node", Username = "own-user" };

    public ConnectionEditingTests()
    {
        _root.AddChild(_folder);
        _folder.AddChild(_subFolder);
        _subFolder.AddChild(_node);
    }

    [Fact]
    public void GetOwnValue_IgnoresInheritance_GetValueResolvesIt()
    {
        _node.Inheritance.Username = true;

        ConnectionInheritanceAccessor.GetValue<string>(_node, nameof(ConnectionInfo.Username)).Should().Be("sub-user");
        ConnectionInheritanceAccessor.GetOwnValue<string>(_node, nameof(ConnectionInfo.Username)).Should().Be("own-user");
        _node.Inheritance.Username.Should().BeTrue("reading the own value must not change the flag");
    }

    [Fact]
    public void SetInheritFlag_SwitchesEffectiveValue()
    {
        ConnectionInheritanceAccessor.SetInheritFlag(_node, nameof(ConnectionInfo.Username), true);
        _node.Username.Should().Be("sub-user");

        ConnectionInheritanceAccessor.SetInheritFlag(_node, nameof(ConnectionInfo.Username), false);
        _node.Username.Should().Be("own-user");
    }

    [Fact]
    public void SetOwnValue_WhileInheriting_KeepsEffectiveParentValue()
    {
        _node.Inheritance.Port = true;
        _subFolder.Port = 2222;

        ConnectionInheritanceAccessor.SetOwnValue(_node, nameof(ConnectionInfo.Port), 3390);

        _node.Port.Should().Be(2222);
        ConnectionInheritanceAccessor.GetOwnValue<int>(_node, nameof(ConnectionInfo.Port)).Should().Be(3390);
    }

    [Theory]
    [InlineData(nameof(ConnectionInfo.Username), true)]
    [InlineData(nameof(ConnectionInfo.Colors), true)]
    [InlineData(nameof(ConnectionInfo.Name), false)]
    [InlineData(nameof(ConnectionInfo.Hostname), false)]
    [InlineData("EverythingInherited", false)]
    public void SupportsInheritance(string property, bool expected) =>
        ConnectionInheritanceAccessor.SupportsInheritance(property).Should().Be(expected);

    [Fact]
    public void CanInheritFrom_OnlyNonRootFolders()
    {
        ConnectionInheritanceAccessor.CanInheritFrom(_root).Should().BeFalse();
        ConnectionInheritanceAccessor.CanInheritFrom(null).Should().BeFalse();
        ConnectionInheritanceAccessor.CanInheritFrom(_folder).Should().BeTrue();
    }

    [Fact]
    public void ApplyNewConnectionDefaults_UsesLegacyDefaults()
    {
        var info = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo());

        info.Colors.Should().Be(RDPColors.Colors16Bit);
        info.UseCredSsp.Should().BeTrue();
        info.RedirectSound.Should().Be(RDPSounds.DoNotPlay);
        info.Icon.Should().Be("mRemoteNG");
        info.Panel.Should().Be("General");
    }

    [Theory]
    [InlineData(CoreProtocol.SSH2, CoreProtocol.RDP, 22, 3389)]     // default port follows protocol
    [InlineData(CoreProtocol.SSH2, CoreProtocol.RDP, 0, 3389)]      // unset port gets the default
    [InlineData(CoreProtocol.SSH2, CoreProtocol.RDP, 2222, 2222)]   // custom port is kept
    [InlineData(CoreProtocol.RDP, CoreProtocol.Terminal, 3389, 0)]  // no-port protocol
    public void PortAfterProtocolChange(CoreProtocol from, CoreProtocol to, int port, int expected) =>
        ConnectionDefaults.PortAfterProtocolChange(from, to, port).Should().Be(expected);

    [Theory]
    [InlineData(CoreProtocol.SSH2, "server.example.com", true)]
    [InlineData(CoreProtocol.SSH2, "10.1.2.3", true)]
    [InlineData(CoreProtocol.SSH2, "fe80::1", true)]
    [InlineData(CoreProtocol.SSH2, "[fe80::1]", true)]
    [InlineData(CoreProtocol.SSH2, "", false)]
    [InlineData(CoreProtocol.SSH2, "bad host", false)]
    [InlineData(CoreProtocol.SSH2, "bad_host!", false)]
    [InlineData(CoreProtocol.HTTPS, "https://intranet/path?q=1", true)]
    [InlineData(CoreProtocol.AnyDesk, "alias@ad", true)]
    [InlineData(CoreProtocol.Terminal, "", true)]
    [InlineData(CoreProtocol.PowerShell, "", true)]
    public void ValidateHostname(CoreProtocol protocol, string host, bool valid) =>
        (ConnectionDefaults.ValidateHostname(protocol, host) is null).Should().Be(valid);

    [Theory]
    [InlineData(CoreProtocol.SSH2, 22, true)]
    [InlineData(CoreProtocol.SSH2, 0, false)]
    [InlineData(CoreProtocol.SSH2, 70000, false)]
    [InlineData(CoreProtocol.Terminal, 0, true)]
    public void ValidatePort(CoreProtocol protocol, int port, bool valid) =>
        (ConnectionDefaults.ValidatePort(protocol, port) is null).Should().Be(valid);
}
