using System.Net;
using FluentAssertions;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

public sealed class DelineaSecretServerProviderTests
{
    private const string Base = "https://cred.example.test/SecretServer";

    // Shapes as documented in the Secret Server REST API reference (TokenResponse, SecretModel, RestSecretItem).
    private const string TokenJson = """{"access_token":"AgJf5bG7tz-access","token_type":"bearer","expires_in":1199,"refresh_token":"AgKoDCX-refresh"}""";

    private const string WindowsSecretJson = """
        {
          "id": 42, "name": "dc01 admin", "secretTemplateId": 6003, "secretTemplateName": "Windows Account",
          "folderId": 12, "active": true, "checkedOut": false, "checkOutEnabled": false,
          "items": [
            {"itemId": 201, "fileAttachmentId": null, "filename": null, "itemValue": "dc01.corp.example", "fieldId": 87,
             "fieldName": "Machine", "slug": "machine", "fieldDescription": "The Server or Location of the Windows Machine.",
             "isFile": false, "isNotes": false, "isPassword": false, "isList": false, "listType": "None"},
            {"itemId": 202, "fileAttachmentId": null, "filename": null, "itemValue": "CORP", "fieldId": 88,
             "fieldName": "Domain", "slug": "domain", "isFile": false, "isNotes": false, "isPassword": false},
            {"itemId": 203, "fileAttachmentId": null, "filename": null, "itemValue": "Administrator", "fieldId": 89,
             "fieldName": "Username", "slug": "username", "isFile": false, "isNotes": false, "isPassword": false},
            {"itemId": 204, "fileAttachmentId": null, "filename": null, "itemValue": "S3cr3t!pw", "fieldId": 90,
             "fieldName": "Password", "slug": "password", "isFile": false, "isNotes": false, "isPassword": true},
            {"itemId": 205, "fileAttachmentId": null, "filename": null, "itemValue": "", "fieldId": 91,
             "fieldName": "Notes", "slug": "notes", "isFile": false, "isNotes": true, "isPassword": false}
          ]
        }
        """;

    private const string UnixKeySecretJson = """
        {
          "id": 77, "name": "web01 deploy", "secretTemplateId": 6007, "secretTemplateName": "Unix Account (SSH Key)",
          "items": [
            {"itemId": 301, "itemValue": "web01", "fieldName": "Machine", "slug": "machine", "isFile": false},
            {"itemId": 302, "itemValue": "deploy", "fieldName": "Username", "slug": "username", "isFile": false},
            {"itemId": 303, "itemValue": "", "fieldName": "Password", "slug": "password", "isFile": false, "isPassword": true},
            {"itemId": 304, "fileAttachmentId": 9001, "filename": "id_ed25519", "itemValue": "*** Not Valid For Display ***",
             "fieldName": "Private Key", "slug": "private-key", "isFile": true},
            {"itemId": 305, "itemValue": "key-pass", "fieldName": "Private Key Passphrase", "slug": "private-key-passphrase", "isPassword": true}
          ]
        }
        """;

    private const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

    private readonly FakeHttpHandler _server = new();
    private readonly AppSettings _settings = new()
    {
        DelineaUrl = Base + "/",
        DelineaUsername = "svc-mremote",
        DelineaPasswordProtected = TestSecrets.Protect("login-pw"),
    };

    private DelineaSecretServerProvider Create(IExternalProviderPrompt? prompt = null, FakeHttpClientFactory? http = null) =>
        new(() => _settings, http ?? new FakeHttpClientFactory(_server), TestSecrets.Create(prompt));

    [Fact]
    public async Task GetAsync_LogsInWithPasswordGrant_AndReturnsTheSecretFields()
    {
        _server.OnJson("POST /SecretServer/oauth2/token", TokenJson)
            .OnJson("GET /SecretServer/api/v1/secrets/42", WindowsSecretJson);

        var credential = await Create().GetAsync("42");

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "Administrator", Password = "S3cr3t!pw", Domain = "CORP" });
        var login = _server.Requests[0];
        login.Body.Should().Be("grant_type=password&username=svc-mremote&password=login-pw");
        login.Header("OTP").Should().BeNull();
        var read = _server.Requests[1];
        read.PathAndQuery.Should().Be("/SecretServer/api/v1/secrets/42?noAutoCheckout=true");
        read.Header("Authorization").Should().Be("Bearer AgJf5bG7tz-access");
    }

    [Fact]
    public async Task GetAsync_ReusesTheTokenForLaterSecrets()
    {
        _server.OnJson("POST /SecretServer/oauth2/token", TokenJson)
            .OnJson("GET /SecretServer/api/v1/secrets/42", WindowsSecretJson);
        var provider = Create();

        await provider.GetAsync("42");
        await provider.GetAsync("42");

        _server.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/oauth2/token")).Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_DownloadsThePrivateKeyFileField_WithItsPassphrase()
    {
        _server.OnJson("POST /SecretServer/oauth2/token", TokenJson)
            .OnJson("GET /SecretServer/api/v1/secrets/77", UnixKeySecretJson)
            .On("GET /SecretServer/api/v1/secrets/77/fields/private-key", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PrivateKey),
            });

        var credential = await Create().GetAsync("77");

        credential.Username.Should().Be("deploy");
        credential.Password.Should().BeNull();
        credential.PrivateKey.Should().Be(PrivateKey);
        credential.PrivateKeyPassphrase.Should().Be("key-pass");
        _server.Requests.Last().Header("Authorization").Should().Be("Bearer AgJf5bG7tz-access");
    }

    [Fact]
    public async Task GetAsync_AsksForAnUnsavedPasswordOnce_AndForTheOtpAtEveryLogin()
    {
        _settings.DelineaPasswordProtected = string.Empty;
        _settings.DelineaRequireOtp = true;
        _settings.DelineaDomain = "CORP";
        _server.OnJson("POST /SecretServer/oauth2/token", """{"access_token":"t","token_type":"bearer","expires_in":1}""")
            .OnJson("GET /SecretServer/api/v1/secrets/42", WindowsSecretJson);
        var prompt = new ScriptedPrompt("typed-pw", "123456", "654321");
        var provider = Create(prompt);

        await provider.GetAsync("42");
        await provider.GetAsync("42"); // token already expired (expires_in 1 s): logs in again

        prompt.Asked.Select(a => a.Watermark).Should().Equal("Password", "OTP code", "OTP code");
        prompt.Asked[0].IsSecret.Should().BeTrue();
        var logins = _server.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/oauth2/token")).ToList();
        logins.Select(l => l.Header("OTP")).Should().Equal("123456", "654321");
        logins[0].Body.Should().Be("grant_type=password&username=svc-mremote&password=typed-pw&domain=CORP");
    }

    [Fact]
    public async Task GetAsync_UsesTheRefreshTokenWhenTheAccessTokenExpired()
    {
        var logins = 0;
        _server.On("POST /SecretServer/oauth2/token", r =>
            {
                logins++;
                return FakeHttpHandler.Json(r.Body.StartsWith("grant_type=refresh_token")
                    ? """{"access_token":"refreshed","token_type":"bearer","expires_in":1199,"refresh_token":"r2"}"""
                    : """{"access_token":"first","token_type":"bearer","expires_in":5,"refresh_token":"r1"}""");
            })
            .OnJson("GET /SecretServer/api/v1/secrets/42", WindowsSecretJson);
        var provider = Create();

        await provider.GetAsync("42");
        await provider.GetAsync("42");

        logins.Should().Be(2);
        _server.Requests.Single(r => r.Body.StartsWith("grant_type=refresh_token")).Body
            .Should().Be("grant_type=refresh_token&refresh_token=r1");
        _server.Requests.Last().Header("Authorization").Should().Be("Bearer refreshed");
    }

    [Fact]
    public async Task GetAsync_LogsInAgainOnce_WhenTheTokenIsRejected()
    {
        var reads = 0;
        _server.OnJson("POST /SecretServer/oauth2/token", TokenJson)
            .On("GET /SecretServer/api/v1/secrets/42", _ => ++reads == 1
                ? FakeHttpHandler.Json("""{"errorCode":"API_AuthenticationFailed","message":"Authentication failed or expired token"}""", HttpStatusCode.Unauthorized)
                : FakeHttpHandler.Json(WindowsSecretJson));

        var credential = await Create().GetAsync("42");

        credential.Password.Should().Be("S3cr3t!pw");
        _server.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/oauth2/token")).Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_ReportsALoginFailure_AndAsksForATypedPasswordAgainNextTime()
    {
        _settings.DelineaPasswordProtected = string.Empty;
        _server.OnJson("POST /SecretServer/oauth2/token", """{"error":"Login failed."}""", HttpStatusCode.BadRequest);
        var prompt = new ScriptedPrompt("wrong", "wrong-again");
        var provider = Create(prompt);

        var first = () => provider.GetAsync("42");
        (await first.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Delinea Secret Server: login failed (400): Login failed.");
        await first.Should().ThrowAsync<ExternalProviderException>();

        prompt.Asked.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_ReportsServerErrorsForTheSecret()
    {
        _server.OnJson("POST /SecretServer/oauth2/token", TokenJson)
            .OnJson("GET /SecretServer/api/v1/secrets/43", """{"message":"Access Denied","messageDetail":"User does not have access to secret 43"}""", HttpStatusCode.Forbidden);

        var act = () => Create().GetAsync("43");

        (await act.Should().ThrowAsync<ExternalProviderException>())
            .WithMessage("Delinea Secret Server: reading secret 43 failed (403 Forbidden): Access Denied; User does not have access to secret 43");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    public async Task GetAsync_RejectsNonNumericReferences(string reference)
    {
        var act = () => Create().GetAsync(reference);

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("*numeric secret ID*");
        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WithoutUrl_PointsToTheOptions()
    {
        _settings.DelineaUrl = string.Empty;

        var act = () => Create().GetAsync("42");

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("*URL is not configured*External Providers*");
    }

    [Fact]
    public async Task GetAsync_WithSso_UsesIntegratedAuthenticationAndNoToken()
    {
        _settings.DelineaUseSso = true;
        _server.OnJson("GET /SecretServer/winauthwebservices/api/v1/secrets/42", WindowsSecretJson);
        var http = new FakeHttpClientFactory(_server);

        var credential = await Create(http: http).GetAsync("42");

        credential.Username.Should().Be("Administrator");
        http.Options.Should().OnlyContain(o => o.UseDefaultCredentials);
        _server.Requests.Should().ContainSingle().Which.Header("Authorization").Should().BeNull();
    }

    [Fact]
    public async Task TestAsync_LogsInAndNamesTheUser()
    {
        _server.OnJson("POST /SecretServer/oauth2/token", TokenJson)
            .OnJson("GET /SecretServer/api/v1/users/current", """{"id":5,"userName":"svc-mremote","displayName":"mRemoteNG service","enabled":true}""");

        var message = await Create().TestAsync();

        message.Should().Be($"Connected to {Base} as svc-mremote.");
    }
}
