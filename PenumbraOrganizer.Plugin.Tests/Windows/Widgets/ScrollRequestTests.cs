using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Tests.Windows.Widgets;

/// <summary>The one-shot "scroll to this and open it" request behind the Recommended screen's Show rules button.</summary>
public class ScrollRequestTests
{
    [Fact]
    public void Idle_NeverScrollsOrOpens()
    {
        var request = new ScrollRequest();

        Assert.False(request.Pending);
        Assert.False(request.TryConsume(drawn: true, y: 100f));
        Assert.False(request.TakeOpen());
    }

    [Fact]
    public void KeepsScrolling_UntilTheTargetIsDrawnTwiceAtTheSamePlace()
    {
        var request = new ScrollRequest();
        request.Request();

        Assert.True(request.Pending);
        Assert.True(request.TryConsume(drawn: true, y: 120f)); // the click frame: the Advanced card is not there yet
        Assert.True(request.TryConsume(drawn: true, y: 140f)); // the Advanced card's first, short frame
        Assert.True(request.TryConsume(drawn: true, y: 600f)); // its full height
        Assert.True(request.TryConsume(drawn: true, y: 600f)); // settled: scrolls once more, then ends
        Assert.False(request.Pending);
        Assert.False(request.TryConsume(drawn: true, y: 600f));
    }

    [Fact]
    public void SubPixelMovement_CountsAsSettled()
    {
        var request = new ScrollRequest();
        request.Request();

        Assert.True(request.TryConsume(drawn: true, y: 600f));
        Assert.True(request.TryConsume(drawn: true, y: 600.25f));
        Assert.False(request.Pending);
    }

    [Fact]
    public void ATargetThatNeverSettles_StopsAfterMaxFrames()
    {
        var request = new ScrollRequest();
        request.Request();

        for (var frame = 0; frame < ScrollRequest.MaxFrames; frame++)
            Assert.True(request.TryConsume(drawn: true, y: frame * 10f));

        Assert.False(request.Pending);
        Assert.False(request.TryConsume(drawn: true, y: 999f));
    }

    [Fact]
    public void FramesWithoutTheTarget_UseUpTheRequest_AndNeverScroll()
    {
        var request = new ScrollRequest();
        request.Request();

        for (var frame = 0; frame < ScrollRequest.MaxFrames; frame++)
            Assert.False(request.TryConsume(drawn: false, y: 0f));

        // Switched back to Advanced much later: no surprise scroll.
        Assert.False(request.TryConsume(drawn: true, y: 600f));
        Assert.False(request.TakeOpen());
    }

    [Fact]
    public void ANotDrawnFrame_DoesNotCountAsAPosition()
    {
        var request = new ScrollRequest();
        request.Request();

        Assert.True(request.TryConsume(drawn: true, y: 600f));
        Assert.False(request.TryConsume(drawn: false, y: 0f));
        Assert.True(request.TryConsume(drawn: true, y: 600f)); // same place as the last DRAWN frame: settled
        Assert.False(request.Pending);
    }

    [Fact]
    public void TakeOpen_IsTrueOncePerRequest()
    {
        var request = new ScrollRequest();
        request.Request();

        Assert.True(request.TakeOpen());
        Assert.False(request.TakeOpen());

        request.Request();
        Assert.True(request.TakeOpen());
    }

    [Fact]
    public void TakeOpen_StillWorksOnTheFrameTheRequestSettles()
    {
        // In a frame the position is reported (before the card) before the header asks to open (inside it).
        var request = new ScrollRequest();
        request.Request();

        Assert.True(request.TryConsume(drawn: true, y: 600f));
        Assert.True(request.TryConsume(drawn: true, y: 600f));
        Assert.False(request.Pending);
        Assert.True(request.TakeOpen());
    }

    [Fact]
    public void AnOpenNeverTaken_IsDroppedTheFrameAfterTheRequestEnds()
    {
        var request = new ScrollRequest();
        request.Request();

        Assert.True(request.TryConsume(drawn: true, y: 600f));
        Assert.True(request.TryConsume(drawn: true, y: 600f));
        Assert.False(request.TryConsume(drawn: true, y: 600f));
        Assert.False(request.TakeOpen());
    }

    [Fact]
    public void ANewRequest_ForgetsTheLastPosition()
    {
        var request = new ScrollRequest();
        request.Request();
        Assert.True(request.TryConsume(drawn: true, y: 600f));

        request.Request();
        Assert.True(request.TryConsume(drawn: true, y: 600f)); // first position of the new request, not "settled"
        Assert.True(request.Pending);
    }
}
