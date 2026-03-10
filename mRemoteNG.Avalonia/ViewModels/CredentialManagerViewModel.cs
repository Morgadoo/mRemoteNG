using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

public class CredentialEntryViewModel : ReactiveObject
{
    private string _name = string.Empty, _username = string.Empty, _password = string.Empty, _domain = string.Empty;
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }
    public string Domain { get => _domain; set => this.RaiseAndSetIfChanged(ref _domain, value); }
}

public class CredentialManagerViewModel : ReactiveObject
{
    public ObservableCollection<CredentialEntryViewModel> Credentials { get; } = new();
    private CredentialEntryViewModel? _selected;
    public CredentialEntryViewModel? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public CredentialManagerViewModel()
    {
        AddCommand = ReactiveCommand.Create(() => {
            var entry = new CredentialEntryViewModel { Name = "New Credential" };
            Credentials.Add(entry);
            Selected = entry;
        });
        RemoveCommand = ReactiveCommand.Create(() => {
            if (Selected != null) Credentials.Remove(Selected);
        }, this.WhenAnyValue(x => x.Selected, s => s != null));
        SaveCommand = ReactiveCommand.Create(() => { /* persist via ISettingsProvider in Phase 5 */ });
    }
}
