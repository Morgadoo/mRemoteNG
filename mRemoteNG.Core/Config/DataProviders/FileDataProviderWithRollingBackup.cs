namespace mRemoteNG.Core.Config.DataProviders
{
    /// <summary>
    /// File provider that keeps rolling backups: before the file is overwritten, the current version is
    /// copied to a timestamped backup (legacy naming) and the oldest backups beyond the configured count
    /// are deleted. Writes are atomic (temp file + rename), so a crash never truncates the file.
    /// </summary>
    public class FileDataProviderWithRollingBackup : IDataProvider<string>
    {
        private readonly FileDataProvider _fileDataProvider;
        private readonly FileBackupCreator _backupCreator;

        public string FilePath => _fileDataProvider.FilePath;

        public FileBackupOptions Options { get; }

        /// <summary>The backup made by the last <see cref="Save"/>, if any.</summary>
        public string? LastBackupPath { get; private set; }

        public FileDataProviderWithRollingBackup(string filePath, int maxBackups = 10)
            : this(filePath, new FileBackupOptions { KeepCount = maxBackups })
        {
        }

        public FileDataProviderWithRollingBackup(string filePath, FileBackupOptions options, FileBackupCreator? backupCreator = null)
        {
            _fileDataProvider = new FileDataProvider(filePath);
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _backupCreator = backupCreator ?? new FileBackupCreator();
        }

        public string Load()
        {
            return _fileDataProvider.Load();
        }

        public void Save(string data)
        {
            LastBackupPath = _backupCreator.CreateBackup(FilePath, Options);
            _fileDataProvider.Save(data);
        }
    }
}
