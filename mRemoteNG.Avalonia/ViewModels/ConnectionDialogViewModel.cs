using System.Reactive.Linq;
using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>Result data from the connection dialog Save action.</summary>
public sealed record ConnectionSavedResult(string Name, string Protocol, string Hostname, int Port, string Username);

public sealed class ConnectionDialogViewModel : ReactiveObject
{
    private string _name = "New Connection";
    private string _hostname = string.Empty;
    private string _description = string.Empty;
    private string _selectedProtocol = "SSH";
    private int _port = 22;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _domain = string.Empty;
    private bool _rdpSmartSize = true;
    private int _rdpColorDepth = 32;
    private string _sshKeyPath = string.Empty;
    private bool _sshCompression = true;
    private bool _isEditMode;
    private string _testStatus = string.Empty;

    public string WindowTitle => _isEditMode ? $"Edit Connection \u2014 {Name}" : "New Connection";

    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Hostname { get => _hostname; set => this.RaiseAndSetIfChanged(ref _hostname, value); }
    public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }
    public string SelectedProtocol
    {
        get => _selectedProtocol;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedProtocol, value);
            Port = ConnectionNodeViewModel.DefaultPortForString(value);
            this.RaisePropertyChanged(nameof(IsRdp));
            this.RaisePropertyChanged(nameof(IsSsh));
        }
    }
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }
    public string Domain { get => _domain; set => this.RaiseAndSetIfChanged(ref _domain, value); }
    public bool RdpSmartSize { get => _rdpSmartSize; set => this.RaiseAndSetIfChanged(ref _rdpSmartSize, value); }
    public int RdpColorDepth { get => _rdpColorDepth; set => this.RaiseAndSetIfChanged(ref _rdpColorDepth, value); }
    public string SshKeyPath { get => _sshKeyPath; set => this.RaiseAndSetIfChanged(ref _sshKeyPath, value); }
    public bool SshCompression { get => _sshCompression; set => this.RaiseAndSetIfChanged(ref _sshCompression, value); }

    /// <summary>Status text from the Test button (separate from Description).</summary>
    public string TestStatus { get => _testStatus; set => this.RaiseAndSetIfChanged(ref _testStatus, value); }

    public bool IsRdp => SelectedProtocol is "RDP";
    public bool IsSsh => SelectedProtocol is "SSH" or "SSH1" or "SSH2";

    public string[] Protocols { get; } = ["SSH", "RDP", "VNC", "Telnet", "HTTP", "HTTPS", "RAW", "Rlogin", "Serial", "PowerShell"];
    public int[] RdpColorDepths { get; } = [8, 15, 16, 24, 32];

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestCommand { get; }

    /// <summary>Raised when the user clicks Save with valid data.</summary>
    public event Action<ConnectionSavedResult>? Saved;

    /// <summary>Raised when the dialog should close.</summary>
    public event Action? CloseRequested;

    public ConnectionDialogViewModel(bool isEditMode = false)
    {
        _isEditMode = isEditMode;

        var canSave = this.WhenAnyValue(
            x => x.Name, x => x.Hostname,
            (name, host) => !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(host))
            .ObserveOn(RxApp.MainThreadScheduler);

        SaveCommand = ReactiveCommand.Create(OnSave, canSave);
        CancelCommand = ReactiveCommand.Create(OnCancel);
        TestCommand = ReactiveCommand.CreateFromTask(OnTest);
    }

    private void OnSave()
    {
        Saved?.Invoke(new ConnectionSavedResult(Name, SelectedProtocol, Hostname, Port, Username));
        CloseRequested?.Invoke();
    }

    private void OnCancel()
    {
        CloseRequested?.Invoke();
    }

    private async Task OnTest()
    {
        TestStatus = $"Testing {Hostname}:{Port}...";
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(Hostname, Port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(5000));
            if (completed == connectTask && client.Connected)
            {
                TestStatus = $"Success \u2014 {Hostname}:{Port} is reachable";
            }
            else
            {
                TestStatus = $"Timed out \u2014 {Hostname}:{Port} did not respond within 5s";
            }
        }
        catch (Exception ex)
        {
            TestStatus = $"Failed \u2014 {ex.Message}";
        }
    }
}
