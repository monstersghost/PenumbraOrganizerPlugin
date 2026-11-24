using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// A label drawn BEFORE its control, on the same line. ImGui draws a combo's or text box's own label to its right,
/// so the Sort tab draws the label first with this and gives the control a hidden (##) label; the text is lined up
/// with the control's frame (AlignTextToFramePadding), so every labelled field on the tab reads the same way.
/// </summary>
public static class FieldLabel
{
    public static void Draw(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
        ImGui.SameLine();
    }
}
