using FluentAssertions;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Platform;

/// <summary>
/// Integration tests for ISettingsProvider implementations.
/// Uses a temp directory so tests don't pollute real user settings.
/// </summary>
public sealed class SettingsProviderTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mremoteng-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void GetValue_ShouldReturnDefault_WhenKeyNotSet()
    {
        var provider = CreateProvider();
        int result = provider.GetValue("missing.key", 42);
        result.Should().Be(42);
    }

    [Fact]
    public void SetValue_GetValue_ShouldRoundtrip_String()
    {
        var provider = CreateProvider();
        provider.SetValue("test.string", "hello world");
        provider.GetValue("test.string", string.Empty).Should().Be("hello world");
    }

    [Fact]
    public void SetValue_GetValue_ShouldRoundtrip_Int()
    {
        var provider = CreateProvider();
        provider.SetValue("test.int", 12345);
        provider.GetValue("test.int", 0).Should().Be(12345);
    }

    [Fact]
    public void SetValue_GetValue_ShouldRoundtrip_Bool()
    {
        var provider = CreateProvider();
        provider.SetValue("flags.enabled", true);
        provider.GetValue("flags.enabled", false).Should().BeTrue();
    }

    [Fact]
    public void Save_And_Reload_ShouldPersistValues()
    {
        var provider = CreateProvider();
        provider.SetValue("persistent.key", "saved-value");
        provider.Save();

        // Simulate restart: new instance, same file
        var provider2 = CreateProvider();
        provider2.Reload();
        provider2.GetValue("persistent.key", string.Empty).Should().Be("saved-value");
    }

    [Fact]
    public void RemoveValue_ShouldReturnDefault_AfterRemoval()
    {
        var provider = CreateProvider();
        provider.SetValue("to.remove", "temporary");
        provider.RemoveValue("to.remove");
        provider.GetValue("to.remove", "default").Should().Be("default");
    }

    [Fact]
    public void GetKeys_ShouldListAllStoredKeys()
    {
        var provider = CreateProvider();
        provider.SetValue("alpha", "a");
        provider.SetValue("beta", "b");
        provider.SetValue("gamma", "c");

        var keys = provider.GetKeys();
        keys.Should().Contain(["alpha", "beta", "gamma"]);
    }

    [Fact]
    public void SettingsFilePath_ShouldBeUnderApplicationDataDirectory()
    {
        var provider = CreateProvider();
        provider.SettingsFilePath.Should().StartWith(provider.ApplicationDataDirectory);
    }

    private mRemoteNG.Platform.ISettingsProvider CreateProvider()
    {
        // Use a simple portable XML provider for cross-platform tests
        return new TestXmlSettingsProvider(_tempDir);
    }

    public void Dispose() =>
        Directory.Delete(_tempDir, recursive: true);
}

/// <summary>
/// Minimal in-process settings provider backed by an in-memory dict + XML file,
/// used for testing without referencing platform-specific implementations.
/// </summary>
internal sealed class TestXmlSettingsProvider : mRemoteNG.Platform.ISettingsProvider
{
    private readonly Dictionary<string, string> _values = new();
    private readonly string _dir;

    public TestXmlSettingsProvider(string dir) => _dir = dir;

    public string ApplicationDataDirectory => _dir;
    public string SettingsFilePath => Path.Combine(_dir, "settings.xml");

    public T GetValue<T>(string key, T defaultValue)
    {
        if (!_values.TryGetValue(key, out string? raw)) return defaultValue;
        try { return (T)Convert.ChangeType(raw, typeof(T)); }
        catch { return defaultValue; }
    }

    public void SetValue<T>(string key, T value) =>
        _values[key] = value?.ToString() ?? string.Empty;

    public void RemoveValue(string key) => _values.Remove(key);
    public IEnumerable<string> GetKeys() => _values.Keys;

    public void Save()
    {
        var doc = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XElement("settings",
                _values.Select(kv =>
                    new System.Xml.Linq.XElement("entry",
                        new System.Xml.Linq.XAttribute("key", kv.Key),
                        new System.Xml.Linq.XAttribute("value", kv.Value)))));
        doc.Save(SettingsFilePath);
    }

    public void Reload()
    {
        _values.Clear();
        if (!File.Exists(SettingsFilePath)) return;
        var doc = System.Xml.Linq.XDocument.Load(SettingsFilePath);
        foreach (var e in doc.Root?.Elements("entry") ?? [])
        {
            string key = e.Attribute("key")?.Value ?? string.Empty;
            string value = e.Attribute("value")?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(key)) _values[key] = value;
        }
    }
}
