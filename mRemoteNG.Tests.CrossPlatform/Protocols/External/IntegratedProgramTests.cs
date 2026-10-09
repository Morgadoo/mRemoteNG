using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Embedding;
using mRemoteNG.Protocols.External;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.External;

/// <summary>IntApp parameters and the platform-neutral part of finding a launched program's window.</summary>
public sealed class IntegratedProgramTests
{
    [Fact]
    public void Parameters_CarryTheToolAndTheConnectionVariables()
    {
        var info = new ConnectionInfo
        {
            Protocol = CoreProtocol.IntApp, ExtApp = "Terminal", Name = "db01", Hostname = "",
            Description = "primary", MacAddress = "00:11:22:33:44:55", UserField = "rack 4", Username = "dba", Password = "pw", Port = 8080,
        };

        var parameters = ConnectionParametersFactory.FromConnectionInfo(info);
        var variables = IntegratedProgramProtocol.BuildVariables(parameters);

        parameters.Protocol.Should().Be(ProtocolType.IntApp);
        parameters.Extras[ConnectionParametersFactory.Keys.IntAppTool].Should().Be("Terminal");
        variables.Should().BeEquivalentTo(new
        {
            Name = "db01", Hostname = "", Port = "8080", Username = "dba", Password = "pw", Domain = "",
            Description = "primary", MacAddress = "00:11:22:33:44:55", UserField = "rack 4", Protocol = "IntApp",
        });
    }

    [Fact]
    public void ProcessTree_IncludesDescendantsOnly()
    {
        var table = new[] { (10, 1), (11, 10), (12, 11), (13, 1), (14, 13), (15, 12) };

        ForeignWindowDiscovery.GetProcessTree(10, table).Should().BeEquivalentTo([10, 11, 12, 15]);
        ForeignWindowDiscovery.GetProcessTree(99, table).Should().BeEquivalentTo([99]);
    }

    private static WindowCandidate Window(nint id, int? pid, bool wmState = true, bool viewable = true, bool overrideRedirect = false,
        bool rootChild = false, int width = 400, int height = 300) =>
        new(id, pid, wmState, viewable, overrideRedirect, rootChild, width, height);

    [Fact]
    public void SelectWindow_PicksAManagedVisibleWindowOfTheProcess()
    {
        var candidates = new[]
        {
            Window(1, 500),                                  // another program
            Window(2, 42, overrideRedirect: true),           // a menu
            Window(3, 42, viewable: false),                  // not shown yet
            Window(4, 42, width: 1, height: 1),              // a leader / helper window
            Window(5, 42, wmState: false),                   // a frame-less inner window
            Window(6, 42),
        };

        ForeignWindowDiscovery.SelectWindow(candidates, new HashSet<int> { 42 }, 42).Should().Be(6);
    }

    [Fact]
    public void SelectWindowByClass_PicksANewWindowNamedLikeTheProgram()
    {
        var candidates = new[]
        {
            Window(1, null) with { WmClassInstance = "xcalc", WmClassName = "XCalc" },  // already open before launch
            Window(2, 500) with { WmClassInstance = "xcalc", WmClassName = "XCalc" },   // has a pid: not a fallback case
            Window(3, null) with { WmClassInstance = "xterm", WmClassName = "XTerm" },  // another program
            Window(4, null, viewable: false) with { WmClassInstance = "xcalc" },        // not shown yet
            Window(5, null) with { WmClassInstance = "calc", WmClassName = "XCalc" },   // class matches
        };

        ForeignWindowDiscovery.SelectWindowByClass(candidates, new HashSet<nint> { 1 }, ["xcalc"]).Should().Be(5);
        ForeignWindowDiscovery.SelectWindowByClass(candidates, new HashSet<nint> { 1 }, ["gedit"]).Should().Be(0);
    }

    [Fact]
    public void SelectWindow_WithoutAWindowManager_AcceptsRootChildren()
    {
        var candidates = new[] { Window(7, 42, wmState: false, rootChild: true) };

        ForeignWindowDiscovery.SelectWindow(candidates, new HashSet<int> { 42 }, 42).Should().Be(7);
    }

    [Fact]
    public void SelectWindow_PrefersTheLaunchedProcess_ThenTheLargestWindow()
    {
        var candidates = new[]
        {
            Window(1, 43, width: 1000, height: 1000), // child process, larger
            Window(2, 42, width: 200, height: 100),
            Window(3, 42, width: 300, height: 200),
        };

        ForeignWindowDiscovery.SelectWindow(candidates, new HashSet<int> { 42, 43 }, 42).Should().Be(3);
        ForeignWindowDiscovery.SelectWindow(candidates.Take(1), new HashSet<int> { 42, 43 }, 42).Should().Be(1);
    }

    [Fact]
    public void SelectWindow_NoMatch_IsZero() =>
        ForeignWindowDiscovery.SelectWindow([Window(1, null), Window(2, 7)], new HashSet<int> { 42 }, 42).Should().Be(0);

    [Theory]
    [InlineData("1234 (xterm) S 1000 1234 1234 0 -1", 1234, 1000)]
    [InlineData("77 (weird ) name) R 5 77 77", 77, 5)]
    public void ProcStat_IsParsed(string stat, int pid, int parentPid)
    {
        ForeignWindowDiscovery.TryParseProcStat(stat, out int parsedPid, out int parsedParent).Should().BeTrue();
        parsedPid.Should().Be(pid);
        parsedParent.Should().Be(parentPid);
    }

    [SkippableFact]
    public void ProcessTable_ContainsThisProcess()
    {
        Skip.IfNot(OperatingSystem.IsLinux());

        ForeignWindowDiscovery.ReadLinuxProcessTable().Should().Contain(p => p.Pid == Environment.ProcessId);
    }
}
