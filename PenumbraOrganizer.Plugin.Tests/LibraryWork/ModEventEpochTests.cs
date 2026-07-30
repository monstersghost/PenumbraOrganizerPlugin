using PenumbraOrganizer.Plugin.LibraryWork;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class ModEventEpochTests
{
    [Fact]
    public void Current_StartsAtZero()
    {
        Assert.Equal(0, new ModEventEpoch().Current);
    }

    [Fact]
    public void Increment_AdvancesCurrent()
    {
        var epoch = new ModEventEpoch();

        epoch.Increment();
        epoch.Increment();

        Assert.Equal(2, epoch.Current);
    }

    [Fact]
    public void ConcurrentIncrements_AreNotLost()
    {
        var epoch = new ModEventEpoch();
        const int threads = 8;
        const int perThread = 1000;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                epoch.Increment();
        });

        Assert.Equal(threads * perThread, epoch.Current);
    }
}
