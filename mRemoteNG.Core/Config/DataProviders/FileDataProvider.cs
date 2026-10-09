namespace mRemoteNG.Core.Config.DataProviders
{
    /// <summary>Reads and writes a text file. Writes go to a temp file first and are then renamed into place.</summary>
    public class FileDataProvider : IDataProvider<string>
    {
        public string FilePath { get; }

        public FileDataProvider(string filePath)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public string Load()
        {
            return File.ReadAllText(FilePath);
        }

        public void Save(string data)
        {
            // Write to a temp file first so a crash mid-write never truncates the user's file.
            var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, data);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
    }
}
