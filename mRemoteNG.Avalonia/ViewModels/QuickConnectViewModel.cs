using Avalonia.Controls;
using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

public sealed class QuickConnectViewModel : ReactiveObject
{
    private string _hostname = string.Empty;
    private string _selectedProtocol = "SSH";
    private string _username = string.Empty;
    private string _password = string.Empty;

    private readonly Window? _owner;

    public QuickConnectViewModel(Window? owner = null) => _owner = owner;

    public string Hostname { get => _hostname; set => this.RaiseAndSetIfChanged(ref _hostname, value); }
    public string SelectedProtocol { get => _selectedProtocol; set => this.RaiseAndSetIfChanged(ref _selectedProtocol, value); }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }

    public string[] Protocols { get; } = ["SSH", "RDP", "VNC", "Telnet", "HTTP", "HTTPS", "RAW", "Rlogin"];

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; set; } = ReactiveCommand.Create(() => { });

    public QuickConnectResult? BuildResult() => string.IsNullOrWhiteSpace(Hostname)
        ? null
        : new QuickConnectResult(Hostname, SelectedProtocol, Username, Password);
}

public record QuickConnectResult(string Hostname, string Protocol, string Username, string Password);
