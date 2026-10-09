using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using Avalonia.Media.Imaging;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.External;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>A tool as a toolbar button or menu item: name, icon and a command that runs it.</summary>
public sealed class ExternalToolCommandItem(ExternalTool tool, Bitmap? icon, ReactiveCommand<Unit, Unit> command)
{
    public ExternalTool Tool { get; } = tool;

    public string DisplayName => Tool.DisplayName;

    /// <summary>The tool's own image; null when it has none and <see cref="IconKind"/> is shown instead.</summary>
    public Bitmap? Icon { get; } = icon;

    public Material.Icons.MaterialIconKind IconKind { get; } = Services.ExternalToolIcons.KindFor(tool);

    public ReactiveCommand<Unit, Unit> RunCommand { get; } = command;

    public string ToolTip => Tool.TryIntegrate
        ? Localizer.Format("ExternalToolTipIntegratedFormat", Tool.DisplayName)
        : Localizer.Format("ExternalToolTipFormat", Tool.DisplayName);
}

/// <summary>
/// The External Tools toolbar (legacy ExternalToolsToolStrip): one button per tool marked "Show on toolbar" for this
/// OS, running it for the connection selected in the tree. Rebuilt whenever the tool list changes.
/// </summary>
public sealed class ExternalToolsToolbarViewModel : ReactiveObject
{
    private readonly ExternalToolsService _service;
    private readonly Func<ConnectionInfo?> _selectedConnection;
    private readonly AppSettingsService? _settings;
    private bool _showText = true;

    public ExternalToolsToolbarViewModel(ExternalToolsService service, Func<ConnectionInfo?> selectedConnection, AppSettingsService? settings = null)
    {
        _service = service;
        _selectedConnection = selectedConnection;
        _settings = settings;
        _showText = settings?.Current.ShowExternalToolsText ?? true;

        ToggleShowTextCommand = ReactiveCommand.Create(() => { ShowText = !ShowText; });
        ManageCommand = ReactiveCommand.Create(() => ManageRequested?.Invoke(this, EventArgs.Empty));

        _service.ToolsChanged += (_, _) => Rebuild();
        _service.Tools.CollectionChanged += OnToolsCollectionChanged;
        foreach (var tool in _service.Tools)
            tool.PropertyChanged += OnToolChanged;
        Rebuild();
    }

    public ObservableCollection<ExternalToolCommandItem> Buttons { get; } = [];

    public bool HasButtons => Buttons.Count > 0;

    /// <summary>At most this many tools get their own button; the rest go to the "⋯" overflow menu.</summary>
    public const int MaxInlineButtons = 4;

    /// <summary>The tools shown as buttons (the first <see cref="MaxInlineButtons"/>, or all when one more would overflow).</summary>
    public IReadOnlyList<ExternalToolCommandItem> InlineButtons { get; private set; } = [];

    /// <summary>The tools listed in the overflow menu.</summary>
    public IReadOnlyList<ExternalToolCommandItem> OverflowButtons { get; private set; } = [];

    public bool HasOverflow => OverflowButtons.Count > 0;

    /// <summary>Show the tools' names next to their icons (persisted in the options).</summary>
    public bool ShowText
    {
        get => _showText;
        set
        {
            if (_showText == value)
                return;
            this.RaiseAndSetIfChanged(ref _showText, value);
            _settings?.Update(s => s.ShowExternalToolsText = value);
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleShowTextCommand { get; }

    /// <summary>Opens the External Tools window (handled by the view).</summary>
    public ReactiveCommand<Unit, Unit> ManageCommand { get; }

    public event EventHandler? ManageRequested;

    /// <summary>The connection the toolbar's tools run for.</summary>
    public ConnectionInfo? SelectedConnection => _selectedConnection();

    public ExternalToolsService Service => _service;

    private void OnToolsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ExternalTool tool in e.OldItems)
                tool.PropertyChanged -= OnToolChanged;
        if (e.NewItems is not null)
            foreach (ExternalTool tool in e.NewItems)
                tool.PropertyChanged += OnToolChanged;
        Rebuild();
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Buttons.Clear();
        foreach (var tool in _service.ToolbarTools)
            Buttons.Add(CreateItem(_service, tool, _selectedConnection));
        // An overflow menu holding a single tool would save nothing: show it inline instead.
        var inline = Buttons.Count <= MaxInlineButtons + 1 ? Buttons.Count : MaxInlineButtons;
        InlineButtons = Buttons.Take(inline).ToList();
        OverflowButtons = Buttons.Skip(inline).ToList();
        this.RaisePropertyChanged(nameof(HasButtons));
        this.RaisePropertyChanged(nameof(InlineButtons));
        this.RaisePropertyChanged(nameof(OverflowButtons));
        this.RaisePropertyChanged(nameof(HasOverflow));
    }

    /// <summary>A command item that runs <paramref name="tool"/> for the connection <paramref name="target"/> returns.</summary>
    public static ExternalToolCommandItem CreateItem(ExternalToolsService service, ExternalTool tool, Func<ConnectionInfo?> target) =>
        new(tool, ExternalToolIcons.GetCustom(tool),
            ReactiveCommand.CreateFromTask(async () => { await service.RunAsync(tool, target()); }));
}
