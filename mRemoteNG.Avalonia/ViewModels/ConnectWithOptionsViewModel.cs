using System.Reactive;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>RDP console-session choice for a single connection attempt.</summary>
public enum ConsoleSessionChoice
{
    /// <summary>Use the connection's "Console session" setting.</summary>
    AsConfigured,
    /// <summary>Connect to the console session.</summary>
    Console,
    /// <summary>Do not connect to the console session.</summary>
    NoConsole,
}

/// <summary>The legacy "Connect (with options)" sub-menu entries.</summary>
public enum ConnectPreset
{
    NoCredentials,
    ConsoleSession,
    NoConsoleSession,
    Fullscreen,
    ViewOnly,
}

/// <summary>
/// "Connect with options" dialog: the choices of the legacy sub-menu (no credentials, console / no console,
/// full screen, view only, choose panel) combined, mapped to <see cref="ConnectOptions"/>.
/// </summary>
public sealed class ConnectWithOptionsViewModel : ReactiveObject
{
    private bool _noCredentials;
    private ConsoleSessionChoice _consoleSession;
    private bool _fullscreen;
    private bool _viewOnly;
    private string _panel = string.Empty;

    public ConnectWithOptionsViewModel(string connectionName, IReadOnlyList<string> panels, bool isRdp = true, bool supportsViewOnly = true)
    {
        ConnectionName = connectionName;
        Panels = panels;
        IsRdp = isRdp;
        SupportsViewOnly = supportsViewOnly;
        ConnectCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(true));
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(false));
    }

    public string ConnectionName { get; }
    public string Title => $"Connect with Options — {ConnectionName}";

    /// <summary>Panel names offered in the "Panel" box.</summary>
    public IReadOnlyList<string> Panels { get; }

    /// <summary>True when the console-session choice applies (RDP, or a folder that may contain RDP).</summary>
    public bool IsRdp { get; }

    public bool SupportsViewOnly { get; }

    public ConsoleSessionChoice[] ConsoleSessionChoices { get; } = Enum.GetValues<ConsoleSessionChoice>();

    public bool NoCredentials
    {
        get => _noCredentials;
        set => this.RaiseAndSetIfChanged(ref _noCredentials, value);
    }

    public ConsoleSessionChoice ConsoleSession
    {
        get => _consoleSession;
        set => this.RaiseAndSetIfChanged(ref _consoleSession, value);
    }

    public bool Fullscreen
    {
        get => _fullscreen;
        set => this.RaiseAndSetIfChanged(ref _fullscreen, value);
    }

    public bool ViewOnly
    {
        get => _viewOnly;
        set => this.RaiseAndSetIfChanged(ref _viewOnly, value);
    }

    /// <summary>Panel to open the session in; empty for the connection's own Panel.</summary>
    public string Panel
    {
        get => _panel;
        set => this.RaiseAndSetIfChanged(ref _panel, value ?? string.Empty);
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Raised when the dialog should close: true to connect.</summary>
    public event Action<bool>? CloseRequested;

    public ConnectOptions ToOptions() => new()
    {
        NoCredentials = NoCredentials,
        ConsoleSession = ConsoleSession switch
        {
            ConsoleSessionChoice.Console => true,
            ConsoleSessionChoice.NoConsole => false,
            _ => null,
        },
        Fullscreen = Fullscreen,
        ViewOnly = ViewOnly,
        Panel = string.IsNullOrWhiteSpace(Panel) ? null : Panel.Trim(),
    };

    /// <summary>The options for one of the legacy sub-menu entries.</summary>
    public static ConnectOptions ForPreset(ConnectPreset preset) => preset switch
    {
        ConnectPreset.NoCredentials => new ConnectOptions { NoCredentials = true },
        ConnectPreset.ConsoleSession => new ConnectOptions { ConsoleSession = true },
        ConnectPreset.NoConsoleSession => new ConnectOptions { ConsoleSession = false },
        ConnectPreset.Fullscreen => new ConnectOptions { Fullscreen = true },
        ConnectPreset.ViewOnly => new ConnectOptions { ViewOnly = true },
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };
}
