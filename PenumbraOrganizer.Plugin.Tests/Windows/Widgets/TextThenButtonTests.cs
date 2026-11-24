using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Tests.Windows.Widgets;

/// <summary>
/// A wrapped line of text with a button after it (the Sort tab's Heliosphere/Proteus status line in the pinned bar):
/// the text wraps short of the button's room so the button always stays on screen.
/// </summary>
public class TextThenButtonTests
{
    [Fact]
    public void WideEnough_TextWrapsShortOfTheButton_ButtonOnTheSameLine()
    {
        // About the 900 px minimum window: 884 px of content, a 120 px button, 8 px item spacing.
        var line = WidgetLayout.TextThenButton(cursorX: 8f, available: 884f, buttonWidth: 120f, spacing: 8f);

        Assert.Equal(8f + 884f - 120f - 8f, line.WrapX);
        Assert.True(line.ButtonOnSameLine);
    }

    [Fact]
    public void TooNarrowForTheMinimumText_ButtonMovesBelow_TextUsesTheFullWidth()
    {
        var line = WidgetLayout.TextThenButton(cursorX: 8f, available: 300f, buttonWidth: 120f, spacing: 8f);

        Assert.Equal(308f, line.WrapX);
        Assert.False(line.ButtonOnSameLine);
    }

    [Fact]
    public void ExactlyTheMinimumTextWidth_StillFitsOnOneLine()
    {
        var available = WidgetLayout.MinWrappedTextWidth + 120f + 8f;
        var line = WidgetLayout.TextThenButton(cursorX: 0f, available, buttonWidth: 120f, spacing: 8f);

        Assert.Equal(WidgetLayout.MinWrappedTextWidth, line.WrapX);
        Assert.True(line.ButtonOnSameLine);
    }

    [Fact]
    public void NoRoomAtAll_WrapStaysRightOfTheCursor()
    {
        // ImGui reads a wrap position at or left of the cursor as "no wrap" or a zero-width column; never hand it one.
        var line = WidgetLayout.TextThenButton(cursorX: 20f, available: -5f, buttonWidth: 120f, spacing: 8f);

        Assert.Equal(21f, line.WrapX);
        Assert.False(line.ButtonOnSameLine);
    }
}
