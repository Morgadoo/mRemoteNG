namespace mRemoteNG.Core.Config.DataProviders
{
    public interface IDataProvider<T>
    {
        T Load();
        void Save(T data);
    }
}
