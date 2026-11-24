namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// A one-shot "bring this into view and open it" request for something drawn further down a scrolling page, such
/// as the Rules card the Recommended screen's Show rules button points at. Pure frame counting, no ImGui: the caller
/// reports each frame where the target sits and scrolls when told to.
/// </summary>
/// <remarks>
/// Cards open at last frame's height (<see cref="Card"/>, <see cref="WidgetLayout.CardHeight"/>), so after a screen
/// switch the cards above the target grow over a frame or two and push it down. One scroll on the first frame would
/// aim at where the target was, not where it ends up, so the request scrolls on every frame the target is drawn until
/// it is drawn twice at the same place, and gives up after <see cref="MaxFrames"/> frames (drawn or not) so a target
/// that is never shown cannot cause a surprise scroll much later.
/// </remarks>
public sealed class ScrollRequest
{
    /// <summary>The most frames a request stays alive, whether the target was drawn in them or not.</summary>
    public const int MaxFrames = 10;

    // Positions closer than this are the same place: layout maths can leave a sub-pixel difference.
    private const float SettleTolerance = 0.5f;

    private int _framesLeft;
    private float? _lastY;
    private bool _open;

    /// <summary>True while the request still wants the page scrolled to the target.</summary>
    public bool Pending => _framesLeft > 0;

    /// <summary>Starts (or restarts) a request: scroll to the target and open it.</summary>
    public void Request()
    {
        _framesLeft = MaxFrames;
        _lastY = null;
        _open = true;
    }

    /// <summary>
    /// Called once per frame. <paramref name="drawn"/> says whether the target is on the page this frame and
    /// <paramref name="y"/> is its position in the page's content (scroll-independent). True means scroll to it now.
    /// </summary>
    public bool TryConsume(bool drawn, float y)
    {
        if (_framesLeft <= 0)
        {
            // The frame after the request ended: an open nobody took is dropped, so it cannot fire much later.
            _open = false;
            return false;
        }

        _framesLeft--;
        if (!drawn)
            return false;

        if (_lastY is { } last && MathF.Abs(last - y) < SettleTolerance)
            _framesLeft = 0; // settled: this frame's scroll is the last
        _lastY = y;
        return true;
    }

    /// <summary>True once per request: the caller opens the target's header (ImGui.SetNextItemOpen) when it draws it.</summary>
    public bool TakeOpen()
    {
        var open = _open;
        _open = false;
        return open;
    }
}
