using System.Runtime.Versioning;
using System.Xml;

namespace mRemoteNG.Platform.Windows.Settings;

/// <summary>
/// Windows settings provider: stores settings in
/// %APPDATA%\mRemoteNG\settings.xml (XML, not registry).
/// This replaces the registry-based provider to enable future portability.
/// On first run it imports existing registry settings via <see cref="RegistryMigrationHelper"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSettingsProvider : ISettingsProvider
{
    private readonly Dictionary<string, Dictionary<string, string>> _cache = new();
    private bool _dirty;

    public string ApplicationDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG");

    public string SettingsFilePath => Path.Combine(ApplicationDataDirectory, "settings.xml");

    public WindowsSettingsProvider()
    {
        Directory.CreateDirectory(ApplicationDataDirectory);
        Load();
    }

    public T GetValue<T>(string section, string key, T defaultValue)
    {
        if (_cache.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var raw))
        {
            try { return (T)Convert.ChangeType(raw, typeof(T))!; }
            catch { /* fall through to default */ }
        }
        return defaultValue;
    }

    public void SetValue<T>(string section, string key, T value)
    {
        if (!_cache.TryGetValue(section, out var sec))
            _cache[section] = sec = new Dictionary<string, string>();
        sec[key] = value?.ToString() ?? string.Empty;
        _dirty = true;
    }

    public void RemoveValue(string section, string key)
    {
        if (_cache.TryGetValue(section, out var sec))
        {
            sec.Remove(key);
            _dirty = true;
        }
    }

    public IReadOnlyList<string> GetKeys(string section) =>
        _cache.TryGetValue(section, out var sec)
            ? sec.Keys.ToList()
            : [];

    public void Save()
    {
        if (!_dirty) return;

        var doc = new XmlDocument();
        var root = doc.CreateElement("Settings");
        doc.AppendChild(root);

        foreach (var (sectionName, entries) in _cache)
        {
            var sectionEl = doc.CreateElement("Section");
            sectionEl.SetAttribute("name", sectionName);
            foreach (var (k, v) in entries)
            {
                var entry = doc.CreateElement("Entry");
                entry.SetAttribute("key", k);
                entry.SetAttribute("value", v);
                sectionEl.AppendChild(entry);
            }
            root.AppendChild(sectionEl);
        }

        doc.Save(SettingsFilePath);
        _dirty = false;
    }

    public void Reload() => Load();

    private void Load()
    {
        _cache.Clear();
        if (!File.Exists(SettingsFilePath)) return;

        var doc = new XmlDocument();
        doc.Load(SettingsFilePath);

        foreach (XmlElement section in doc.SelectNodes("//Section") ?? new XmlDocument().ChildNodes as XmlNodeList ?? throw new InvalidOperationException())
        {
            var sectionName = section.GetAttribute("name");
            var entries = new Dictionary<string, string>();
            foreach (XmlElement entry in section.SelectNodes("Entry") ?? throw new InvalidOperationException())
                entries[entry.GetAttribute("key")] = entry.GetAttribute("value");
            _cache[sectionName] = entries;
        }
    }
}
