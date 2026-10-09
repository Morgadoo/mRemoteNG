using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>A terminal-like protocol that records input and can be dropped on demand.</summary>
internal sealed class FakeTerminalProtocol(FakeProtocolFactory factory, bool failConnect) : ProtocolBase, ITerminalProtocol, IVisualProtocol
{
    public List<string> Received { get; } = [];

    public ConnectionParameters? ConnectedWith { get; private set; }

    public bool Disposed { get; private set; }

    public Control CreateView() => new Border { Child = new TextBlock { Text = "fake terminal" }, Focusable = true };

    public override Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        ConnectedWith = parameters;
        State = ConnectionState.Connecting;
        if (failConnect)
        {
            State = ConnectionState.Error;
            throw new InvalidOperationException("simulated connect failure");
        }
        State = ConnectionState.Connected;
        factory.Connects++;
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <summary>The server went away (not initiated by the user).</summary>
    public void Drop() => State = ConnectionState.Error;

    public Task SendInputAsync(byte[] data, CancellationToken ct = default)
    {
        Received.Add(Encoding.UTF8.GetString(data));
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing) => Disposed = true;
}

/// <summary>A protocol without any capability interface.</summary>
internal sealed class FakePlainProtocol : ProtocolBase
{
    public override Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connected;
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }
}

internal sealed class FakeProtocolFactory : IProtocolFactory
{
    public List<FakeTerminalProtocol> Created { get; } = [];

    /// <summary>The next n protocols fail to connect.</summary>
    public int FailNext { get; set; }

    public int Connects { get; set; }

    public FakeTerminalProtocol Last => Created[^1];

    public IProtocol Create(ProtocolType type)
    {
        var fail = FailNext > 0;
        if (fail) FailNext--;
        var protocol = new FakeTerminalProtocol(this, fail);
        Created.Add(protocol);
        return protocol;
    }
}

/// <summary>Accepts any number of TCP connections on a loopback port (RAW sessions).</summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = [];
    private readonly CancellationTokenSource _cts = new();

    public LoopbackServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }

    public int Accepted
    {
        get
        {
            lock (_clients) return _clients.Count;
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_clients) _clients.Add(client);
            }
        }
        catch (Exception)
        {
            // Stopped.
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
    }
}

internal static class SessionTestSupport
{
    public static SessionsDockable SessionDock => TestHost.ViewModel.Sessions;

    public static AppSettingsService Settings => AppServices.GetRequired<AppSettingsService>();

    public static ConnectionInfo Connection(string name, int port, string panel = "General", CoreProtocolType protocol = CoreProtocolType.RAW)
    {
        var info = new ConnectionInfo
        {
            Name = name,
            Protocol = protocol,
            Hostname = "127.0.0.1",
            Port = port,
            Panel = panel,
        };
        return info;
    }

    public static void Pump()
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Runs the UI loop until <paramref name="condition"/> holds (or fails after the timeout).</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Pump();
    }

    /// <summary>Closes every session and panel, and restores the session settings changed by tests.</summary>
    public static async Task ResetAsync()
    {
        _ = TestHost.MainWindow;
        var dock = SessionDock;
        await dock.CloseAllSessionsAsync();
        foreach (var panel in dock.Panels.ToList())
            await dock.ClosePanelAsync(panel);
        dock.Arrangement = PanelArrangement.Tabbed;
        dock.AutoReconnectBaseDelay = TimeSpan.FromSeconds(1);
        Settings.Update(s =>
        {
            s.ReconnectOnDisconnect = false;
            s.ReconnectAttempts = AppSettings.DefaultReconnectAttempts;
            s.OpenConnectionsFromLastSession = false;
            s.ShowProtocolOnTabs = false;
            s.ShowLogonInfoOnTabs = false;
            s.IdentifyQuickConnectTabs = false;
            s.AlwaysShowPanelSelectionDlg = false;
            s.WindowLayout = string.Empty;
        });
        Pump();
    }
}
