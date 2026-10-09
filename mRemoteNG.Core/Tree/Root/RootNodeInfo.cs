using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;

namespace mRemoteNG.Core.Tree.Root
{
    public class RootNodeInfo : ContainerInfo
    {
        private string _customPassword = "";

        public RootNodeType Type { get; }

        /// <summary>
        /// The key used to encrypt the connection file. Returns <see cref="DefaultPassword"/>
        /// unless a custom master password has been set.
        /// </summary>
        public string PasswordString
        {
            get => IsPasswordProtected ? _customPassword : DefaultPassword;
            set => SetField(ref _customPassword, value ?? "");
        }

        /// <summary>True when the file is protected by a user-supplied master password.</summary>
        public bool IsPasswordProtected =>
            !string.IsNullOrEmpty(_customPassword) && _customPassword != DefaultPassword;

        public string DefaultPassword { get; } = "mR3m";

        public RootNodeInfo(RootNodeType type, string uniqueId = "")
            : base(uniqueId)
        {
            Type = type;
            Name = type == RootNodeType.Connection ? "Connections" : "PuTTY Sessions";
        }

        public override TreeNodeType GetTreeNodeType() => TreeNodeType.Root;
    }
}
