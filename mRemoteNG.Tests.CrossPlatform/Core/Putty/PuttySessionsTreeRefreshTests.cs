using FluentAssertions;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Platform;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Putty;

public class PuttySessionsTreeRefreshTests
{
    private sealed class BlockingProvider : IPuttySessionsProvider
    {
        public TaskCompletionSource<IReadOnlyList<PuttySession>> Result { get; } = new();

        public Task<IReadOnlyList<PuttySession>> GetSessionsAsync() => Result.Task;
    }

    [Fact]
    public async Task RefreshStartedBeforeANewerApply_DoesNotOverwriteIt()
    {
        var provider = new BlockingProvider();
        var tree = new PuttySessionsTree(new PuttySessionCatalog(provider));

        var refresh = tree.RefreshAsync();
        tree.Apply([new PuttySession("newer", "new.example", 22, "", "ssh")]);
        provider.Result.SetResult([new PuttySession("stale", "old.example", 22, "", "ssh")]);

        (await refresh).Should().BeFalse();
        tree.Root.Children.Select(c => c.Name).Should().Equal("newer");
    }

    [Fact]
    public async Task Refresh_AppliesSessionsWhenNothingNewerHappened()
    {
        var provider = new BlockingProvider();
        var tree = new PuttySessionsTree(new PuttySessionCatalog(provider));

        var refresh = tree.RefreshAsync();
        provider.Result.SetResult([new PuttySession("s", "h.example", 22, "", "ssh")]);

        (await refresh).Should().BeTrue();
        tree.Root.Children.Select(c => c.Name).Should().Equal("s");
    }
}
