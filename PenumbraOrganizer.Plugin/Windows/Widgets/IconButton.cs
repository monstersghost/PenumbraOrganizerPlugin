using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// A small button showing a FontAwesome icon (Dalamud's <see cref="FontAwesomeIcon"/>), with an optional plain
/// tooltip. A caller that has a help topic passes no tooltip and calls <c>Help.Tooltip</c> right after instead.
/// </summary>
public static class IconButton
{
    public static bool Draw(string id, FontAwesomeIcon icon, string? tooltip = null)
    {
        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            clicked = ImGui.Button($"{icon.ToIconString()}###{id}");

        if (tooltip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>The width <see cref="Draw"/> will take for <paramref name="icon"/>, for placing it before drawing.</summary>
    public static float Width(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            return ImGui.CalcTextSize(icon.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;
    }
}
