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
    /// and values the XML file does not store (Color, RDGatewayAccessToken).
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
            .Except([nameof(ConnectionInfo.Color)]);

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

    private static List<string> Relevant(ConnectionInfo node) => ConnectionPropertyCatalog.RelevantProperties(node).ToList();
}
