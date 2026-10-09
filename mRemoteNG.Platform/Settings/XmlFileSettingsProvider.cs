using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace mRemoteNG.Platform.Settings;

/// <summary>
/// <see cref="ISettingsProvider"/> backed by an XML file:
/// <code>
/// &lt;Settings&gt;
///   &lt;Section name="Appearance"&gt;
///     &lt;Entry key="Theme" value="Dark" /&gt;
///   &lt;/Section&gt;
/// &lt;/Settings&gt;
/// </code>
/// <list type="bullet">
///   <item>Values are formatted and parsed with the invariant culture (<see cref="SettingsValueConverter"/>).</item>
///   <item><see cref="Save"/> writes atomically (temp file + rename).</item>
///   <item>An unreadable file is moved aside to <c>settings.xml.corrupt-&lt;timestamp&gt;</c> and the
///         provider starts with defaults instead of crashing the application.</item>
///   <item>All members are thread-safe.</item>
/// </list>
/// The per-OS providers only differ in <see cref="ApplicationDataDirectory"/>.
/// </summary>
public class XmlFileSettingsProvider : ISettingsProvider
{
    public const string DefaultFileName = "settings.xml";

    private const string RootElement = "Settings";
    private const string SectionElement = "Section";
    private const string EntryElement = "Entry";

    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private bool _dirty;

    public XmlFileSettingsProvider(string applicationDataDirectory, ILogger? logger = null, string fileName = DefaultFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _logger = logger ?? NullLogger.Instance;
        ApplicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        SettingsFilePath = Path.Combine(ApplicationDataDirectory, fileName);

        try
        {
            Directory.CreateDirectory(ApplicationDataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal here: reading falls back to defaults and Save() reports the error.
            _logger.LogError(ex, "Could not create settings directory {Directory}", ApplicationDataDirectory);
        }

        lock (_sync)
            LoadUnsafe();
    }

    public string ApplicationDataDirectory { get; }

    public string SettingsFilePath { get; }

    /// <summary>
    /// When the settings file could not be parsed on the last load, the path it was moved to; otherwise null.
    /// </summary>
    public string? CorruptFileBackupPath { get; private set; }

    public T GetValue<T>(string section, string key, T defaultValue)
    {
        string? raw;
        lock (_sync)
        {
            if (!_cache.TryGetValue(section, out var entries) || !entries.TryGetValue(key, out raw))
                return defaultValue;
        }

        if (SettingsValueConverter.TryParse<T>(raw, out var value))
            return value;

        _logger.LogWarning("Ignoring invalid value '{Value}' for setting {Section}/{Key}", raw, section, key);
        return defaultValue;
    }

    public void SetValue<T>(string section, string key, T value)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        var formatted = SettingsValueConverter.Format(value);

        lock (_sync)
        {
            if (!_cache.TryGetValue(section, out var entries))
                _cache[section] = entries = new Dictionary<string, string>(StringComparer.Ordinal);

            if (entries.TryGetValue(key, out var existing) && existing == formatted)
                return;

            entries[key] = formatted;
            _dirty = true;
        }
    }

    public void RemoveValue(string section, string key)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(section, out var entries) && entries.Remove(key))
                _dirty = true;
        }
    }

    public IReadOnlyList<string> GetKeys(string section)
    {
        lock (_sync)
            return _cache.TryGetValue(section, out var entries) ? entries.Keys.ToList() : [];
    }

    public void Save()
    {
        lock (_sync)
        {
            if (!_dirty)
                return;

            var document = BuildXml();
            AtomicFile.WriteAllText(SettingsFilePath, $"{document.Declaration}\n{document}\n");
            _dirty = false;
        }
    }

    public void Reload()
    {
        lock (_sync)
            LoadUnsafe();
    }

    private void LoadUnsafe()
    {
        _cache.Clear();
        _dirty = false;
        CorruptFileBackupPath = null;

        if (!File.Exists(SettingsFilePath))
            return;

        try
        {
            var document = LoadDocument(SettingsFilePath);
            foreach (var section in document.Root!.Elements(SectionElement))
            {
                var name = (string?)section.Attribute("name");
                if (name is null)
                    continue;

                if (!_cache.TryGetValue(name, out var entries))
                    _cache[name] = entries = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var entry in section.Elements(EntryElement))
                {
                    var key = (string?)entry.Attribute("key");
                    if (key is not null)
                        entries[key] = (string?)entry.Attribute("value") ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            _cache.Clear();
            CorruptFileBackupPath = BackUpCorruptFile();
            _logger.LogError(ex,
                "Settings file {Path} is corrupt; it was moved to {Backup} and default settings are used",
                SettingsFilePath, CorruptFileBackupPath ?? "(backup failed)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _cache.Clear();
            _logger.LogError(ex, "Could not read settings file {Path}; default settings are used", SettingsFilePath);
        }
    }

    private static XDocument LoadDocument(string path)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        };

        using var reader = XmlReader.Create(path, readerSettings);
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != RootElement)
            throw new InvalidDataException($"Unexpected root element '{document.Root?.Name}' (expected '{RootElement}').");
        return document;
    }

    private string? BackUpCorruptFile()
    {
        var backupPath = $"{SettingsFilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (File.Exists(backupPath))
            backupPath += $"-{Guid.NewGuid():N}";

        try
        {
            File.Move(SettingsFilePath, backupPath);
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not back up corrupt settings file {Path}", SettingsFilePath);
            return null;
        }
    }

    private XDocument BuildXml()
    {
        var root = new XElement(RootElement);
        foreach (var (sectionName, entries) in _cache.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (entries.Count == 0)
                continue;

            var section = new XElement(SectionElement, new XAttribute("name", sectionName));
            foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                section.Add(new XElement(EntryElement, new XAttribute("key", key), new XAttribute("value", value)));
            root.Add(section);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }
}
