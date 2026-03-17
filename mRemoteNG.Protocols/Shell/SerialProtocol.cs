using System.IO.Ports;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Shell;

/// <summary>
/// Serial port protocol using <see cref="System.IO.Ports.SerialPort"/>.
///
/// Cross-platform:
///   • Windows: COM1, COM2, …
///   • Linux:   /dev/ttyS0, /dev/ttyUSB0, /dev/ttyACM0, …
///   • macOS:   /dev/cu.usbserial-*, /dev/tty.usbmodem*
///
/// <see cref="ConnectionParameters"/> extras used:
///   • "serial.baudRate"  — default 115200
///   • "serial.dataBits"  — default 8
///   • "serial.stopBits"  — default "One"
///   • "serial.parity"    — default "None"
///   • "serial.handshake" — default "None"
///
/// The <see cref="ConnectionParameters.Hostname"/> field is used as the port name
/// (e.g. "COM3", "/dev/ttyUSB0").
/// </summary>
public sealed class SerialProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<SerialProtocol> _logger;
    private SerialPort? _port;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;

    public SerialProtocol(ILogger<SerialProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new TerminalView();
        _view.DataToSend += OnDataToSend;
        return _view;
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        string portName = parameters.Hostname; // repurpose Hostname as port name

        int baudRate = int.TryParse(parameters.Extras.GetValueOrDefault("serial.baudRate", "115200"), out int b) ? b : 115200;
        int dataBits = int.TryParse(parameters.Extras.GetValueOrDefault("serial.dataBits", "8"), out int d) ? d : 8;

        Parity parity = Enum.TryParse(parameters.Extras.GetValueOrDefault("serial.parity", "None"), out Parity p) ? p : Parity.None;
        StopBits stopBits = Enum.TryParse(parameters.Extras.GetValueOrDefault("serial.stopBits", "One"), out StopBits sb) ? sb : StopBits.One;
        Handshake handshake = Enum.TryParse(parameters.Extras.GetValueOrDefault("serial.handshake", "None"), out Handshake h) ? h : Handshake.None;

        RaiseStatus($"Opening {portName} at {baudRate} baud…");

        try
        {
            _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                Handshake = handshake,
                ReadTimeout = 500,
                WriteTimeout = 500,
                Encoding = Encoding.UTF8,
                NewLine = "\r\n",
            };
            _port.Open();

            State = ConnectionState.Connected;
            RaiseStatus($"Serial: {portName} {baudRate} {dataBits}{parity.ToString()[0]}{(int)stopBits}");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = ReadLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            RaiseStatus($"Serial error: {ex.Message}");
            _logger.LogError(ex, "Serial connect failed");
        }

        await Task.CompletedTask;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _port?.Close();
        State = ConnectionState.Disconnected;
        await Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_port is null) return;
        var buffer = new byte[256];
        try
        {
            while (!ct.IsCancellationRequested && _port.IsOpen)
            {
                try
                {
                    int read = _port.BaseStream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        string text = Encoding.UTF8.GetString(buffer, 0, read);
                        _view?.Write(text);
                    }
                }
                catch (TimeoutException) { /* normal — poll */ }
                await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Serial read error");
            State = ConnectionState.Error;
        }
    }

    private void OnDataToSend(object? sender, byte[] data)
    {
        try { _port?.BaseStream.Write(data); }
        catch (Exception ex) { _logger.LogWarning(ex, "Serial write error"); }
    }

    /// <summary>Returns available serial port names on the current platform.</summary>
    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _port?.Dispose();
        }
    }
}
