namespace mRemoteNG.Core.Tree.Root
{
    public class RootPuttySessionsNodeInfo : RootNodeInfo
    {
        public RootPuttySessionsNodeInfo() : base(RootNodeType.PuttySessions)
        {
            Name = "PuTTY Sessions";
        }

        public override TreeNodeType GetTreeNodeType() => TreeNodeType.PuttyRoot;
    }
}
