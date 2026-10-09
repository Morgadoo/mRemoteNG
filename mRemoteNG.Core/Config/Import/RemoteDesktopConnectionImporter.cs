using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>Imports a Microsoft .rdp file as one RDP connection named after the file.</summary>
    public sealed class RemoteDesktopConnectionImporter : IConnectionImporter
    {
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var content = ImportHelpers.ReadFile(source);
            var model = new RemoteDesktopConnectionDeserializer().Deserialize(content);

            var connection = model.RootNode.Children.Single();
            if (string.IsNullOrEmpty(connection.Hostname))
                throw new InvalidDataException("The .rdp file has no \"full address\" setting.");

            connection.Name = Path.GetFileNameWithoutExtension(source);
            return ImportHelpers.AddNodes([connection], destinationContainer);
        }
    }
}
