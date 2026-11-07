using Dalamud.Bindings.ImGui;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Operations;

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
    string? HairRaceCode = null, bool MidlanderFallback = false,
    bool SplitGearBody = false, bool SplitBodyMods = false,
    BodyPriority? BodyPriority = null, LayoutOptions Layout = default)
{
    // By Creator never consults category, so no split can change its output. All split checkboxes
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
    // Every Sort tab choice - Group by, the split checkboxes, the hair race, the Midlander fallback
    // and the body priority - is read from Configuration on the first frame and written back the
    // moment it changes, so reopening the plugin does not reset the user's setup. (Group by and the
    // splits were session-only at first; people re-set them every launch, so they now persist.)
    private int _groupingIndex = DefaultGroupingIndex;

    /// <summary>The Groupings index for a saved strategy name; the default when missing or unknown.</summary>
    public static int GroupingIndexFor(string? saved)
    {
        var index = Array.FindIndex(Groupings, g => g.Strategy.ToString() == saved);
        return index >= 0 ? index : DefaultGroupingIndex;
    }

    private void PersistSortChoices(Configuration config, Action saveConfig, SortStrategy strategy)
    {
        var name = strategy.ToString();
        if (config.SortGrouping == name
            && config.SortSplitGear == _splitGear && config.SortSplitNpc == _splitNpc
            && config.SortSplitHair == _splitHair && config.SortSplitGearBody == _splitGearBody
            && config.SortSplitBodyMods == _splitBodyMods
            && config.SortSharedHandsFeet == _sharedHandsFeet && config.SortSplitHairByRace == _splitHairByRace
            && config.SortSplitEmotes == _splitEmotes)
            return;

        config.SortGrouping = name;
        config.SortSplitGear = _splitGear;
        config.SortSplitNpc = _splitNpc;
        config.SortSplitHair = _splitHair;
        config.SortSplitGearBody = _splitGearBody;
        config.SortSplitBodyMods = _splitBodyMods;
        config.SortSharedHandsFeet = _sharedHandsFeet;
        config.SortSplitHairByRace = _splitHairByRace;
        config.SortSplitEmotes = _splitEmotes;
        saveConfig();
    }
    private bool _splitGear;
    private bool _splitNpc = true;        // matches the always-on NPC subdivision it replaces
    private bool _splitHair;              // off, like gear: an unchanged install keeps flat Hair/
    private int _hairRaceIndex;              // 0 is None; see HairRaceLabels
    private bool _midlanderFallback;         // off: an unchanged install keeps the 0.6.1.0 rule
    private bool _splitGearBody;         // off: no body folders until asked, like gear and hair
    private bool _splitBodyMods;
    private bool _sharedHandsFeet;           // off: hands and feet stay under their body
    private bool _splitHairByRace;           // off: hair keeps the 0.6.4.0 layout
    private bool _splitEmotes;           // off: emotes stay in one Emotes folder
    private const string BodyModsHelpPopup = "sort-body-mods-help";
    private SortSelection? _lastSorted;
    private bool _prefsLoaded;
    private BodyPriority _bodyPriority = BodyPriority.Default([]);
    private IReadOnlyList<string>? _bodyLabelsSeen;

    private const string Male = "\u2642";
    private const string Female = "\u2640";

    // None first, then every confirmed race in table order. Labels are "Miqo'te <male sign>" and so on.
    private static readonly string[] HairRaceLabels =
        ["None", .. HairRaceCodes.All.Select(r => $"{r.Race} {(r.Gender == "Male" ? Male : Female)}")];

    // public, with HairRaceIndexFor and RefreshBodyPriority below: the pure decisions inside Draw,
    // reachable from the test project (no InternalsVisibleTo) without touching ImGui.
    public static string? HairRaceCodeAt(int index) =>
        index == 0 ? null : HairRaceCodes.All[index - 1].Code;

    /// <summary>
    /// The combo index for a saved race code: 0 (None) for null, and for a code this build's race
    /// table does not contain, so a stale saved value can neither throw nor select a random row.
    /// The inverse of <see cref="HairRaceCodeAt"/>.
    /// </summary>
    public static int HairRaceIndexFor(string? raceCode)
    {
        if (raceCode is null)
            return 0;

        var index = 0;
        foreach (var race in HairRaceCodes.All)
        {
            index++;
            if (race.Code == raceCode)
                return index;
        }

        return 0;
    }

    /// <summary>
    /// Decides whether the saved body priority must be re-validated this frame, and does it.
    /// Null means "nothing to do"; otherwise the repaired priority, with <paramref name="needsSave"/>
    /// saying whether the saved list changed and must be written back.
    /// </summary>
    /// <remarks>
    /// Nothing happens for an EMPTY label list: Repair against it would drop every saved entry and
    /// report a save, wiping the user's ranking if the body list ever loaded empty. And nothing
    /// happens while the label list is the same instance as last time, which is how a scan that
    /// published a different body list is told apart from a frame that did not.
    /// </remarks>
    public static BodyPriority? RefreshBodyPriority(
        IReadOnlyList<string> bodyLabels,
        IReadOnlyList<string>? labelsSeen,
        IReadOnlyList<string>? saved,
        out bool needsSave)
    {
        needsSave = false;
        if (bodyLabels.Count == 0 || ReferenceEquals(labelsSeen, bodyLabels))
            return null;

        return BodyPriority.Repair(saved, bodyLabels, out needsSave);
    }

    /// <summary>
    /// The body at rank 1, which is what the First-choice dropdown shows. Derived from the priority
    /// every frame and never stored separately, so it cannot disagree with the full list after an
    /// arrow move, a Reset or a body-list refresh. Null only for an empty list.
    /// </summary>
    public static string? FirstChoice(BodyPriority priority) =>
        priority.Labels.Count > 0 ? priority.Labels[0] : null;

    // A rule and a coloured heading above each mod-type group. The rule is ImGui's own separator,
    // so the groups read as separate without a new widget.
    private static void GroupHeading(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(PluginTheme.ChangedGood, title);
    }

    private string? _collectionBody;      // the single body the last check found
    private string? _collectionMessage;   // shown under the button after a check
    private string? _collectionLabel;     // which collection was read, for the messages

    public void Draw(
        OrganizerState state,
        ActivityGates gates,
        Configuration config,             // the scraped-list opt-in is persistent, not session
        Action saveConfig,
        Func<string, string> canonicalizeCreator,
        Func<IReadOnlyList<string>> getBodyLabels,
        Action? onShowSortHelp = null,
        Action? onSortButtonDrawn = null,
        Func<CollectionRead?>? readEnabledMods = null)
    {
        var bodyLabels = getBodyLabels();

        if (!_prefsLoaded)
        {
            _prefsLoaded = true;
            _hairRaceIndex = HairRaceIndexFor(config.HairRaceCode);
            _midlanderFallback = config.MidlanderFallback;
            _groupingIndex = GroupingIndexFor(config.SortGrouping);
            _splitGear = config.SortSplitGear;
            _splitNpc = config.SortSplitNpc;
            _splitHair = config.SortSplitHair;
            _splitGearBody = config.SortSplitGearBody;
            _splitBodyMods = config.SortSplitBodyMods;
            _sharedHandsFeet = config.SortSharedHandsFeet;
            _splitHairByRace = config.SortSplitHairByRace;
            _splitEmotes = config.SortSplitEmotes;
        }

        // Re-repaired only when a scan published a different body list (see RefreshBodyPriority), so
        // this costs nothing per frame. Repair drops stale labels and appends new bodies, and the
        // result is WRITTEN BACK when it changed a non-empty saved list. An empty saved list means
        // "default" and stays empty.
        if (RefreshBodyPriority(bodyLabels, _bodyLabelsSeen, config.BodyPriority, out var needsSave) is { } repaired)
        {
            _bodyLabelsSeen = bodyLabels;
            _bodyPriority = repaired;
            if (needsSave)
            {
                config.BodyPriority = [.. _bodyPriority.Labels];
                saveConfig();
            }
        }

        // Group by is its own tiny section so the tab reads top to bottom with the same headings.
        ImGui.TextColored(PluginTheme.ChangedGood, "Group by");
        ImGui.BeginDisabled(!gates.CanStageProposals);
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("##group-by", ref _groupingIndex, GroupingLabels, GroupingLabels.Length);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortGrouping, gates.CanStageProposals ? null : ActivityGateReason);

        // The strategy is settled the moment the combo has been submitted. Every control below takes
        // its visible and enabled state from SortLayout, which is what the tests pin.
        var strategy = Groupings[_groupingIndex].Strategy;
        var states = SortLayout.Compute(
            strategy, _splitHair, Configuration.ScrapedNpcListFeatureEnabled, gates.CanStageProposals, _splitGear);
        var splitReason = !gates.CanStageProposals ? ActivityGateReason
            : strategy == SortStrategy.CreatorOnly ? SplitsInapplicableReason
            : null;

        GroupHeading("Gear");
        DrawCheckbox("Split gear by equipment slot", ref _splitGear, states.SplitGear, HelpTopics.SortSplitGear, splitReason);
        if (states.SharedHandsFeet.Visible)
            DrawCheckbox("Hands and feet in a shared folder", ref _sharedHandsFeet, states.SharedHandsFeet, HelpTopics.SortSharedHandsFeet, splitReason);

        GroupHeading("NPC");
        DrawCheckbox("Split NPC mods by kind", ref _splitNpc, states.SplitNpc, HelpTopics.SortSplitNpc, splitReason);

        // The scraped-list opt-in and its refresh button are hidden while the feature is switched
        // off at compile time (Configuration.ScrapedNpcListFeatureEnabled): they did nothing but
        // explain, at length, why they were disabled. MainWindow draws no refresh controls either.
        if (states.ScrapedNpcCheckbox.Visible)
        {
            ImGui.BeginDisabled(!states.ScrapedNpcCheckbox.Enabled);
            var useScraped = config.UseScrapedNpcNameList;
            if (ImGui.Checkbox("Also use the NPC list scraped from the wiki", ref useScraped))
            {
                config.UseScrapedNpcNameList = useScraped;
                saveConfig();
            }
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.SortScrapedNpcList, states.ScrapedNpcCheckbox.Enabled ? null : ActivityGateReason);
        }

        GroupHeading("Hair");
        DrawCheckbox("Split hair by hair number", ref _splitHair, states.SplitHair, HelpTopics.SortSplitHair, splitReason);
        DrawCheckbox("Split hair by race", ref _splitHairByRace, states.SplitHairByRace, HelpTopics.SortSplitHairByRace, splitReason);
        if (states.PriorityRace.Visible)
        {
            ImGui.BeginDisabled(!states.PriorityRace.Enabled);
            ImGui.SetNextItemWidth(220);
            ImGui.Combo("Priority race", ref _hairRaceIndex, HairRaceLabels, HairRaceLabels.Length);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.SortPriorityRace, splitReason);
        }
        if (states.MidlanderFallback.Visible)
            DrawCheckbox("Midlander fallback", ref _midlanderFallback, states.MidlanderFallback,
                HelpTopics.SortMidlanderFallback, splitReason);

        var raceCode = HairRaceCodeAt(_hairRaceIndex);
        if (config.HairRaceCode != raceCode || config.MidlanderFallback != _midlanderFallback)
        {
            config.HairRaceCode = raceCode;
            config.MidlanderFallback = _midlanderFallback;
            saveConfig();
        }

        GroupHeading("Body type");
        DrawCheckbox("Split gear by body type", ref _splitGearBody, states.SplitGearBody,
            HelpTopics.SortSplitGearBody, splitReason);
        DrawCheckbox("Split body mods by body type", ref _splitBodyMods, states.SplitBodyMods,
            HelpTopics.SortSplitBodyMods, splitReason);
        DrawBodyModsHelpMarker();

        GroupHeading("Animation");
        DrawCheckbox("Split emotes by emote", ref _splitEmotes, states.SplitEmotes,
            HelpTopics.SortSplitEmotes, splitReason);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Every control above has now been submitted, so this is the selection as the user has it
        // THIS frame. The body priority section below may change the priority later in the frame; the
        // staleness hint then picks that up on the next frame, which is not visible.
        PersistSortChoices(config, saveConfig, strategy);

        var selection = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair,
            HairRaceCodeAt(_hairRaceIndex), _midlanderFallback, _splitGearBody, _splitBodyMods, _bodyPriority,
            new LayoutOptions(_sharedHandsFeet, _splitHairByRace, _splitEmotes));

        // The primary action: the plugin's purple accent, like Scan's Refresh and Search's Build.
        // Stable id: a label varying with mod count would vary the widget id, so a background scan
        // publishing mid-click would drop the click.
        bool sortClicked;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!states.SortButton.Enabled);
            sortClicked = ImGui.Button("Sort##sort-mods");
            onSortButtonDrawn?.Invoke();
            ImGui.EndDisabled();
        }
        Help.Tooltip(HelpTopics.SortButton, gates.CanStageProposals ? null : ActivityGateReason);
        if (sortClicked)
        {
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
                canonicalizeCreator, selection.HairPriority, selection.SplitGearBody, selection.SplitBodyMods,
                selection.BodyPriority, selection.Layout);
            _lastSorted = selection;
        }

        // Outside the activity gate on purpose: it only navigates to Help, so it works while a scan runs.
        ImGui.SameLine();
        if (ImGui.Button("?##sort-help"))
            onShowSortHelp?.Invoke();
        Help.Tooltip(HelpTopics.SortHelpButton);

        if (_lastSorted is { } last && last != selection)
            ImGui.TextDisabled("Selection changed since the last sort.");

        ImGui.Spacing();
        DrawBodyPriority(config, saveConfig, bodyLabels, states, anyBodySplit: _splitGearBody || _splitBodyMods,
            state, readEnabledMods);
    }

    // One checkbox: not drawn when hidden, greyed when disabled, and its tooltip always carries the
    // reason when it is greyed. Each closes its own disabled scope BEFORE the tooltip because
    // Help.Tooltip binds to the last submitted item and EndDisabled submits none.
    private static void DrawCheckbox(
        string label, ref bool value, ControlState state, HelpTopic topic, string? disabledReason)
    {
        if (!state.Visible)
            return;

        ImGui.BeginDisabled(!state.Enabled);
        ImGui.Checkbox(label, ref value);
        ImGui.EndDisabled();
        Help.Tooltip(topic, state.Enabled ? null : disabledReason);
    }

    // The "(?)" beside "Split body mods by body type": a small CLICKABLE button that opens the
    // explanation in a popup (a visible question mark is not itself an explanation), with the same
    // text as a hover tooltip as an extra. SmallButton is a normal ImGui item, so it takes
    // keyboard focus and Enter/Space like any button where ImGui keyboard navigation is on.
    private static void DrawBodyModsHelpMarker()
    {
        ImGui.SameLine();
        if (ImGui.SmallButton("(?)##body-mods-help"))
            ImGui.OpenPopup(BodyModsHelpPopup);
        Help.Tooltip(HelpTopics.SortSplitBodyMods);

        if (ImGui.BeginPopup(BodyModsHelpPopup))
        {
            ImGui.PushTextWrapPos(400f);
            ImGui.TextUnformatted(Help.Short(HelpTopics.SortSplitBodyMods) ?? string.Empty);
            ImGui.PopTextWrapPos();
            ImGui.EndPopup(); // BeginPopup closes on click-away and on Escape by itself
        }
    }

    // Always present, collapsed by default. Set once and forget: most people use one body, so the
    // First-choice dropdown is the common path and the full ranked list is nested beneath it.
    private void DrawBodyPriority(
        Configuration config, Action saveConfig, IReadOnlyList<string> bodyLabels,
        SortControlStates states, bool anyBodySplit,
        OrganizerState state, Func<CollectionRead?>? readEnabledMods)
    {
        var open = ImGui.CollapsingHeader("Body priority##body-priority");
        // Right after the header it explains: Help.Tooltip binds to the last submitted item, so a
        // tooltip at the end of the method would land on the Reset button or the nested header.
        Help.Tooltip(HelpTopics.SortBodyPriority);
        if (!open)
            return;

        ImGui.BeginDisabled(!states.FirstChoiceBody.Enabled);
        ImGui.TextDisabled(anyBodySplit
            ? "Highest first. Used when a mod supports more than one body."
            : "Highest first. Used when a mod supports more than one body. It applies when a body split above is ticked.");

        var order = _bodyPriority.Labels; // immutable: safe to iterate while a click replaces the field
        if (FirstChoice(_bodyPriority) is { } first)
        {
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("First-choice body##first-choice-body", first))
            {
                for (var i = 0; i < order.Count; i++)
                {
                    if (ImGui.Selectable($"{order[i]}##first-choice-{i}", i == 0))
                        SetBodyPriority(config, saveConfig, _bodyPriority.MoveToTop(order[i]));
                }
                ImGui.EndCombo();
            }
            Help.Tooltip(HelpTopics.SortFirstChoiceBody);
        }

        if (readEnabledMods is not null)
        {
            if (ImGui.SmallButton("Check my collection##check-collection"))
            {
                var read = readEnabledMods();
                if (read is null)
                {
                    _collectionBody = null;
                    _collectionLabel = null;
                    _collectionMessage = "Could not read your active collection. Is Penumbra running?";
                }
                else
                {
                    _collectionLabel = $"{(read.IsPlayerCollection ? "Player collection" : "Selected collection")} \"{read.Name}\"";
                    _collectionBody = ActiveCollectionBodyDetector.Detect(
                        state.Mods, read.EnabledModDirectories, _bodyPriority);
                    _collectionMessage = _collectionBody is null
                        ? $"{_collectionLabel}: no single body replacer found (none, or several, enabled)."
                        : null;
                }
            }
            Help.Tooltip(HelpTopics.SortCheckCollection);

            if (_collectionBody is { } suggested)
            {
                if (string.Equals(FirstChoice(_bodyPriority), suggested, StringComparison.OrdinalIgnoreCase))
                    ImGui.TextDisabled($"{_collectionLabel} has one enabled body replacer, {suggested}, already your first choice.");
                else
                {
                    // Metadata wording on purpose: this counts enabled replacer mods, it does not prove which
                    // body's files win in game (mod priority and options can still decide that).
                    ImGui.TextUnformatted($"{_collectionLabel} has one enabled body replacer: {suggested}.");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Make first choice##collection-first"))
                    {
                        SetBodyPriority(config, saveConfig, _bodyPriority.MoveToTop(suggested));
                        _collectionBody = null;
                    }
                }
            }
            else if (_collectionMessage is not null)
                ImGui.TextDisabled(_collectionMessage);
        }

        if (ImGui.CollapsingHeader("Full priority order##body-priority-full"))
        {
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

            ImGui.Spacing();
            if (ImGui.Button("Reset to default##body-priority-reset"))
            {
                // An EMPTY saved list is what "default" means, so a reset keeps following the shipped
                // order across releases instead of freezing today's order into the file.
                _bodyPriority = BodyPriority.Default(bodyLabels);
                config.BodyPriority = [];
                saveConfig();
            }
            ImGui.Spacing();
        }

        ImGui.EndDisabled();
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
