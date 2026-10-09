using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// Delinea (formerly Thycotic) Secret Server REST API. The reference is the numeric secret ID.
/// Logs in with the OAuth2 password grant (optional OTP header) and keeps the access token, refreshing
/// it with the refresh token, or uses integrated Windows/Kerberos authentication (winauthwebservices).
/// </summary>
public sealed class DelineaSecretServerProvider(
    Func<AppSettings> settings,
    IProviderHttpClientFactory http,
    ProviderSecrets secrets) : IExternalCredentialProvider
{
    private const string Name = "Delinea Secret Server";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private TokenState? _token;

    public ExternalCredentialProvider Kind => ExternalCredentialProvider.DelineaSecretServer;

    public string DisplayName => Name;

    public async Task<ExternalCredential> GetAsync(ExternalCredentialRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = request.Reference?.Trim() ?? string.Empty;
        if (!int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var secretId) || secretId <= 0)
            throw new ExternalProviderException($"{Name}: the secret reference (UserViaAPI) must be a numeric secret ID, not \"{reference}\".");

        var s = settings();
        var api = ApiBase(s);
        using var client = http.Create(new ProviderHttpOptions(UseDefaultCredentials: s.DelineaUseSso));

        var secret = await SendAuthorizedAsync(s, client,
            () => new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Combine(api, $"v1/secrets/{secretId}?noAutoCheckout=true")),
            $"reading secret {secretId}", ct);
        var json = await ProviderHttp.ReadJsonAsync(secret, Name, $"reading secret {secretId}", ct);

        string? username = null, password = null, domain = null, privateKey = null, passphrase = null;
        if (json["items"] is not JsonArray items)
            throw new ExternalProviderException($"{Name}: secret {secretId} has no fields.");

        foreach (var item in items)
        {
            var field = (ProviderHttp.Text(item, "fieldName") ?? ProviderHttp.Text(item, "slug") ?? string.Empty).Trim().ToLowerInvariant();
            var value = ProviderHttp.Text(item, "itemValue");
            switch (field)
            {
                case "domain":
                    domain = value;
                    break;
                case "username":
                    username = value;
                    break;
                case "password":
                    password = value;
                    break;
                case "private key":
                    // File fields are not inlined: download the attachment.
                    var slug = ProviderHttp.Text(item, "slug") ?? "private-key";
                    privateKey = await ReadFieldAsync(s, client, api, secretId, slug, ct);
                    break;
                case "private key passphrase":
                    passphrase = value;
                    break;
            }
        }

        var credential = new ExternalCredential
        {
            Username = username,
            Password = password,
            Domain = domain,
            PrivateKey = privateKey,
            PrivateKeyPassphrase = passphrase,
        };
        if (!credential.HasSecret)
            throw new ExternalProviderException($"{Name}: secret {secretId} has neither a password nor a private key.");
        return credential;
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var s = settings();
        var api = ApiBase(s);
        using var client = http.Create(new ProviderHttpOptions(UseDefaultCredentials: s.DelineaUseSso));
        if (!s.DelineaUseSso)
        {
            await _tokenLock.WaitAsync(ct);
            try
            {
                _token = null; // always log in again so the stored credentials are really checked
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        var response = await SendAuthorizedAsync(s, client,
            () => new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Combine(api, "v1/users/current")), "reading the current user", ct);
        var user = await ProviderHttp.ReadJsonAsync(response, Name, "reading the current user", ct);
        var name = ProviderHttp.Text(user, "userName") ?? ProviderHttp.Text(user, "displayName") ?? "(unknown user)";
        return $"Connected to {ProviderHttp.RequireUrl(s.DelineaUrl, Name)} as {name}.";
    }

    private static string ApiBase(AppSettings s)
    {
        var url = ProviderHttp.RequireUrl(s.DelineaUrl, Name);
        // Integrated authentication needs IIS to expose the winauthwebservices application.
        return s.DelineaUseSso ? url + "/winauthwebservices/api" : url + "/api";
    }

    private async Task<string> ReadFieldAsync(AppSettings s, HttpClient client, string api, int secretId, string slug, CancellationToken ct)
    {
        var action = $"downloading the private key of secret {secretId}";
        var response = await SendAuthorizedAsync(s, client,
            () => new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Combine(api, $"v1/secrets/{secretId}/fields/{Uri.EscapeDataString(slug)}?noAutoCheckout=true")),
            action, ct);
        await ProviderHttp.EnsureSuccessAsync(response, Name, action, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Sends with the bearer token; on 401 logs in again once (the token may have been revoked).</summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        AppSettings s, HttpClient client, Func<HttpRequestMessage> create, string action, CancellationToken ct)
    {
        if (s.DelineaUseSso)
            return await ProviderHttp.SendAsync(client, create(), Name, ct);

        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(s, client, forceLogin: attempt > 0, ct);
            var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await ProviderHttp.SendAsync(client, request, Name, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                return response;
            response.Dispose();
        }
    }

    private async Task<string> GetTokenAsync(AppSettings s, HttpClient client, bool forceLogin, CancellationToken ct)
    {
        var url = ProviderHttp.RequireUrl(s.DelineaUrl, Name);
        if (string.IsNullOrWhiteSpace(s.DelineaUsername))
            throw new ExternalProviderException($"{Name}: the username is not configured (Options → External Providers).");
        var owner = $"{url}|{s.DelineaDomain}\\{s.DelineaUsername}";

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (forceLogin || _token is not null && _token.Owner != owner)
                _token = null;
            if (_token is { } current && current.ExpiresUtc > DateTime.UtcNow)
                return current.AccessToken;

            if (_token?.RefreshToken is { } refresh)
            {
                var (refreshed, _) = await RequestTokenAsync(client, url, owner,
                    [new("grant_type", "refresh_token"), new("refresh_token", refresh)], otp: null, ct);
                if (refreshed is not null)
                    return (_token = refreshed).AccessToken;
            }

            _token = null;
            var sessionKey = $"delinea|{owner}";
            var password = await secrets.GetAsync(s.DelineaPasswordProtected, sessionKey,
                new ExternalProviderPromptRequest(Name, $"Password for {s.DelineaUsername} on {url}", IsSecret: true, "Password"), ct);
            var otp = s.DelineaRequireOtp
                ? await secrets.AskAsync(new ExternalProviderPromptRequest(Name, $"One-time password for {s.DelineaUsername} on {url}", IsSecret: false, "OTP code"), ct)
                : null;

            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "password"),
                new("username", s.DelineaUsername),
                new("password", password),
            };
            if (!string.IsNullOrWhiteSpace(s.DelineaDomain))
                form.Add(new("domain", s.DelineaDomain));

            var (token, error) = await RequestTokenAsync(client, url, owner, form, otp, ct);
            if (token is null)
            {
                secrets.Forget(sessionKey); // ask again next time
                throw new ExternalProviderException(error ?? $"{Name}: login failed.");
            }
            return (_token = token).AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>POST /oauth2/token; a null token (with the reason) when the server rejected the grant.</summary>
    private async Task<(TokenState? Token, string? Error)> RequestTokenAsync(
        HttpClient client, string url, string owner, List<KeyValuePair<string, string>> form, string? otp, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.Combine(url, "oauth2/token"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (otp is not null)
            request.Headers.Add("OTP", otp);

        using var response = await ProviderHttp.SendAsync(client, request, Name, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (null, $"{Name}: login failed ({(int)response.StatusCode}){ProviderHttp.Detail(body)}");
        }
        if (!response.IsSuccessStatusCode)
            throw new ExternalProviderException($"{Name}: login failed ({(int)response.StatusCode} {response.ReasonPhrase}){ProviderHttp.Detail(body)}");

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ExternalProviderException($"{Name}: the token endpoint returned an unexpected response.", ex);
        }

        var accessToken = ProviderHttp.Text(json, "access_token")
            ?? throw new ExternalProviderException($"{Name}: the token response has no access_token.");
        var expiresIn = int.TryParse(ProviderHttp.Text(json, "expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 1200;
        // Renew a little early so a token never expires between the check and the request.
        var expires = DateTime.UtcNow.AddSeconds(Math.Max(0, expiresIn - 30));
        return (new TokenState(owner, accessToken, ProviderHttp.Text(json, "refresh_token"), expires), null);
    }

    private sealed record TokenState(string Owner, string AccessToken, string? RefreshToken, DateTime ExpiresUtc);
}
