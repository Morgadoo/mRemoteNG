using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// HashiCorp Vault / OpenBao HTTP API. What is read depends on the connection's secret engine:
/// <list type="bullet">
/// <item>KV (v1 or v2, detected from the mount): the secret at path "role"; as in the legacy app the
/// password is the value stored under the connection's username. Without such a key the conventional
/// keys username / password / domain / private_key / private_key_passphrase are used;</item>
/// <item>LDAP dynamic / static: <c>{mount}/creds/{role}</c> / <c>{mount}/static-cred/{role}</c>;</item>
/// <item>SSH OTP: <c>{mount}/creds/{role}</c> issues a one-time password for the host's IP, which the
/// SSH session sends as the password (verified on the server by vault-ssh-helper).</item>
/// </list>
/// Logs in with a token (legacy), userpass, LDAP or AppRole.
/// </summary>
public sealed class VaultOpenbaoProvider(
    Func<AppSettings> settings,
    IProviderHttpClientFactory http,
    ProviderSecrets secrets) : IExternalCredentialProvider
{
    private const string Name = "Vault/OpenBao";
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private LoginState? _login;

    public ExternalCredentialProvider Kind => ExternalCredentialProvider.VaultOpenbao;

    public string DisplayName => Name;

    /// <summary>Resolves host names for SSH OTP (replaced in tests).</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> ResolveHost { get; set; } = Dns.GetHostAddressesAsync;

    public async Task<ExternalCredential> GetAsync(ExternalCredentialRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mount = request.VaultMount?.Trim().Trim('/') ?? string.Empty;
        var role = request.VaultRole?.Trim().Trim('/') ?? string.Empty;
        if (mount.Length == 0)
            throw new ExternalProviderException($"{Name}: the connection has no Vault mount (VaultOpenbaoMount).");
        if (role.Length == 0)
            throw new ExternalProviderException($"{Name}: the connection has no Vault role / secret path (VaultOpenbaoRole).");

        var s = settings();
        using var client = CreateClient(s);
        var mountInfo = await GetMountInfoAsync(s, client, mount, ct);
        CheckMountType(mount, mountInfo?.Type, request.VaultEngine);

        return request.VaultEngine switch
        {
            VaultOpenbaoSecretEngine.Kv => await ReadKvAsync(s, client, mount, role, mountInfo?.KvVersion, request.Username, ct),
            VaultOpenbaoSecretEngine.LdapDynamic => await ReadLdapAsync(s, client, $"{mount}/creds/{role}", "dynamic LDAP credentials", ct),
            VaultOpenbaoSecretEngine.LdapStatic => await ReadLdapAsync(s, client, $"{mount}/static-cred/{role}", "static LDAP credentials", ct),
            VaultOpenbaoSecretEngine.SSHOTP => await IssueSshOtpAsync(s, client, mount, role, request, ct),
            _ => throw new ExternalProviderException($"{Name}: secret engine {request.VaultEngine} is not supported."),
        };
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var s = settings();
        using var client = CreateClient(s);
        await _loginLock.WaitAsync(ct);
        try
        {
            _login = null; // log in again so the configured credentials are really checked
        }
        finally
        {
            _loginLock.Release();
        }

        var json = await SendJsonAsync(s, client, HttpMethod.Get, "auth/token/lookup-self", null, "looking up the token", ct);
        var data = json["data"];
        var who = ProviderHttp.Text(data, "display_name") ?? "token";
        var policies = (data?["policies"] as JsonArray)?.Select(p => p?.GetValue<string>()).Where(p => p is not null) ?? [];
        return $"Connected to {ProviderHttp.RequireUrl(s.VaultUrl, Name)} as {who} (policies: {string.Join(", ", policies)}).";
    }

    private HttpClient CreateClient(AppSettings s) =>
        http.Create(new ProviderHttpOptions(CaCertificatePath: string.IsNullOrWhiteSpace(s.VaultCaCertificatePath) ? null : s.VaultCaCertificatePath));

    // ── Secret engines ─────────────────────────────────────────────────────

    private async Task<ExternalCredential> ReadKvAsync(
        AppSettings s, HttpClient client, string mount, string path, int? version, string? username, CancellationToken ct)
    {
        JsonNode? data;
        if (version == 1)
        {
            data = (await SendJsonAsync(s, client, HttpMethod.Get, $"{mount}/{path}", null, $"reading {mount}/{path}", ct))["data"];
        }
        else if (version == 2)
        {
            data = (await SendJsonAsync(s, client, HttpMethod.Get, $"{mount}/data/{path}", null, $"reading {mount}/{path}", ct))["data"]?["data"];
        }
        else
        {
            // Mount options not readable with this token: try KV v2, then v1.
            var v2 = await SendJsonOrNotFoundAsync(s, client, $"{mount}/data/{path}", $"reading {mount}/{path}", ct);
            data = v2 is not null
                ? v2["data"]?["data"]
                : (await SendJsonAsync(s, client, HttpMethod.Get, $"{mount}/{path}", null, $"reading {mount}/{path}", ct))["data"];
        }

        if (data is not JsonObject values)
            throw new ExternalProviderException($"{Name}: {mount}/{path} has no data (deleted secret version?).");

        // Legacy layout: { "<username>": "<password>" } with the username taken from the connection.
        if (!string.IsNullOrEmpty(username) && ProviderHttp.Text(values, username) is { } byUser)
            return new ExternalCredential { Password = byUser };

        var credential = new ExternalCredential
        {
            Username = ProviderHttp.Text(values, "username"),
            Password = ProviderHttp.Text(values, "password"),
            Domain = ProviderHttp.Text(values, "domain"),
            PrivateKey = ProviderHttp.Text(values, "private_key"),
            PrivateKeyPassphrase = ProviderHttp.Text(values, "private_key_passphrase"),
        };
        if (!credential.HasSecret)
        {
            var hint = string.IsNullOrEmpty(username) ? string.Empty : $"no key named \"{username}\" and ";
            throw new ExternalProviderException($"{Name}: {mount}/{path} has {hint}no \"password\" or \"private_key\" key.");
        }
        return credential;
    }

    private async Task<ExternalCredential> ReadLdapAsync(AppSettings s, HttpClient client, string path, string what, CancellationToken ct)
    {
        var data = (await SendJsonAsync(s, client, HttpMethod.Get, path, null, $"reading {what} ({path})", ct))["data"];
        var password = ProviderHttp.Text(data, "password")
            ?? throw new ExternalProviderException($"{Name}: {path} returned no password.");
        return new ExternalCredential { Username = ProviderHttp.Text(data, "username"), Password = password };
    }

    private async Task<ExternalCredential> IssueSshOtpAsync(
        AppSettings s, HttpClient client, string mount, string role, ExternalCredentialRequest request, CancellationToken ct)
    {
        var host = request.Hostname?.Trim();
        if (string.IsNullOrEmpty(host))
            throw new ExternalProviderException($"{Name}: an SSH one-time password needs the connection's hostname.");
        var ip = await ResolveIpAsync(host, ct);

        var body = new JsonObject { ["ip"] = ip };
        if (!string.IsNullOrEmpty(request.Username))
            body["username"] = request.Username;
        var data = (await SendJsonAsync(s, client, HttpMethod.Post, $"{mount}/creds/{role}", body, $"issuing an SSH one-time password for {ip}", ct))["data"];
        var otp = ProviderHttp.Text(data, "key")
            ?? throw new ExternalProviderException($"{Name}: {mount}/creds/{role} returned no one-time password.");
        return new ExternalCredential { Username = ProviderHttp.Text(data, "username"), Password = otp };
    }

    private async Task<string> ResolveIpAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal.ToString();
        IPAddress[] addresses;
        try
        {
            addresses = await ResolveHost(host, ct);
        }
        catch (SocketException ex)
        {
            throw new ExternalProviderException($"{Name}: could not resolve \"{host}\" for the SSH one-time password: {ex.Message}", ex);
        }
        // Prefer IPv4 like the legacy app: OTP roles are usually written with IPv4 CIDR lists.
        var selected = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new ExternalProviderException($"{Name}: \"{host}\" has no IP address.");
        return selected.ToString();
    }

    // ── Mounts ─────────────────────────────────────────────────────────────

    private sealed record MountInfo(string? Type, int? KvVersion);

    /// <summary>
    /// Mount type and KV version from sys/internal/ui/mounts (readable by any token with access to the
    /// mount — the endpoint the vault/bao CLI uses); null when the token may not read it.
    /// </summary>
    private async Task<MountInfo?> GetMountInfoAsync(AppSettings s, HttpClient client, string mount, CancellationToken ct)
    {
        using var response = await SendAsync(s, client, HttpMethod.Get, $"sys/internal/ui/mounts/{mount}", null, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;
        var json = await ProviderHttp.ReadJsonAsync(response, Name, $"reading mount {mount}", ct);
        var data = json["data"];
        var type = ProviderHttp.Text(data, "type");
        // KV mounts without a version option are version 1.
        int? kvVersion = int.TryParse(ProviderHttp.Text(data?["options"], "version"), out var v) ? v
            : type is "kv" or "generic" ? 1 : null;
        return new MountInfo(type, kvVersion);
    }

    private static void CheckMountType(string mount, string? type, VaultOpenbaoSecretEngine engine)
    {
        if (type is null)
            return;
        var ok = engine switch
        {
            VaultOpenbaoSecretEngine.Kv => type is "kv" or "generic",
            VaultOpenbaoSecretEngine.LdapDynamic or VaultOpenbaoSecretEngine.LdapStatic => type is "ldap" or "openldap" or "ad",
            VaultOpenbaoSecretEngine.SSHOTP => type is "ssh",
            _ => true,
        };
        if (!ok)
            throw new ExternalProviderException($"{Name}: mount \"{mount}\" is a {type} secrets engine, which does not match the connection's engine ({engine}).");
    }

    // ── HTTP and login ─────────────────────────────────────────────────────

    private async Task<JsonNode> SendJsonAsync(
        AppSettings s, HttpClient client, HttpMethod method, string path, JsonObject? body, string action, CancellationToken ct)
    {
        using var response = await SendAsync(s, client, method, path, body, ct);
        return await ProviderHttp.ReadJsonAsync(response, Name, action, ct);
    }

    private async Task<JsonNode?> SendJsonOrNotFoundAsync(AppSettings s, HttpClient client, string path, string action, CancellationToken ct)
    {
        using var response = await SendAsync(s, client, HttpMethod.Get, path, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ProviderHttp.ReadJsonAsync(response, Name, action, ct);
    }

    /// <summary>Sends with the client token; a 403 with a token from a login method triggers one fresh login (expired token).</summary>
    private async Task<HttpResponseMessage> SendAsync(
        AppSettings s, HttpClient client, HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        var url = ProviderHttp.RequireUrl(s.VaultUrl, Name);
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(s, client, url, forceLogin: attempt > 0, ct);
            var request = NewRequest(s, method, ProviderHttp.Combine(url, "v1/" + path), body);
            request.Headers.Add("X-Vault-Token", token);
            var response = await ProviderHttp.SendAsync(client, request, Name, ct);
            if (response.StatusCode != HttpStatusCode.Forbidden || attempt > 0
                || !await IsTokenInvalidAsync(s, client, url, token, ct))
                return response;
            if (s.VaultAuthMethod == VaultAuthMethod.Token)
            {
                // A typed token was wrong or has expired: ask again next time.
                secrets.Forget(SessionKey(s, url));
                return response;
            }
            response.Dispose(); // the login token expired: log in again and retry once
        }
    }

    /// <summary>True when the token itself is not valid (as opposed to a policy denial).</summary>
    private async Task<bool> IsTokenInvalidAsync(AppSettings s, HttpClient client, string url, string token, CancellationToken ct)
    {
        using var request = NewRequest(s, HttpMethod.Get, ProviderHttp.Combine(url, "v1/auth/token/lookup-self"), null);
        request.Headers.Add("X-Vault-Token", token);
        using var response = await ProviderHttp.SendAsync(client, request, Name, ct);
        return !response.IsSuccessStatusCode;
    }

    private static HttpRequestMessage NewRequest(AppSettings s, HttpMethod method, string url, JsonObject? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(s.VaultNamespace))
            request.Headers.Add("X-Vault-Namespace", s.VaultNamespace.Trim());
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private static string AuthMount(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.VaultAuthMount) ? DefaultAuthMount(s.VaultAuthMethod) : s.VaultAuthMount.Trim().Trim('/');

    private static string Owner(AppSettings s, string url) =>
        $"{url}|{s.VaultNamespace}|{s.VaultAuthMethod}|{AuthMount(s)}|{s.VaultUsername?.Trim()}";

    private static string SessionKey(AppSettings s, string url) => "vault|" + Owner(s, url);

    private async Task<string> GetTokenAsync(AppSettings s, HttpClient client, string url, bool forceLogin, CancellationToken ct)
    {
        var method = s.VaultAuthMethod;
        var authMount = AuthMount(s);
        var username = s.VaultUsername?.Trim() ?? string.Empty;
        var owner = Owner(s, url);
        var sessionKey = SessionKey(s, url);

        if (method == VaultAuthMethod.Token)
        {
            return await secrets.GetAsync(s.VaultSecretProtected, sessionKey,
                new ExternalProviderPromptRequest(Name, $"Access token for {url}", IsSecret: true, "Token"), ct);
        }

        if (username.Length == 0)
            throw new ExternalProviderException($"{Name}: the {(method == VaultAuthMethod.AppRole ? "role ID" : "username")} is not configured (Options → External Providers).");

        await _loginLock.WaitAsync(ct);
        try
        {
            if (forceLogin || _login is not null && _login.Owner != owner)
                _login = null;
            if (_login is { } current && current.ExpiresUtc > DateTime.UtcNow)
                return current.Token;

            var (secretLabel, loginPath, body) = method switch
            {
                VaultAuthMethod.AppRole => ("Secret ID", $"auth/{authMount}/login", (Func<string, JsonObject>)(secret => new JsonObject { ["role_id"] = username, ["secret_id"] = secret })),
                _ => ("Password", $"auth/{authMount}/login/{Uri.EscapeDataString(username)}", secret => new JsonObject { ["password"] = secret }),
            };
            var secret = await secrets.GetAsync(s.VaultSecretProtected, sessionKey,
                new ExternalProviderPromptRequest(Name, $"{secretLabel} for {username} ({authMount}) on {url}", IsSecret: true, secretLabel), ct);

            using var request = NewRequest(s, HttpMethod.Post, ProviderHttp.Combine(url, "v1/" + loginPath), body(secret));
            using var response = await ProviderHttp.SendAsync(client, request, Name, ct);
            if (!response.IsSuccessStatusCode)
                secrets.Forget(sessionKey);
            var json = await ProviderHttp.ReadJsonAsync(response, Name, $"login ({method}, {authMount})", ct);
            var auth = json["auth"];
            var token = ProviderHttp.Text(auth, "client_token")
                ?? throw new ExternalProviderException($"{Name}: the login response has no client token.");
            var lease = int.TryParse(ProviderHttp.Text(auth, "lease_duration"), out var seconds) && seconds > 0 ? seconds : 300;
            _login = new LoginState(owner, token, DateTime.UtcNow.AddSeconds(Math.Max(5, lease - 30)));
            return token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private static string DefaultAuthMount(VaultAuthMethod method) => method switch
    {
        VaultAuthMethod.UserPass => "userpass",
        VaultAuthMethod.Ldap => "ldap",
        VaultAuthMethod.AppRole => "approle",
        _ => "token",
    };

    private sealed record LoginState(string Owner, string Token, DateTime ExpiresUtc);
}
