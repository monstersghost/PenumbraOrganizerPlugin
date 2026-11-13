using PenumbraOrganizer.Plugin.Organizer.NewMods;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NewMods;

public class NewModTrackerTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static (NewModTracker Tracker, Clock Clock) Make(params string[] known)
    {
        var clock = new Clock();
        var tracker = new NewModTracker(() => clock.Now);
        tracker.Seed(known);
        return (tracker, clock);
    }

    [Fact]
    public void NeedsSeed_UntilSeeded()
    {
        var tracker = new NewModTracker(() => DateTimeOffset.UnixEpoch);
        Assert.True(tracker.NeedsSeed);
        tracker.Seed(["a"]);
        Assert.False(tracker.NeedsSeed);
    }

    [Fact]
    public void EventsBeforeSeed_AreHeldUntilSeeded()
    {
        var clock = new Clock();
        var tracker = new NewModTracker(() => clock.Now);
        tracker.EnqueueAdded("a");
        tracker.EnqueueAdded("b");

        tracker.Seed(["a"]);
        clock.Now += TimeSpan.FromSeconds(6);

        Assert.Equal(["b"], tracker.TakeReady());
    }

    [Fact]
    public void AddOfKnownId_IsNotNew()
    {
        var (tracker, clock) = Make("a");
        tracker.EnqueueAdded("a");
        clock.Now += TimeSpan.FromSeconds(6);

        Assert.Null(tracker.TakeReady());
    }

    [Fact]
    public void AddOfUnknownId_IsNew_AndBecomesKnown()
    {
        var (tracker, clock) = Make("a");
        tracker.EnqueueAdded("b");
        clock.Now += TimeSpan.FromSeconds(6);

        Assert.Equal(["b"], tracker.TakeReady());
        Assert.Contains("b", tracker.KnownIdentifiers);

        tracker.EnqueueAdded("b");
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Null(tracker.TakeReady());
    }

    [Fact]
    public void PublishScan_ExtendsKnown()
    {
        var (tracker, clock) = Make();
        tracker.PublishScan(["x", "y"]);
        tracker.EnqueueAdded("x");
        clock.Now += TimeSpan.FromSeconds(6);

        Assert.Null(tracker.TakeReady());
    }

    [Fact]
    public void NotReadyBeforeFiveSeconds()
    {
        var (tracker, clock) = Make();
        tracker.EnqueueAdded("b");
        clock.Now += TimeSpan.FromSeconds(4);

        Assert.Null(tracker.TakeReady());
        Assert.True(tracker.HasPending);
    }

    [Fact]
    public void ANewEvent_RestartsTheClock()
    {
        var (tracker, clock) = Make();
        tracker.EnqueueAdded("b");
        clock.Now += TimeSpan.FromSeconds(4);
        tracker.EnqueueAdded("c");
        clock.Now += TimeSpan.FromSeconds(4);

        Assert.Null(tracker.TakeReady());

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(["b", "c"], tracker.TakeReady()!.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Delete_RemovesFromPending()
    {
        var (tracker, clock) = Make();
        tracker.EnqueueAdded("b");
        tracker.EnqueueDeleted("b");
        clock.Now += TimeSpan.FromSeconds(6);

        Assert.Null(tracker.TakeReady());
    }

    [Fact]
    public void Move_RenamesPending()
    {
        var (tracker, clock) = Make();
        tracker.EnqueueAdded("b");
        tracker.EnqueueMoved("b", "b2");
        clock.Now += TimeSpan.FromSeconds(6);

        Assert.Equal(["b2"], tracker.TakeReady());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryChangedAndDisposed_ClearPending_AndNeedReseed(bool disposed)
    {
        var (tracker, clock) = Make();
        tracker.EnqueueAdded("b");
        if (disposed) tracker.EnqueueDisposed(); else tracker.EnqueueDirectoryChanged();
        clock.Now += TimeSpan.FromSeconds(6);

        Assert.Null(tracker.TakeReady());
        Assert.False(tracker.HasPending);
        Assert.True(tracker.NeedsSeed);
    }

    [Fact]
    public void TakeAll_SkipsTheDebounce_AndEmpties()
    {
        var (tracker, _) = Make();
        tracker.EnqueueAdded("b");

        Assert.Equal(["b"], tracker.TakeAll());
        Assert.Empty(tracker.TakeAll());
        Assert.False(tracker.HasPending);
    }

    [Fact]
    public void Duplicates_Collapse()
    {
        var (tracker, _) = Make();
        tracker.EnqueueAdded("b");
        tracker.EnqueueAdded("b");

        Assert.Single(tracker.TakeAll());
    }

    [Fact]
    public void ConcurrentEnqueue_LosesNothing()
    {
        var (tracker, _) = Make();
        Parallel.For(0, 500, i => tracker.EnqueueAdded("m" + i));

        Assert.Equal(500, tracker.TakeAll().Count);
    }
}
