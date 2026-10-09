using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using mRemoteNG.Platform;
using mRemoteNG.Platform.Security;
using mRemoteNG.Protocols.Abstractions;
using NSubstitute;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

public sealed class ExternalProviderPreparationStepsTests
{
    private readonly IExternalCredentialProvider _vault = Substitute.For<IExternalCredentialProvider>();
    private readonly IExternalCredentialProvider _delinea = Substitute.For<IExternalCredentialProvider>();
    private readonly IExternalAddressProvider _aws = Substitute.For<IExternalAddressProvider>();
    private readonly AppSettings _settings = new();

    public ExternalProviderPreparationStepsTests()
    {
        _vault.Kind.Returns(ExternalCredentialProvider.VaultOpenbao);
        _vault.DisplayName.Returns("Vault/OpenBao");
        _delinea.Kind.Returns(ExternalCredentialProvider.DelineaSecretServer);
        _delinea.DisplayName.Returns("Delinea Secret Server");
        _aws.Kind.Returns(ExternalAddressProvider.AmazonWebServices);
        _aws.DisplayName.Returns("AWS EC2");
    }

    private ConnectionPreparer Preparer() => new(
    [
        new ExternalCredentialPreparationStep([_vault, _delinea], () => _settings),
        new ExternalAddressPreparationStep([_aws]),
    ]);

    private static ConnectionInfo Ssh(string username = "root") => new()
    {
        Name = "web01",
        Hostname = "web01.example",
        Protocol = CoreProtocol.SSH2,
        Username = username,
        Password = "stored-pw",
        Domain = "STORED",
    };

    [Fact]
    public async Task AddressStep_ReplacesTheHostname_BeforeCredentialsAreResolved()
    {
        var connection = Ssh();
        connection.ExternalAddressProvider = ExternalAddressProvider.AmazonWebServices;
        connection.EC2InstanceId = "i-0abc";
        connection.EC2Region = "eu-west-1";
        connection.ExternalCredentialProvider = ExternalCredentialProvider.VaultOpenbao;
        connection.VaultOpenbaoSecretEngine = VaultOpenbaoSecretEngine.SSHOTP;
        _aws.ResolveAsync(new ExternalAddressRequest("i-0abc", "eu-west-1"), Arg.Any<CancellationToken>()).Returns("54.1.2.3");
        _vault.GetAsync(Arg.Any<ExternalCredentialRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalCredential { Password = "otp" });

        await using var prepared = await Preparer().PrepareAsync(connection);

        prepared.Parameters.Hostname.Should().Be("54.1.2.3");
        await _vault.Received(1).GetAsync(Arg.Is<ExternalCredentialRequest>(r => r.Hostname == "54.1.2.3"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddressStep_WithoutInstanceId_StopsTheConnect()
    {
        var connection = Ssh();
        connection.ExternalAddressProvider = ExternalAddressProvider.AmazonWebServices;

        var act = () => Preparer().PrepareAsync(connection);

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("AWS EC2: \"web01\" uses the AWS EC2 address provider but has no EC2 instance ID.");
    }

    [Fact]
    public async Task CredentialStep_PassesTheVaultFields_AndKeepsValuesTheProviderDoesNotSupply()
    {
        var connection = Ssh();
        connection.ExternalCredentialProvider = ExternalCredentialProvider.VaultOpenbao;
        connection.VaultOpenbaoMount = "secret";
        connection.VaultOpenbaoRole = "ssh/web01";
        _vault.GetAsync(Arg.Any<ExternalCredentialRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalCredential { Password = "from-vault" });

        await using var prepared = await Preparer().PrepareAsync(connection);

        prepared.Parameters.Username.Should().Be("root");
        prepared.Parameters.Password.Should().Be("from-vault");
        prepared.Parameters.Domain.Should().Be("STORED");
        await _vault.Received(1).GetAsync(
            Arg.Is<ExternalCredentialRequest>(r => r.VaultMount == "secret" && r.VaultRole == "ssh/web01"
                && r.VaultEngine == VaultOpenbaoSecretEngine.Kv && r.Username == "root" && r.Hostname == "web01.example"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CredentialStep_ReplacesUsernamePasswordAndDomain_WithTheSecret()
    {
        var connection = Ssh();
        connection.ExternalCredentialProvider = ExternalCredentialProvider.DelineaSecretServer;
        connection.UserViaAPI = "42";
        _delinea.GetAsync(Arg.Is<ExternalCredentialRequest>(r => r.Reference == "42"), Arg.Any<CancellationToken>())
            .Returns(new ExternalCredential { Username = "Administrator", Password = "S3cr3t", Domain = "CORP" });

        await using var prepared = await Preparer().PrepareAsync(connection);

        prepared.Parameters.Should().BeEquivalentTo(new { Username = "Administrator", Password = "S3cr3t", Domain = "CORP" });
    }

    [Fact]
    public async Task CredentialStep_WritesAPrivateKeyToAPrivateTemporaryFile_DeletedWithTheSession()
    {
        var connection = Ssh();
        connection.ExternalCredentialProvider = ExternalCredentialProvider.DelineaSecretServer;
        connection.UserViaAPI = "77";
        const string key = "-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3Bl\r\n-----END OPENSSH PRIVATE KEY-----";
        _delinea.GetAsync(Arg.Any<ExternalCredentialRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalCredential { Username = "deploy", PrivateKey = key, PrivateKeyPassphrase = "pp" });

        string path;
        await using (var prepared = await Preparer().PrepareAsync(connection))
        {
            path = prepared.Parameters.PrivateKeyPath!;
            File.ReadAllText(path).Should().Be("-----BEGIN OPENSSH PRIVATE KEY-----\nb3Bl\n-----END OPENSSH PRIVATE KEY-----\n");
            prepared.Parameters.PrivateKeyPassphrase.Should().Be("pp");
            prepared.Parameters.Username.Should().Be("deploy");
            if (!OperatingSystem.IsWindows())
                File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task TemporaryKeyFile_RemovesKeysLeftByProcessesThatNoLongerRun()
    {
        await using var first = await TemporaryKeyFile.CreateAsync("key", CancellationToken.None);
        var directory = Path.GetDirectoryName(first.Path)!;
        var orphan = Path.Combine(directory, $"{int.MaxValue - 7}-{Guid.NewGuid():N}.key"); // no such process
        await File.WriteAllTextAsync(orphan, "stale");

        await using var second = await TemporaryKeyFile.CreateAsync("key", CancellationToken.None);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(first.Path).Should().BeTrue("keys of the running app are kept");
    }

    [Fact]
    public async Task CredentialStep_NoCredentialsOption_SkipsTheProvider()
    {
        var connection = Ssh();
        connection.ExternalCredentialProvider = ExternalCredentialProvider.DelineaSecretServer;
        connection.UserViaAPI = "42";

        await using var prepared = await Preparer().PrepareAsync(connection, new ConnectOptions { NoCredentials = true });

        prepared.Parameters.Username.Should().BeNull();
        await _delinea.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    [Fact]
    public async Task CredentialStep_ProviderError_StopsTheConnect()
    {
        var connection = Ssh();
        connection.ExternalCredentialProvider = ExternalCredentialProvider.DelineaSecretServer;
        connection.UserViaAPI = "42";
        _delinea.GetAsync(Arg.Any<ExternalCredentialRequest>(), Arg.Any<CancellationToken>())
            .Returns<ExternalCredential>(_ => throw new ExternalProviderException("Delinea Secret Server: login failed (400): Login failed."));

        var act = () => Preparer().PrepareAsync(connection);

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Delinea Secret Server: login failed*");
    }

    [Fact]
    public async Task CredentialStep_UsesTheDefaultProvider_ForConnectionsWithoutUsername()
    {
        _settings.DefaultExternalCredentialProvider = ExternalCredentialProvider.DelineaSecretServer;
        _settings.DefaultUserViaApi = "99";
        _delinea.GetAsync(Arg.Is<ExternalCredentialRequest>(r => r.Reference == "99"), Arg.Any<CancellationToken>())
            .Returns(new ExternalCredential { Username = "fallback", Password = "fb-pw" });

        await using var withoutUser = await Preparer().PrepareAsync(Ssh(username: ""));
        await using var withUser = await Preparer().PrepareAsync(Ssh());

        withoutUser.Parameters.Username.Should().Be("fallback");
        withUser.Parameters.Username.Should().Be("root");
        await _delinea.Received(1).GetAsync(Arg.Any<ExternalCredentialRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GatewayCredentials_ComeFromTheGatewayProvider_IntoTheRdpExtras()
    {
        var connection = new ConnectionInfo
        {
            Name = "desk01",
            Hostname = "desk01.corp",
            Protocol = CoreProtocol.RDP,
            Username = "user",
            Password = "pw",
            RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
            RDGatewayHostname = "gw.corp",
            RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.ExternalCredentialProvider,
            RDGatewayExternalCredentialProvider = ExternalCredentialProvider.DelineaSecretServer,
            RDGatewayUserViaAPI = "500",
        };
        _delinea.GetAsync(Arg.Is<ExternalCredentialRequest>(r => r.Reference == "500"), Arg.Any<CancellationToken>())
            .Returns(new ExternalCredential { Username = "gwuser", Password = "gw-pw", Domain = "GW" });

        await using var prepared = await Preparer().PrepareAsync(connection);

        prepared.Parameters.Extras.Should().Contain(new Dictionary<string, string>
        {
            [ConnectionParametersFactory.Keys.RdpGateway] = "gw.corp",
            [ConnectionParametersFactory.Keys.RdpGatewayUsername] = "gwuser",
            [ConnectionParametersFactory.Keys.RdpGatewayPassword] = "gw-pw",
            [ConnectionParametersFactory.Keys.RdpGatewayDomain] = "GW",
        });
        prepared.Parameters.Username.Should().Be("user"); // the session's own credentials are untouched
    }

    [Theory]
    [InlineData(RDGatewayUseConnectionCredentials.Yes)]
    [InlineData(RDGatewayUseConnectionCredentials.SmartCard)]
    public async Task GatewayCredentials_AreNotFetched_WhenTheGatewayUsesOtherCredentials(RDGatewayUseConnectionCredentials mode)
    {
        var connection = new ConnectionInfo
        {
            Hostname = "desk01.corp",
            Protocol = CoreProtocol.RDP,
            RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
            RDGatewayHostname = "gw.corp",
            RDGatewayUseConnectionCredentials = mode,
            RDGatewayExternalCredentialProvider = ExternalCredentialProvider.DelineaSecretServer,
            RDGatewayUserViaAPI = "500",
        };

        await using var prepared = await Preparer().PrepareAsync(connection);

        prepared.Parameters.Extras.Should().NotContainKey(ConnectionParametersFactory.Keys.RdpGatewayPassword);
        await _delinea.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    [Fact]
    public void AddExternalProviders_RegistersAProviderPerKind_AndBothSteps()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AppSettingsService(Substitute.For<ISettingsProvider>()));
        services.AddSingleton(TestSecrets.Crypto);
        services.AddExternalProviders();
        services.AddSingleton<ConnectionPreparer>();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IExternalCredentialProvider>().Select(p => p.Kind).Should().BeEquivalentTo(
        [
            ExternalCredentialProvider.DelineaSecretServer, ExternalCredentialProvider.ClickstudiosPasswordState,
            ExternalCredentialProvider.OnePassword, ExternalCredentialProvider.VaultOpenbao,
        ]);
        provider.GetServices<IExternalAddressProvider>().Should().ContainSingle().Which.Kind.Should().Be(ExternalAddressProvider.AmazonWebServices);
        provider.GetServices<IConnectionPreparationStep>().Select(s => s.Order).Should().BeEquivalentTo([100, 200]);
        provider.GetRequiredService<IExternalProviderPrompt>().Should().BeOfType<NonInteractiveExternalProviderPrompt>();
        provider.GetRequiredService<ConnectionPreparer>().Should().NotBeNull();
    }
}
