using Dalamud.Bindings.ImGui;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Windows;

// The two collapsible cards under the screens: Rules (Advanced only), then Sort settings (both screens).
public sealed partial class SortPanel
{
    private void DrawRulesCard(
        OrganizerState state, ActivityGates gates, Configuration config, Action saveConfig,
        Func<string, string> canonicalizeCreator)
    {
        ImGui.Spacing();
        using var card = Card.Begin("##sort-rules-card");
        if (card)
            DrawRules(state, gates, config, saveConfig, canonicalizeCreator);
    }

    private static void DrawSortSettingsCard(Configuration config, Action saveConfig)
    {
        ImGui.Spacing();
        using var card = Card.Begin("##sort-settings-card");
        if (card)
            DrawSortSettings(config, saveConfig);
    }
}
