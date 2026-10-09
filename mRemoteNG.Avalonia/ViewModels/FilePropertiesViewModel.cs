using System.Reactive;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Security;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// File ▸ Properties: the open connection file's root name, master password (set, change, remove — the
/// current password is asked before changing or removing it) and encryption settings (cipher engine,
/// mode, key derivation iterations, full-file encryption). Applied to the tree on OK and written on the
/// next save.
/// </summary>
public sealed class FilePropertiesViewModel : ReactiveObject
{
    private readonly ConnectionsService _service;
    private string _rootName;
    private bool _usePassword;
    private bool _changePassword;
    private string _currentPassword = string.Empty;
    private string _newPassword = string.Empty;
    private string _confirmPassword = string.Empty;
    private BlockCipherEngines _engine;
    private BlockCipherModes _mode;
    private decimal? _iterations;
    private bool _fullFileEncryption;
    private string _errorText = string.Empty;

    public FilePropertiesViewModel(ConnectionsService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        var root = service.ConnectionTreeModel?.RootNode
                   ?? throw new InvalidOperationException("No connection file is open.");
        FileName = service.CurrentFilePath is { } path ? Path.GetFileName(path) : Localizer.Get("NotSavedYet");
        IsPasswordProtected = root.IsPasswordProtected;
        _rootName = root.Name;
        _usePassword = IsPasswordProtected;
        _engine = service.Encryption.Engine;
        _mode = service.Encryption.Mode;
        _iterations = service.Encryption.KeyDerivationIterations;
        _fullFileEncryption = service.Encryption.FullFileEncryption;

        OkCommand = ReactiveCommand.Create(OnOk);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(false));
    }

    public string FileName { get; }

    /// <summary>True when the file currently has a master password.</summary>
    public bool IsPasswordProtected { get; }

    public string RootName
    {
        get => _rootName;
        set => this.RaiseAndSetIfChanged(ref _rootName, value ?? string.Empty);
    }

    /// <summary>Protect the file with a master password.</summary>
    public bool UsePassword
    {
        get => _usePassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _usePassword, value);
            RaisePasswordState();
        }
    }

    /// <summary>Replace the existing master password.</summary>
    public bool ChangePassword
    {
        get => _changePassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _changePassword, value);
            RaisePasswordState();
        }
    }

    public bool CanChangePassword => IsPasswordProtected && UsePassword;

    /// <summary>The current password is needed to change or remove an existing one.</summary>
    public bool NeedsCurrentPassword => Action != MasterPasswordAction.Keep && IsPasswordProtected;

    public bool NeedsNewPassword => Action == MasterPasswordAction.Set;

    public string CurrentPassword
    {
        get => _currentPassword;
        set => this.RaiseAndSetIfChanged(ref _currentPassword, value ?? string.Empty);
    }

    public string NewPassword
    {
        get => _newPassword;
        set => this.RaiseAndSetIfChanged(ref _newPassword, value ?? string.Empty);
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => this.RaiseAndSetIfChanged(ref _confirmPassword, value ?? string.Empty);
    }

    public MasterPasswordAction Action => (IsPasswordProtected, UsePassword, ChangePassword) switch
    {
        (true, false, _) => MasterPasswordAction.Remove,
        (true, true, true) => MasterPasswordAction.Set,
        (false, true, _) => MasterPasswordAction.Set,
        _ => MasterPasswordAction.Keep,
    };

    public string PasswordStatus => IsPasswordProtected
        ? Localizer.Get("FileProtectedByMasterPassword")
        : Localizer.Get("FileNotProtectedByMasterPassword");

    public BlockCipherEngines[] Engines { get; } = Enum.GetValues<BlockCipherEngines>();
    public BlockCipherModes[] Modes { get; } = Enum.GetValues<BlockCipherModes>();

    public BlockCipherEngines Engine
    {
        get => _engine;
        set => this.RaiseAndSetIfChanged(ref _engine, value);
    }

    public BlockCipherModes Mode
    {
        get => _mode;
        set => this.RaiseAndSetIfChanged(ref _mode, value);
    }

    public decimal MinIterations => ConnectionFileSecurity.MinKeyDerivationIterations;
    public decimal MaxIterations => ConnectionFileSecurity.MaxKeyDerivationIterations;

    public decimal? KeyDerivationIterations
    {
        get => _iterations;
        set => this.RaiseAndSetIfChanged(ref _iterations, value);
    }

    public bool FullFileEncryption
    {
        get => _fullFileEncryption;
        set => this.RaiseAndSetIfChanged(ref _fullFileEncryption, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => this.RaiseAndSetIfChanged(ref _errorText, value);
    }

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Raised when the dialog should close: true for OK.</summary>
    public event Action<bool>? CloseRequested;

    public ConnectionFileSecurityChange ToChange() => new()
    {
        PasswordAction = Action,
        CurrentPassword = CurrentPassword,
        NewPassword = NewPassword,
        ConfirmPassword = ConfirmPassword,
        Engine = Engine,
        Mode = Mode,
        KeyDerivationIterations = (int)Math.Clamp(KeyDerivationIterations ?? 0, 0, int.MaxValue),
        FullFileEncryption = FullFileEncryption,
    };

    /// <summary>Problems with the entered values; empty when OK may be pressed.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = ConnectionFileSecurity.Validate(_service, ToChange()).ToList();
        if (string.IsNullOrWhiteSpace(RootName))
            errors.Insert(0, Localizer.Get("TheNameIsRequired"));
        ErrorText = string.Join(Environment.NewLine, errors);
        return errors;
    }

    /// <summary>Applies the settings to the open tree; true when anything changed (the file then needs saving).</summary>
    public bool Apply()
    {
        if (Validate().Count > 0)
            throw new InvalidOperationException(ErrorText);

        var root = _service.ConnectionTreeModel!.RootNode;
        var changed = false;
        if (root.Name != RootName.Trim())
        {
            root.Name = RootName.Trim();
            changed = true;
        }
        return ConnectionFileSecurity.Apply(_service, ToChange()) | changed;
    }

    private void OnOk()
    {
        if (Validate().Count == 0)
            CloseRequested?.Invoke(true);
    }

    private void RaisePasswordState()
    {
        this.RaisePropertyChanged(nameof(Action));
        this.RaisePropertyChanged(nameof(CanChangePassword));
        this.RaisePropertyChanged(nameof(NeedsCurrentPassword));
        this.RaisePropertyChanged(nameof(NeedsNewPassword));
    }
}
