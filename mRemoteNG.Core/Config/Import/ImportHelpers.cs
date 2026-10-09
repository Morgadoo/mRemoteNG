using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    internal static class ImportHelpers
    {
        public static string ReadFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("No file was given to import.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"The file to import does not exist: {filePath}", filePath);
            return File.ReadAllText(filePath);
        }

        /// <summary>Moves <paramref name="children"/> into a new folder named after the file and adds it to the destination.</summary>
        public static ImportResult AddAsFolder(
            string filePath,
            IEnumerable<ConnectionInfo> children,
            ContainerInfo destinationContainer,
            IReadOnlyList<string>? warnings = null)
        {
            var folder = ConnectionDefaults.NewContainer(Path.GetFileNameWithoutExtension(filePath));
            folder.AddChildRange(children.ToArray());
            destinationContainer.AddChild(folder);
            return new ImportResult([folder], warnings?.ToList());
        }

        /// <summary>Adds <paramref name="nodes"/> to the destination as they are.</summary>
        public static ImportResult AddNodes(
            IEnumerable<ConnectionInfo> nodes,
            ContainerInfo destinationContainer,
            IReadOnlyList<string>? warnings = null)
        {
            var added = nodes.ToArray();
            destinationContainer.AddChildRange(added);
            return new ImportResult(added, warnings?.ToList());
        }
    }
}
