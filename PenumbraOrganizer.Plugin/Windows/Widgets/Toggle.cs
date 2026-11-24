using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// A switch-style on/off control with its label. One item (an invisible button over the switch and the label), so a
/// click on either, or Space/Enter while it has keyboard focus, flips it exactly like a checkbox; it shows a focus
/// ring when focused and is inert under <c>ImGui.BeginDisabled</c>. Returns true on the frame it flips.
/// </summary>
public static class Toggle
{
    public static bool Draw(string id, string label, ref bool value)
    {
        var height = ImGui.GetFrameHeight();
        var style = ImGui.GetStyle();
        var trackWidth = height * WidgetLayout.ToggleAspect;
        var labelSize = ImGui.CalcTextSize(label);
        var start = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton(id, new Vector2(trackWidth + style.ItemInnerSpacing.X + labelSize.X, height));
        if (clicked)
            value = !value;

        var hovered = ImGui.IsItemHovered();
        var focused = ImGui.IsItemFocused();
        var geometry = WidgetLayout.Toggle(start, height, value);
        var draw = ImGui.GetWindowDrawList();
        var track = value ? PluginTheme.AccentColor : hovered ? PluginTheme.RowHover : PluginTheme.ToggleOff;

        // GetColorU32 applies the style alpha, so a disabled toggle draws faded like every other widget.
        draw.AddRectFilled(geometry.TrackMin, geometry.TrackMax, ImGui.GetColorU32(track), height / 2f);
        draw.AddCircleFilled(geometry.KnobCenter, geometry.KnobRadius, ImGui.GetColorU32(PluginTheme.TextNormal));
        draw.AddText(
            new Vector2(geometry.TrackMax.X + style.ItemInnerSpacing.X, start.Y + style.FramePadding.Y),
            ImGui.GetColorU32(PluginTheme.TextNormal), label);
        if (focused)
        {
            draw.AddRect(start - new Vector2(2f, 2f), ImGui.GetItemRectMax() + new Vector2(2f, 2f),
                ImGui.GetColorU32(PluginTheme.AccentText), PluginTheme.RowRounding, ImDrawFlags.None, 1.5f);
        }

        return clicked;
    }
}
