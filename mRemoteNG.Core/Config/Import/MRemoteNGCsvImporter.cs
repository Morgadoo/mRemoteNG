using mRemoteNG.Core.Config.Serializers.Csv;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>Imports an mRemoteNG CSV export into a folder named after the file.</summary>
    public sealed class MRemoteNGCsvImporter : IConnectionImporter
    {
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var csv = ImportHelpers.ReadFile(source);
            var deserializer = new CsvConnectionsDeserializerMremotengFormat();
            var model = deserializer.Deserialize(csv);
            return ImportHelpers.AddAsFolder(source, model.RootNode.Children, destinationContainer, deserializer.Warnings);
        }
    }
}
