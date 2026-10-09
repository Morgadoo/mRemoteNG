using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace mRemoteNG.ExternalProviders;

/// <param name="UseDefaultCredentials">Authenticate with the logged-on user (Windows integrated / Kerberos).</param>
/// <param name="CaCertificatePath">Optional PEM file with additional trusted root certificates.</param>
public sealed record ProviderHttpOptions(bool UseDefaultCredentials = false, string? CaCertificatePath = null);

/// <summary>Creates the HTTP clients the REST providers use (replaced by a fake in tests).</summary>
public interface IProviderHttpClientFactory
{
    HttpClient Create(ProviderHttpOptions options);
}

public sealed class DefaultProviderHttpClientFactory : IProviderHttpClientFactory
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(30);

    public HttpClient Create(ProviderHttpOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (options.UseDefaultCredentials)
            handler.Credentials = CredentialCache.DefaultCredentials;
        if (!string.IsNullOrWhiteSpace(options.CaCertificatePath))
            handler.SslOptions.RemoteCertificateValidationCallback = TrustAlso(LoadCertificates(options.CaCertificatePath));

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mRemoteNG", null));
        return client;
    }

    private static X509Certificate2Collection LoadCertificates(string path)
    {
        try
        {
            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPemFile(path);
            if (certificates.Count == 0)
                throw new ExternalProviderException($"The CA certificate file \"{path}\" contains no PEM certificate.");
            return certificates;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new ExternalProviderException($"The CA certificate file \"{path}\" could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>Accepts the system-trusted chain, or a chain ending in one of <paramref name="roots"/>.</summary>
    private static RemoteCertificateValidationCallback TrustAlso(X509Certificate2Collection roots) =>
        (_, certificate, _, errors) =>
        {
            if (errors == SslPolicyErrors.None)
                return true;
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                return false; // missing certificate or name mismatch: never acceptable

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate as X509Certificate2 ?? new X509Certificate2(certificate));
        };
}

internal static class ProviderHttp
{
    /// <summary>Joins a configured base URL and a relative path.</summary>
    public static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>Returns the configured URL, or throws a message pointing at the Options window.</summary>
    public static string RequireUrl(string? url, string provider)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ExternalProviderException($"{provider}: the server URL is not configured (Options → External Providers).");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ExternalProviderException($"{provider}: \"{url}\" is not a valid http(s) URL.");
        return url.Trim().TrimEnd('/');
    }

    /// <summary>Sends a request, turning transport failures into <see cref="ExternalProviderException"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string provider, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalProviderException($"{provider}: cannot reach {request.RequestUri?.GetLeftPart(UriPartial.Authority)}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExternalProviderException($"{provider}: {request.RequestUri?.GetLeftPart(UriPartial.Authority)} did not answer in time.", ex);
        }
    }

    /// <summary>Throws for a non-success status, with the server's error text when it sent one.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ExternalProviderException($"{provider}: {action} failed ({(int)response.StatusCode} {response.ReasonPhrase}){Detail(body)}");
    }

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string provider, string action, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, provider, action, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(body) ?? throw new JsonException("empty document");
        }
        catch (JsonException ex)
        {
            throw new ExternalProviderException($"{provider}: {action} returned an unexpected response (not JSON).", ex);
        }
    }

    /// <summary>": message" extracted from a JSON or text error body; empty when there is nothing useful.</summary>
    internal static string Detail(string? body)
    {
        var message = ErrorMessage(body);
        return string.IsNullOrWhiteSpace(message) ? string.Empty : ": " + message;
    }

    private static string? ErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            var messages = new List<string>();
            Collect(JsonNode.Parse(body), messages);
            if (messages.Count > 0)
                return string.Join("; ", messages.Distinct());
        }
        catch (JsonException)
        {
        }

        var text = body.Trim();
        return text.StartsWith('<') ? null : text.Length > 300 ? text[..300] + "…" : text;
    }

    // Understands the error shapes of the supported servers:
    // Vault {"errors":["…"]}, OAuth2 {"error":"…","error_description":"…"},
    // Delinea {"message":"…","messageDetail":"…"}, Passwordstate [{"errors":[{"message":"…"},{"phrase":"…"}]}].
    private static void Collect(JsonNode? node, List<string> messages)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    Collect(item, messages);
                break;
            case JsonObject obj:
                foreach (var key in new[] { "error_description", "message", "messageDetail", "phrase", "error" })
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                        messages.Add(text);
                }
                if (obj["errors"] is { } errors)
                    Collect(errors, messages);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text):
                messages.Add(text);
                break;
        }
    }

    /// <summary>A string member of a JSON object (any JSON scalar is converted); null when missing or empty.</summary>
    public static string? Text(JsonNode? node, string name)
    {
        var value = node is JsonObject obj ? obj[name] : null;
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
            return null;
        var text = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
