using System.Net;
using FluentAssertions;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

public sealed class PasswordstateProviderTests
{
    // Shape of GET /api/passwords/{PasswordID} from the Passwordstate API documentation (all values are strings).
    private const string PasswordJson = """
        [
          {
            "PasswordListID": "3", "PasswordList": "Windows Servers", "PasswordID": "6", "Title": "app01 admin",
            "Domain": "CORP", "HostName": "app01", "UserName": "svc-app", "Description": "",
            "GenericField1": "", "GenericField2": "", "GenericField3": "", "GenericField4": "", "GenericField5": "",
            "GenericField6": "", "GenericField7": "", "GenericField8": "", "GenericField9": "", "GenericField10": "",
            "AccountTypeID": "0", "Notes": "", "URL": "", "Password": "Pa$$w0rd!", "ExpiryDate": "",
            "AllowExport": "True", "AccountType": ""
          }
        ]
        """;

    private const string KeyPasswordJson = """
        [
          {
            "PasswordListID": "4", "PasswordID": "9", "Title": "web01 deploy", "Domain": "", "UserName": "deploy",
            "GenericField1": "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----",
            "GenericField3": "phrase", "Password": ""
          }
        ]
        """;

    private const string ApiKeyErrorJson = """
        [{"errors":[{"message":"Invalid API key used, or the API key is not authorised for this request."},{"phrase":"Please check your API Key and try again."}]}]
        """;

    private readonly FakeHttpHandler _server = new();
    private readonly AppSettings _settings = new()
    {
        PasswordstateUrl = "https://passwordstate.example.test",
        PasswordstateApiKeyProtected = TestSecrets.Protect("e7c5a1d1-api-key"),
    };

    private PasswordstateProvider Create(IExternalProviderPrompt? prompt = null, FakeHttpClientFactory? http = null) =>
        new(() => _settings, http ?? new FakeHttpClientFactory(_server), TestSecrets.Create(prompt));

    [Fact]
    public async Task GetAsync_SendsTheApiKey_AndMapsTheFields()
    {
        _server.OnJson("GET /api/passwords/6", PasswordJson);

        var credential = await Create().GetAsync("6");

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "svc-app", Password = "Pa$$w0rd!", Domain = "CORP" });
        var request = _server.Requests.Single();
        request.Header("APIKey").Should().Be("e7c5a1d1-api-key");
        request.Header("OTP").Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReadsThePrivateKeyFromGenericField1_AndThePassphraseFromGenericField3()
    {
        _server.OnJson("GET /api/passwords/9", KeyPasswordJson);

        var credential = await Create().GetAsync(" 9 ");

        credential.Username.Should().Be("deploy");
        credential.Password.Should().BeNull();
        credential.PrivateKey.Should().StartWith("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA");
        credential.PrivateKeyPassphrase.Should().Be("phrase");
    }

    [Fact]
    public async Task GetAsync_AsksForTheApiKeyOnce_AndTheOtpEveryTime()
    {
        _settings.PasswordstateApiKeyProtected = string.Empty;
        _settings.PasswordstateRequireOtp = true;
        _server.OnJson("GET /api/passwords/6", PasswordJson);
        var prompt = new ScriptedPrompt("typed-key", "111111", "222222");
        var provider = Create(prompt);

        await provider.GetAsync("6");
        await provider.GetAsync("6");

        prompt.Asked.Select(a => a.Watermark).Should().Equal("API key", "OTP code", "OTP code");
        _server.Requests.Select(r => (r.Header("APIKey"), r.Header("OTP")))
            .Should().Equal(("typed-key", "111111"), ("typed-key", "222222"));
    }

    [Fact]
    public async Task GetAsync_WithSso_UsesWinApiWithoutApiKey()
    {
        _settings.PasswordstateUseSso = true;
        _server.OnJson("GET /winapi/passwords/6", PasswordJson);
        var http = new FakeHttpClientFactory(_server);

        await Create(http: http).GetAsync("6");

        http.Options.Should().ContainSingle().Which.UseDefaultCredentials.Should().BeTrue();
        _server.Requests.Single().Header("APIKey").Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReportsTheServerError_AndForgetsATypedKey()
    {
        _settings.PasswordstateApiKeyProtected = string.Empty;
        _server.OnJson("GET /api/passwords/6", ApiKeyErrorJson, HttpStatusCode.Forbidden);
        var prompt = new ScriptedPrompt("bad-key", "bad-key-2");
        var provider = Create(prompt);

        var act = () => provider.GetAsync("6");

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage(
            "Passwordstate: reading password 6 failed (403 Forbidden): Invalid API key used, or the API key is not authorised for this request.; Please check your API Key and try again.");
        await act.Should().ThrowAsync<ExternalProviderException>();
        prompt.Asked.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_EmptyResult_IsNotFound()
    {
        _server.OnJson("GET /api/passwords/6", "[]");

        var act = () => Create().GetAsync("6");

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Passwordstate: password 6 was not found.");
    }

    [Fact]
    public async Task GetAsync_CancelledApiKeyPrompt_StopsWithAClearMessage()
    {
        _settings.PasswordstateApiKeyProtected = string.Empty;

        var act = () => Create(new ScriptedPrompt(new string?[] { null })).GetAsync("6");

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Passwordstate: API key was not entered.");
        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TestAsync_ListsPasswordLists_LikeTheLegacyApp()
    {
        _server.OnJson("GET /api/passwordlists", """[{"PasswordListID":"3","PasswordList":"Windows Servers"},{"PasswordListID":"4","PasswordList":"Linux"}]""");

        var message = await Create().TestAsync();

        message.Should().Be("Connected to https://passwordstate.example.test: 2 password list(s) visible.");
    }
}
