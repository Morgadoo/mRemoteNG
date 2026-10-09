using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>Reads connections from an external source and adds them to a folder.</summary>
    public interface IConnectionImporter
    {
        /// <summary>
        /// Reads <paramref name="source"/> (usually a file path) and adds the imported nodes to
        /// <paramref name="destinationContainer"/>. Imported nodes keep the IDs found in the source;
        /// <see cref="ConnectionImportService"/> makes them unique within the target tree.
        /// </summary>
        /// <exception cref="IOException">The source could not be read.</exception>
        /// <exception cref="InvalidDataException">The source is not in the expected format.</exception>
        ImportResult Import(string source, ContainerInfo destinationContainer);
    }
}
