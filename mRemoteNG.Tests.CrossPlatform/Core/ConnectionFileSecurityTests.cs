using System.Xml;
using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Security.Factories;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

/// <summary>File ▸ Properties: master password and cipher settings applied on save.</summary>
public class ConnectionFileSecurityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mrng-security-{Guid.NewGuid():N}");

    public ConnectionFileSecurityTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

    private static ConnectionsService NewService(out ConnectionInfo connection)
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        var tree = service.CreateNew();
        connection = new ConnectionInfo { Name = "db", Hostname = "db.local", Password = "s3cret" };
        tree.RootNode.AddChild(connection);
        return service;
    }

    [Fact]
    public void Validate_ReportsWrongCurrentPasswordMismatchAndIterations()
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.LoadFromFile(Fixture("confCons_v2_6_passwordis_Password.xml"), "Password");

        var errors = ConnectionFileSecurity.Validate(service, new ConnectionFileSecurityChange
        {
            PasswordAction = MasterPasswordAction.Set,
            CurrentPassword = "wrong",
            NewPassword = "a",
            ConfirmPassword = "b",
            KeyDerivationIterations = 999,
        });

        errors.Should().HaveCount(3);
        errors.Should().Contain(e => e.Contains("current master password"));
        errors.Should().Contain(e => e.Contains("do not match"));
        errors.Should().Contain(e => e.Contains("iterations"));
    }

    [Fact]
    public void Validate_CipherChangeAlone_DoesNotNeedThePassword()
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.LoadFromFile(Fixture("confCons_v2_6_passwordis_Password.xml"), "Password");

        ConnectionFileSecurity.Validate(service, new ConnectionFileSecurityChange
        {
            Engine = BlockCipherEngines.Serpent,
            Mode = BlockCipherModes.CCM,
            KeyDerivationIterations = 2000,
        }).Should().BeEmpty();
    }

    [Theory]
    [InlineData(BlockCipherEngines.AES, BlockCipherModes.GCM, false)]
    [InlineData(BlockCipherEngines.Twofish, BlockCipherModes.EAX, true)]
    [InlineData(BlockCipherEngines.Serpent, BlockCipherModes.CCM, false)]
    public void SetPasswordAndCipher_SavesLegacyHeader_AndReloadNeedsNewPassword(
        BlockCipherEngines engine, BlockCipherModes mode, bool fullFile)
    {
        var service = NewService(out _);
        var changed = ConnectionFileSecurity.Apply(service, new ConnectionFileSecurityChange
        {
            PasswordAction = MasterPasswordAction.Set,
            NewPassword = "n3w-master",
            ConfirmPassword = "n3w-master",
            Engine = engine,
            Mode = mode,
            KeyDerivationIterations = 5000,
            FullFileEncryption = fullFile,
        });
        var path = Path.Combine(_tempDir, $"{engine}-{mode}.xml");

        changed.Should().BeTrue();
        service.SaveToFile(path);

        // What the WinForms reader looks at: cipher header and the "Protected" marker encrypted with it.
        var root = new XmlDocument().Also(d => d.Load(path)).DocumentElement!;
        root.GetAttribute("EncryptionEngine").Should().Be(engine.ToString());
        root.GetAttribute("BlockCipherMode").Should().Be(mode.ToString());
        root.GetAttribute("KdfIterations").Should().Be("5000");
        root.GetAttribute("FullFileEncryption").Should().Be(fullFile ? "true" : "false");
        root.GetAttribute("ConfVersion").Should().Be("2.8");
        new CryptoProviderFactory().Build(engine, mode, 5000).Decrypt(root.GetAttribute("Protected"), "n3w-master")
            .Should().Be("ThisIsProtected");
        if (fullFile)
            root.SelectNodes("Node")!.Count.Should().Be(0, "the node list is encrypted as one block");

        var withoutPassword = () => new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);
        withoutPassword.Should().Throw<ConnectionFilePasswordException>();
        var reloaded = new ConnectionsService(new CryptoProviderFactory());
        reloaded.LoadFromFile(path, "n3w-master").GetConnections().Single().Password.Should().Be("s3cret");
        reloaded.Encryption.Should().Be(new ConnectionFileEncryption(engine, mode, 5000, fullFile));
    }

    [Fact]
    public void ChangePassword_OfLegacyProtectedFile_RequiresCurrentAndReplacesIt()
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.LoadFromFile(Fixture("confCons_v2_6_passwordis_Password.xml"), "Password");
        var change = new ConnectionFileSecurityChange
        {
            PasswordAction = MasterPasswordAction.Set,
            CurrentPassword = "Password",
            NewPassword = "Changed!",
            ConfirmPassword = "Changed!",
            KeyDerivationIterations = 1000,
        };

        ConnectionFileSecurity.Apply(service, change).Should().BeTrue();
        var path = Path.Combine(_tempDir, "changed.xml");
        service.SaveToFile(path);

        var withOld = () => new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path, "Password");
        withOld.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeTrue();
        new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path, "Changed!").RootNode
            .PasswordString.Should().Be("Changed!");
    }

    [Fact]
    public void RemovePassword_ReloadsWithoutPrompt()
    {
        var service = new ConnectionsService(new CryptoProviderFactory());
        service.LoadFromFile(Fixture("confCons_v2_6_passwordis_Password.xml"), "Password");

        var wrong = () => ConnectionFileSecurity.Apply(service, new ConnectionFileSecurityChange
        {
            PasswordAction = MasterPasswordAction.Remove,
            CurrentPassword = "nope",
            KeyDerivationIterations = 1000,
        });
        wrong.Should().Throw<InvalidOperationException>();
        service.ConnectionTreeModel!.RootNode.IsPasswordProtected.Should().BeTrue();

        ConnectionFileSecurity.Apply(service, new ConnectionFileSecurityChange
        {
            PasswordAction = MasterPasswordAction.Remove,
            CurrentPassword = "Password",
            KeyDerivationIterations = 1000,
        }).Should().BeTrue();
        var path = Path.Combine(_tempDir, "unprotected.xml");
        service.SaveToFile(path);

        var reloaded = new ConnectionsService(new CryptoProviderFactory()).LoadFromFile(path);
        reloaded.RootNode.IsPasswordProtected.Should().BeFalse();
        new CryptoProviderFactory().Build().Decrypt(
                new XmlDocument().Also(d => d.Load(path)).DocumentElement!.GetAttribute("Protected"), "mR3m")
            .Should().Be("ThisIsNotProtected");
    }

    [Fact]
    public void Apply_WithoutChanges_ReportsNothingChanged()
    {
        var service = NewService(out _);

        ConnectionFileSecurity.Apply(service, new ConnectionFileSecurityChange()).Should().BeFalse();
    }
}
