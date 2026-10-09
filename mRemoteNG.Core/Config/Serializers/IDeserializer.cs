namespace mRemoteNG.Core.Config.Serializers
{
    public interface IDeserializer<in TIn, out TOut>
    {
        TOut Deserialize(TIn data);
    }
}
