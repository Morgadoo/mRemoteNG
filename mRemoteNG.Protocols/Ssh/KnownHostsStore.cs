using System.Security.Cryptography;
using System.Text;
using mRemoteNG.Core.App.Info;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>Result of looking a presented host key up in the known_hosts files.</summary>
public enum HostKeyStatus
{
    /// <summary>A stored key for this host matches.</summary>
    Trusted,

    /// <summary>No key of this type is stored for this host.</summary>
    Unknown,

    /// <summary>A different key of the same type is stored for this host.</summary>
    Mismatch,

    /// <summary>The key is listed with the <c>@revoked</c> marker.</summary>
    Revoked,
}

public enum KnownHostMarker
{
    None,
    Revoked,
    CertAuthority,
}

/// <summary>One parsed known_hosts line.</summary>
public sealed record KnownHostEntry(
    string HostPatterns,
    string KeyType,
    byte[] KeyBlob,
    KnownHostMarker Marker,
    string Source,
    bool IsReadOnly)
{
    public string Fingerprint => HostKeyInfo.ComputeFingerprint(KeyBlob);

    public bool Matches(string hostName) => KnownHostsStore.HostPatternMatches(HostPatterns, hostName);
}

public sealed record HostKeyLookup(HostKeyStatus Status, IReadOnlyList<KnownHostEntry> KnownEntries);

/// <summary>
/// OpenSSH-compatible known_hosts store. Keys accepted in mRemoteNG are written to a file in the
/// application settings directory; additional files (normally <c>~/.ssh/known_hosts</c>) are read
/// but never modified. Supports plain and hashed (<c>|1|salt|hash</c>) host fields, wildcards,
/// negation, <c>[host]:port</c> for non-standard ports and the <c>@revoked</c> marker.
/// </summary>
public sealed class KnownHostsStore
{
    public const int DefaultSshPort = 22;
    private const string HashMagic = "|1|";

    private readonly IReadOnlyList<string> _readOnlyFiles;
    private readonly object _fileLock = new();

    public KnownHostsStore(string writableFile, IEnumerable<string>? readOnlyFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writableFile);
        WritableFile = writableFile;
        _readOnlyFiles = readOnlyFiles?.ToList() ?? [];
    }

    /// <summary>The file mRemoteNG writes accepted keys to.</summary>
    public string WritableFile { get; }

    public IReadOnlyList<string> ReadOnlyFiles => _readOnlyFiles;

    /// <summary>
    /// Creates the store used by the application: <c>known_hosts</c> in the settings directory,
    /// plus the user's OpenSSH <c>~/.ssh/known_hosts</c> (read-only).
    /// </summary>
    public static KnownHostsStore CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new KnownHostsStore(
            Path.Combine(ApplicationPaths.SettingsDirectory, "known_hosts"),
            [Path.Combine(home, ".ssh", "known_hosts")]);
    }

    /// <summary>Host name as it appears in known_hosts: <c>host</c> for port 22, otherwise <c>[host]:port</c>.</summary>
    public static string FormatHost(string host, int port)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return port == DefaultSshPort || port <= 0 ? normalized : $"[{normalized}]:{port}";
    }

    /// <summary>All entries (any key type) that apply to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public IReadOnlyList<KnownHostEntry> FindEntries(string host, int port)
    {
        var name = FormatHost(host, port);
        return LoadAll().Where(e => e.Marker != KnownHostMarker.CertAuthority && e.Matches(name)).ToList();
    }

    /// <summary>Key types stored for a host, so the client can prefer them during negotiation.</summary>
    public IReadOnlyCollection<string> GetKnownKeyTypes(string host, int port) =>
        FindEntries(host, port)
            .Where(e => e.Marker == KnownHostMarker.None)
            .Select(e => e.KeyType)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Checks a presented key. Entries in the writable file take precedence over read-only files, so a
    /// key the user explicitly replaced in mRemoteNG wins over a stale one in <c>~/.ssh/known_hosts</c>.
    /// </summary>
    public HostKeyLookup Check(HostKeyInfo key)
    {
        var entries = FindEntries(key.Host, key.Port);

        if (entries.Any(e => e.Marker == KnownHostMarker.Revoked && e.KeyBlob.AsSpan().SequenceEqual(key.KeyBlob)))
            return new HostKeyLookup(HostKeyStatus.Revoked, entries);

        var sameType = entries
            .Where(e => e.Marker == KnownHostMarker.None && e.KeyType == key.KeyType)
            .ToList();

        var writable = sameType.Where(e => !e.IsReadOnly).ToList();
        var authoritative = writable.Count > 0 ? writable : sameType;

        if (authoritative.Count == 0)
            return new HostKeyLookup(HostKeyStatus.Unknown, entries);

        return authoritative.Any(e => e.KeyBlob.AsSpan().SequenceEqual(key.KeyBlob))
            ? new HostKeyLookup(HostKeyStatus.Trusted, entries)
            : new HostKeyLookup(HostKeyStatus.Mismatch, entries);
    }

    /// <summary>Appends the key to the writable file.</summary>
    public void Add(HostKeyInfo key)
    {
        lock (_fileLock)
        {
            var lines = ReadLines(WritableFile);
            lines.Add(FormatLine(key));
            WriteLines(lines);
        }
    }

    /// <summary>
    /// Removes every entry in the writable file for this host and key type, then adds the key.
    /// Read-only files are not touched; the writable file takes precedence over them.
    /// </summary>
    public void Replace(HostKeyInfo key)
    {
        var name = FormatHost(key.Host, key.Port);
        lock (_fileLock)
        {
            var kept = ReadLines(WritableFile)
                .Where(line =>
                {
                    var entry = ParseLine(line, WritableFile, false);
                    return entry is null
                        || entry.Marker != KnownHostMarker.None
                        || entry.KeyType != key.KeyType
                        || !entry.Matches(name);
                })
                .ToList();
            kept.Add(FormatLine(key));
            WriteLines(kept);
        }
    }

    public static string FormatLine(HostKeyInfo key) =>
        $"{FormatHost(key.Host, key.Port)} {key.KeyType} {key.KeyBase64}";

    // ── Parsing ───────────────────────────────────────────────────────────

    public static IReadOnlyList<KnownHostEntry> Parse(string content, string source, bool isReadOnly)
    {
        var result = new List<KnownHostEntry>();
        foreach (var line in content.Split('\n'))
        {
            var entry = ParseLine(line, source, isReadOnly);
            if (entry is not null)
                result.Add(entry);
        }
        return result;
    }

    /// <summary>Parses one known_hosts line; returns null for comments, blank or malformed lines.</summary>
    public static KnownHostEntry? ParseLine(string line, string source, bool isReadOnly)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return null;

        var fields = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        var marker = KnownHostMarker.None;
        if (fields[0].StartsWith('@'))
        {
            switch (fields[0])
            {
                case "@revoked": marker = KnownHostMarker.Revoked; break;
                case "@cert-authority": marker = KnownHostMarker.CertAuthority; break;
                default: return null;
            }
            index = 1;
        }

        if (fields.Length < index + 3)
            return null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(fields[index + 2]);
        }
        catch (FormatException)
        {
            return null;
        }

        // The type inside the blob is authoritative; skip lines where it disagrees with the type field.
        var blobType = HostKeyInfo.ReadKeyType(blob);
        if (blobType is null || blobType != fields[index + 1])
            return null;

        return new KnownHostEntry(fields[index], blobType, blob, marker, source, isReadOnly);
    }

    /// <summary>
    /// Evaluates a comma-separated host pattern list against a formatted host name
    /// (see <see cref="FormatHost"/>). A matching negated pattern (<c>!pattern</c>) excludes the host.
    /// </summary>
    public static bool HostPatternMatches(string patternList, string hostName)
    {
        if (patternList.StartsWith(HashMagic, StringComparison.Ordinal))
            return HashedHostMatches(patternList, hostName);

        bool matched = false;
        foreach (var raw in patternList.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            bool negated = raw.StartsWith('!');
            var pattern = negated ? raw[1..] : raw;
            if (!WildcardMatch(pattern.ToLowerInvariant(), hostName))
                continue;
            if (negated)
                return false;
            matched = true;
        }
        return matched;
    }

    /// <summary>Computes an OpenSSH hashed host field (<c>|1|base64(salt)|base64(HMAC-SHA1(salt, host))</c>).</summary>
    public static string HashHostName(string hostName, byte[] salt)
    {
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(hostName));
        return $"{HashMagic}{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
    }

    private static bool HashedHostMatches(string field, string hostName)
    {
        var parts = field[HashMagic.Length..].Split('|');
        if (parts.Length != 2)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(hostName));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>OpenSSH-style glob: <c>*</c> matches any run of characters, <c>?</c> exactly one.</summary>
    private static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    // ── File access ───────────────────────────────────────────────────────

    private List<KnownHostEntry> LoadAll()
    {
        var entries = new List<KnownHostEntry>();
        lock (_fileLock)
            entries.AddRange(LoadFile(WritableFile, false));
        foreach (var file in _readOnlyFiles)
            entries.AddRange(LoadFile(file, true));
        return entries;
    }

    private static IEnumerable<KnownHostEntry> LoadFile(string path, bool isReadOnly)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path), path, isReadOnly) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> ReadLines(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : [];

    private void WriteLines(IEnumerable<string> lines)
    {
        var directory = Path.GetDirectoryName(WritableFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Write to a temp file and swap it in so a crash never leaves a truncated known_hosts.
        var temp = WritableFile + ".tmp";
        File.WriteAllText(temp, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, WritableFile, overwrite: true);
    }
}
