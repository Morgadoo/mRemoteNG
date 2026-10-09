using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Tools.PortScanning;
using ReactiveUI;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>One scanned host in the results grid.</summary>
public sealed class PortScanResult : ReactiveObject
{
    private bool _isSelected = true;

    public PortScanResult(ScanHost host) => Host = host;

    public ScanHost Host { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public string HostName => Host.HostName;
    public string Address => Host.Address;
    public string OpenPorts => string.Join(", ", Host.OpenPorts.Select(p => p.Port));
    public string Services => string.Join(", ", Host.OpenPorts.Where(p => p.Service != ScannedService.Unknown)
        .Select(p => $"{Label(p.Service)} ({p.Port})"));
    public string Ssh => Mark(Host.Ssh);
    public string Telnet => Mark(Host.Telnet);
    public string Http => Mark(Host.Http);
    public string Https => Mark(Host.Https);
    public string Rlogin => Mark(Host.Rlogin);
    public string Rdp => Mark(Host.Rdp);
    public string Vnc => Mark(Host.Vnc);
    public long LatencyMs => Host.OpenPorts.Count == 0 ? 0 : Host.OpenPorts.Min(p => p.LatencyMs);

    private static string Mark(bool value) => value ? "✓" : "";

    public static string Label(ScannedService service) => service switch
    {
        ScannedService.Ssh => "SSH",
        ScannedService.Telnet => "Telnet",
        ScannedService.Http => "HTTP",
        ScannedService.Https => "HTTPS",
        ScannedService.Rlogin => "rlogin",
        ScannedService.Rdp => "RDP",
        ScannedService.Vnc => "VNC",
        _ => "?",
    };
}

/// <summary>
/// Port scanner (legacy Tools ▸ Port Scan): scans IP ranges / CIDR blocks / host names on a port list, shows the
/// services found per host, and imports the selected hosts into the connection tree as connections of one protocol.
/// </summary>
public class PortScannerViewModel : ReactiveObject
{
    public const string DefaultPortList = "22, 23, 80, 443, 513, 3389, 5900";

    private readonly Func<IReadOnlyList<ScanHost>, CoreProtocolType, string>? _import;
    private string _hosts = string.Empty;
    private string _ports = DefaultPortList;
    private int _timeoutMs = 1000;
    private int _parallelism = 64;
    private bool _detectServices = true;
    private bool _resolveHostNames = true;
    private double _scanProgress;
    private string _statusText = Localizer.Get("PortScanHint");
    private bool _isScanning;
    private CoreProtocolType _importProtocol = CoreProtocolType.SSH2;
    private CancellationTokenSource? _cts;

    /// <param name="import">Imports hosts as connections of a protocol and returns a summary; null hides importing.</param>
    public PortScannerViewModel(Func<IReadOnlyList<ScanHost>, CoreProtocolType, string>? import = null)
    {
        _import = import;
        var canStart = this.WhenAnyValue(x => x.IsScanning, x => x.Hosts, x => x.Ports,
            (scanning, hosts, ports) => !scanning && !string.IsNullOrWhiteSpace(hosts) && !string.IsNullOrWhiteSpace(ports));
        StartCommand = ReactiveCommand.CreateFromTask(StartScanAsync, canStart);
        StopCommand = ReactiveCommand.Create(Stop, this.WhenAnyValue(x => x.IsScanning));
        DefaultPortsCommand = ReactiveCommand.Create(() => { Ports = DefaultPortList; });
        ImportCommand = ReactiveCommand.Create(ImportSelected,
            this.WhenAnyValue(x => x.IsScanning, scanning => !scanning && import is not null));
    }

    /// <summary>Hosts: addresses, ranges (10.0.0.1-20), CIDR blocks and names, separated by commas or spaces.</summary>
    public string Hosts { get => _hosts; set => this.RaiseAndSetIfChanged(ref _hosts, value); }

    /// <summary>Ports and ranges, e.g. "22, 80, 5900-5910".</summary>
    public string Ports { get => _ports; set => this.RaiseAndSetIfChanged(ref _ports, value); }

    public int TimeoutMs { get => _timeoutMs; set => this.RaiseAndSetIfChanged(ref _timeoutMs, value); }
    public int Parallelism { get => _parallelism; set => this.RaiseAndSetIfChanged(ref _parallelism, value); }
    public bool DetectServices { get => _detectServices; set => this.RaiseAndSetIfChanged(ref _detectServices, value); }
    public bool ResolveHostNames { get => _resolveHostNames; set => this.RaiseAndSetIfChanged(ref _resolveHostNames, value); }
    public double ScanProgress { get => _scanProgress; set => this.RaiseAndSetIfChanged(ref _scanProgress, value); }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public bool IsScanning { get => _isScanning; set => this.RaiseAndSetIfChanged(ref _isScanning, value); }

    public bool CanImport => _import is not null;

    /// <summary>Protocols the import can create (legacy list: SSH, Telnet, HTTP, HTTPS, rlogin, RDP, VNC, ARD).</summary>
    public IReadOnlyList<CoreProtocolType> ImportProtocols => PortScanImporter.SupportedProtocols;

    public CoreProtocolType ImportProtocol { get => _importProtocol; set => this.RaiseAndSetIfChanged(ref _importProtocol, value); }

    /// <summary>Hosts with at least one open port, in scan order.</summary>
    public ObservableCollection<PortScanResult> Results { get; } = [];

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> DefaultPortsCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }

    /// <summary>Runs the scan; parse errors are shown in <see cref="StatusText"/>.</summary>
    public async Task StartScanAsync()
    {
        IReadOnlyList<string> hosts;
        IReadOnlyList<int> ports;
        try
        {
            hosts = HostRangeParser.Parse(Hosts);
            ports = PortListParser.Parse(Ports);
        }
        catch (FormatException ex)
        {
            StatusText = ex.Message;
            return;
        }
        if (hosts.Count == 0 || ports.Count == 0)
        {
            StatusText = Localizer.Get("PortScanNeedHostAndPort");
            return;
        }

        Results.Clear();
        ScanProgress = 0;
        IsScanning = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var options = new PortScanOptions
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Clamp(TimeoutMs, 50, 30000)),
            Parallelism = Math.Clamp(Parallelism, 1, 1024),
            DetectServices = DetectServices,
            ResolveHostNames = ResolveHostNames,
        };
        StatusText = Localizer.Format("PortScanScanningFormat", hosts.Count, ports.Count);

        var progress = new Progress<PortScanProgress>(p =>
        {
            ScanProgress = p.Total == 0 ? 100 : 100.0 * p.Completed / p.Total;
            StatusText = Localizer.Format("PortScanProgressFormat", p.HostsCompleted, p.HostCount, p.Completed, p.Total);
        });

        try
        {
            var all = await new PortScanner().ScanAsync(hosts, ports, options,
                host =>
                {
                    if (host.IsReachable)
                        Dispatcher.UIThread.Post(() => Results.Add(new PortScanResult(host)));
                },
                progress, cts.Token);
            // Let queued result rows land before the summary.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            ScanProgress = 100;
            StatusText = Localizer.Format("PortScanDoneFormat", all.Count, all.Count(h => h.IsReachable));
        }
        catch (OperationCanceledException)
        {
            StatusText = Localizer.Format("PortScanStoppedFormat", Results.Count);
        }
        finally
        {
            IsScanning = false;
            _cts = null;
        }
    }

    /// <summary>Cancels a running scan.</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>Imports the checked hosts as <see cref="ImportProtocol"/> connections.</summary>
    public void ImportSelected()
    {
        if (_import is null) return;
        var selected = Results.Where(r => r.IsSelected).Select(r => r.Host).ToList();
        if (selected.Count == 0)
        {
            StatusText = Localizer.Get("PortScanSelectHosts");
            return;
        }
        try
        {
            StatusText = _import(selected, ImportProtocol);
        }
        catch (Exception ex)
        {
            StatusText = Localizer.Format("ImportFailedFormat", ex.Message);
        }
    }
}
