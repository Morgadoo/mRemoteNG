namespace mRemoteNG.Core.Connection
{
    public interface IHasParent
    {
        Container.ContainerInfo? Parent { get; }

        void SetParent(Container.ContainerInfo containerInfo);

        void RemoveParent();
    }
}
