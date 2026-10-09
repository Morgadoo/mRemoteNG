using System.ComponentModel;
using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Platform;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

/// <summary>Core logic behind the connection tree commands (sort, expand, inheritance, connect targets, PuTTY).</summary>
public class TreeCommandsTests
{
    private static (RootNodeInfo Root, ContainerInfo Folder) BuildTree()
    {
        var root = new RootNodeInfo(RootNodeType.Connection);
        var folder = new ContainerInfo { Name = "b-folder" };
        root.AddChildRange([new ConnectionInfo { Name = "Server10" }, folder, new ConnectionInfo { Name = "server2" }]);
        folder.AddChildRange([new ConnectionInfo { Name = "z" }, new ConnectionInfo { Name = "A" }]);
        return (root, folder);
    }

    [Fact]
    public void SortRecursive_Ascending_IsCaseInsensitiveAndNatural()
    {
        var (root, folder) = BuildTree();

        ConnectionTreeOperations.SortRecursive(root);

        root.Children.Select(c => c.Name).Should().Equal("b-folder", "server2", "Server10");
        folder.Children.Select(c => c.Name).Should().Equal("A", "z");
    }

    [Fact]
    public void SortRecursive_Descending_SortsSubfoldersToo()
    {
        var (root, folder) = BuildTree();

        ConnectionTreeOperations.SortRecursive(root, ListSortDirection.Descending);

        root.Children.Select(c => c.Name).Should().Equal("Server10", "server2", "b-folder");
        folder.Children.Select(c => c.Name).Should().Equal("z", "A");
    }

    [Fact]
    public void SetExpandedRecursive_ChangesEveryFolder()
    {
        var (root, folder) = BuildTree();
        var sub = new ContainerInfo { IsExpanded = true };
        folder.AddChild(sub);

        ConnectionTreeOperations.SetExpandedRecursive(root, false);
        new[] { root, folder, sub }.Should().OnlyContain(c => !c.IsExpanded);

        ConnectionTreeOperations.SetExpandedRecursive(root, true);
        new[] { root, folder, sub }.Should().OnlyContain(c => c.IsExpanded);
    }

    [Fact]
    public void ApplyInheritanceToChildren_CopiesFlagsRecursively()
    {
        var (_, folder) = BuildTree();
        var sub = new ContainerInfo();
        var deep = new ConnectionInfo();
        folder.AddChild(sub);
        sub.AddChild(deep);
        folder.Inheritance.Username = true;
        folder.Inheritance.Resolution = true;

        var count = ConnectionTreeOperations.ApplyInheritanceToChildren(folder);

        count.Should().Be(4);
        foreach (var node in folder.GetRecursiveChildList())
        {
            node.Inheritance.Username.Should().BeTrue();
            node.Inheritance.Resolution.Should().BeTrue();
            node.Inheritance.Password.Should().BeFalse();
            node.Inheritance.Parent.Should().BeSameAs(node);
        }
    }

    [Fact]
    public void ConnectionsToOpen_FolderOpensEveryConnectionBelowIt()
    {
        var (root, folder) = BuildTree();
        var sub = new ContainerInfo();
        var deep = new ConnectionInfo { Name = "deep" };
        folder.AddChild(sub);
        sub.AddChild(deep);

        ConnectionTreeOperations.ConnectionsToOpen(folder).Select(c => c.Name).Should().Equal("z", "A", "deep");
        ConnectionTreeOperations.ConnectionsToOpen(deep).Should().ContainSingle().Which.Should().BeSameAs(deep);
        ConnectionTreeOperations.ConnectionsToOpen(root).Should().HaveCount(5);
    }

    [Fact]
    public void SshTunnelCandidates_ListSshConnectionsExceptSelf()
    {
        var root = new RootNodeInfo(RootNodeType.Connection);
        var jump = new ConnectionInfo { Name = "jump", Protocol = ProtocolType.SSH2 };
        var rdp = new ConnectionInfo { Name = "desktop", Protocol = ProtocolType.RDP };
        var folder = new ContainerInfo { Name = "ssh-folder", Protocol = ProtocolType.SSH2 };
        folder.AddChild(new ConnectionInfo { Name = "bastion", Protocol = ProtocolType.SSH1 });
        root.AddChildRange([jump, rdp, folder]);

        ConnectionTreeOperations.SshTunnelCandidates(root).Should().Equal("bastion", "jump");
        ConnectionTreeOperations.SshTunnelCandidates(root, jump).Should().Equal("bastion");
    }

    [Fact]
    public void PanelNames_AreDistinctAndIncludeGeneral()
    {
        var root = new RootNodeInfo(RootNodeType.Connection);
        root.AddChildRange([new ConnectionInfo { Panel = "Prod" }, new ConnectionInfo { Panel = "Prod" }, new ConnectionInfo { Panel = "" }]);

        ConnectionTreeOperations.PanelNames(root).Should().Equal("General", "Prod");
    }

    [Fact]
    public void PuttySession_MapsToAConnection()
    {
        var info = new PuttySessionInfo(new PuttySession("Jump Host", "jump.example.org", 2222, "ops", "ssh"));

        info.GetTreeNodeType().Should().Be(TreeNodeType.PuttySession);
        info.Protocol.Should().Be(ProtocolType.SSH2);
        info.Port.Should().Be(2222);
        info.PuttySession.Should().Be("Jump Host");

        var copy = info.ToConnection();
        copy.Should().NotBeOfType<PuttySessionInfo>();
        copy.ConstantID.Should().NotBe(info.ConstantID);
        (copy.Name, copy.Hostname, copy.Username, copy.PuttySession).Should().Be(("Jump Host", "jump.example.org", "ops", "Jump Host"));

        new PuttySessionInfo(new PuttySession("sw", "10.0.0.5", 0, "", "telnet")).Port.Should().Be(23);
        new RootPuttySessionsNodeInfo().GetTreeNodeType().Should().Be(TreeNodeType.PuttyRoot);
    }
}
