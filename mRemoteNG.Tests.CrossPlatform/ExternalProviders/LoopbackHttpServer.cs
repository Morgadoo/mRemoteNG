using System.Net;
using System.Text;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

/// <summary>
/// A real HTTP server on 127.0.0.1 (first free port of 8250–8269) for clients that cannot take a fake
/// handler, such as the AWS SDK with a ServiceURL override.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<RecordedRequest, (int Status, string ContentType, string Body)> _respond;
    private readonly Task _loop;

    public LoopbackHttpServer(Func<RecordedRequest, (int Status, string ContentType, string Body)> respond)
    {
        _respond = respond;
        for (var port = 8250; ; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Url = $"http://127.0.0.1:{port}/";
                break;
            }
            catch (HttpListenerException) when (port < 8269)
            {
                CloseQuietly(listener);
            }
        }
        _loop = Task.Run(LoopAsync);
    }

    public string Url { get; }

    public List<RecordedRequest> Requests { get; } = [];

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.AllKeys
                .Where(k => k is not null)
                .ToDictionary(k => k!, k => context.Request.Headers[k] ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            var recorded = new RecordedRequest(new HttpMethod(context.Request.HttpMethod), context.Request.Url!, headers, body);
            lock (Requests)
                Requests.Add(recorded);

            var (status, contentType, responseBody) = _respond(recorded);
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        CloseQuietly(_listener);
        try { _loop.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }

    /// <summary>
    /// Close() alone stops the listener. With the managed HttpListener (macOS/Linux), Stop() followed by Close()
    /// unregisters twice, and the second time looks the endpoint up by re-binding the port, which fails when a
    /// parallel test's server has taken it in the meantime. A failure while closing is harmless here.
    /// </summary>
    private static void CloseQuietly(HttpListener listener)
    {
        try
        {
            listener.Close();
        }
        catch (HttpListenerException)
        {
        }
    }
}
