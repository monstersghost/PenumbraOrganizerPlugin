using Dalamud.Bindings.ImGui;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

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
public readonly record struct SortSelection(
    SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair,
    string? HairRaceCode = null, bool MidlanderFallback = false, bool SplitBody = false,
    BodyPriority? BodyPriority = null)
{
    // By Creator never consults category, so no split can change its output. All four checkboxes
    // are disabled when this is false.
    public bool SplitsApply => Strategy != SortStrategy.CreatorOnly;

    public HairPriority HairPriority => new(HairRaceCode, MidlanderFallback);
}

/// <summary>
/// The Sort tab's grouping control: a Group by dropdown, four split checkboxes, the scraped-list
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
    // Group by and the four split checkboxes are SESSION state: a sort strategy is a choice about
    // the action you are about to take, not a preference worth persisting.
    //
    // The hair race, the Midlander fallback and the body priority are PREFERENCES. They are set once
    // and left (most people use one body per character), so they are read from Configuration on the
    // first frame and written back the moment they change.
    private int _groupingIndex = DefaultGroupingIndex;
    private bool _splitGear;
    private bool _splitNpc = true;        // matches the always-on NPC subdivision it replaces
    private bool _splitHair;              // off, like gear: an unchanged install keeps flat Hair/
    private int _hairRaceIndex;              // 0 is None; see HairRaceLabels
    private bool _midlanderFallback;         // off: an unchanged install keeps the 0.6.1.0 rule
    private bool _splitBody;                 // off, like gear and hair: no body folders until asked
    private SortSelection? _lastSorted;
    private bool _prefsLoaded;
    private BodyPriority _bodyPriority = BodyPriority.Default([]);
    private IReadOnlyList<string>? _bodyLabelsSeen;

    private const string Male = "\u2642";
    private const string Female = "\u2640";

    // None first, then every confirmed race in table order. Labels are "Miqo'te <male sign>" and so on.
    private static readonly string[] HairRaceLabels =
        ["None", .. HairRaceCodes.All.Select(r => $"{r.Race} {(r.Gender == "Male" ? Male : Female)}")];

    private static string? HairRaceCodeAt(int index) =>
        index == 0 ? null : HairRaceCodes.All[index - 1].Code;

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
        Func<IReadOnlyList<string>> getBodyLabels,
        Action? onShowSortHelp = null)
    {
        var bodyLabels = getBodyLabels();

        if (!_prefsLoaded)
        {
            _prefsLoaded = true;
            var raceIndex = config.HairRaceCode is null
                ? 0
                : Array.FindIndex(HairRaceCodes.All.ToArray(), r => r.Code == config.HairRaceCode) + 1;
            _hairRaceIndex = Math.Max(raceIndex, 0);
            _midlanderFallback = config.MidlanderFallback;
        }

        // Re-repaired only when the label list is a new instance, i.e. after a rescan published a
        // different body list, so this costs nothing per frame. Repair drops stale labels and
        // appends new bodies, and WRITES THE RESULT BACK when it changed a non-empty saved list.
        // An empty saved list means "default" and stays empty. Guarded on a non-empty label list:
        // Repair against an empty list would report needsSave and wipe the user's saved ranking.
        if (bodyLabels.Count > 0 && !ReferenceEquals(_bodyLabelsSeen, bodyLabels))
        {
            _bodyLabelsSeen = bodyLabels;
            _bodyPriority = BodyPriority.Repair(config.BodyPriority, bodyLabels, out var needsSave);
            if (needsSave)
            {
                config.BodyPriority = [.. _bodyPriority.Labels];
                saveConfig();
            }
        }

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

        ImGui.BeginDisabled(!splitsApply);
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Priority race", ref _hairRaceIndex, HairRaceLabels, HairRaceLabels.Length);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortPriorityRace, splitsApply ? null : SplitsInapplicableReason);

        ImGui.BeginDisabled(!splitsApply);
        ImGui.Checkbox("Midlander fallback", ref _midlanderFallback);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortMidlanderFallback, splitsApply ? null : SplitsInapplicableReason);

        var raceCode = HairRaceCodeAt(_hairRaceIndex);
        if (config.HairRaceCode != raceCode || config.MidlanderFallback != _midlanderFallback)
        {
            config.HairRaceCode = raceCode;
            config.MidlanderFallback = _midlanderFallback;
            saveConfig();
        }

        GroupHeading("Body type");
        ImGui.BeginDisabled(!splitsApply);
        ImGui.Checkbox("Split gear and body mods by body type", ref _splitBody);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortSplitBody, splitsApply ? null : SplitsInapplicableReason);

        DrawBodyPriority(config, saveConfig, bodyLabels, splitsApply);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Every control above has now been submitted, so this is the selection as the user has it
        // THIS frame. Building it earlier would sort with, and compare staleness against, whatever
        // the checkboxes held before the user's click was processed.
        var selection = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair,
            HairRaceCodeAt(_hairRaceIndex), _midlanderFallback, _splitBody, _bodyPriority);

        // Stable id: a label varying with mod count makes the widget id vary with it, so a background
        // scan publishing mid-click would change the active id and the click would be dropped.
        if (ImGui.Button("Sort##sort-mods"))
        {
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
                canonicalizeCreator, selection.HairPriority, selection.SplitBody, selection.BodyPriority);
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

    // Collapsed by default: set once and forget. Arrows rather than drag-and-drop, which is fiddly
    // in ImGui and unusable from a keyboard. Disabled under the same condition as the body checkbox.
    private void DrawBodyPriority(
        Configuration config, Action saveConfig, IReadOnlyList<string> bodyLabels, bool splitsApply)
    {
        if (!ImGui.CollapsingHeader("Body priority##body-priority"))
            return;

        ImGui.BeginDisabled(!splitsApply);
        ImGui.TextDisabled("Highest first. Used when a mod supports more than one body.");

        var order = _bodyPriority.Labels; // immutable: safe to iterate while a click replaces the field
        if (ImGui.BeginChild("##body-priority-list", new System.Numerics.Vector2(0, 220)))
        {
            for (var i = 0; i < order.Count; i++)
            {
                ImGui.PushID(i);

                ImGui.BeginDisabled(i == 0);
                if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                    SetBodyPriority(config, saveConfig, _bodyPriority.MoveUp(i));
                ImGui.EndDisabled();

                ImGui.SameLine();
                ImGui.BeginDisabled(i == order.Count - 1);
                if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                    SetBodyPriority(config, saveConfig, _bodyPriority.MoveDown(i));
                ImGui.EndDisabled();

                ImGui.SameLine();
                ImGui.TextUnformatted($"{i + 1}. {order[i]}");

                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Reset to default##body-priority-reset"))
        {
            // An EMPTY saved list is what "default" means, so a reset keeps following the shipped
            // order across releases instead of freezing today's order into the file.
            _bodyPriority = BodyPriority.Default(bodyLabels);
            config.BodyPriority = [];
            saveConfig();
        }

        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortBodyPriority, splitsApply ? null : SplitsInapplicableReason);
    }

    private void SetBodyPriority(Configuration config, Action saveConfig, BodyPriority next)
    {
        if (next == _bodyPriority)
            return;
        _bodyPriority = next;
        config.BodyPriority = [.. next.Labels];
        saveConfig();
    }
}
