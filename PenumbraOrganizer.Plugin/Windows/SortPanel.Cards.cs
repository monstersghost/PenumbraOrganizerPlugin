using Dalamud.Bindings.ImGui;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Windows;

// The two collapsible cards under the screens: Rules (Advanced only), then Sort settings (both screens).
public sealed partial class SortPanel
{
    // Set by Recommended's Show rules: Advanced then scrolls the page to the Rules card and opens its header. Kept
    // alive until the card stops moving (the cards above it grow over the first frames after the switch).
    private readonly ScrollRequest _rulesReveal = new();

    private void DrawRulesCard(
        OrganizerState state, ActivityGates gates, Configuration config, Action saveConfig,
        Func<string, string> canonicalizeCreator)
    {
        ImGui.Spacing();
        // Here, in the page's scrolling child, not inside the card: SetScrollHereY scrolls the current window, and
        // inside the card that would be the card itself. Ratio 0 puts the line just above the card at the top.
        if (_rulesReveal.TryConsume(drawn: true, ImGui.GetCursorPosY()))
            ImGui.SetScrollHereY(0f);
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
