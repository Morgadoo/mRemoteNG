using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;

namespace mRemoteNG.Core.Tree.Root
{
    public class RootNodeInfo : ContainerInfo
    {
        private string _customPassword = "";

        public RootNodeType Type { get; }

        public string PasswordString
        {
            get => _customPassword;
            set => SetField(ref _customPassword, value);
        }

        public string DefaultPassword { get; set; } = "mR3m";

        public RootNodeInfo(RootNodeType type, string uniqueId = "")
            : base(uniqueId)
        {
            Type = type;
            Name = type == RootNodeType.Connection ? "Connections" : "PuTTY Sessions";
        }

        public override TreeNodeType GetTreeNodeType() => TreeNodeType.Root;
    }
}
