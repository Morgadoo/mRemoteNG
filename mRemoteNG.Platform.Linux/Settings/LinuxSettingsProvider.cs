using System.Xml;

namespace mRemoteNG.Platform.Linux.Settings;

/// <summary>
/// Linux settings provider following XDG Base Directory Specification.
/// Stores settings in: $XDG_CONFIG_HOME/mRemoteNG/settings.xml
/// Defaults to: ~/.config/mRemoteNG/settings.xml
/// </summary>
public sealed class LinuxSettingsProvider : ISettingsProvider
{
    private readonly Dictionary<string, Dictionary<string, string>> _cache = new();
    private bool _dirty;

    public string ApplicationDataDirectory { get; }
    public string SettingsFilePath => Path.Combine(ApplicationDataDirectory, "settings.xml");

    public LinuxSettingsProvider()
    {
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        ApplicationDataDirectory = Path.Combine(xdgConfig, "mRemoteNG");
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
        if (_cache.TryGetValue(section, out var sec))
        {
            sec.Remove(key);
            _dirty = true;
        }
    }

    public IReadOnlyList<string> GetKeys(string section) =>
        _cache.TryGetValue(section, out var sec) ? sec.Keys.ToList() : [];

    public void Save()
    {
        if (!_dirty) return;
        var doc = BuildXml();
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
        foreach (XmlElement section in doc.SelectNodes("//Section")!)
        {
            var name = section.GetAttribute("name");
            var entries = new Dictionary<string, string>();
            foreach (XmlElement entry in section.SelectNodes("Entry")!)
                entries[entry.GetAttribute("key")] = entry.GetAttribute("value");
            _cache[name] = entries;
        }
    }

    private XmlDocument BuildXml()
    {
        var doc = new XmlDocument();
        var root = doc.CreateElement("Settings");
        doc.AppendChild(root);
        foreach (var (sectionName, entries) in _cache)
        {
            var sEl = doc.CreateElement("Section");
            sEl.SetAttribute("name", sectionName);
            foreach (var (k, v) in entries)
            {
                var eEl = doc.CreateElement("Entry");
                eEl.SetAttribute("key", k);
                eEl.SetAttribute("value", v);
                sEl.AppendChild(eEl);
            }
            root.AppendChild(sEl);
        }
        return doc;
    }
}
