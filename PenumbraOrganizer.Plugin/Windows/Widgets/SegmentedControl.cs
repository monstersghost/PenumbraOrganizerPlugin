using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// A row of buttons in one rounded frame; the selected one uses the accent. Returns true on the frame the selection
/// changes. Respects an enclosing <c>ImGui.BeginDisabled</c>. Each segment's id is <c>{id}-{index}</c>, never its label.
/// </summary>
public static class SegmentedControl
{
    public static bool Draw(string id, IReadOnlyList<string> labels, ref int selected)
    {
        var widths = WidgetLayout.SegmentWidths([.. labels.Select(label => ImGui.CalcTextSize(label).X)]);
        var start = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight();
        var changed = false;

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0f, 0f);

            var isSelected = i == selected;
            ImGui.PushStyleColor(ImGuiCol.Button, isSelected ? PluginTheme.AccentColor : PluginTheme.PathBackground);
            ImGui.PushStyleColor(ImGuiCol.Text, isSelected ? PluginTheme.TextNormal : PluginTheme.TextMuted);
            if (ImGui.Button($"{labels[i]}###{id}-{i}", new Vector2(widths[i], height)) && !isSelected)
            {
                selected = i;
                changed = true;
            }

            ImGui.PopStyleColor(2);
        }

        ImGui.PopStyleVar(2);

        // One rounded outline around the whole row, so the buttons read as one control.
        var end = start + new Vector2(WidgetLayout.TotalWidth(widths), height);
        ImGui.GetWindowDrawList().AddRect(
            start, end, ImGui.GetColorU32(PluginTheme.CardBorder), PluginTheme.RowRounding, ImDrawFlags.None, 1f);
        return changed;
    }
}
