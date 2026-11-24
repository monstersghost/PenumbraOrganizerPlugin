using Dalamud.Bindings.ImGui;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Windows;

// The Recommended screen and the controls both screens share (spec 2, section 3). Drawing only: every decision is
// in CategoryLayoutEditor, ExamplePaths and SortLayout, which the tests pin.
public sealed partial class SortPanel
{
    private static readonly string[] ModeLabels = ["Recommended", "Advanced"];

    // Session state: chosen once from the saved layouts (Recommended when nothing is customised), never saved.
    private SortTabMode _mode;
    private bool _modeChosen;

    // Navigation only, so it stays usable while another operation runs.
    private void DrawModeSwitch()
    {
        var index = (int)_mode;
        if (SegmentedControl.Draw("sort-mode", ModeLabels, ref index))
            SetMode((SortTabMode)index);
        Help.Tooltip(HelpTopics.SortMode);
    }

    // A folder still being typed is saved before the screen changes.
    private void SetMode(SortTabMode mode)
    {
        _editor?.Commit();
        _mode = mode;
    }

    // Group by and What to sort, in one row. Group by is also the creator level every category inherits.
    private void DrawGroupingRow(ActivityGates gates)
    {
        var reason = gates.CanStageProposals ? null : ActivityGateReason;
        // ImGui draws a combo's label to its RIGHT, so each label is drawn first as plain text and the combo
        // gets a hidden (##) label; that way the label reads before its dropdown.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Group by");
        ImGui.SameLine();
        ImGui.BeginDisabled(!gates.CanStageProposals);
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("##group-by", ref _groupingIndex, GroupingLabels, GroupingLabels.Length);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortGrouping, reason);

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("What to sort");
        ImGui.SameLine();
        ImGui.BeginDisabled(!gates.CanStageProposals);
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("##what-to-sort", ref _whatToSortIndex, WhatToSortLabels, WhatToSortLabels.Length);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortWhatToSort, reason);
    }

    private void DrawRecommended(Configuration config, ActivityGates gates, SortStrategy strategy)
    {
        // The saved profile, through the per-config cache: free per frame, and what the next Sort will use.
        var profile = LayoutProfileCache.For(config);

        using (var card = Card.Begin("##sort-recommended"))
        {
            if (card)
            {
                ImGui.TextUnformatted("Recommended layout");
                ImGui.TextDisabled("A tidy layout that suits most libraries. Nothing moves until you review the plan and Apply.");
                ImGui.Spacing();
                DrawGroupingRow(gates);
                ImGui.Spacing();
                foreach (var category in ExamplePaths.RecommendedPreview)
                {
                    PathPreview.Draw(ExamplePaths.For(category, profile[category], strategy, _raceEnabled), category.ToString());
                    Help.Tooltip(HelpTopics.SortExamplePath);
                }

                // Customised categories show in the examples above, so this screen never describes a layout it
                // will not produce; the note says why they may differ from the list below.
                var customised = LayoutCatalog.Categories.Count(profile.IsCustom);
                if (customised > 0)
                {
                    ImGui.TextColored(PluginTheme.Warning,
                        customised == 1 ? "1 category customised" : $"{customised} categories customised");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Show in Advanced##recommended-show-advanced"))
                        SetMode(SortTabMode.Advanced);
                }
            }
        }

        ImGui.Spacing();
        using (var card = Card.Begin("##sort-recommended-what"))
        {
            if (card)
            {
                ImGui.TextUnformatted("What Recommended does");
                ImGui.SameLine();
                if (ImGui.SmallButton("Customise per category##customise-per-category"))
                    SetMode(SortTabMode.Advanced);
                Help.Tooltip(HelpTopics.SortMode);
                ImGui.TextDisabled("One line per kind of mod. Switch to Advanced to change any of it.");
                ImGui.Spacing();
                ImGui.PushTextWrapPos(0f);
                foreach (var (title, text) in CategoryLayoutEditor.RecommendedLines(_raceEnabled))
                {
                    ImGui.TextColored(PluginTheme.ChangedGood, title);
                    ImGui.SameLine();
                    ImGui.TextUnformatted(text);
                }

                ImGui.PopTextWrapPos();
            }
        }

        ImGui.Spacing();
    }
}
