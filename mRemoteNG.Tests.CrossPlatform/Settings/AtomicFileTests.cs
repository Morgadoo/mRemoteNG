using System.Text;
using FluentAssertions;
using mRemoteNG.Platform.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TryCreateNew_ConcurrentWriters_ExactlyOneWins_AndTheFileHoldsItsContent()
    {
        // File.Move(overwrite: false) is not exclusive on Unix (check-then-rename); several writers used to "win".
        for (var round = 0; round < 50; round++)
        {
            var path = _dir.Combine($"round{round}");
            var winners = new List<int>();

            Parallel.For(0, 16, i =>
            {
                if (AtomicFile.TryCreateNew(path, Encoding.ASCII.GetBytes($"writer {i}")))
                {
                    lock (winners)
                        winners.Add(i);
                }
            });

            winners.Should().ContainSingle($"round {round}");
            File.ReadAllText(path).Should().Be($"writer {winners[0]}");
        }

        Directory.GetFiles(_dir.Path).Should().HaveCount(50, "no temporary files may be left behind");
    }

    [Fact]
    public void TryCreateNew_LeavesAnExistingFileUntouched()
    {
        var path = _dir.Combine("existing");
        File.WriteAllText(path, "original");

        AtomicFile.TryCreateNew(path, Encoding.ASCII.GetBytes("replacement")).Should().BeFalse();

        File.ReadAllText(path).Should().Be("original");
    }
}
