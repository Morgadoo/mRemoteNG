using System.Reflection;
using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Core.Container;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Core;

public class ConnectionPropertyCatalogTests
{
    /// <summary>
    /// ConnectionInfo properties that are not edited in the connection dialog: tree state, runtime flags,
    /// Color, and the RD Gateway access token (stored in the file, filled by the gateway rather than the dialog).
    /// </summary>
    private static readonly string[] NotEditable =
    [
        nameof(ConnectionInfo.Inheritance), nameof(ConnectionInfo.IsContainer), nameof(ConnectionInfo.IsDefault),
        nameof(ConnectionInfo.IsQuickConnect), nameof(ConnectionInfo.PleaseConnect), nameof(ConnectionInfo.Color),
        nameof(ConnectionInfo.RDGatewayAccessToken),
    ];

    [Fact]
    public void Catalog_CoversEveryEditableConnectionProperty()
    {
        var editable = typeof(ConnectionInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic && !NotEditable.Contains(p.Name))
            .Select(p => p.Name);

        ConnectionPropertyCatalog.All.Select(d => d.Name).Should().BeEquivalentTo(editable);
        ConnectionPropertyCatalog.All.Select(d => d.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryInheritanceFlag_HasAnEditor()
    {
        var flags = new ConnectionInfo().Inheritance.GetProperties()
            .Where(p => p.PropertyType == typeof(bool))
            .Select(p => p.Name)
            .Except([nameof(ConnectionInfo.Color), nameof(ConnectionInfo.RDGatewayAccessToken)]);

        ConnectionPropertyCatalog.All.Where(d => d.SupportsInheritance).Select(d => d.Name)
            .Should().BeEquivalentTo(flags);
    }

    [Fact]
    public void Descriptors_UseCategoriesAndEditorsMatchingTheirType()
    {
        foreach (var descriptor in ConnectionPropertyCatalog.All)
        {
            ConnectionPropertyCategories.All.Should().Contain(descriptor.Category);
            descriptor.DisplayName.Should().NotBeNullOrWhiteSpace();
            descriptor.Description.Should().NotBeNullOrWhiteSpace();
            var expectedType = descriptor.Editor switch
            {
                ConnectionPropertyEditor.Boolean => typeof(bool),
                ConnectionPropertyEditor.Number => typeof(int),
                ConnectionPropertyEditor.Choice => descriptor.PropertyType,
                _ => typeof(string),
            };
            descriptor.PropertyType.Should().Be(expectedType, descriptor.Name);
            if (descriptor.Editor == ConnectionPropertyEditor.Choice)
                descriptor.PropertyType.IsEnum.Should().BeTrue(descriptor.Name);
        }
    }

    [Fact]
    public void RdpSettings_AreOnlyRelevantForRdp()
    {
        var rdp = new ConnectionInfo { Protocol = CoreProtocol.RDP };
        var ssh = new ConnectionInfo { Protocol = CoreProtocol.SSH2 };

        ConnectionPropertyCatalog.RelevantProperties(rdp).Should()
            .Contain([nameof(ConnectionInfo.Resolution), nameof(ConnectionInfo.RedirectPrinters), nameof(ConnectionInfo.UseCredSsp)])
            .And.NotContain([nameof(ConnectionInfo.PuttySession), nameof(ConnectionInfo.VNCViewOnly)]);
        ConnectionPropertyCatalog.RelevantProperties(ssh).Should()
            .Contain([nameof(ConnectionInfo.PuttySession), nameof(ConnectionInfo.SSHOptions), nameof(ConnectionInfo.OpeningCommand)])
            .And.NotContain([nameof(ConnectionInfo.Resolution), nameof(ConnectionInfo.RDGatewayUsageMethod)]);
    }

    [Fact]
    public void DependentSettings_FollowLegacyRules()
    {
        var node = new ConnectionInfo { Protocol = CoreProtocol.RDP, RDGatewayUsageMethod = RDGatewayUsageMethod.Never };
        Relevant(node).Should().NotContain(nameof(ConnectionInfo.RDGatewayHostname));

        node.RDGatewayUsageMethod = RDGatewayUsageMethod.Always;
        node.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes;
        Relevant(node).Should().Contain(nameof(ConnectionInfo.RDGatewayHostname))
            .And.NotContain(nameof(ConnectionInfo.RDGatewayPassword));

        node.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.No;
        Relevant(node).Should().Contain([nameof(ConnectionInfo.RDGatewayUsername), nameof(ConnectionInfo.RDGatewayPassword)]);

        node.RedirectSound = RDPSounds.DoNotPlay;
        Relevant(node).Should().NotContain(nameof(ConnectionInfo.SoundQuality));
        node.RedirectSound = RDPSounds.BringToThisComputer;
        Relevant(node).Should().Contain(nameof(ConnectionInfo.SoundQuality));

        node.ExternalAddressProvider = ExternalAddressProvider.AmazonWebServices;
        Relevant(node).Should().Contain([nameof(ConnectionInfo.EC2InstanceId), nameof(ConnectionInfo.EC2Region)]);

        node.ExternalCredentialProvider = ExternalCredentialProvider.VaultOpenbao;
        Relevant(node).Should().Contain(nameof(ConnectionInfo.VaultOpenbaoMount))
            .And.NotContain(nameof(ConnectionInfo.Password));
    }

    [Fact]
    public void VncProxyFields_NeedAProxyType()
    {
        var node = new ConnectionInfo { Protocol = CoreProtocol.VNC, VNCProxyType = VncProxyType.ProxyNone };
        Relevant(node).Should().Contain(nameof(ConnectionInfo.VNCProxyType)).And.NotContain(nameof(ConnectionInfo.VNCProxyIP));

        node.VNCProxyType = VncProxyType.ProxySocks5;
        Relevant(node).Should().Contain([nameof(ConnectionInfo.VNCProxyIP), nameof(ConnectionInfo.VNCProxyPort)]);
    }

    [Fact]
    public void Folders_HaveNoHostName()
    {
        Relevant(new ContainerInfo()).Should().NotContain(nameof(ConnectionInfo.Hostname))
            .And.Contain(nameof(ConnectionInfo.Username));
    }

    [Fact]
    public void PluginProperty_ReadsAndWritesTypedValues_InPluginProperties()
    {
        var flag = ConnectionPropertyDescriptor.ForPlugin("p.flag", "Plugin", "Flag", "A flag", ConnectionPropertyEditor.Boolean);
        var count = ConnectionPropertyDescriptor.ForPlugin("p.count", "Plugin", "Count", "", ConnectionPropertyEditor.Number, "3");
        var text = ConnectionPropertyDescriptor.ForPlugin("p.text", "", "", "");
        var node = new ConnectionInfo();

        flag.GetPluginValue(node).Should().Be(false);
        count.GetPluginValue(node).Should().Be(3);
        text.GetPluginValue(node).Should().Be(string.Empty);
        (flag.PropertyType, count.PropertyType, text.PropertyType).Should().Be((typeof(bool), typeof(int), typeof(string)));
        text.DisplayName.Should().Be("p.text");
        text.Section.Should().Be(ConnectionPropertyCategories.Plugins);

        flag.SetPluginValue(node, false);
        count.SetPluginValue(node, 3);
        text.SetPluginValue(node, string.Empty);
        node.PluginProperties.Should().BeEmpty("defaults are not stored on a connection that has no value yet");

        flag.SetPluginValue(node, true);
        count.SetPluginValue(node, -7);
        text.SetPluginValue(node, "hello");
        node.PluginProperties.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["p.flag"] = "True", ["p.count"] = "-7", ["p.text"] = "hello",
        });
        (flag.GetPluginValue(node), count.GetPluginValue(node), text.GetPluginValue(node)).Should().Be((true, -7, "hello"));

        flag.SetPluginValue(node, false);
        text.SetPluginValue(node, "");
        node.PluginProperties.Should().ContainKey("p.flag").WhoseValue.Should().Be("False");
        node.PluginProperties.Should().NotContainKey("p.text");
    }

    [Fact]
    public void PluginProperty_AppliesToItsProtocols_AndNotToFolders()
    {
        var rdpOnly = ConnectionPropertyDescriptor.ForPlugin("p.rdp", "Plugin", "RDP only", "", protocols: [CoreProtocol.RDP]);
        object? Values(string name, CoreProtocol protocol) => name == nameof(ConnectionInfo.Protocol) ? protocol : null;

        rdpOnly.IsRelevant(n => Values(n, CoreProtocol.RDP), isFolder: false).Should().BeTrue();
        rdpOnly.IsRelevant(n => Values(n, CoreProtocol.SSH2), isFolder: false).Should().BeFalse();
        rdpOnly.IsRelevant(n => Values(n, CoreProtocol.RDP), isFolder: true).Should().BeFalse();
        rdpOnly.SupportsInheritance.Should().BeFalse();
        FluentActions.Invoking(() => rdpOnly.Property).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => ConnectionPropertyDescriptor.ForPlugin("p", "", "", "", ConnectionPropertyEditor.Choice))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    private static List<string> Relevant(ConnectionInfo node) => ConnectionPropertyCatalog.RelevantProperties(node).ToList();
}
