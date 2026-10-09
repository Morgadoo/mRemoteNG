using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

internal static class ImportTestHelpers
{
    public static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Resources", .. parts]);

    public static RootNodeInfo NewRoot() => new(RootNodeType.Connection);

    public static ContainerInfo Folder(IEnumerable<ConnectionInfo> nodes, string name) =>
        nodes.OfType<ContainerInfo>().Single(n => n.Name == name);

    public static ConnectionInfo Connection(IEnumerable<ConnectionInfo> nodes, string name) =>
        nodes.Single(n => n is not ContainerInfo && n.Name == name);
}
