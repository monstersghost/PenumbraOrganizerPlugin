using System.Numerics;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>What a piece of an example path stands for; each role is drawn in its own theme colour.</summary>
public enum PathRole
{
    /// <summary>The creator folder.</summary>
    Creator,

    /// <summary>The category's own folder, possibly nested (<c>Equipment/Gear</c>), or a fixed folder under it.</summary>
    Folder,

    /// <summary>A folder one of the category's splits adds (<c>02. Top</c>, <c>115</c>, <c>Viera</c>).</summary>
    Split,

    /// <summary>The mod's own folder at the end of the path.</summary>
    Mod,
}

/// <summary>One piece of an example path.</summary>
public sealed record PathSegment(string Text, PathRole Role);

/// <summary>Where a switch-style toggle draws its track and knob.</summary>
public readonly record struct ToggleGeometry(Vector2 TrackMin, Vector2 TrackMax, Vector2 KnobCenter, float KnobRadius);

/// <summary>Where wrapped text ends (a local x for ImGui.PushTextWrapPos) and whether the button after it fits beside it.</summary>
public readonly record struct TextThenButtonLine(float WrapX, bool ButtonOnSameLine);

/// <summary>How the Advanced card splits its width between the category list and the editor.</summary>
public readonly record struct ColumnLayout(bool Stacked, float ListWidth, float EditorWidth);

/// <summary>
/// The widget kit's layout maths as pure functions, so the tests pin it without ImGui (spec 2, section 11). The
/// widgets measure text with ImGui and pass the numbers in; nothing here reads ImGui state.
/// </summary>
public static class WidgetLayout
{
    /// <summary>Horizontal padding inside each segment of a segmented control.</summary>
    public const float SegmentPadding = 12f;

    /// <summary>Padding inside a badge, around its text.</summary>
    public static readonly Vector2 BadgePadding = new(6f, 1f);

    /// <summary>A toggle's track is this many times as wide as it is tall.</summary>
    public const float ToggleAspect = 1.8f;

    /// <summary>Gap between a toggle's knob and its track edge.</summary>
    public const float ToggleKnobInset = 2f;

    /// <summary>The category list's width beside the editor.</summary>
    public const float CategoryListWidth = 200f;

    /// <summary>Below this editor width the list and the editor stack instead of sitting side by side.</summary>
    public const float MinEditorWidth = 520f;

    /// <summary>Each segment is its label plus padding on both sides. Negative widths count as zero.</summary>
    public static float[] SegmentWidths(IReadOnlyList<float> labelWidths, float padding = SegmentPadding) =>
        [.. labelWidths.Select(width => MathF.Max(0f, width) + padding * 2f)];

    /// <summary>The left edge of each item laid side by side with <paramref name="gap"/> between items, from 0.</summary>
    public static float[] Offsets(IReadOnlyList<float> widths, float gap = 0f)
    {
        var offsets = new float[widths.Count];
        var x = 0f;
        for (var i = 0; i < widths.Count; i++)
        {
            offsets[i] = x;
            x += widths[i] + gap;
        }

        return offsets;
    }

    /// <summary>The total width of items laid side by side with <paramref name="gap"/> between them.</summary>
    public static float TotalWidth(IReadOnlyList<float> widths, float gap = 0f) =>
        widths.Count == 0 ? 0f : widths.Sum() + gap * (widths.Count - 1);

    /// <summary>A switch of the given height whose track starts at <paramref name="topLeft"/>; the knob sits right when on.</summary>
    public static ToggleGeometry Toggle(Vector2 topLeft, float height, bool on)
    {
        var width = height * ToggleAspect;
        var radius = MathF.Max(1f, height / 2f - ToggleKnobInset);
        var centerY = topLeft.Y + height / 2f;
        var centerX = on ? topLeft.X + width - height / 2f : topLeft.X + height / 2f;
        return new ToggleGeometry(topLeft, topLeft + new Vector2(width, height), new Vector2(centerX, centerY), radius);
    }

    /// <summary>A badge's outer size for text of the given size.</summary>
    public static Vector2 BadgeSize(Vector2 textSize) => textSize + BadgePadding * 2f;

    /// <summary>The x where an item of <paramref name="width"/> starts so its right edge sits <paramref name="padding"/> inside <paramref name="maxX"/>.</summary>
    public static float RightAlign(float maxX, float width, float padding) => maxX - padding - width;

    /// <summary>The space a top-level card leaves between its right edge and the scrollbar or window edge.</summary>
    public const float PageRightGutter = 8f;

    /// <summary>
    /// A card's BeginChild width: a top-level card (<paramref name="depth"/> 0) is the available width less
    /// <see cref="PageRightGutter"/> (ImGui reads a negative width as "available minus this"), so nothing touches
    /// the scrollbar; a card inside another card fills its parent, whose own padding already insets it.
    /// </summary>
    public static float CardWidth(int depth) => depth <= 0 ? -PageRightGutter : 0f;

    /// <summary>
    /// The x for an item pushed to the right end of a line (right edge on <paramref name="contentMaxX"/>), but never
    /// left of <paramref name="cursorX"/>, where the line's earlier items end, so it can only move right.
    /// </summary>
    public static float TrailingItemX(float contentMaxX, float itemWidth, float cursorX) =>
        MathF.Max(cursorX, contentMaxX - itemWidth);

    /// <summary>The narrowest column <see cref="TextThenButton"/> wraps text into before it moves the button below.</summary>
    public const float MinWrappedTextWidth = 200f;

    /// <summary>
    /// Wrapped text followed by a button on the same line: the text wraps short of the button's width plus the item
    /// spacing, so the button is never pushed off the window. When that would leave the text less than
    /// <see cref="MinWrappedTextWidth"/>, the text takes the full width and the button goes on the next line instead.
    /// The wrap x is always right of <paramref name="cursorX"/>.
    /// </summary>
    public static TextThenButtonLine TextThenButton(
        float cursorX, float available, float buttonWidth, float spacing, float minTextWidth = MinWrappedTextWidth)
    {
        var textRoom = available - buttonWidth - spacing;
        return textRoom >= minTextWidth
            ? new TextThenButtonLine(cursorX + textRoom, true)
            : new TextThenButtonLine(cursorX + MathF.Max(1f, available), false);
    }

    /// <summary>
    /// The list beside the editor when both fit (the list at its fixed width, the editor taking the rest), otherwise
    /// both full width, stacked.
    /// </summary>
    public static ColumnLayout Columns(float available, float spacing)
    {
        var editor = available - CategoryListWidth - spacing;
        return editor < MinEditorWidth
            ? new ColumnLayout(true, MathF.Max(0f, available), MathF.Max(0f, available))
            : new ColumnLayout(false, CategoryListWidth, editor);
    }

    /// <summary>
    /// A card's height from where its content ended last frame (the cursor's local Y, top padding included): that
    /// plus the bottom padding. Before the first measurement (zero or less) just the two paddings and one pixel, so a
    /// new card starts small and grows on the next frame instead of filling the window.
    /// </summary>
    public static float CardHeight(float contentBottom, float paddingY) =>
        contentBottom <= 0f ? paddingY * 2f + 1f : contentBottom + paddingY;

    /// <summary>
    /// What a card remembers for next frame's <see cref="CardHeight"/>: the new measurement when its content was
    /// drawn, otherwise the previous one. A card scrolled fully out of view is skipped by ImGui (BeginChild returns
    /// false), so its cursor stays at the top padding; remembering that collapsed the card, shortened the page by
    /// the card's height and made ImGui clamp the scroll position, which is the Sort tab's scroll jump.
    /// </summary>
    public static float CardContentBottom(bool drawn, float remembered, float measured) =>
        drawn ? measured : remembered;

    /// <summary>
    /// The pieces of a path preview interleaved with separators: the widths of segment 0, separator, segment 1, and
    /// so on, so <see cref="Offsets"/> places them. Empty for no segments.
    /// </summary>
    public static float[] PathPieceWidths(IReadOnlyList<float> segmentWidths, float separatorWidth)
    {
        if (segmentWidths.Count == 0)
            return [];

        var pieces = new float[segmentWidths.Count * 2 - 1];
        for (var i = 0; i < segmentWidths.Count; i++)
        {
            pieces[i * 2] = segmentWidths[i];
            if (i < segmentWidths.Count - 1)
                pieces[i * 2 + 1] = separatorWidth;
        }

        return pieces;
    }
}
