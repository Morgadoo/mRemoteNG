namespace mRemoteNG.Platform.Settings;

/// <summary>
/// Decides where mRemoteNG keeps its data (settings, credentials, key file, known_hosts, logs, themes).
/// Normally each platform's settings provider picks the per-user directory. In portable mode
/// everything lives next to the executable instead, like the legacy portable edition
/// (which used the executable directory as its settings path).
/// <para>
/// Portable mode is enabled by a <see cref="PortableMarkerFileName"/> file in the executable directory
/// or by the <c>--portable</c> command-line switch; call <see cref="Initialize"/> once at startup,
/// before any settings provider is created.
/// </para>
/// </summary>
public static class AppDataLocation
{
    /// <summary>An (empty) file with this name next to the executable turns on portable mode.</summary>
    public const string PortableMarkerFileName = "mRemoteNG.portable";

    private static readonly object Sync = new();
    private static string? _overrideDirectory;

    /// <summary>True when data is kept in <see cref="OverrideDirectory"/> (portable mode).</summary>
    public static bool IsPortable
    {
        get
        {
            lock (Sync)
                return _overrideDirectory is not null;
        }
    }

    /// <summary>The portable data directory, or null when the per-user directory is used.</summary>
    public static string? OverrideDirectory
    {
        get
        {
            lock (Sync)
                return _overrideDirectory;
        }
    }

    /// <summary>True when <paramref name="executableDirectory"/> contains the portable marker file.</summary>
    public static bool HasPortableMarker(string executableDirectory) =>
        !string.IsNullOrWhiteSpace(executableDirectory)
        && File.Exists(Path.Combine(executableDirectory, PortableMarkerFileName));

    /// <summary>
    /// Enables portable mode when <paramref name="portableSwitch"/> is set or the marker file exists
    /// in <paramref name="executableDirectory"/>. Returns true when portable mode is active afterwards.
    /// </summary>
    public static bool Initialize(string executableDirectory, bool portableSwitch)
    {
        if (portableSwitch || HasPortableMarker(executableDirectory))
            UsePortableDirectory(executableDirectory);
        return IsPortable;
    }

    /// <summary>Keeps all data in <paramref name="directory"/>.</summary>
    public static void UsePortableDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (Sync)
            _overrideDirectory = Path.GetFullPath(directory);
    }

    /// <summary>Returns to the per-user data directory (used by tests).</summary>
    public static void Reset()
    {
        lock (Sync)
            _overrideDirectory = null;
    }

    /// <summary>The portable directory when active, otherwise <paramref name="platformDefault"/>().</summary>
    public static string Resolve(Func<string> platformDefault)
    {
        ArgumentNullException.ThrowIfNull(platformDefault);
        return OverrideDirectory ?? platformDefault();
    }
}
