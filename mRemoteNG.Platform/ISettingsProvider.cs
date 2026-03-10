namespace mRemoteNG.Platform;

/// <summary>
/// Abstracts persistent settings storage across platforms.
/// On Windows: reads from Windows Registry (HKCU\Software\mRemoteNG) or XML.
/// On Linux: reads from ~/.config/mRemoteNG/settings.xml.
/// On macOS: reads from ~/Library/Application Support/mRemoteNG/settings.xml.
/// </summary>
public interface ISettingsProvider
{
    /// <summary>Gets the platform-specific directory where all mRemoteNG data is stored.</summary>
    string ApplicationDataDirectory { get; }

    /// <summary>Gets the full path to the settings XML file.</summary>
    string SettingsFilePath { get; }

    /// <summary>Reads a strongly-typed value. Returns <paramref name="defaultValue"/> if not found.</summary>
    T GetValue<T>(string section, string key, T defaultValue);

    /// <summary>Writes a value to persistent storage.</summary>
    void SetValue<T>(string section, string key, T value);

    /// <summary>Removes a key from settings.</summary>
    void RemoveValue(string section, string key);

    /// <summary>Returns all keys in a given section.</summary>
    IReadOnlyList<string> GetKeys(string section);

    /// <summary>Persists all pending changes to disk.</summary>
    void Save();

    /// <summary>Reloads settings from disk, discarding any unsaved changes.</summary>
    void Reload();
}
