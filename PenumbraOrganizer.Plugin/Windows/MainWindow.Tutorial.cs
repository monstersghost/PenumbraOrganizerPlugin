using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed partial class MainWindow
{
    // Supplied by the plugin: what the first-run walkthrough is pointing at right now.
    internal Func<(HighlightTarget Tab, HighlightTarget Button)>? TutorialHighlight { get; set; }

    private (HighlightTarget Tab, HighlightTarget Button) _highlight;
    private HighlightTarget _highlightedTab;
    private HighlightTarget _tabToSelect;

    // Once per frame, before the tabs. A change of tab target selects that tab for ONE frame, so the
    // user can click away afterwards without the walkthrough dragging them back.
    private void RefreshTutorialHighlight()
    {
        _highlight = TutorialHighlight?.Invoke() ?? (HighlightTarget.None, HighlightTarget.None);
        if (_highlight.Tab != _highlightedTab)
        {
            _highlightedTab = _highlight.Tab;
            _tabToSelect = _highlight.Tab;
        }
    }

    // The flags for a tab: selects it for one frame when the walkthrough has just moved to it. The
    // caller opens the tab with these, then calls OutlineLastItem for the same target.
    private ImGuiTabItemFlags TutorialTabFlags(HighlightTarget me, ImGuiTabItemFlags extra = ImGuiTabItemFlags.None)
    {
        if (me != HighlightTarget.None && _tabToSelect == me)
        {
            _tabToSelect = HighlightTarget.None;
            return extra | ImGuiTabItemFlags.SetSelected;
        }

        return extra;
    }

    /// <summary>Outlines the item just drawn when the walkthrough is pointing at <paramref name="me"/>.</summary>
    internal void OutlineLastItem(HighlightTarget me)
    {
        if (me == HighlightTarget.None || (_highlight.Tab != me && _highlight.Button != me))
            return;

        // A slow pulse so it reads as "look here" without flashing.
        var pulse = 0.65f + 0.35f * MathF.Sin((float)ImGui.GetTime() * 4f);
        var colour = ImGui.GetColorU32(new Vector4(1f, 0.82f, 0.2f, pulse));
        var pad = new Vector2(2, 2);
        ImGui.GetWindowDrawList().AddRect(
            ImGui.GetItemRectMin() - pad, ImGui.GetItemRectMax() + pad, colour, 3f, ImDrawFlags.None, 2f);
    }
}
