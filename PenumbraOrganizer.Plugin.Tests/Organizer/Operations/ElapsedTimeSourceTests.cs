using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class ElapsedTimeSourceTests
{
    [Fact]
    public void GetElapsedTime_ReturnsNonNegativeSpanForAPriorTimestamp()
    {
        var clock = new StopwatchElapsedTimeSource();

        var start = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(start);

        Assert.True(elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void GetElapsedTime_IncreasesAcrossTwoReadingsFromTheSameStart()
    {
        var clock = new StopwatchElapsedTimeSource();

        var start = clock.GetTimestamp();
        var first = clock.GetElapsedTime(start);
        // Busy-wait a tiny amount without sleeping the test thread on a timer.
        var spin = clock.GetTimestamp();
        while (clock.GetElapsedTime(spin) < TimeSpan.FromMilliseconds(1)) { }
        var second = clock.GetElapsedTime(start);

        Assert.True(second >= first);
    }
}
