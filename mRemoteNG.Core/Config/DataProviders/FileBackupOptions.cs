using System.Globalization;

namespace mRemoteNG.Core.Config.DataProviders
{
    /// <summary>When the connection file is backed up.</summary>
    public enum BackupFrequency
    {
        /// <summary>No backups.</summary>
        Never,

        /// <summary>Before every save, the previous version of the file is kept as a backup.</summary>
        OnSave,

        /// <summary>Once, when the application exits.</summary>
        OnExit,
    }

    /// <summary>
    /// Rolling backup settings for connection files. Backups use the legacy naming
    /// <c>{0}.{1:yyyyMMdd-HHmmssffff}.backup</c>, where {0} is the connection file (inside
    /// <see cref="BackupDirectory"/> when set) and {1} the time of the backup, e.g.
    /// <c>confCons.xml.20240131-1405590123.backup</c>.
    /// </summary>
    public sealed record FileBackupOptions
    {
        /// <summary>The legacy default (<c>OptionsBackupPage.BackupFileNameFormat</c>).</summary>
        public const string DefaultNameFormat = "{0}.{1:yyyyMMdd-HHmmssffff}.backup";

        public const int MaxKeepCount = 1000;

        /// <summary>How many backups to keep; 0 turns backups off (as in the legacy app).</summary>
        public int KeepCount { get; init; } = 10;

        /// <summary>Folder for the backups; empty means next to the connection file.</summary>
        public string BackupDirectory { get; init; } = string.Empty;

        /// <summary>Composite format: {0} = path of the file, {1} = timestamp.</summary>
        public string NameFormat { get; init; } = DefaultNameFormat;

        public bool Enabled => KeepCount > 0;

        /// <summary>Where backups of <paramref name="filePath"/> go.</summary>
        public string GetBackupDirectory(string filePath) =>
            string.IsNullOrWhiteSpace(BackupDirectory)
                ? Path.GetDirectoryName(Path.GetFullPath(filePath))!
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(BackupDirectory));

        /// <summary>The backup file name for <paramref name="filePath"/> taken at <paramref name="timestamp"/>.</summary>
        public string GetBackupPath(string filePath, DateTime timestamp) =>
            string.Format(CultureInfo.InvariantCulture, NameFormat,
                Path.Combine(GetBackupDirectory(filePath), Path.GetFileName(filePath)), timestamp);

        /// <summary>A file-system search pattern matching every backup of <paramref name="filePath"/>.</summary>
        public string GetSearchPattern(string filePath) =>
            string.Format(CultureInfo.InvariantCulture, NameFormat, Path.GetFileName(filePath), "*");
    }
}
