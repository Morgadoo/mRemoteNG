using System.Net;
using System.Net.Sockets;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>An incoming (reverse) VNC connection accepted by <see cref="VncListener"/>.</summary>
public sealed class VncIncomingConnectionEventArgs(TcpClient client) : EventArgs
{
    /// <summary>The accepted socket. The handler takes ownership (usually via <see cref="VncProtocol.UseIncomingConnection"/>).</summary>
    public TcpClient Client { get; } = client;

    public IPEndPoint? RemoteEndPoint { get; } = client.Client.RemoteEndPoint as IPEndPoint;
}

/// <summary>
/// Listens for VNC servers that connect to the viewer — UltraVNC SingleClick, <c>x11vnc -connect</c>,
/// TigerVNC <c>vncconfig -connect</c> and other "add new client" features. Once accepted, the RFB handshake runs
/// exactly as for an outgoing connection (the server still speaks first).
/// </summary>
public sealed class VncListener : IAsyncDisposable
{
    /// <summary>The conventional port for listening viewers.</summary>
    public const int DefaultPort = 5500;

    private readonly IPAddress _address;
    private readonly int _requestedPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task _acceptLoop = Task.CompletedTask;

    /// <param name="port">Port to listen on; 0 picks a free port.</param>
    /// <param name="address">Local address; default all IPv4 interfaces.</param>
    public VncListener(int port = DefaultPort, IPAddress? address = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _requestedPort = port;
        _address = address ?? IPAddress.Any;
    }

    public bool IsListening => _listener is not null;

    /// <summary>The port actually listened on (useful with port 0); 0 while stopped.</summary>
    public int Port => _listener?.LocalEndpoint is IPEndPoint endPoint ? endPoint.Port : 0;

    /// <summary>Raised on a thread-pool thread for every accepted connection. Unhandled connections are closed.</summary>
    public event EventHandler<VncIncomingConnectionEventArgs>? ConnectionAccepted;

    /// <summary>Raised when accepting stops because of an error (not when <see cref="StopAsync"/> is called).</summary>
    public event EventHandler<Exception>? Faulted;

    /// <exception cref="SocketException">The port is in use or not permitted.</exception>
    public void Start()
    {
        if (_listener is not null) return;
        var listener = new TcpListener(_address, _requestedPort);
        listener.Start();
        _listener = listener;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(this, ex);
                return;
            }

            client.NoDelay = true;
            var handler = ConnectionAccepted;
            if (handler is null)
            {
                client.Dispose();
                continue;
            }
            try
            {
                handler(this, new VncIncomingConnectionEventArgs(client));
            }
            catch (Exception)
            {
                // A failing handler must not stop the listener; the connection is abandoned.
                client.Dispose();
            }
        }
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        if (listener is null) return;
        _listener = null;
        _stop?.Cancel();
        listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (Exception) { /* stopping */ }
        _stop?.Dispose();
        _stop = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
