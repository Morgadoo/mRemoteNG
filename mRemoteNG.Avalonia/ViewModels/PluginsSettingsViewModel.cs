using mRemoteNG.Core.App.Info;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders.Plugins;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// Options ▸ Plugins: the plugins found at startup (with why one did not load), switches to disable them, and the
/// extra plugin folders. Changes take effect after a restart, because plugins load once.
/// </summary>
public sealed class PluginsSettingsViewModel : SettingsPageViewModel
{
    private readonly PluginHost? _host;

    public PluginsSettingsViewModel(AppSettings working, PluginHost? host) : base(working)
    {
        _host = host;
        var disabled = PluginHost.ParseIds(working.DisabledPlugins);
        Entries = (host?.Catalog ?? [])
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new PluginEntryViewModel(e, !disabled.Contains(e.Id), OnEnabledChanged))
            .ToList();
    }

    public IReadOnlyList<PluginEntryViewModel> Entries { get; }

    public bool HasPlugins => Entries.Count > 0;

    /// <summary>The folders always searched (next to the executable and in the data directory).</summary>
    public string DefaultFolders => string.Join(Environment.NewLine, PluginHost.DefaultDirectories());

    /// <summary>The user data folder's Plugins folder (created by "Open plugin folder").</summary>
    public string UserPluginFolder => Path.Combine(ApplicationPaths.SettingsDirectory, PluginHost.PluginsFolderName);

    /// <summary>Extra folders, separated by ';'.</summary>
    public string PluginFolderPaths
    {
        get => Working.PluginFolderPaths;
        set => Set(Working.PluginFolderPaths, value?.Trim() ?? string.Empty, v => Working.PluginFolderPaths = v);
    }

    public string LoadedSummary => _host is null
        ? string.Empty
        : Localizer.Format("PluginsLoadedSummaryFormat", Entries.Count(e => e.State == PluginState.Loaded), Entries.Count);

    private void OnEnabledChanged(PluginEntryViewModel entry)
    {
        var disabled = PluginHost.ParseIds(Working.DisabledPlugins);
        if (entry.IsEnabled)
            disabled.Remove(entry.Id);
        else
            disabled.Add(entry.Id);
        Working.DisabledPlugins = string.Join(";", disabled.Order(StringComparer.OrdinalIgnoreCase));
        this.RaisePropertyChanged(nameof(Entries));
    }
}

/// <summary>One plugin on the Plugins page.</summary>
public sealed class PluginEntryViewModel : ReactiveObject
{
    private readonly Action<PluginEntryViewModel> _enabledChanged;
    private bool _isEnabled;

    public PluginEntryViewModel(PluginCatalogEntry entry, bool isEnabled, Action<PluginEntryViewModel> enabledChanged)
    {
        Entry = entry;
        _isEnabled = isEnabled;
        _enabledChanged = enabledChanged;
    }

    public PluginCatalogEntry Entry { get; }
    public string Id => Entry.Id;
    public string DisplayName => Entry.DisplayName;
    public PluginState State => Entry.State;
    public string AssemblyPath => Entry.AssemblyPath;

    /// <summary>"1.0.0 · Address resolver, Connection properties".</summary>
    public string Details => string.Join(" · ", new[] { Entry.Version, string.Join(", ", Entry.Kinds) }.Where(s => !string.IsNullOrEmpty(s)));

    public string StateText => State switch
    {
        PluginState.Loaded => Localizer.Get("PluginStateLoaded"),
        PluginState.Disabled => Localizer.Get("PluginStateDisabled"),
        PluginState.Incompatible => Localizer.Get("PluginStateIncompatible"),
        PluginState.Duplicate => Localizer.Get("PluginStateDuplicate"),
        PluginState.Unsupported => Localizer.Get("PluginStateUnsupported"),
        _ => Localizer.Get("PluginStateFailed"),
    };

    public string Message => Entry.Message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Entry.Message);

    public bool IsLoaded => State == PluginState.Loaded;

    public bool IsProblem => State is not (PluginState.Loaded or PluginState.Disabled);

    /// <summary>Only plugins that could load can be switched on or off.</summary>
    public bool CanToggle => State is PluginState.Loaded or PluginState.Disabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!CanToggle || _isEnabled == value)
                return;
            this.RaiseAndSetIfChanged(ref _isEnabled, value);
            _enabledChanged(this);
        }
    }
}
