using FluentAssertions;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

/// <summary>Search, structural edits and dirty tracking on the Core connection tree.</summary>
public class ConnectionTreeTests
{
    private readonly RootNodeInfo _root = new(RootNodeType.Connection);
    private readonly ContainerInfo _servers = new() { Name = "Servers" };
    private readonly ContainerInfo _linux = new() { Name = "Linux", IsExpanded = false };
    private readonly ConnectionInfo _web = new() { Name = "web01", Hostname = "10.0.0.5", Description = "Front end" };
    private readonly ConnectionInfo _db = new() { Name = "db01", Hostname = "DB.corp.local" };
    private readonly ConnectionInfo _desktop = new() { Name = "desktop", Hostname = "pc1" };

    public ConnectionTreeTests()
    {
        _root.AddChild(_servers);
        _root.AddChild(_desktop);
        _servers.AddChild(_linux);
        _linux.AddChild(_web);
        _linux.AddChild(_db);
    }

    // ── Search ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WEB01")]          // name, case-insensitive
    [InlineData("10.0.0")]         // hostname
    [InlineData("front")]          // description
    public void Matches_NameHostnameOrDescription_CaseInsensitive(string filter)
    {
        ConnectionTreeSearch.Matches(_web, filter).Should().BeTrue();
        ConnectionTreeSearch.Matches(_desktop, filter).Should().BeFalse();
    }

    [Fact]
    public void Filter_KeepsAncestorsVisibleAndExpanded()
    {
        var result = ConnectionTreeSearch.Filter(_root, "corp.local");

        result.Matches.Should().BeEquivalentTo(new[] { _db });
        result.Visible.Should().Contain(new ConnectionInfo[] { _root, _servers, _linux, _db });
        result.Visible.Should().NotContain(new[] { _web, _desktop });
        result.ContainersToExpand.Should().Contain(new ContainerInfo[] { _linux, _servers, _root });
    }

    [Fact]
    public void Filter_MatchingFolder_ShowsItsContent()
    {
        var result = ConnectionTreeSearch.Filter(_root, "linux");

        result.Visible.Should().Contain(new[] { _web, _db });
        result.Visible.Should().NotContain(_desktop);
    }

    [Fact]
    public void Filter_BlankFilter_EverythingVisibleNothingExpanded()
    {
        var result = ConnectionTreeSearch.Filter(_root, "  ");

        result.Visible.Should().HaveCount(6);
        result.ContainersToExpand.Should().BeEmpty();
    }

    // ── Structural edits ──────────────────────────────────────────────────

    [Fact]
    public void Duplicate_Connection_NewIdSuffixAndPlacedBelowOriginal()
    {
        _web.Username = "admin";

        var copy = ConnectionTreeOperations.Duplicate(_web);

        copy.ConstantID.Should().NotBe(_web.ConstantID);
        copy.Name.Should().Be("web01 (copy)");
        copy.Hostname.Should().Be("10.0.0.5");
        copy.Username.Should().Be("admin");
        copy.Parent.Should().BeSameAs(_linux);
        _linux.Children.Should().ContainInOrder(_web, copy, _db);
    }

    [Fact]
    public void Duplicate_Folder_DeepCopiesWithNewIds()
    {
        var copy = (ContainerInfo)ConnectionTreeOperations.Duplicate(_linux);

        copy.Name.Should().Be("Linux (copy)");
        copy.Children.Select(c => c.Name).Should().Equal("web01", "db01");
        copy.Children.Select(c => c.ConstantID).Should().NotIntersectWith(new[] { _web.ConstantID, _db.ConstantID });
        _linux.Children.Should().HaveCount(2, "the original folder is unchanged");
    }

    [Fact]
    public void Duplicate_Root_Throws()
    {
        var act = () => ConnectionTreeOperations.Duplicate(_root);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MoveUpAndDown_ReorderSiblings()
    {
        ConnectionTreeOperations.MoveUp(_db).Should().BeTrue();
        _linux.Children.Should().Equal(_db, _web);
        ConnectionTreeOperations.MoveUp(_db).Should().BeFalse("it is already first");

        ConnectionTreeOperations.MoveDown(_db).Should().BeTrue();
        _linux.Children.Should().Equal(_web, _db);
        ConnectionTreeOperations.MoveDown(_db).Should().BeFalse("it is already last");
    }

    [Fact]
    public void CanMoveInto_RejectsRootSelfAndDescendants()
    {
        ConnectionTreeOperations.CanMoveInto(_root, _servers).Should().BeFalse();
        ConnectionTreeOperations.CanMoveInto(_servers, _servers).Should().BeFalse();
        ConnectionTreeOperations.CanMoveInto(_servers, _linux).Should().BeFalse();
        ConnectionTreeOperations.CanMoveInto(_linux, _root).Should().BeTrue();
        ConnectionTreeOperations.CanMoveInto(_desktop, _linux).Should().BeTrue();
    }

    [Fact]
    public void MoveInto_OtherFolder_ReparentsAtIndex()
    {
        ConnectionTreeOperations.MoveInto(_desktop, _linux, 1);

        _desktop.Parent.Should().BeSameAs(_linux);
        _linux.Children.Should().Equal(_web, _desktop, _db);
        _root.Children.Should().NotContain(_desktop);
    }

    [Fact]
    public void MoveInto_Descendant_Throws()
    {
        var act = () => ConnectionTreeOperations.MoveInto(_servers, _linux);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MoveAbove_WorksAcrossAndWithinFolders()
    {
        var extra = new ConnectionInfo { Name = "extra" };
        _linux.AddChild(extra); // web, db, extra

        ConnectionTreeOperations.MoveAbove(extra, _web);
        _linux.Children.Should().Equal(extra, _web, _db);

        ConnectionTreeOperations.MoveAbove(extra, _db);
        _linux.Children.Should().Equal(_web, extra, _db);

        ConnectionTreeOperations.MoveAbove(_desktop, _db);
        _linux.Children.Should().Equal(_web, extra, _desktop, _db);
    }

    [Fact]
    public void CountDescendants_IsRecursive()
    {
        ConnectionTreeOperations.CountDescendants(_root).Should().Be(5);
        ConnectionTreeOperations.CountDescendants(_linux).Should().Be(2);
    }

    // ── Dirty tracking ────────────────────────────────────────────────────

    [Fact]
    public void Tracker_NestedPropertyChange_MarksDirty()
    {
        using var tracker = new ConnectionTreeChangeTracker(_root);
        var raised = 0;
        tracker.DirtyChanged += (_, _) => raised++;

        tracker.IsDirty.Should().BeFalse();
        _db.Hostname = "db02";

        tracker.IsDirty.Should().BeTrue();
        raised.Should().Be(1);
    }

    [Fact]
    public void Tracker_ExpandingAFolder_IsNotAChange()
    {
        using var tracker = new ConnectionTreeChangeTracker(_root);

        _linux.IsExpanded = !_linux.IsExpanded;

        tracker.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Tracker_StructureChanges_MarkDirty_AndMarkCleanResets()
    {
        using var tracker = new ConnectionTreeChangeTracker(_root);

        ConnectionTreeOperations.MoveUp(_db);
        tracker.IsDirty.Should().BeTrue();

        tracker.MarkClean();
        tracker.IsDirty.Should().BeFalse();

        _linux.RemoveChild(_web);
        tracker.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Tracker_AfterDispose_IgnoresChanges()
    {
        var tracker = new ConnectionTreeChangeTracker(_root);
        tracker.Dispose();

        _web.Name = "renamed";

        tracker.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Tracker_LoadedLegacyFile_StartsClean_AndTracksEdits()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "confCons_v2_6.xml");
        var service = new ConnectionsService(new CryptoProviderFactory());
        var model = service.LoadFromFile(path);

        using var tracker = new ConnectionTreeChangeTracker(model.RootNode);
        tracker.IsDirty.Should().BeFalse();

        model.GetRecursiveChildList().First(c => c is not ContainerInfo).Description = "changed";
        tracker.IsDirty.Should().BeTrue();
    }
}
