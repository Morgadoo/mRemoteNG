using System.Net;
using System.Text;
using mRemoteNG.ExternalProviders;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

/// <summary>A request as the fake server saw it (bodies are read eagerly).</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string PathAndQuery => Uri.PathAndQuery;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// In-process HTTP server: routes "METHOD /path" (path without query) to a responder and records every
/// request. Unrouted requests get 404.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler On(string methodAndPath, Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _routes[methodAndPath] = respond;
        return this;
    }

    public FakeHttpHandler OnJson(string methodAndPath, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(methodAndPath, _ => Json(json, status));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        Requests.Add(recorded);

        var key = $"{request.Method.Method} {request.RequestUri!.AbsolutePath}";
        return _routes.TryGetValue(key, out var respond)
            ? respond(recorded)
            : Json("""{"errors":[]}""", HttpStatusCode.NotFound);
    }
}

/// <summary>Hands out clients over one <see cref="FakeHttpHandler"/> and remembers the options asked for.</summary>
internal sealed class FakeHttpClientFactory(FakeHttpHandler handler) : IProviderHttpClientFactory
{
    public List<ProviderHttpOptions> Options { get; } = [];

    public HttpClient Create(ProviderHttpOptions options)
    {
        Options.Add(options);
        return new HttpClient(handler, disposeHandler: false);
    }
}

/// <summary>Answers prompts from a queue (null = cancel) and records the questions.</summary>
internal sealed class ScriptedPrompt(params string?[] answers) : IExternalProviderPrompt
{
    private readonly Queue<string?> _answers = new(answers);

    public List<ExternalProviderPromptRequest> Asked { get; } = [];

    public Task<string?> PromptAsync(ExternalProviderPromptRequest request, CancellationToken ct = default)
    {
        Asked.Add(request);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}

internal static class TestSecrets
{
    /// <summary>A real AES-GCM provider with a fixed test key.</summary>
    public static ICryptoProvider Crypto { get; } = new AesGcmCryptoProvider(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    public static ProviderSecrets Create(IExternalProviderPrompt? prompt = null) =>
        new(Crypto, prompt ?? new NonInteractiveExternalProviderPrompt());

    public static string Protect(string value) => Crypto.Protect(value);
}
