using System.Net;
using System.Net.Sockets;
using System.Text;

namespace mRemoteNG.Avalonia.Diagnostics;

/// <summary>
/// A loopback TCP echo listener on a free port, so the smoke test can open a real session on every OS
/// without external servers. It records what it receives so the test can check typed input arrived.
/// </summary>
public sealed class SmokeEchoServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _received = new();
    private readonly List<TcpClient> _clients = [];

    private SmokeEchoServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>Everything received so far (UTF-8).</summary>
    public string Received
    {
        get
        {
            lock (_received)
                return _received.ToString();
        }
    }

    public static SmokeEchoServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new SmokeEchoServer(listener);
    }

    /// <summary>Completes once <paramref name="text"/> has been received; throws <see cref="TimeoutException"/> otherwise.</summary>
    public async Task WaitForAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!Received.Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The echo listener did not receive \"{text}\" within {timeout.TotalSeconds:0} s (got \"{Received}\").");
            await Task.Delay(50);
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_clients)
                    _clients.Add(client);
                _ = EchoAsync(client);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (SocketException)
        {
            // Listener closed.
        }
    }

    private async Task EchoAsync(TcpClient client)
    {
        var buffer = new byte[4096];
        try
        {
            var stream = client.GetStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
            {
                lock (_received)
                    _received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                await stream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            }
        }
        catch (Exception)
        {
            // Client gone or listener stopped: nothing to report for an echo server.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var client in _clients)
                client.Dispose();
        }
        _cts.Dispose();
    }
}
