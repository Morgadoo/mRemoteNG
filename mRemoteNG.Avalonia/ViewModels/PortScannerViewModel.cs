using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

public class PortScanResult : ReactiveObject
{
    public int Port { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Service { get; init; } = string.Empty;
    public long LatencyMs { get; init; }
}

public class PortScannerViewModel : ReactiveObject
{
    private string _targetHost = string.Empty;
    private int _portFrom = 1, _portTo = 1024, _timeoutMs = 500;
    private double _scanProgress;
    private string _statusText = "Ready";
    private bool _isScanning;
    private CancellationTokenSource? _cts;

    public string TargetHost { get => _targetHost; set => this.RaiseAndSetIfChanged(ref _targetHost, value); }
    public int PortFrom { get => _portFrom; set => this.RaiseAndSetIfChanged(ref _portFrom, value); }
    public int PortTo { get => _portTo; set => this.RaiseAndSetIfChanged(ref _portTo, value); }
    public int TimeoutMs { get => _timeoutMs; set => this.RaiseAndSetIfChanged(ref _timeoutMs, value); }
    public double ScanProgress { get => _scanProgress; set => this.RaiseAndSetIfChanged(ref _scanProgress, value); }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public bool IsScanning { get => _isScanning; set => this.RaiseAndSetIfChanged(ref _isScanning, value); }

    public ObservableCollection<PortScanResult> Results { get; } = new();

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    public PortScannerViewModel()
    {
        var canStart = this.WhenAnyValue(x => x.IsScanning, x => x.TargetHost,
            (scanning, host) => !scanning && !string.IsNullOrWhiteSpace(host));
        var canStop = this.WhenAnyValue(x => x.IsScanning);

        StartCommand = ReactiveCommand.CreateFromTask(StartScanAsync, canStart);
        StopCommand = ReactiveCommand.Create(() => _cts?.Cancel(), canStop);
    }

    private async Task StartScanAsync()
    {
        Results.Clear();
        IsScanning = true;
        _cts = new CancellationTokenSource();
        var total = PortTo - PortFrom + 1;
        var done = 0;

        try
        {
            var semaphore = new SemaphoreSlim(50, 50);
            var tasks = new List<Task>();
            for (var port = PortFrom; port <= PortTo; port++)
            {
                var p = port;
                await semaphore.WaitAsync(_cts.Token);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        using var tcp = new TcpClient();
                        var connectTask = tcp.ConnectAsync(TargetHost, p);
                        var timeoutTask = Task.Delay(TimeoutMs, _cts.Token);
                        var winner = await Task.WhenAny(connectTask, timeoutTask);
                        sw.Stop();
                        var status = winner == connectTask && !connectTask.IsFaulted ? "Open" : "Closed";
                        if (status == "Open")
                        {
                            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                Results.Add(new PortScanResult { Port = p, Status = status, Service = GetServiceName(p), LatencyMs = sw.ElapsedMilliseconds }));
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                        var d = Interlocked.Increment(ref done);
                        ScanProgress = (double)d / total * 100;
                        StatusText = $"Scanning... {d}/{total}";
                    }
                }, _cts.Token));
            }
            await Task.WhenAll(tasks);
            StatusText = $"Done. Found {Results.Count} open ports.";
        }
        catch (OperationCanceledException) { StatusText = "Stopped."; }
        finally { IsScanning = false; }
    }

    private static string GetServiceName(int port) => port switch
    {
        21 => "FTP", 22 => "SSH", 23 => "Telnet", 25 => "SMTP", 53 => "DNS",
        80 => "HTTP", 110 => "POP3", 143 => "IMAP", 443 => "HTTPS", 445 => "SMB",
        3306 => "MySQL", 3389 => "RDP", 5432 => "PostgreSQL", 5900 => "VNC",
        8080 => "HTTP-Alt", 8443 => "HTTPS-Alt", _ => string.Empty
    };
}
