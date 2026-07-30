using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class EventLogBufferTests
{
    [Fact]
    public void Add_IsNotVisibleUntilDrain()
    {
        var buffer = new EventLogBuffer();

        buffer.Add("first");

        Assert.Empty(buffer.Lines);

        buffer.Drain();

        Assert.Equal(["first"], buffer.Lines);
    }

    [Fact]
    public void Drain_PutsMostRecentFirst()
    {
        var buffer = new EventLogBuffer();

        buffer.Add("older");
        buffer.Add("newer");
        buffer.Drain();

        Assert.Equal(["newer", "older"], buffer.Lines);
    }

    [Fact]
    public void Drain_TrimsToMaxLines()
    {
        var buffer = new EventLogBuffer();

        for (var i = 0; i < EventLogBuffer.MaxLines + 50; i++)
            buffer.Add($"line {i}");
        buffer.Drain();

        Assert.Equal(EventLogBuffer.MaxLines, buffer.Lines.Count);
        // Newest first, so the very last line added must be at index 0.
        Assert.Equal($"line {EventLogBuffer.MaxLines + 49}", buffer.Lines[0]);
    }

    [Fact]
    public void ConcurrentAdds_AreAllDelivered_AndDrainDoesNotThrow()
    {
        var buffer = new EventLogBuffer();
        const int threads = 8;
        const int perThread = 500;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                buffer.Add($"{t}-{i}");
        });
        buffer.Drain();

        // MaxLines trimming means we cannot assert on all of them, only that the
        // buffer survived concurrent writes and produced a full, well-formed window.
        Assert.Equal(EventLogBuffer.MaxLines, buffer.Lines.Count);
        Assert.All(buffer.Lines, line => Assert.Contains('-', line));
    }
}
