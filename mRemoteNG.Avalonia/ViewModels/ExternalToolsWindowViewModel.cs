using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.External;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// External Tools window (legacy ExternalToolsWindow): edits a copy of the tool list; Save writes
/// <c>extApps.xml</c>, Cancel discards. "Launch" runs the selected tool (as edited) for the connection that was
/// selected in the tree when the window opened.
/// </summary>
public sealed class ExternalToolsWindowViewModel : ReactiveObject
{
    private const string MaskedPassword = "••••••";

    private readonly ExternalToolsService _service;
    private ExternalTool? _selected;
    private bool _isDirty;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private string _preview = string.Empty;

    public ExternalToolsWindowViewModel(ExternalToolsService service, ConnectionInfo? targetConnection = null)
    {
        _service = service;
        TargetConnection = targetConnection;
        foreach (var tool in service.Tools)
            Add(tool.Clone());
        Tools.CollectionChanged += (_, _) => IsDirty = true;
        Selected = Tools.FirstOrDefault();

        var hasSelection = this.WhenAnyValue(x => x.Selected).Select(s => s is not null);
        AddCommand = ReactiveCommand.Create(OnAdd);
        DuplicateCommand = ReactiveCommand.Create(OnDuplicate, hasSelection);
        DeleteCommand = ReactiveCommand.CreateFromTask(OnDeleteAsync, hasSelection);
        MoveUpCommand = ReactiveCommand.Create(() => Move(-1), hasSelection);
        MoveDownCommand = ReactiveCommand.Create(() => Move(+1), hasSelection);
        LaunchCommand = ReactiveCommand.CreateFromTask(OnLaunchAsync, hasSelection);
        SaveCommand = ReactiveCommand.Create(OnSave);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
    }

    /// <summary>Editable copies of the tools, in display order.</summary>
    public ObservableCollection<ExternalTool> Tools { get; } = [];

    public ExternalTool? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
                return;
            if (_selected is not null)
                _selected.PropertyChanged -= OnSelectedToolChanged;
            this.RaiseAndSetIfChanged(ref _selected, value);
            if (_selected is not null)
                _selected.PropertyChanged += OnSelectedToolChanged;
            UpdatePreview();
        }
    }

    /// <summary>The connection "Launch" uses (the tree selection when the window opened), or null.</summary>
    public ConnectionInfo? TargetConnection { get; }

    public string TargetDescription => TargetConnection is null
        ? Localizer.Get("ExternalToolsNoConnection")
        : Localizer.Format("ExternalToolsTargetFormat", TargetConnection.Name);

    public IReadOnlyList<ExternalToolPlatform> Platforms { get; } = Enum.GetValues<ExternalToolPlatform>();

    /// <summary>The selected tool's command for <see cref="TargetConnection"/> (password masked).</summary>
    public string Preview
    {
        get => _preview;
        private set => this.RaiseAndSetIfChanged(ref _preview, value);
    }

    public string VariablesHelp { get; } =
        Localizer.Format("ExternalToolsVariablesHelpFormat",
            string.Join(", ", ExternalToolVariables.Names.Select(n => $"%{n}%")));

    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => this.RaiseAndSetIfChanged(ref _statusIsError, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> LaunchCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Asks the user to confirm deleting the named tool; when unset, deletes without asking.</summary>
    public Func<string, Task<bool>>? ConfirmDeleteAsync { get; set; }

    public event Action? CloseRequested;

    private void Add(ExternalTool tool, int index = -1)
    {
        tool.PropertyChanged += (_, _) => IsDirty = true;
        if (index < 0 || index > Tools.Count)
            Tools.Add(tool);
        else
            Tools.Insert(index, tool);
    }

    private void OnAdd()
    {
        var tool = new ExternalTool(UniqueName(Localizer.Get("ExternalToolDefaultName")));
        Add(tool);
        Selected = tool;
    }

    private void OnDuplicate()
    {
        if (Selected is null)
            return;
        var copy = Selected.Clone();
        copy.DisplayName = UniqueName(Selected.DisplayName);
        Add(copy, Tools.IndexOf(Selected) + 1);
        Selected = copy;
    }

    private async Task OnDeleteAsync()
    {
        if (Selected is not { } tool)
            return;
        if (ConfirmDeleteAsync is { } confirm && !await confirm(tool.DisplayName))
            return;
        int index = Tools.IndexOf(tool);
        Tools.Remove(tool);
        Selected = Tools.Count == 0 ? null : Tools[Math.Min(index, Tools.Count - 1)];
    }

    private void Move(int offset)
    {
        if (Selected is not { } tool)
            return;
        int index = Tools.IndexOf(tool);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= Tools.Count)
            return;
        Tools.Move(index, target);
        Selected = tool;
    }

    private async Task OnLaunchAsync()
    {
        if (Selected is not { } tool)
            return;
        if (tool.TryIntegrate && (IsDirty || _service.Find(tool.DisplayName) is null))
        {
            SetStatus(Localizer.Get("ExternalToolsSaveBeforeIntegrate"), isError: true);
            return;
        }

        bool started = await _service.RunAsync(tool.Clone(), TargetConnection);
        SetStatus(started
            ? Localizer.Format("ExternalToolStartedFormat", tool.DisplayName)
            : Localizer.Format("ExternalToolNotStartedFormat", tool.DisplayName), !started);
    }

    /// <summary>Checks the edited tools; returns the problems (empty when they can be saved).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var tool in Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.DisplayName))
                problems.Add(Localizer.Get("ExternalToolNeedsName"));
            else if (string.IsNullOrWhiteSpace(tool.FileName))
                problems.Add(Localizer.Format("ExternalToolNoFileNameFormat", tool.DisplayName));
        }
        foreach (var duplicate in Tools.GroupBy(t => t.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
            problems.Add(Localizer.Format("ExternalToolDuplicateNameFormat", duplicate.Key));
        return problems.Distinct().ToList();
    }

    /// <summary>Saves the tools; returns false (with <see cref="StatusMessage"/> set) when they are invalid or could not be written.</summary>
    public bool Save()
    {
        var problems = Validate();
        if (problems.Count > 0)
        {
            SetStatus(problems[0], isError: true);
            return false;
        }
        foreach (var tool in Tools)
            tool.DisplayName = tool.DisplayName.Trim();
        if (!_service.ReplaceAll(Tools.Select(t => t.Clone())))
        {
            SetStatus(Localizer.Format("CouldNotSaveSeeLogFormat", _service.Repository.FilePath), isError: true);
            return false;
        }
        IsDirty = false;
        return true;
    }

    private void OnSave()
    {
        if (Save())
            CloseRequested?.Invoke();
    }

    private void OnSelectedToolChanged(object? sender, PropertyChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (Selected is not { } tool)
        {
            Preview = string.Empty;
            return;
        }

        var variables = _service.GetVariables(TargetConnection);
        if (variables is not null && variables.Password.Length > 0)
            variables = variables with { Password = MaskedPassword };
        try
        {
            var psi = _service.Launcher.BuildStartInfo(tool, variables);
            var args = psi.ArgumentList.Count > 0
                ? string.Join(' ', psi.ArgumentList.Select(QuoteForDisplay))
                : psi.Arguments;
            Preview = string.IsNullOrEmpty(args) ? psi.FileName : $"{psi.FileName} {args}";
        }
        catch (ExternalToolException ex)
        {
            Preview = ex.Message;
        }
    }

    private static string QuoteForDisplay(string arg) =>
        arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? ExternalToolLauncher.ShellQuote(arg) : arg;

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int i = 2; Tools.Any(t => string.Equals(t.DisplayName, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName} ({i})";
        return name;
    }
}
