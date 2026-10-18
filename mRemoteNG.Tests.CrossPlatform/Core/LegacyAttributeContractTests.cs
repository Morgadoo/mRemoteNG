using System.Text.RegularExpressions;
using System.Xml;
using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

/// <summary>
/// Guards against silent data loss: every attribute the legacy WinForms app writes for a connection
/// node must also be written (and therefore read back) by the cross-platform serializer.
/// </summary>
public class LegacyAttributeContractTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mrng-contract-{Guid.NewGuid():N}");

    public LegacyAttributeContractTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mRemoteNG.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    [Theory]
    [InlineData("XmlConnectionNodeSerializer28.cs")]
    [InlineData("XmlConnectionNodeSerializer29.cs")]
    public void NewSerializer_WritesEveryAttributeTheLegacySerializerWrites(string legacySerializer)
    {
        var xmlDirectory = Path.Combine(RepoRoot(), "mRemoteNG", "Config", "Serializers", "ConnectionSerializers", "Xml");
        // 2.9 derives from 2.8 and only adds attributes.
        var legacyAttributes = new[] { "XmlConnectionNodeSerializer28.cs", legacySerializer }
            .Distinct()
            .Select(file => File.ReadAllText(Path.Combine(xmlDirectory, file)))
            // Inheritance names moved to AddInheritanceAttribute(..., "InheritX"); the attribute is still written.
            .SelectMany(source => Regex.Matches(source, @"XAttribute\(""(\w+)""|AddInheritanceAttribute\([^""]*""(\w+)""\)"))
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToHashSet();
        legacyAttributes.Should().HaveCountGreaterThan(100, "the legacy source was parsed");
        if (legacySerializer.EndsWith("29.cs", StringComparison.Ordinal))
            legacyAttributes.Should().Contain(["TerminalBackend", "InheritTerminalBackend"]);

        var root = new RootNodeInfo(RootNodeType.Connection);
        // A folder: it gets every connection attribute plus "Expanded".
        var node = new mRemoteNG.Core.Container.ContainerInfo { Name = "n" };
        root.AddChild(node);
        // Legacy writes Inherit* attributes only when set; set all so every name is emitted.
        foreach (var property in typeof(ConnectionInfoInheritance).GetProperties()
                     .Where(p => p.PropertyType == typeof(bool) && p.CanWrite))
            property.SetValue(node.Inheritance, true);

        var xml = new XmlConnectionsSerializer(new CryptoProviderFactory().Build()).Serialize(new ConnectionTreeModel(root));
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var written = doc.DocumentElement!.FirstChild!.Attributes!.Cast<XmlAttribute>().Select(a => a.Name).ToHashSet();

        legacyAttributes.Except(written).Should().BeEmpty("these legacy attributes would be lost on save");
    }

    [Fact]
    public void IsRootGroup_RoundTripsAndDefaultsToFalseWhenTheAttributeIsMissing()
    {
        var path = Path.Combine(_tempDir, "roots.xml");
        var marker = new CryptoProviderFactory().Build().Encrypt("ThisIsNotProtected", "mR3m");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="{marker}" ConfVersion="2.8">
              <Node Name="extra" Type="Container" IsRootGroup="true" Expanded="true" Id="33333333-3333-3333-3333-333333333333">
                <Node Name="nested" Type="Container" Expanded="true" Id="44444444-4444-4444-4444-444444444444" />
              </Node>
            </Connections>
            """);

        var service = new ConnectionsService(new CryptoProviderFactory());
        var folders = service.LoadFromFile(path).RootNode.Children.OfType<mRemoteNG.Core.Container.ContainerInfo>().ToList();
        folders.Should().ContainSingle();
        folders[0].IsRootGroup.Should().BeTrue();
        folders[0].Children.OfType<mRemoteNG.Core.Container.ContainerInfo>().Single().IsRootGroup.Should().BeFalse();

        service.SaveToFile(path);
        var reloaded = new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);
        reloaded.RootNode.Children.OfType<mRemoteNG.Core.Container.ContainerInfo>().Single().IsRootGroup.Should().BeTrue();
    }

    [Fact]
    public void GatewayTokenAndRedirectionServerName_RoundTrip()
    {
        var path = Path.Combine(_tempDir, "gateway.xml");
        var service = new ConnectionsService(new CryptoProviderFactory());
        var root = service.CreateNew().RootNode;
        var node = new ConnectionInfo { Name = "gw", Hostname = "host", Protocol = mRemoteNG.Core.Connection.Protocol.ProtocolType.RDP };
        node.RDGatewayAccessToken = "tok&en<1>";
        node.UseRedirectionServerName = true;
        node.Inheritance.RDGatewayAccessToken = true;
        node.Inheritance.UseRedirectionServerName = true;
        root.AddChild(node);
        service.SaveToFile(path);

        var loaded = new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path).GetConnections().Single();
        loaded.RDGatewayAccessToken.Should().Be("tok&en<1>");
        loaded.UseRedirectionServerName.Should().BeTrue();
        loaded.Inheritance.RDGatewayAccessToken.Should().BeTrue();
        loaded.Inheritance.UseRedirectionServerName.Should().BeTrue();

        var element = (XmlElement)new XmlDocument().Also(d => d.Load(path)).DocumentElement!.FirstChild!;
        element.GetAttribute("RDGatewayAccessToken").Should().NotBe("tok&en<1>");
        element.GetAttribute("UseRedirectionServerName").Should().Be("true");
    }

    [Fact]
    public void ConsoleSessionAndConnectedFlags_RoundTripUnderLegacyNames()
    {
        var path = Path.Combine(_tempDir, "flags.xml");
        var marker = new CryptoProviderFactory().Build().Encrypt("ThisIsNotProtected", "mR3m");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="{marker}" ConfVersion="2.8">
              <Node Name="srv" Type="Connection" Protocol="RDP" Hostname="h" ConnectToConsole="true" Connected="true" Id="11111111-1111-1111-1111-111111111111" />
            </Connections>
            """);

        var service = new ConnectionsService(new CryptoProviderFactory());
        var loaded = service.LoadFromFile(path).GetConnections().Single();
        loaded.UseConsoleSession.Should().BeTrue();
        loaded.PleaseConnect.Should().BeTrue();

        service.SaveToFile(path);
        var element = (XmlElement)new XmlDocument().Also(d => d.Load(path)).DocumentElement!.FirstChild!;
        element.GetAttribute("ConnectToConsole").Should().Be("true");
        element.GetAttribute("Connected").Should().Be("true");
        element.HasAttribute("UseConsoleSession").Should().BeFalse();
    }

    [Theory]
    [InlineData("True", RDPDiskDrives.Local)]
    [InlineData("False", RDPDiskDrives.None)]
    [InlineData("All", RDPDiskDrives.All)]
    public void RedirectDiskDrives_BooleanFromOlderFilesIsMigratedLikeTheLegacyApp(string stored, RDPDiskDrives expected)
    {
        var path = Path.Combine(_tempDir, $"drives-{stored}.xml");
        var marker = new CryptoProviderFactory().Build().Encrypt("ThisIsNotProtected", "mR3m");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="{marker}" ConfVersion="2.6">
              <Node Name="srv" Type="Connection" Protocol="RDP" Hostname="h" RedirectDiskDrives="{stored}" Id="22222222-2222-2222-2222-222222222222" />
            </Connections>
            """);

        var node = new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path).GetConnections().Single();

        node.RedirectDiskDrives.Should().Be(expected);
    }

    [Fact]
    public void Version29File_FromTheWinFormsApp_LoadsTerminalBackendAndPluginData()
    {
        var path = Path.Combine(_tempDir, "v29.xml");
        var marker = new CryptoProviderFactory().Build().Encrypt("ThisIsNotProtected", "mR3m");
        File.WriteAllText(path, $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="{{marker}}" ConfVersion="2.9">
              <Node Name="ssh" Type="Connection" Protocol="SSH2" Hostname="h" TerminalBackend="WindowsTerminal" InheritTerminalBackend="true" Id="55555555-5555-5555-5555-555555555555">
                <PluginData>{"mRp.AWS.Enabled":"True","mRp.MultiAddress.Hosts":"a,b"}</PluginData>
              </Node>
            </Connections>
            """);

        var service = new ConnectionsService(new CryptoProviderFactory());
        var node = service.LoadFromFile(path).GetConnections().Single();

        node.Inheritance.TerminalBackend.Should().BeTrue();
        node.Inheritance.DisableInheritance();
        node.TerminalBackend.Should().Be(mRemoteNG.Core.Connection.Protocol.TerminalBackend.WindowsTerminal);
        node.Inheritance.EnableInheritance();
        node.GetPluginProperty("mRp.AWS.Enabled").Should().Be("True");
        node.GetPluginProperty("MRP.MULTIADDRESS.HOSTS").Should().Be("a,b", "keys are case-insensitive like the legacy app");

        service.SaveToFile(path);
        var element = (XmlElement)new XmlDocument().Also(d => d.Load(path)).DocumentElement!.FirstChild!;
        element.GetAttribute("TerminalBackend").Should().Be("WindowsTerminal");
        element.GetAttribute("InheritTerminalBackend").Should().Be("true");
        element["PluginData"]!.InnerText.Should().Contain("\"mRp.MultiAddress.Hosts\":\"a,b\"");

        var reloaded = new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path).GetConnections().Single();
        reloaded.PluginProperties.Should().HaveCount(2);
    }

    [Fact]
    public void Version30File_IsStillRefused()
    {
        var path = Path.Combine(_tempDir, "v30.xml");
        File.WriteAllText(path, """<?xml version="1.0" encoding="utf-8"?><Connections Name="Connections" ConfVersion="3.0" />""");

        var act = () => new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);

        act.Should().Throw<ConnectionFileVersionException>();
    }

    [Fact]
    public void PluginData_SurvivesFullFileEncryption_AndClone()
    {
        var path = Path.Combine(_tempDir, "encrypted-plugins.xml");
        var service = new ConnectionsService(new CryptoProviderFactory());
        var root = service.CreateNew().RootNode;
        var node = new ConnectionInfo { Name = "n", Hostname = "h" };
        node.SetPluginProperty("Plugin.Key", "va;lue <&>");
        root.AddChild(node);
        node.Clone().GetPluginProperty("plugin.key").Should().Be("va;lue <&>");
        service.Encryption = service.Encryption with { FullFileEncryption = true };
        service.SaveToFile(path);

        File.ReadAllText(path).Should().NotContain("Plugin.Key");
        new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path).GetConnections().Single()
            .GetPluginProperty("Plugin.Key").Should().Be("va;lue <&>");
    }
}
