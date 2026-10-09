using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Platform;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Core.Settings;

/// <summary>Raised after settings were committed.</summary>
public sealed class AppSettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    /// <summary>Snapshot of the values before the change.</summary>
    public AppSettings Previous { get; } = previous;

    /// <summary>Snapshot of the values after the change.</summary>
    public AppSettings Current { get; } = current;

    public bool Changed<T>(Func<AppSettings, T> selector) =>
        !EqualityComparer<T>.Default.Equals(selector(Previous), selector(Current));
}

/// <summary>
/// Owns the application's <see cref="AppSettings"/>: loads them from <see cref="ISettingsProvider"/>,
/// hands out editable copies, validates and commits edits, and persists them.
/// Register as a singleton. <see cref="Current"/> is always the same instance (updated in place).
/// </summary>
public sealed class AppSettingsService
{
    private readonly ISettingsProvider _provider;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    public AppSettingsService(ISettingsProvider provider, ILogger<AppSettingsService>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The live settings. Do not mutate directly; use <see cref="Apply"/> or <see cref="Update"/>.</summary>
    public AppSettings Current { get; } = new();

    /// <summary>The underlying store (for its directory and file path).</summary>
    public ISettingsProvider Provider => _provider;

    /// <summary>Raised (on the caller's thread) after <see cref="Apply"/>, <see cref="Update"/> or <see cref="Load"/> changed values.</summary>
    public event EventHandler<AppSettingsChangedEventArgs>? Changed;

    /// <summary>Loads every setting from the provider; missing or invalid values get their defaults.</summary>
    public void Load()
    {
        var loaded = new AppSettings();
        foreach (var (property, section) in AppSettings.PersistedProperties)
        {
            var raw = _provider.GetValue<string?>(section, property.Name, null);
            if (raw is null)
                continue;

            if (SettingsValueConverter.TryParse(raw, property.PropertyType, out var value)
                && (value is not null || Nullable.GetUnderlyingType(property.PropertyType) is not null))
            {
                property.SetValue(loaded, value);
            }
            else
            {
                _logger.LogWarning("Ignoring invalid value '{Value}' for setting {Section}/{Key}", raw, section, property.Name);
            }
        }

        foreach (var name in loaded.Normalize())
            _logger.LogWarning("Setting {Name} was out of range and has been reset to its default", name);

        Commit(loaded, persist: false);
    }

    /// <summary>Returns a copy for an editor (e.g. the Options window) to modify freely.</summary>
    public AppSettings CreateEditableCopy()
    {
        lock (_sync)
            return Current.Clone();
    }

    /// <summary>
    /// Validates <paramref name="edited"/>, copies it into <see cref="Current"/>, saves it and raises <see cref="Changed"/>.
    /// </summary>
    /// <returns>The validation errors; when non-empty nothing was changed.</returns>
    public IReadOnlyList<string> Apply(AppSettings edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var errors = edited.Validate();
        if (errors.Count > 0)
            return errors;

        Commit(edited.Clone(), persist: true);
        return errors;
    }

    /// <summary>Changes individual values (e.g. the last opened file) and saves.</summary>
    public void Update(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings edited;
        lock (_sync)
            edited = Current.Clone();

        change(edited);
        edited.Normalize();
        Commit(edited, persist: true);
    }

    /// <summary>Writes <see cref="Current"/> to the provider and flushes it to disk.</summary>
    public void Save()
    {
        lock (_sync)
        {
            foreach (var (property, section) in AppSettings.PersistedProperties)
            {
                var value = property.GetValue(Current);
                if (value is null)
                    _provider.RemoveValue(section, property.Name);
                else
                    _provider.SetValue(section, property.Name, SettingsValueConverter.FormatObject(value));
            }

            _provider.Save();
        }
    }

    private void Commit(AppSettings next, bool persist)
    {
        AppSettings previous;
        bool changed;
        lock (_sync)
        {
            previous = Current.Clone();
            changed = !previous.ValueEquals(next);
            Current.CopyFrom(next);
        }

        if (persist)
        {
            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not save settings to {Path}", _provider.SettingsFilePath);
                throw;
            }
        }

        if (changed)
            Changed?.Invoke(this, new AppSettingsChangedEventArgs(previous, Current.Clone()));
    }
}
