namespace mRemoteNG.Core.Config.DataProviders
{
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
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(FilePath, data);
        }
    }
}
