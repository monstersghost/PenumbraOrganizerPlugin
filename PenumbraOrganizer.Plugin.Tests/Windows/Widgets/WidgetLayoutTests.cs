using System.Numerics;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Tests.Windows.Widgets;

public class WidgetLayoutTests
{
    [Fact]
    public void SegmentWidths_AreTheLabelPlusPaddingOnBothSides()
    {
        Assert.Equal([124f, 84f], WidgetLayout.SegmentWidths([100f, 60f]));
        Assert.Equal([20f], WidgetLayout.SegmentWidths([-5f], padding: 10f));
        Assert.Empty(WidgetLayout.SegmentWidths([]));
    }

    [Fact]
    public void Offsets_PlaceItemsSideBySide_WithTheGap()
    {
        Assert.Equal([0f, 124f, 208f], WidgetLayout.Offsets([124f, 84f, 50f]));
        Assert.Equal([0f, 128f], WidgetLayout.Offsets([124f, 84f], gap: 4f));
        Assert.Empty(WidgetLayout.Offsets([]));
    }

    [Fact]
    public void TotalWidth_CountsOneGapBetweenEachPair()
    {
        Assert.Equal(208f, WidgetLayout.TotalWidth([124f, 84f]));
        Assert.Equal(216f, WidgetLayout.TotalWidth([124f, 84f], gap: 8f));
        Assert.Equal(0f, WidgetLayout.TotalWidth([], gap: 8f));
    }

    [Fact]
    public void Toggle_KnobSitsLeftWhenOff_AndRightWhenOn()
    {
        var off = WidgetLayout.Toggle(new Vector2(10f, 20f), 20f, on: false);
        var on = WidgetLayout.Toggle(new Vector2(10f, 20f), 20f, on: true);

        Assert.Equal(new Vector2(10f, 20f), off.TrackMin);
        Assert.Equal(new Vector2(46f, 40f), off.TrackMax); // 20 * 1.8 wide
        Assert.Equal(new Vector2(20f, 30f), off.KnobCenter);
        Assert.Equal(new Vector2(36f, 30f), on.KnobCenter);
        Assert.Equal(8f, on.KnobRadius);
        Assert.Equal(off.TrackMax, on.TrackMax);
    }

    [Fact]
    public void Toggle_KnobRadiusNeverDropsBelowOnePixel() =>
        Assert.Equal(1f, WidgetLayout.Toggle(Vector2.Zero, 2f, true).KnobRadius);

    [Fact]
    public void BadgeSize_AddsThePaddingOnEverySide() =>
        Assert.Equal(new Vector2(52f, 16f), WidgetLayout.BadgeSize(new Vector2(40f, 14f)));

    [Fact]
    public void RightAlign_KeepsThePaddingInsideTheEdge() =>
        Assert.Equal(140f, WidgetLayout.RightAlign(200f, 52f, 8f));

    [Fact]
    public void Columns_SitSideBySide_WhenTheEditorKeepsItsMinimumWidth()
    {
        var wide = WidgetLayout.Columns(900f, 14f);
        Assert.False(wide.Stacked);
        Assert.Equal(WidgetLayout.CategoryListWidth, wide.ListWidth);
        Assert.Equal(900f - WidgetLayout.CategoryListWidth - 14f, wide.EditorWidth);

        var edge = WidgetLayout.Columns(WidgetLayout.CategoryListWidth + 14f + WidgetLayout.MinEditorWidth, 14f);
        Assert.False(edge.Stacked);
    }

    [Fact]
    public void Columns_Stack_OnANarrowWindow()
    {
        var narrow = WidgetLayout.Columns(WidgetLayout.CategoryListWidth + 14f + WidgetLayout.MinEditorWidth - 1f, 14f);
        Assert.True(narrow.Stacked);
        Assert.Equal(narrow.ListWidth, narrow.EditorWidth);

        Assert.Equal(new ColumnLayout(true, 0f, 0f), WidgetLayout.Columns(-10f, 14f));
    }

    [Fact]
    public void PathPieceWidths_InterleaveTheSeparators()
    {
        Assert.Equal([30f, 9f, 40f, 9f, 50f], WidgetLayout.PathPieceWidths([30f, 40f, 50f], 9f));
        Assert.Equal([30f], WidgetLayout.PathPieceWidths([30f], 9f));
        Assert.Empty(WidgetLayout.PathPieceWidths([], 9f));

        // Placed with Offsets, the third segment starts after two segments and two separators.
        Assert.Equal(88f, WidgetLayout.Offsets(WidgetLayout.PathPieceWidths([30f, 40f, 50f], 9f))[4]);
    }

    [Fact]
    public void CardHeight_IsTheContentPlusTheBottomPadding_AndSmallBeforeTheFirstMeasurement()
    {
        Assert.Equal(130f, WidgetLayout.CardHeight(120f, 10f));
        Assert.Equal(21f, WidgetLayout.CardHeight(0f, 10f));
        Assert.Equal(21f, WidgetLayout.CardHeight(-3f, 10f));
    }

    // The scroll-jump fix: a card scrolled out of view is skipped by ImGui, so its cursor never moves past the top
    // padding. Remembering that would collapse the card, shrink the page and make ImGui clamp the scroll position.
    [Fact]
    public void CardContentBottom_KeepsTheLastMeasurement_WhileTheCardIsClipped()
    {
        Assert.Equal(480f, WidgetLayout.CardContentBottom(drawn: false, remembered: 480f, measured: 10f));
        Assert.Equal(0f, WidgetLayout.CardContentBottom(drawn: false, remembered: 0f, measured: 10f));
    }

    [Fact]
    public void CardContentBottom_TakesTheNewMeasurement_WhenTheCardWasDrawn()
    {
        Assert.Equal(520f, WidgetLayout.CardContentBottom(drawn: true, remembered: 480f, measured: 520f));
        Assert.Equal(200f, WidgetLayout.CardContentBottom(drawn: true, remembered: 480f, measured: 200f));
    }

    [Fact]
    public void CardHeight_StaysTheSame_AcrossAClippedFrame()
    {
        // Drawn, then clipped (measured only the top padding), then drawn again: the height never dips.
        var padding = 10f;
        var remembered = WidgetLayout.CardContentBottom(true, 0f, 300f);
        var visibleHeight = WidgetLayout.CardHeight(remembered, padding);
        remembered = WidgetLayout.CardContentBottom(false, remembered, padding);
        Assert.Equal(visibleHeight, WidgetLayout.CardHeight(remembered, padding));
        remembered = WidgetLayout.CardContentBottom(true, remembered, 300f);
        Assert.Equal(visibleHeight, WidgetLayout.CardHeight(remembered, padding));
    }
}
