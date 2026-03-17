using System.Xml;

namespace mRemoteNG.Platform.Mac.Settings;

/// <summary>
/// macOS settings provider following Apple's recommended storage conventions.
/// Stores settings in: ~/Library/Application Support/mRemoteNG/settings.xml
/// </summary>
public sealed class MacSettingsProvider : ISettingsProvider
{
    private readonly Dictionary<string, Dictionary<string, string>> _cache = new();
    private bool _dirty;

    public string ApplicationDataDirectory { get; }
    public string SettingsFilePath => Path.Combine(ApplicationDataDirectory, "settings.xml");

    public MacSettingsProvider()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ApplicationDataDirectory = Path.Combine(home, "Library", "Application Support", "mRemoteNG");
        Directory.CreateDirectory(ApplicationDataDirectory);
        Load();
    }

    public T GetValue<T>(string section, string key, T defaultValue)
    {
        if (_cache.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var raw))
        {
            try { return (T)Convert.ChangeType(raw, typeof(T))!; }
            catch { }
        }
        return defaultValue;
    }

    public void SetValue<T>(string section, string key, T value)
    {
        if (!_cache.TryGetValue(section, out var sec))
            _cache[section] = sec = [];
        sec[key] = value?.ToString() ?? string.Empty;
        _dirty = true;
    }

    public void RemoveValue(string section, string key)
    {
        if (_cache.TryGetValue(section, out var sec)) { sec.Remove(key); _dirty = true; }
    }

    public IReadOnlyList<string> GetKeys(string section) =>
        _cache.TryGetValue(section, out var sec) ? sec.Keys.ToList() : [];

    public void Save()
    {
        if (!_dirty) return;
        var doc = new XmlDocument();
        var root = doc.CreateElement("Settings");
        doc.AppendChild(root);
        foreach (var (sn, entries) in _cache)
        {
            var s = doc.CreateElement("Section");
            s.SetAttribute("name", sn);
            foreach (var (k, v) in entries)
            {
                var e = doc.CreateElement("Entry");
                e.SetAttribute("key", k);
                e.SetAttribute("value", v);
                s.AppendChild(e);
            }
            root.AppendChild(s);
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
        foreach (XmlElement sec in doc.SelectNodes("//Section")!)
        {
            var entries = new Dictionary<string, string>();
            foreach (XmlElement e in sec.SelectNodes("Entry")!)
                entries[e.GetAttribute("key")] = e.GetAttribute("value");
            _cache[sec.GetAttribute("name")] = entries;
        }
    }
}
