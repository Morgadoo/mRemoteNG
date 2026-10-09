using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

public class DefaultConnectionSettingsTests
{
    [Fact]
    public void BuiltInDefaults_AreTheLegacyNewConnectionDefaultsWithSsh()
    {
        var defaults = DefaultConnectionSettings.Load(null, null);

        defaults.Protocol.Should().Be(ProtocolType.SSH2);
        defaults.Port.Should().Be(22);
        defaults.Resolution.Should().Be(RDPResolutions.FitToWindow);
        defaults.UseCredSsp.Should().BeTrue();
        defaults.Inheritance.EverythingInherited.Should().BeFalse();
    }

    [Fact]
    public void SaveAndLoad_RoundTripsValuesAndInheritance()
    {
        var template = DefaultConnectionSettings.Load(null, null);
        template.Protocol = ProtocolType.RDP;
        template.Port = 3390;
        template.Panel = "Servers";
        template.Colors = RDPColors.Colors32Bit;
        template.RedirectClipboard = true;
        template.Inheritance.Username = true;
        template.Inheritance.Domain = true;

        var (values, inheritance) = DefaultConnectionSettings.Save(template);
        var loaded = DefaultConnectionSettings.Load(values, inheritance);

        (loaded.Protocol, loaded.Port, loaded.Panel, loaded.Colors, loaded.RedirectClipboard)
            .Should().Be((ProtocolType.RDP, 3390, "Servers", RDPColors.Colors32Bit, true));
        loaded.Inheritance.Username.Should().BeTrue();
        loaded.Inheritance.Domain.Should().BeTrue();
        loaded.Inheritance.Password.Should().BeFalse();
        inheritance.Split(',').Should().BeEquivalentTo("Username", "Domain");
    }

    [Fact]
    public void Save_NeverStoresSecretsOrNames()
    {
        var template = DefaultConnectionSettings.Load(null, null);
        template.Password = "secret";
        template.RDGatewayPassword = "gw-secret";
        template.VNCProxyPassword = "proxy-secret";
        template.Hostname = "host";

        var (values, _) = DefaultConnectionSettings.Save(template);

        values.Should().NotContain("secret").And.NotContain("\"Hostname\"").And.NotContain("\"Name\"");
    }

    [Fact]
    public void Load_IgnoresInvalidEntries()
    {
        var loaded = DefaultConnectionSettings.Load(
            """{"Protocol":"NotAProtocol","Port":"abc","Panel":"Ok","Unknown":"x","Password":"p"}""",
            "Username,NoSuchFlag, Panel");

        loaded.Protocol.Should().Be(ProtocolType.SSH2);
        loaded.Port.Should().Be(22);
        loaded.Panel.Should().Be("Ok");
        loaded.Password.Should().BeEmpty();
        loaded.Inheritance.Username.Should().BeTrue();
        loaded.Inheritance.Panel.Should().BeTrue();

        DefaultConnectionSettings.Load("not json", "").Protocol.Should().Be(ProtocolType.SSH2);
    }

    [Fact]
    public void ApplyTo_GivesNewNodesTheDefaultsButKeepsTheirName()
    {
        var template = DefaultConnectionSettings.Load(null, null);
        template.Protocol = ProtocolType.VNC;
        template.Port = 5901;
        template.Inheritance.Password = true;

        var connection = DefaultConnectionSettings.ApplyTo(template, new ConnectionInfo { Name = "mine" });
        var folder = DefaultConnectionSettings.ApplyTo(template, new ContainerInfo());

        (connection.Name, connection.Protocol, connection.Port).Should().Be(("mine", ProtocolType.VNC, 5901));
        connection.Inheritance.Password.Should().BeTrue();
        folder.Name.Should().Be("New Folder");
        folder.Protocol.Should().Be(ProtocolType.VNC);
    }

    [Fact]
    public void ApplyInheritance_OnlyChangesFlags()
    {
        var template = DefaultConnectionSettings.Load(null, null);
        template.Inheritance.Username = true;
        var node = new ConnectionInfo { Username = "own", Panel = "P" };
        node.Inheritance.Domain = true;

        DefaultConnectionSettings.ApplyInheritance(template, node);

        node.Inheritance.Username.Should().BeTrue();
        node.Inheritance.Domain.Should().BeFalse();
        node.Panel.Should().Be("P");
    }
}
