using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// One example path on a tinted strip: an optional muted caption, then the segments joined by " / ", each coloured
/// by its <see cref="PathRole"/> (creator, folder, split, mod). Positions come from <see cref="WidgetLayout"/>; a path
/// wider than the strip is clipped by the enclosing card.
/// </summary>
public static class PathPreview
{
    private const string Separator = " / ";
    private const float CaptionGap = 10f;

    public static void Draw(IReadOnlyList<PathSegment> segments, string? caption = null)
    {
        var padding = ImGui.GetStyle().FramePadding;
        var height = ImGui.GetFrameHeight();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(PluginTheme.PathBackground),
            PluginTheme.RowRounding);
        draw.AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(PluginTheme.CardBorder),
            PluginTheme.RowRounding, ImDrawFlags.None, 1f);

        var x = start.X + padding.X;
        var y = start.Y + padding.Y;
        if (caption is not null)
        {
            draw.AddText(new Vector2(x, y), ImGui.GetColorU32(PluginTheme.TextMuted), caption);
            x += ImGui.CalcTextSize(caption).X + CaptionGap;
        }

        var widths = WidgetLayout.PathPieceWidths(
            [.. segments.Select(s => ImGui.CalcTextSize(s.Text).X)], ImGui.CalcTextSize(Separator).X);
        var offsets = WidgetLayout.Offsets(widths);
        for (var i = 0; i < segments.Count; i++)
        {
            draw.AddText(new Vector2(x + offsets[i * 2], y), ImGui.GetColorU32(ColourFor(segments[i].Role)), segments[i].Text);
            if (i < segments.Count - 1)
                draw.AddText(new Vector2(x + offsets[i * 2 + 1], y), ImGui.GetColorU32(PluginTheme.TextMuted), Separator);
        }
    }

    private static Vector4 ColourFor(PathRole role) => role switch
    {
        PathRole.Creator => PluginTheme.Warning,
        PathRole.Folder => PluginTheme.AccentText,
        PathRole.Split => PluginTheme.ChangedGood,
        _ => PluginTheme.TextNormal,
    };
}
