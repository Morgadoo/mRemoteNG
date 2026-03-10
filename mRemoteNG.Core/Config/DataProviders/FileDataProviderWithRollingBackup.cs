namespace mRemoteNG.Core.Config.DataProviders
{
    public class FileDataProviderWithRollingBackup : IDataProvider<string>
    {
        private readonly FileDataProvider _fileDataProvider;
        private readonly int _maxBackups;

        public string FilePath => _fileDataProvider.FilePath;

        public FileDataProviderWithRollingBackup(string filePath, int maxBackups = 5)
        {
            _fileDataProvider = new FileDataProvider(filePath);
            _maxBackups = maxBackups;
        }

        public string Load()
        {
            return _fileDataProvider.Load();
        }

        public void Save(string data)
        {
            if (File.Exists(FilePath))
                RollBackups();

            _fileDataProvider.Save(data);
        }

        private void RollBackups()
        {
            // Delete the oldest backup
            var oldestBackup = $"{FilePath}.backup{_maxBackups}";
            if (File.Exists(oldestBackup))
                File.Delete(oldestBackup);

            // Roll existing backups
            for (var i = _maxBackups - 1; i >= 1; i--)
            {
                var current = $"{FilePath}.backup{i}";
                var next = $"{FilePath}.backup{i + 1}";
                if (File.Exists(current))
                    File.Move(current, next);
            }

            // Copy current file as backup1
            File.Copy(FilePath, $"{FilePath}.backup1");
        }
    }
}
