using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Imports a Remote Desktop Connection Manager file (.rdg). The file's own group becomes the
    /// imported folder. DPAPI-encrypted passwords cannot be read and are reported as warnings.
    /// </summary>
    public sealed class RemoteDesktopConnectionManagerImporter : IConnectionImporter
    {
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var xml = ImportHelpers.ReadFile(source);
            var deserializer = new RemoteDesktopConnectionManagerDeserializer();
            var model = deserializer.Deserialize(xml);
            return ImportHelpers.AddNodes(model.RootNode.Children, destinationContainer, deserializer.Warnings);
        }
    }
}
