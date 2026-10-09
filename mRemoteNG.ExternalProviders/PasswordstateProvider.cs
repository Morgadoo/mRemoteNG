using System.Globalization;
using System.Text.Json.Nodes;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// Clickstudios Passwordstate REST API. The reference is the numeric PasswordID.
/// Authenticates with an API key header (/api) or integrated Windows/Kerberos authentication (/winapi),
/// optionally with a one-time password. As in the legacy app, GenericField1 holds a private key and
/// GenericField3 its passphrase.
/// </summary>
public sealed class PasswordstateProvider(
    Func<AppSettings> settings,
    IProviderHttpClientFactory http,
    ProviderSecrets secrets) : IExternalCredentialProvider
{
    private const string Name = "Passwordstate";

    public ExternalCredentialProvider Kind => ExternalCredentialProvider.ClickstudiosPasswordState;

    public string DisplayName => Name;

    public async Task<ExternalCredential> GetAsync(ExternalCredentialRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = request.Reference?.Trim() ?? string.Empty;
        if (!int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var passwordId) || passwordId <= 0)
            throw new ExternalProviderException($"{Name}: the secret reference (UserViaAPI) must be a numeric password ID, not \"{reference}\".");

        var json = await GetJsonAsync($"passwords/{passwordId}", $"reading password {passwordId}", ct);
        var entry = json is JsonArray array ? array.FirstOrDefault() : json;
        if (entry is not JsonObject)
            throw new ExternalProviderException($"{Name}: password {passwordId} was not found.");

        var credential = new ExternalCredential
        {
            Username = ProviderHttp.Text(entry, "UserName"),
            Password = ProviderHttp.Text(entry, "Password"),
            Domain = ProviderHttp.Text(entry, "Domain"),
            PrivateKey = ProviderHttp.Text(entry, "GenericField1"),
            PrivateKeyPassphrase = ProviderHttp.Text(entry, "GenericField3"),
        };
        if (!credential.HasSecret)
            throw new ExternalProviderException($"{Name}: password {passwordId} has neither a password nor a private key.");
        return credential;
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        // Same check as the legacy app: list the password lists the key can see.
        var json = await GetJsonAsync("passwordlists", "listing password lists", ct);
        var count = json is JsonArray lists ? lists.Count : 1;
        return $"Connected to {ProviderHttp.RequireUrl(settings().PasswordstateUrl, Name)}: {count} password list(s) visible.";
    }

    private async Task<JsonNode> GetJsonAsync(string path, string action, CancellationToken ct)
    {
        var s = settings();
        var url = ProviderHttp.RequireUrl(s.PasswordstateUrl, Name);
        var apiKeySessionKey = $"passwordstate|{url}";

        using var client = http.Create(new ProviderHttpOptions(UseDefaultCredentials: s.PasswordstateUseSso));
        using var request = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Combine(url, (s.PasswordstateUseSso ? "winapi/" : "api/") + path));
        if (!s.PasswordstateUseSso)
        {
            var apiKey = await secrets.GetAsync(s.PasswordstateApiKeyProtected, apiKeySessionKey,
                new ExternalProviderPromptRequest(Name, $"API key for {url}", IsSecret: true, "API key"), ct);
            request.Headers.Add("APIKey", apiKey);
        }
        if (s.PasswordstateRequireOtp)
        {
            var otp = await secrets.AskAsync(new ExternalProviderPromptRequest(Name, $"One-time password for {url}", IsSecret: false, "OTP code"), ct);
            request.Headers.Add("OTP", otp);
        }

        using var response = await ProviderHttp.SendAsync(client, request, Name, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            secrets.Forget(apiKeySessionKey); // a typed key was wrong: ask again next time
        return await ProviderHttp.ReadJsonAsync(response, Name, action, ct);
    }
}
