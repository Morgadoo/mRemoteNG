using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

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

    public string WindowTitle => _isEditMode ? $"Edit Connection — {Name}" : "New Connection";

    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Hostname { get => _hostname; set => this.RaiseAndSetIfChanged(ref _hostname, value); }
    public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }
    public string SelectedProtocol
    {
        get => _selectedProtocol;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedProtocol, value);
            Port = DefaultPortFor(value);
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

    public bool IsRdp => SelectedProtocol is "RDP";
    public bool IsSsh => SelectedProtocol is "SSH" or "SSH1" or "SSH2";

    public string[] Protocols { get; } = ["SSH", "RDP", "VNC", "Telnet", "HTTP", "HTTPS", "RAW", "Rlogin", "Serial", "PowerShell"];
    public int[] RdpColorDepths { get; } = [8, 15, 16, 24, 32];

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestCommand { get; }

    public ConnectionDialogViewModel(bool isEditMode = false)
    {
        _isEditMode = isEditMode;
        SaveCommand = ReactiveCommand.Create(OnSave);
        CancelCommand = ReactiveCommand.Create(OnCancel);
        TestCommand = ReactiveCommand.Create(OnTest);
    }

    private static int DefaultPortFor(string protocol) => protocol switch
    {
        "RDP" => 3389,
        "VNC" => 5900,
        "Telnet" => 23,
        "HTTP" => 80,
        "HTTPS" => 443,
        "RAW" => 23,
        "Rlogin" => 513,
        _ => 22,
    };

    private void OnSave() { /* Phase 3: persist to connection list */ }
    private void OnCancel() { /* close */ }
    private void OnTest() { /* attempt connection test */ }
}
