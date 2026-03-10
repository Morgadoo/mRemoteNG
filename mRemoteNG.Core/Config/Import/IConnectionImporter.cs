using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    public interface IConnectionImporter
    {
        void Import(string filePath, ContainerInfo destinationContainer);
    }
}
