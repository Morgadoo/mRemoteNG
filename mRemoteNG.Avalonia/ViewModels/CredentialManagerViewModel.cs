using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using mRemoteNG.Core.Credential;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

public class CredentialEntryViewModel : ReactiveObject
{
    private string _name = string.Empty, _username = string.Empty, _password = string.Empty, _domain = string.Empty;

    public CredentialEntryViewModel(Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
    }

    /// <summary>Stable id a connection can reference (see <see cref="ICredentialLookup"/>).</summary>
    public Guid Id { get; }

    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }
    public string Domain { get => _domain; set => this.RaiseAndSetIfChanged(ref _domain, value); }

    /// <summary>The stored password could not be decrypted on this machine (e.g. it came from another OS).</summary>
    public bool PasswordUnavailable { get; init; }

    public static CredentialEntryViewModel From(ICredentialRecord record, bool passwordUnavailable) => new(record.Id)
    {
        Name = record.Title,
        Username = record.Username,
        Password = record.Password,
        Domain = record.Domain,
        PasswordUnavailable = passwordUnavailable,
    };

    public CredentialRecord ToRecord() => new(Id)
    {
        Title = Name.Trim(),
        Username = Username,
        Password = Password,
        Domain = Domain,
    };
}

/// <summary>
/// Credential manager dialog. Edits a copy of the stored credentials;
/// Save writes them (passwords encrypted) through <see cref="FileCredentialRepository"/>, Cancel discards.
/// </summary>
public class CredentialManagerViewModel : ReactiveObject
{
    private readonly FileCredentialRepository _repository;
    private CredentialEntryViewModel? _selected;
    private string _errorMessage = string.Empty;

    public CredentialManagerViewModel(FileCredentialRepository repository)
    {
        _repository = repository;
        if (!repository.IsLoaded)
            repository.LoadCredentials();

        var undecryptable = repository.UndecryptableRecordIds.ToHashSet();
        foreach (var record in repository.CredentialRecords)
            Credentials.Add(CredentialEntryViewModel.From(record, undecryptable.Contains(record.Id)));

        AddCommand = ReactiveCommand.Create(() =>
        {
            var entry = new CredentialEntryViewModel { Name = "New Credential" };
            Credentials.Add(entry);
            Selected = entry;
        });
        RemoveCommand = ReactiveCommand.Create(() =>
        {
            if (Selected is null) return;
            var index = Credentials.IndexOf(Selected);
            Credentials.Remove(Selected);
            Selected = Credentials.Count == 0 ? null : Credentials[Math.Min(index, Credentials.Count - 1)];
        }, this.WhenAnyValue(x => x.Selected).Select(s => s != null));
        SaveCommand = ReactiveCommand.Create(OnSave);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
        Selected = Credentials.FirstOrDefault();
    }

    public ObservableCollection<CredentialEntryViewModel> Credentials { get; } = new();

    public CredentialEntryViewModel? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Raised when the dialog should close.</summary>
    public event Action? CloseRequested;

    /// <summary>Saves the edited list. Returns false (and sets <see cref="ErrorMessage"/>) on failure.</summary>
    public bool Save()
    {
        var unnamed = Credentials.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Name));
        if (unnamed is not null)
        {
            Selected = unnamed;
            ErrorMessage = "Every credential needs a name.";
            return false;
        }

        try
        {
            _repository.ReplaceAllAndSave(Credentials.Select(c => c.ToRecord()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = $"Could not save credentials: {ex.Message}";
            return false;
        }

        ErrorMessage = string.Empty;
        return true;
    }

    private void OnSave()
    {
        if (Save())
            CloseRequested?.Invoke();
    }
}
