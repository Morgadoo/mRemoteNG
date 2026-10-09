namespace mRemoteNG.Core.Config.DataProviders
{
    /// <summary>
    /// Copies a connection file to a timestamped backup and removes the oldest backups beyond
    /// <see cref="FileBackupOptions.KeepCount"/> (legacy <c>FileBackupCreator</c> + <c>FileBackupPruner</c>).
    /// </summary>
    public sealed class FileBackupCreator
    {
        private readonly Func<DateTime> _clock;

        public FileBackupCreator()
            : this(() => DateTime.Now)
        {
        }

        /// <summary>Test seam: <paramref name="clock"/> supplies the backup timestamp.</summary>
        public FileBackupCreator(Func<DateTime> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Backs up <paramref name="filePath"/> and prunes old backups.
        /// Returns the backup path, or null when backups are off or the file does not exist yet.
        /// </summary>
        public string? CreateBackup(string filePath, FileBackupOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(options);

            if (!options.Enabled || !File.Exists(filePath))
                return null;

            Directory.CreateDirectory(options.GetBackupDirectory(filePath));

            var timestamp = _clock();
            var backupPath = options.GetBackupPath(filePath, timestamp);
            // Two saves within the same 0.1 ms tick must not overwrite each other's backup.
            while (File.Exists(backupPath))
            {
                timestamp = timestamp.AddTicks(1000);
                backupPath = options.GetBackupPath(filePath, timestamp);
            }

            File.Copy(filePath, backupPath);
            Prune(filePath, options);
            return backupPath;
        }

        /// <summary>Deletes the oldest backups of <paramref name="filePath"/> so at most KeepCount remain. Returns the deleted files.</summary>
        public static IReadOnlyList<string> Prune(string filePath, FileBackupOptions options)
        {
            var directory = options.GetBackupDirectory(filePath);
            if (!Directory.Exists(directory))
                return [];

            // Timestamps sort chronologically by name, as in the legacy pruner.
            var toDelete = GetBackups(filePath, options)
                .Skip(Math.Max(options.KeepCount, 0))
                .ToList();

            foreach (var file in toDelete)
                File.Delete(file);
            return toDelete;
        }

        /// <summary>Existing backups of <paramref name="filePath"/>, newest first.</summary>
        public static IReadOnlyList<string> GetBackups(string filePath, FileBackupOptions options)
        {
            var directory = options.GetBackupDirectory(filePath);
            if (!Directory.Exists(directory))
                return [];

            return Directory.GetFiles(directory, options.GetSearchPattern(filePath))
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();
        }
    }
}
