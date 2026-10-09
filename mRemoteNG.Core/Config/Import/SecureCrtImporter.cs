using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>Imports a SecureCRT XML export into a folder named after the file.</summary>
    public sealed class SecureCrtImporter : IConnectionImporter
    {
        public ImportResult Import(string source, ContainerInfo destinationContainer)
        {
            ArgumentNullException.ThrowIfNull(destinationContainer);
            var xml = ImportHelpers.ReadFile(source);
            var deserializer = new SecureCrtFileDeserializer();
            var model = deserializer.Deserialize(xml);
            return ImportHelpers.AddAsFolder(source, model.RootNode.Children, destinationContainer, deserializer.Warnings);
        }
    }
}
