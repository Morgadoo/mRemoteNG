using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Tests.CrossPlatform.Core.Export;

/// <summary>
/// Root
///  |- Prod (folder, Username "produser")
///  |   |- web (SSH2, inherits Username)
///  |   |- Databases (folder)
///  |       |- db1 (RDP, own credentials)
///  |- lab (VNC)
/// </summary>
internal sealed class ExportTestTree
{
    public RootNodeInfo Root { get; } = new(RootNodeType.Connection);
    public ContainerInfo Prod { get; }
    public ContainerInfo Databases { get; }
    public ConnectionInfo Web { get; }
    public ConnectionInfo Db1 { get; }
    public ConnectionInfo Lab { get; }

    public ExportTestTree()
    {
        Prod = ConnectionDefaults.NewContainer("Prod");
        Prod.Username = "produser";
        Prod.Description = "Production; critical";
        Root.AddChild(Prod);

        Web = ConnectionDefaults.NewConnection(ProtocolType.SSH2);
        Web.Name = "web";
        Web.Hostname = "web.example.com";
        Web.Port = 2222;
        Web.Password = "webpass";
        Web.Inheritance.Username = true;
        Prod.AddChild(Web);

        Databases = ConnectionDefaults.NewContainer("Databases");
        Prod.AddChild(Databases);

        Db1 = ConnectionDefaults.NewConnection(ProtocolType.RDP);
        Db1.Name = "db1";
        Db1.Hostname = "db1.example.com";
        Db1.Username = "dbadmin";
        Db1.Password = "dbpass";
        Db1.Domain = "CORP";
        Db1.Colors = RDPColors.Colors16Bit;
        Db1.Resolution = RDPResolutions.Res1366x768;
        Db1.RedirectPorts = true;
        Db1.RedirectAudioCapture = true;
        Db1.RDGatewayPassword = "gwpass";
        Db1.Description = "multi\nline";
        Databases.AddChild(Db1);

        Lab = ConnectionDefaults.NewConnection(ProtocolType.VNC);
        Lab.Name = "lab";
        Lab.Hostname = "lab.example.com";
        Lab.Favorite = true;
        Root.AddChild(Lab);
    }
}
