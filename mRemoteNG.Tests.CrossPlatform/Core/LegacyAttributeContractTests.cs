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

    [Fact]
    public void NewSerializer_WritesEveryAttributeTheLegacyV28SerializerWrites()
    {
        var legacySource = File.ReadAllText(Path.Combine(RepoRoot(),
            "mRemoteNG", "Config", "Serializers", "ConnectionSerializers", "Xml", "XmlConnectionNodeSerializer28.cs"));
        var legacyAttributes = Regex.Matches(legacySource, @"XAttribute\(""(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        legacyAttributes.Should().HaveCountGreaterThan(100, "the legacy source was parsed");

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
}
