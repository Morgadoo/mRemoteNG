using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

public sealed class VaultOpenbaoProviderTests
{
    // Responses recorded from an OpenBao 2.4 dev server (request IDs shortened).
    private const string KvV2MountJson = """
        {"request_id":"fcbd684d","lease_id":"","renewable":false,"lease_duration":0,"data":{"accessor":"kv_e6ffe4f7",
         "config":{"default_lease_ttl":0,"force_no_cache":false,"max_lease_ttl":0},"description":"key/value secret storage",
         "local":false,"options":{"version":"2"},"path":"secret/","plugin_version":"","running_plugin_version":"v2.4.1+builtin.bao",
         "seal_wrap":false,"type":"kv","uuid":"d3911835"},"wrap_info":null,"warnings":null,"auth":null}
        """;

    private const string KvV1MountJson = """
        {"request_id":"1e218f0a","data":{"accessor":"kv_f80ead78","options":{"version":"1"},"path":"kv1/","type":"kv"},"auth":null}
        """;

    private const string KvV2SecretJson = """
        {"request_id":"9a62faab","lease_id":"","renewable":false,"lease_duration":0,
         "data":{"data":{"root":"hunter2","admin":"other"},"metadata":{"created_time":"2026-10-09T11:25:42Z","custom_metadata":null,
         "deletion_time":"","destroyed":false,"version":1}},"wrap_info":null,"warnings":null,"auth":null}
        """;

    private const string SshOtpJson = """
        {"request_id":"ddf6c0e7","lease_id":"ssh/creds/otp/MlMVetfVfh2XSsqKR7Te9J5x","renewable":false,"lease_duration":2764800,
         "data":{"ip":"10.1.2.3","key":"615cc15b-7c0c-446d-96fe-cd9489176a6e","key_type":"otp","port":22,"username":"ubuntu"},
         "wrap_info":null,"warnings":null,"auth":null}
        """;

    private const string LdapCredsJson = """
        {"request_id":"2b1c","lease_id":"ldap/creds/dyn/abc","renewable":true,"lease_duration":3600,
         "data":{"distinguished_names":["cn=v_token_dyn_x1,ou=users,dc=example,dc=org"],"password":"Dyn-Pw-1","username":"v_token_dyn_x1"}}
        """;

    private const string LdapStaticJson = """
        {"request_id":"3c2d","lease_id":"","renewable":false,"lease_duration":0,
         "data":{"dn":"uid=svc,ou=users,dc=example,dc=org","last_password":"","last_vault_rotation":"2026-10-09T11:40:00Z",
                 "password":"Static-Pw-1","rotation_period":86400,"ttl":86399,"username":"svc"}}
        """;

    private const string LoginJson = """
        {"request_id":"7e1f","lease_id":"","renewable":false,"lease_duration":0,"data":null,"wrap_info":null,"warnings":null,
         "auth":{"client_token":"s.login-token","accessor":"acc","policies":["default","mremote"],"token_policies":["default","mremote"],
                 "metadata":{"username":"alice"},"lease_duration":2764800,"renewable":true,"entity_id":"e","token_type":"service","orphan":true}}
        """;

    private readonly FakeHttpHandler _server = new();
    private readonly AppSettings _settings = new()
    {
        VaultUrl = "https://vault.example.test:8200",
        VaultSecretProtected = TestSecrets.Protect("s.root-token"),
    };

    private VaultOpenbaoProvider Create(IExternalProviderPrompt? prompt = null, FakeHttpClientFactory? http = null) =>
        new(() => _settings, http ?? new FakeHttpClientFactory(_server), TestSecrets.Create(prompt));

    private static ExternalCredentialRequest Request(VaultOpenbaoSecretEngine engine, string mount, string role, string? username = null, string? host = null) =>
        new() { VaultEngine = engine, VaultMount = mount, VaultRole = role, Username = username, Hostname = host };

    [Fact]
    public async Task Kv2_ReturnsTheValueStoredUnderTheConnectionsUsername_LikeTheLegacyApp()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/secret", KvV2MountJson)
            .OnJson("GET /v1/secret/data/ssh/web01", KvV2SecretJson);

        var credential = await Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "ssh/web01", "root"));

        credential.Should().BeEquivalentTo(new ExternalCredential { Password = "hunter2" });
        _server.Requests.Should().OnlyContain(r => r.Header("X-Vault-Token") == "s.root-token");
    }

    [Fact]
    public async Task Kv1_ReadsTheSecretWithoutTheDataPrefix_AndUsesConventionalKeys()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/kv1", KvV1MountJson)
            .OnJson("GET /v1/kv1/servers/db01", """{"request_id":"r","data":{"username":"dba","password":"kv1-pw","domain":"CORP"},"auth":null}""");

        var credential = await Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "/kv1/", "servers/db01"));

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "dba", Password = "kv1-pw", Domain = "CORP" });
    }

    [Fact]
    public async Task Kv_WithoutMountAccess_TriesV2ThenV1()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/kv1", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
            .OnJson("GET /v1/auth/token/lookup-self", """{"data":{"display_name":"token","policies":["kv1-reader"]}}""")
            .OnJson("GET /v1/kv1/web01", """{"data":{"root":"from-v1"}}""");

        var credential = await Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "kv1", "web01", "root"));

        credential.Password.Should().Be("from-v1");
        _server.Requests.Select(r => r.Uri.AbsolutePath).Should().Equal(
            "/v1/sys/internal/ui/mounts/kv1", "/v1/auth/token/lookup-self", "/v1/kv1/data/web01", "/v1/kv1/web01");
    }

    [Fact]
    public async Task Kv_ReturnsAPrivateKey()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/secret", KvV2MountJson)
            .OnJson("GET /v1/secret/data/keys/deploy",
                """{"data":{"data":{"username":"deploy","private_key":"-----BEGIN OPENSSH PRIVATE KEY-----\nx\n-----END OPENSSH PRIVATE KEY-----","private_key_passphrase":"pp"}}}""");

        var credential = await Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "keys/deploy"));

        credential.Username.Should().Be("deploy");
        credential.PrivateKey.Should().Contain("OPENSSH PRIVATE KEY");
        credential.PrivateKeyPassphrase.Should().Be("pp");
    }

    [Fact]
    public async Task Kv_WithoutMatchingKey_ExplainsWhatIsExpected()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/secret", KvV2MountJson)
            .OnJson("GET /v1/secret/data/ssh/web01", KvV2SecretJson);

        var act = () => Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "ssh/web01", "nobody"));

        (await act.Should().ThrowAsync<ExternalProviderException>())
            .WithMessage("Vault/OpenBao: secret/ssh/web01 has no key named \"nobody\" and no \"password\" or \"private_key\" key.");
    }

    [Fact]
    public async Task MountOfAnotherType_IsRejected_LikeTheLegacyMountCheck()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/ssh", """{"data":{"type":"ssh","options":null,"path":"ssh/"}}""");

        var act = () => Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "ssh", "x", "root"));

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("*mount \"ssh\" is a ssh secrets engine*Kv*");
    }

    [Fact]
    public async Task LdapDynamic_ReturnsTheGeneratedAccount()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/ldap", """{"data":{"type":"ldap","path":"ldap/"}}""")
            .OnJson("GET /v1/ldap/creds/dyn", LdapCredsJson);

        var credential = await Create().GetAsync(Request(VaultOpenbaoSecretEngine.LdapDynamic, "ldap", "dyn", "ignored"));

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "v_token_dyn_x1", Password = "Dyn-Pw-1" });
    }

    [Fact]
    public async Task LdapStatic_ReturnsTheManagedAccount()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/ldap", """{"data":{"type":"openldap","path":"ldap/"}}""")
            .OnJson("GET /v1/ldap/static-cred/svc", LdapStaticJson);

        var credential = await Create().GetAsync(Request(VaultOpenbaoSecretEngine.LdapStatic, "ldap", "svc"));

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "svc", Password = "Static-Pw-1" });
    }

    [Fact]
    public async Task SshOtp_IsIssuedForTheResolvedIPv4Address()
    {
        _server.OnJson("GET /v1/sys/internal/ui/mounts/ssh", """{"data":{"type":"ssh","path":"ssh/"}}""")
            .OnJson("POST /v1/ssh/creds/otp", SshOtpJson);
        var provider = Create();
        provider.ResolveHost = (host, _) => Task.FromResult(host == "web01.example"
            ? new[] { IPAddress.Parse("2001:db8::5"), IPAddress.Parse("10.1.2.3") }
            : Array.Empty<IPAddress>());

        var credential = await provider.GetAsync(Request(VaultOpenbaoSecretEngine.SSHOTP, "ssh", "otp", "ubuntu", "web01.example"));

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "ubuntu", Password = "615cc15b-7c0c-446d-96fe-cd9489176a6e" });
        var body = JsonNode.Parse(_server.Requests.Last().Body)!;
        body["ip"]!.GetValue<string>().Should().Be("10.1.2.3");
        body["username"]!.GetValue<string>().Should().Be("ubuntu");
    }

    [Fact]
    public async Task UserPassLogin_IsUsedForEveryRequest_AndCached()
    {
        _settings.VaultAuthMethod = VaultAuthMethod.UserPass;
        _settings.VaultUsername = "alice";
        _settings.VaultSecretProtected = TestSecrets.Protect("alice-pw");
        _settings.VaultNamespace = "team-a";
        _server.OnJson("POST /v1/auth/userpass/login/alice", LoginJson)
            .OnJson("GET /v1/sys/internal/ui/mounts/secret", KvV2MountJson)
            .OnJson("GET /v1/secret/data/ssh/web01", KvV2SecretJson);
        var provider = Create();

        await provider.GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "ssh/web01", "root"));
        await provider.GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "ssh/web01", "root"));

        var login = _server.Requests.Single(r => r.Uri.AbsolutePath == "/v1/auth/userpass/login/alice");
        JsonNode.Parse(login.Body)!["password"]!.GetValue<string>().Should().Be("alice-pw");
        _server.Requests.Should().OnlyContain(r => r.Header("X-Vault-Namespace") == "team-a");
        _server.Requests.Where(r => r != login).Should().OnlyContain(r => r.Header("X-Vault-Token") == "s.login-token");
    }

    [Fact]
    public async Task AppRoleLogin_SendsRoleIdAndSecretId_ToTheConfiguredMount()
    {
        _settings.VaultAuthMethod = VaultAuthMethod.AppRole;
        _settings.VaultAuthMount = "approle-mremote";
        _settings.VaultUsername = "5f1c-role-id";
        _settings.VaultSecretProtected = string.Empty;
        _server.OnJson("POST /v1/auth/approle-mremote/login", LoginJson)
            .OnJson("GET /v1/auth/token/lookup-self", """{"data":{"display_name":"approle","policies":["default","mremote"]}}""");
        var prompt = new ScriptedPrompt("typed-secret-id");

        var message = await Create(prompt).TestAsync();

        message.Should().Be("Connected to https://vault.example.test:8200 as approle (policies: default, mremote).");
        prompt.Asked.Single().Watermark.Should().Be("Secret ID");
        var body = JsonNode.Parse(_server.Requests[0].Body)!;
        body["role_id"]!.GetValue<string>().Should().Be("5f1c-role-id");
        body["secret_id"]!.GetValue<string>().Should().Be("typed-secret-id");
    }

    [Fact]
    public async Task ExpiredLoginToken_LogsInAgainOnce()
    {
        _settings.VaultAuthMethod = VaultAuthMethod.Ldap;
        _settings.VaultUsername = "bob";
        var logins = 0;
        _server.On("POST /v1/auth/ldap/login/bob", _ => FakeHttpHandler.Json(LoginJson.Replace("s.login-token", $"s.token-{++logins}")))
            .On("GET /v1/sys/internal/ui/mounts/secret", r => r.Header("X-Vault-Token") == "s.token-1"
                ? FakeHttpHandler.Json("""{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
                : FakeHttpHandler.Json(KvV2MountJson))
            .On("GET /v1/auth/token/lookup-self", r => r.Header("X-Vault-Token") == "s.token-1"
                ? FakeHttpHandler.Json("""{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
                : FakeHttpHandler.Json("""{"data":{}}"""))
            .OnJson("GET /v1/secret/data/ssh/web01", KvV2SecretJson);

        var credential = await Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "ssh/web01", "root"));

        credential.Password.Should().Be("hunter2");
        logins.Should().Be(2);
    }

    [Fact]
    public async Task InvalidTypedToken_IsReported_AndAskedForAgain()
    {
        _settings.VaultSecretProtected = string.Empty;
        _server.OnJson("GET /v1/sys/internal/ui/mounts/secret", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
            .OnJson("GET /v1/auth/token/lookup-self", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
            .OnJson("GET /v1/secret/data/x", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden);
        var prompt = new ScriptedPrompt("bad-1", "bad-2", "bad-3");
        var provider = Create(prompt);

        var act = () => provider.GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "x", "root"));

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Vault/OpenBao: reading secret/x failed (403 Forbidden): permission denied");
        prompt.Asked.Count.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData("", "role")]
    [InlineData("secret", "")]
    public async Task MissingMountOrRole_IsReported(string mount, string role)
    {
        var act = () => Create().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, mount, role, "root"));

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Vault/OpenBao: the connection has no Vault*");
    }

    [Fact]
    public async Task CaCertificatePath_IsPassedToTheHttpClient()
    {
        _settings.VaultCaCertificatePath = "/etc/ssl/private-ca.pem";
        _server.OnJson("GET /v1/auth/token/lookup-self", """{"data":{"display_name":"root","policies":["root"]}}""");
        var http = new FakeHttpClientFactory(_server);

        await Create(http: http).TestAsync();

        http.Options.Should().ContainSingle().Which.CaCertificatePath.Should().Be("/etc/ssl/private-ca.pem");
    }
}
