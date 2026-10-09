using System.Xml;
using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

/// <summary>
/// Verifies mRemoteNG.Core reads connection files written by the legacy WinForms app
/// (same fixtures as mRemoteNGTests' XmlConnectionsDeserializerTests) and writes files it can read back.
/// </summary>
public class XmlConnectionsCompatibilityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mrng-tests-{Guid.NewGuid():N}");

    public XmlConnectionsCompatibilityTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string, string?> LegacyFiles => new()
    {
        { "confCons_v2_5.xml", null },
        { "confCons_v2_5_fullencryption.xml", null },
        { "confCons_v2_5_passwordis_Password_fullencryption.xml", "Password" },
        { "confCons_v2_6.xml", null },
        { "confCons_v2_6_5k-iterations.xml", null },
        { "confCons_v2_6_fullencryption.xml", null },
        { "confCons_v2_6_passwordis_Password.xml", "Password" },
        { "confCons_v2_6_passwordis_Password_fullencryption.xml", "Password" },
    };

    public static TheoryData<string> ProtectedFiles => new()
    {
        "confCons_v2_5_passwordis_Password_fullencryption.xml",
        "confCons_v2_6_passwordis_Password.xml",
        "confCons_v2_6_passwordis_Password_fullencryption.xml",
    };

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

    private static ConnectionTreeModel Load(string file, string? password) =>
        new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(Fixture(file), password);

    private static ContainerInfo Folder(IEnumerable<ConnectionInfo> nodes, string name) =>
        nodes.OfType<ContainerInfo>().Single(n => n.Name == name);

    [Theory]
    [MemberData(nameof(LegacyFiles))]
    public void LegacyFile_LoadsExpectedTree(string file, string? password)
    {
        var root = Load(file, password).RootNode;

        root.Children.Should().HaveCount(3);
        var folder1 = Folder(root.Children, "Folder1");
        folder1.IsExpanded.Should().BeTrue();
        folder1.Children.Count(n => n is not ContainerInfo).Should().Be(3);

        var folder2 = Folder(root.Children, "Folder2");
        folder2.Children.Should().HaveCount(3);
        var folder21 = Folder(folder2.Children, "Folder2.1");
        folder21.Children.Should().HaveCount(2);
        Folder(folder21.Children, "Folder2.1.1").Children.Count(n => n is not ContainerInfo).Should().Be(1);
        Folder(folder2.Children, "Folder2.2").Inheritance.Username.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(LegacyFiles))]
    public void LegacyFile_KeepsMasterPasswordOnRootNode(string file, string? password)
    {
        var root = Load(file, password).RootNode;

        root.IsPasswordProtected.Should().Be(password is not null);
        root.PasswordString.Should().Be(password ?? root.DefaultPassword);
    }

    [Fact]
    public void LegacyFiles_DecryptSamePerConnectionPasswordsAcrossAllVersionsAndCiphers()
    {
        var passwordsByFile = LegacyFiles
            .Select(row => (File: (string)row[0], Password: (string?)row[1]))
            .Select(f => Load(f.File, f.Password).GetRecursiveChildList()
                .Where(n => !string.IsNullOrEmpty(n.Password))
                .ToDictionary(n => n.Name, n => n.Password))
            .ToList();

        // Every fixture holds the same connections, so the plaintext must match across v2.5 (Rijndael)
        // and v2.6 (AES-GCM, 1k/5k iterations, full-file encryption, custom master password).
        passwordsByFile[0].Should().NotBeEmpty();
        foreach (var passwords in passwordsByFile.Skip(1))
            passwords.Should().BeEquivalentTo(passwordsByFile[0]);
    }

    [Theory]
    [MemberData(nameof(ProtectedFiles))]
    public void ProtectedFile_WithoutPassword_ThrowsPasswordRequired(string file)
    {
        var act = () => Load(file, null);

        act.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ProtectedFiles))]
    public void ProtectedFile_WithWrongPassword_ThrowsIncorrectPassword(string file)
    {
        var act = () => Load(file, "not-the-password");

        act.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeTrue();
    }

    [Fact]
    public void FileKdfIterations_AreReadFromHeaderAndPreservedOnSave()
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.LoadFromFile(Fixture("confCons_v2_6_5k-iterations.xml"));
        service.Encryption.KeyDerivationIterations.Should().Be(5000);

        var saved = Path.Combine(_tempDir, "5k.xml");
        service.SaveToFile(saved);

        new XmlDocument().Also(d => d.Load(saved)).DocumentElement!.GetAttribute("KdfIterations").Should().Be("5000");
    }

    [Theory]
    [MemberData(nameof(LegacyFiles))]
    public void LegacyFile_SaveAndReload_PreservesConnections(string file, string? password)
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        var original = service.LoadFromFile(Fixture(file), password);
        var saved = Path.Combine(_tempDir, file);

        service.SaveToFile(saved);
        var reloaded = new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(saved, password);

        Snapshot(reloaded).Should().Equal(Snapshot(original));
        reloaded.RootNode.IsPasswordProtected.Should().Be(password is not null);
    }

    [Theory]
    [InlineData("confCons_v2_5.xml", null, false)]
    [InlineData("confCons_v2_5_fullencryption.xml", null, true)]
    [InlineData("confCons_v2_5_passwordis_Password_fullencryption.xml", "Password", true)]
    [InlineData("confCons_v2_6_fullencryption.xml", null, true)]
    public void FullFileEncryption_IsPreservedOnSave(string file, string? password, bool expected)
    {
        new ConnectionsService(new CryptoProviderFactory()).Also(s => s.LoadFromFile(Fixture(file), password))
            .Encryption.FullFileEncryption.Should().Be(expected);
    }

    [Fact]
    public void SavedFile_UsesLegacyCompatibleHeader()
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.LoadFromFile(Fixture("confCons_v2_5.xml"));
        var saved = Path.Combine(_tempDir, "upgraded.xml");

        service.SaveToFile(saved);

        var root = new XmlDocument().Also(d => d.Load(saved)).DocumentElement!;
        root.GetAttribute("ConfVersion").Should().Be("2.8");
        root.GetAttribute("EncryptionEngine").Should().Be("AES");
        root.GetAttribute("BlockCipherMode").Should().Be("GCM");
        root.GetAttribute("KdfIterations").Should().Be("1000");
        // Unprotected files must carry "ThisIsNotProtected" or the WinForms app prompts for a password.
        new CryptoProviderFactory().Build().Decrypt(root.GetAttribute("Protected"), "mR3m").Should().Be("ThisIsNotProtected");
    }

    [Fact]
    public void FileFromNewerVersion_IsRejected()
    {
        var path = Path.Combine(_tempDir, "future.xml");
        File.WriteAllText(path, """<?xml version="1.0" encoding="utf-8"?><Connections Name="Connections" ConfVersion="3.0" />""");

        var act = () => new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);

        act.Should().Throw<ConnectionFileVersionException>();
    }

    [Fact]
    public void FileWithDtd_IsRejected()
    {
        var path = Path.Combine(_tempDir, "xxe.xml");
        File.WriteAllText(path, """
            <?xml version="1.0"?>
            <!DOCTYPE Connections [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <Connections Name="&xxe;" ConfVersion="2.6" />
            """);

        var act = () => new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);

        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void NamespacedRootWrittenByLegacy28_IsRead()
    {
        var protectedMarker = new CryptoProviderFactory().Build().Encrypt("ThisIsNotProtected", "mR3m");
        var path = Path.Combine(_tempDir, "v28.xml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="{protectedMarker}" ConfVersion="2.8">
              <Node Name="Server" Type="Connection" Hostname="host.example" Protocol="SSH2" Port="2222" Id="9c6a1a8a-6d4e-4e5c-9f7e-0d2b1f1d7c11" />
            </mrng:Connections>
            """);

        var tree = new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);

        var node = tree.RootNode.Children.Should().ContainSingle().Subject;
        node.Hostname.Should().Be("host.example");
        node.Port.Should().Be(2222);
    }

    [Fact]
    public void NewMasterPassword_IsRequiredAfterSave()
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        var tree = service.CreateNew();
        tree.RootNode.AddChild(new ConnectionInfo { Name = "db", Hostname = "db.local", Password = "s3cret" });
        tree.RootNode.PasswordString = "master-pw";
        var path = Path.Combine(_tempDir, "protected.xml");

        service.SaveToFile(path);

        var reload = () => new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);
        reload.Should().Throw<ConnectionFilePasswordException>();
        new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path, "master-pw")
            .GetConnections().Single().Password.Should().Be("s3cret");
    }

    private static List<string> Snapshot(ConnectionTreeModel tree) =>
        tree.GetRecursiveChildList()
            .Select(n => string.Join("|", n.GetType().Name, n.Name, n.Hostname, n.Port, n.Protocol, n.Username,
                n.Password, n.Domain, n.Description, n.Inheritance.Username, (n as ContainerInfo)?.IsExpanded))
            .ToList();
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
