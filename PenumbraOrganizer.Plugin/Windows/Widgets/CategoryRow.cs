using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// One custom-drawn row of the category list: hover highlight, an accent marker on the left of the selected row, the
/// name, and a Custom badge on the right. Full width, one frame tall. Returns true when clicked (or activated with
/// the keyboard).
/// </summary>
public static class CategoryRow
{
    private const string CustomText = "Custom";
    private const float MarkerWidth = 3f;

    public static bool Draw(string id, string name, bool selected, bool custom)
    {
        var height = ImGui.GetFrameHeight();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var end = start + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var padding = ImGui.GetStyle().FramePadding;

        if (selected)
        {
            draw.AddRectFilled(start, end, ImGui.GetColorU32(PluginTheme.AccentSoft), PluginTheme.RowRounding);
            draw.AddRectFilled(start, new Vector2(start.X + MarkerWidth, end.Y), ImGui.GetColorU32(PluginTheme.AccentColor));
        }
        else if (hovered)
        {
            draw.AddRectFilled(start, end, ImGui.GetColorU32(PluginTheme.RowHover), PluginTheme.RowRounding);
        }

        draw.AddText(start + new Vector2(padding.X + MarkerWidth, padding.Y), ImGui.GetColorU32(PluginTheme.TextNormal), name);

        if (custom)
        {
            var badge = Badge.Size(CustomText);
            var x = WidgetLayout.RightAlign(end.X, badge.X, padding.X);
            Badge.DrawAt(draw, new Vector2(x, start.Y + (height - badge.Y) / 2f), CustomText, BadgeKind.Warning);
        }

        if (ImGui.IsItemFocused())
        {
            draw.AddRect(start, end, ImGui.GetColorU32(PluginTheme.AccentText), PluginTheme.RowRounding,
                ImDrawFlags.None, 1.5f);
        }

        return clicked;
    }
}
