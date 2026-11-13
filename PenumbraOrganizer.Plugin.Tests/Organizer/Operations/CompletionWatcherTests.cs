using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class CompletionWatcherTests
{
    private static (CompletionWatcher W, Func<int> Calls, Action<bool> SetAccept) Make()
    {
        var calls = 0;
        var accept = true;
        var w = new CompletionWatcher(() => { calls++; return accept; });
        return (w, () => calls, v => accept = v);
    }

    [Fact]
    public void Generation_Is_Consumed_Once()
    {
        var (w, calls, _) = Make();
        w.Update(1, OperationType.Apply);
        w.Update(1, OperationType.Apply);
        w.Update(1, OperationType.Apply);
        Assert.Equal(1, calls());
    }

    [Theory]
    [InlineData(OperationType.Apply)]
    [InlineData(OperationType.Restore)]
    public void Apply_And_Restore_Request_A_Rescan(OperationType kind)
    {
        var (w, calls, _) = Make();
        w.Update(1, kind);
        Assert.Equal(1, calls());
    }

    [Fact]
    public void No_Kind_Does_Not_Request_A_Rescan()
    {
        var (w, calls, _) = Make();
        w.Update(1, null);
        Assert.Equal(0, calls());
    }

    [Fact]
    public void Refused_Rescan_Is_Retried_Until_It_Starts()
    {
        var (w, calls, setAccept) = Make();
        setAccept(false);
        w.Update(1, OperationType.Apply);
        w.Update(1, OperationType.Apply);
        Assert.Equal(2, calls());
        setAccept(true);
        w.Update(1, OperationType.Apply);
        Assert.Equal(3, calls());
        w.Update(1, OperationType.Apply);
        Assert.Equal(3, calls());
    }

    [Fact]
    public void Two_Completions_Request_Two_Rescans()
    {
        var (w, calls, _) = Make();
        w.Update(1, OperationType.Apply);
        w.Update(2, OperationType.Apply);
        Assert.Equal(2, calls());
    }
}
