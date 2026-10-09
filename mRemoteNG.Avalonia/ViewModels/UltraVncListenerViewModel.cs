using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using ReactiveUI;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>One reverse connection received by the listener.</summary>
public sealed record IncomingVncConnection(DateTime Time, string Remote, string Status);

/// <summary>
/// UltraVNC SingleClick listener (legacy Tools ▸ UltraVNC SC): accepts reverse VNC connections on a port and
/// opens each one as a VNC session tab.
/// </summary>
public sealed class UltraVncListenerViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly Func<TcpClient, IPEndPoint?, Task<string>> _openSession;
    private readonly Action<int>? _savePort;
    private VncListener? _listener;
    private int _port;
    private bool _isListening;
    private string _statusText = "Not listening.";

    /// <param name="openSession">Opens a session over an accepted socket and returns a status for the list.</param>
    /// <param name="port">Initial port.</param>
    /// <param name="savePort">Called with the port when listening starts, to remember it.</param>
    public UltraVncListenerViewModel(Func<TcpClient, IPEndPoint?, Task<string>> openSession, int port = VncListener.DefaultPort,
        Action<int>? savePort = null)
    {
        _openSession = openSession;
        _savePort = savePort;
        _port = port;

        var canStart = this.WhenAnyValue(x => x.IsListening, x => x.Port, (listening, p) => !listening && p is > 0 and <= 65535);
        StartCommand = ReactiveCommand.Create(Start, canStart);
        StopCommand = ReactiveCommand.CreateFromTask(StopAsync, this.WhenAnyValue(x => x.IsListening));
    }

    /// <summary>Uses the app's session dock, protocol factory and settings.</summary>
    public static UltraVncListenerViewModel CreateForApp()
    {
        var settings = AppServices.GetRequired<mRemoteNG.Core.Settings.AppSettingsService>();
        var sessions = AppServices.GetRequired<SessionsDockable>();
        var factory = AppServices.GetRequired<IProtocolFactory>();
        return new UltraVncListenerViewModel(
            (client, remote) => OpenSessionAsync(sessions, factory, client, remote),
            settings.Current.UltraVncSingleClickPort,
            port =>
            {
                if (settings.Current.UltraVncSingleClickPort == port) return;
                settings.Update(s => s.UltraVncSingleClickPort = port);
                settings.Save();
            });
    }

    public int Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    public bool IsListening
    {
        get => _isListening;
        private set => this.RaiseAndSetIfChanged(ref _isListening, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ObservableCollection<IncomingVncConnection> Connections { get; } = [];

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    /// <summary>Starts listening; failures (port in use…) are shown in <see cref="StatusText"/>.</summary>
    public void Start()
    {
        if (IsListening) return;
        var listener = new VncListener(Port);
        listener.ConnectionAccepted += OnConnectionAccepted;
        listener.Faulted += (_, ex) => Dispatcher.UIThread.Post(() =>
        {
            IsListening = false;
            StatusText = $"Listening stopped: {ex.Message}";
        });
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            StatusText = $"Cannot listen on port {Port}: {ex.Message}";
            return;
        }
        _listener = listener;
        IsListening = true;
        StatusText = $"Listening for incoming VNC connections on port {listener.Port}…";
        _savePort?.Invoke(Port);
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            listener.ConnectionAccepted -= OnConnectionAccepted;
            await listener.StopAsync();
        }
        IsListening = false;
        StatusText = "Not listening.";
    }

    private void OnConnectionAccepted(object? sender, VncIncomingConnectionEventArgs e)
    {
        var remote = e.RemoteEndPoint;
        Dispatcher.UIThread.Post(async () =>
        {
            var name = remote?.ToString() ?? "unknown";
            string status;
            try
            {
                status = await _openSession(e.Client, remote);
            }
            catch (Exception ex)
            {
                e.Client.Dispose();
                status = $"Failed: {ex.Message}";
            }
            Connections.Insert(0, new IncomingVncConnection(DateTime.Now, name, status));
        });
    }

    /// <summary>Opens a session tab for an accepted reverse connection.</summary>
    public static async Task<string> OpenSessionAsync(SessionsDockable sessions, IProtocolFactory factory, TcpClient client, IPEndPoint? remote)
    {
        if (factory.Create(ProtocolType.Vnc) is not VncProtocol protocol)
        {
            client.Dispose();
            return "Failed: no VNC protocol is registered.";
        }
        protocol.UseIncomingConnection(client);
        var parameters = new ConnectionParameters
        {
            Hostname = remote?.Address.ToString() ?? "incoming",
            Port = remote?.Port ?? 0,
            Protocol = ProtocolType.Vnc,
            Extras = new Dictionary<string, string> { [ConnectionParametersFactory.Keys.VncScaling] = "fit" },
        };
        var tab = new SessionTabViewModel(protocol, parameters);
        sessions.AddSession(tab);
        try
        {
            await tab.ConnectAsync();
            return protocol.DesktopName is { Length: > 0 } desktop ? $"Connected: \"{desktop}\"" : "Connected";
        }
        catch (Exception ex)
        {
            tab.SetError(ex.Message);
            sessions.ReportError($"Incoming VNC connection from {parameters.Hostname} failed: {ex.Message}");
            return $"Failed: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
