using mRemoteNG.Core.Config.Serializers.Csv;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>Imports a Remote Desktop Manager CSV export into a folder named after the file.</summary>
    public sealed class RemoteDesktopManagerImporter : IConnectionImporter
    {
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var csv = ImportHelpers.ReadFile(source);
            if (string.IsNullOrWhiteSpace(csv))
                throw new InvalidDataException("The file is empty.");

            var deserializer = new CsvConnectionsDeserializerRdmFormat();
            var model = deserializer.Deserialize(csv);
            return ImportHelpers.AddAsFolder(source, model.RootNode.Children, destinationContainer, deserializer.Warnings);
        }
    }
}
