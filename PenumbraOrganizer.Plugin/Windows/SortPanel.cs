using Dalamud.Bindings.ImGui;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// One sort configuration: what to group by, and whether to subdivide gear and NPC mods.
/// </summary>
/// <remarks>
/// A record struct, not a class: the staleness check compares the current selection against the one
/// last sorted with, and that needs value equality.
/// <para>
/// public, not internal: this repo has no InternalsVisibleTo, so an internal type is unreachable
/// from PenumbraOrganizer.Plugin.Tests and SortSelectionTests would not compile.
/// </para>
/// </remarks>
public readonly record struct SortSelection(SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair)
{
    // By Creator never consults category, so no split can change its output. All three checkboxes
    // are disabled when this is false.
    public bool SplitsApply => Strategy != SortStrategy.CreatorOnly;
}

/// <summary>
/// The Sort tab's grouping control: a Group by dropdown, three split checkboxes, the scraped-list
/// opt-in and the Sort button. Replaces the seven sort buttons, and exposes the six combinations
/// they never offered - the whole splitNpc-off column - plus, since the hair split was added,
/// everything in the splitHair-on half.
/// </summary>
/// <remarks>
/// public, not internal: see <see cref="SortSelection"/>.
/// </remarks>
public sealed class SortPanel
{
    /// <summary>Index into <see cref="Groupings"/> the panel starts on.</summary>
    /// <remarks>
    /// A named constant rather than a bare 2, so the backing field below and the test asserting
    /// which strategy is the default cannot drift apart when the array is reordered.
    /// </remarks>
    public const int DefaultGroupingIndex = 2;

    // ONE mapping, not two parallel arrays. An earlier draft had a labels array whose only test
    // compared Count against the enum's length, which passes with the labels in the wrong order.
    // This also removes the assumption that enum numeric values double as UI indices.
    public static readonly (SortStrategy Strategy, string Label)[] Groupings =
    [
        (SortStrategy.CreatorOnly,     "Creator"),
        (SortStrategy.TypeOnly,        "Mod type"),
        (SortStrategy.TypeThenCreator, "Type then creator"),
        (SortStrategy.CreatorThenType, "Creator then type"),
    ];

    private static readonly string[] GroupingLabels = [.. Groupings.Select(g => g.Label)];

    private const string ActivityGateReason = "Another operation is in progress or requires recovery.";
    private const string SplitsInapplicableReason = "Grouping by creator alone never uses the mod's type.";

    // Instance, not static: ImGui.Combo(ref int) and Checkbox(ref bool) need backing storage that
    // survives between frames. A per-frame instance would reset the selection every frame.
    //
    // These five are SESSION state. A sort strategy is a choice about the action you are about to
    // take, not a preference worth persisting.
    private int _groupingIndex = DefaultGroupingIndex;
    private bool _splitGear;
    private bool _splitNpc = true;        // matches the always-on NPC subdivision it replaces
    private bool _splitHair;              // off, like gear: an unchanged install keeps flat Hair/
    private SortSelection? _lastSorted;

    // A rule and a coloured heading above each mod-type group. The rule is ImGui's own separator,
    // so the groups read as separate without a new widget.
    private static void GroupHeading(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(PluginTheme.ChangedGood, title);
    }

    public void Draw(
        OrganizerState state,
        ActivityGates gates,
        Configuration config,             // the scraped-list opt-in is persistent, not session
        Action saveConfig,
        Func<string, string> canonicalizeCreator,
        Action? onShowSortHelp = null)
    {
        ImGui.BeginDisabled(!gates.CanStageProposals);

        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Group by", ref _groupingIndex, GroupingLabels, GroupingLabels.Length);
        Help.Tooltip(HelpTopics.SortGrouping);

        // The strategy is settled the moment the combo has been submitted. SplitsApply reads only
        // Strategy, so gating the checkboxes on it here is correct even though the split values
        // themselves may still change further down this same frame.
        var strategy = Groupings[_groupingIndex].Strategy;
        var splitsApply = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair).SplitsApply;

        // Each checkbox closes its own disabled scope before its tooltip: ImGui is positional, so
        // Help.Tooltip binds to whichever widget was submitted last, and EndDisabled submits none.
        GroupHeading("Gear");
        ImGui.BeginDisabled(!splitsApply);
        ImGui.Checkbox("Split gear by equipment slot", ref _splitGear);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortSplitGear, splitsApply ? null : SplitsInapplicableReason);

        GroupHeading("NPC");
        ImGui.BeginDisabled(!splitsApply);
        ImGui.Checkbox("Split NPC mods by kind", ref _splitNpc);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortSplitNpc, splitsApply ? null : SplitsInapplicableReason);

        // Piece 1's opt-in. Bound directly to config, saved on change, and force-disabled for 0.6.0
        // through the SAME gate the backend consults - so a config value left true cannot quietly
        // enable a path the UI says is off.
        ImGui.BeginDisabled(!Configuration.ScrapedNpcListFeatureEnabled);
        var useScraped = config.UseScrapedNpcNameList;
        if (ImGui.Checkbox("Also use the NPC list scraped from the wiki", ref useScraped))
        {
            config.UseScrapedNpcNameList = useScraped;
            saveConfig();
        }
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortScrapedNpcList,
            Configuration.ScrapedNpcListFeatureEnabled ? null : "Not available in this version.");

        GroupHeading("Hair");
        ImGui.BeginDisabled(!splitsApply);
        ImGui.Checkbox("Split hair by hair number", ref _splitHair);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortSplitHair, splitsApply ? null : SplitsInapplicableReason);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Every control above has now been submitted, so this is the selection as the user has it
        // THIS frame. Building it earlier would sort with, and compare staleness against, whatever
        // the checkboxes held before the user's click was processed.
        var selection = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair);

        // Stable id: a label varying with mod count makes the widget id vary with it, so a background
        // scan publishing mid-click would change the active id and the click would be dropped.
        if (ImGui.Button("Sort##sort-mods"))
        {
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair, canonicalizeCreator);
            _lastSorted = selection;
        }

        // The gate reason rides on the Sort button's own tooltip rather than a second SetTooltip
        // call bound to the same item - two tooltips submitted for one item in one frame fight over
        // the same window. disabledReason is the parameter Help.Tooltip exists to take, and it is
        // what the three controls above already use.
        Help.Tooltip(HelpTopics.SortButton,
            gates.CanStageProposals ? null : ActivityGateReason);

        ImGui.EndDisabled();

        // Outside the activity gate on purpose: it only navigates to Help, so it works while a scan runs.
        ImGui.SameLine();
        if (ImGui.Button("?##sort-help"))
            onShowSortHelp?.Invoke();
        Help.Tooltip(HelpTopics.SortHelpButton);

        if (_lastSorted is { } last && last != selection)
            ImGui.TextDisabled("Selection changed since the last sort.");
    }
}
