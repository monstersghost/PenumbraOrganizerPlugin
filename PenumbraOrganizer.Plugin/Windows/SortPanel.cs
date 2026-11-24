using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Operations;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Windows.Widgets;

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
    BodyPriority? BodyPriority = null, LayoutOptions Layout = default, bool RootOnly = false)
{
    // By Creator never consults category, so no split can change its output. All split checkboxes
    // are disabled when this is false.
    public bool SplitsApply => Strategy != SortStrategy.CreatorOnly;

    public HairPriority HairPriority => new(HairRaceCode, MidlanderFallback);

    /// <summary>
    /// A stable text for "what produced the staged paths": every setting that can change a folder and nothing
    /// else (RootOnly is a scope, not a setting). HairPriority is computed from HairRaceCode and
    /// MidlanderFallback, so both are included. The workbook export compares it to the current settings to
    /// decide whether the staged sort is still current.
    /// </summary>
    public string Fingerprint()
    {
        var race = Layout.Race;
        var raceText = race is null
            ? "-"
            : $"{race.Enabled}:{(int)race.Rows}:{race.MultiRaceToMultiRace}:{string.Join(',', race.Priority.Ranked)}";
        IEnumerable<string> bodyLabels = BodyPriority?.Labels ?? [];

        var core = string.Join(
            '|',
            Strategy, SplitGear, SplitNpc, SplitHair, HairRaceCode ?? "-", MidlanderFallback,
            SplitGearBody, SplitBodyMods, string.Join(',', bodyLabels),
            Layout.SharedHandsAndFeet, Layout.SplitHairByRace, Layout.SplitEmotes, raceText);

        // Appended only when a category has its own folder or creator level, so every layout the split flags
        // alone describe keeps its exact string from before category layouts. Before the rules, which go last.
        if (Layout.Placement is { } placement)
            core = $"{core}|cats={placement.Digest}";

        if (Layout.Rules is not { HasRules: true } rules)
            return core;

        // The digest is the SHA-256 of the length-prefixed rule text (so no character inside a rule can blur two
        // sets together) and goes LAST. Hashed so a hundred long rules still fit the staged export's one cell.
        var rulesRace = rules.UsesRace
            ? $"{rules.Race.Enabled}:{(int)rules.Race.Rows}:{rules.Race.MultiRaceToMultiRace}:{string.Join(',', rules.Race.Priority.Ranked)}"
            : "-";
        return $"{core}|rr={rulesRace}|rules={rules.DigestHash}";
    }
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
public sealed partial class SortPanel
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

    /// <summary>
    /// The panel's selection from its own control state. Public and static so a test can compare it with
    /// <see cref="SortSelectionFactory.FromConfig"/>: the rules come from the shared cache for the same config.
    /// </summary>
    public static SortSelection BuildSelection(
        Configuration config, SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair,
        string? hairRaceCode, bool midlanderFallback, bool splitGearBody, bool splitBodyMods,
        BodyPriority? bodyPriority, bool sharedHandsFeet, bool splitEmotes, RaceSplitOptions race, bool rootOnly) =>
        new(strategy, splitGear, splitNpc, splitHair, hairRaceCode, midlanderFallback, splitGearBody,
            splitBodyMods, bodyPriority,
            new LayoutOptions(
                sharedHandsFeet,
                race.InEffect(RaceRows.Hair, splitBodyMods),
                splitEmotes,
                race.NullIfNoEffect(),
                Rules: SortSelectionFactory.RulesFor(config),
                Placement: SortSelectionFactory.PlacementFor(config)),
            rootOnly);

    /// <summary>
    /// The Rules section: a collapsible list of "condition -> folder" lines. Edits go to a working
    /// <see cref="RulesEditor"/>; the config is written only when a text box is left, or a rule is added,
    /// removed, moved or toggled, never per keystroke or per frame.
    /// </summary>
    private void DrawRules(
        OrganizerState state, ActivityGates gates, Configuration config, Action saveConfig,
        Func<string, string> canonicalizeCreator)
    {
        _rules ??= new RulesEditor(config.SortRules, () => Environment.TickCount64);

        // ### keeps the id stable while the count in the label changes, so the header stays open or closed.
        var open = ImGui.CollapsingHeader($"Rules ({_rules.Rows.Count})###sort-rules");
        Help.Tooltip(HelpTopics.SortRules);
        if (!open)
            return;

        _rules.Observe(state.Mods, state.ScanGeneration, canonicalizeCreator,
            RaceSplitOptions.FromConfig(config), _bodyPriority, _splitGearBody, _splitBodyMods);
        _rules.Tick();

        var enabledEditing = gates.CanStageProposals;
        ImGui.BeginDisabled(!enabledEditing);
        ImGui.TextDisabled("condition -> folder. The first matching rule files the mod; the rest sort as usual.");
        var persist = false;
        for (var i = 0; i < _rules.Rows.Count; i++)
        {
            ImGui.PushID(i);
            var row = _rules.Rows[i];
            var on = row.Enabled;
            if (ImGui.Checkbox("##rule-on", ref on)) { _rules.SetEnabled(i, on); persist = true; }
            ImGui.SameLine();
            var text = row.Text;
            ImGui.SetNextItemWidth(-130);
            // Buffer size is in bytes: four per UTF-16 unit leaves room for any text the 300-unit limit
            // allows; the parser reports anything longer.
            if (ImGui.InputText("##rule-text", ref text, RuleParser.MaxLength * 4))
                _rules.SetText(i, text);
            if (ImGui.IsItemDeactivatedAfterEdit())
                persist = true;
            ImGui.SameLine();
            if (ImGui.SmallButton("Up") && _rules.MoveUp(i)) persist = true;
            ImGui.SameLine();
            if (ImGui.SmallButton("Dn") && _rules.MoveDown(i)) persist = true;
            ImGui.SameLine();
            var remove = ImGui.SmallButton("X");

            var status = _rules.Status(i);
            if (status.IsError)
                ImGui.TextColored(PluginTheme.CollisionBad, status.Text);
            else
                ImGui.TextDisabled(status.Text);
            if (_rules.Warning(i) is { } warning)
                ImGui.TextColored(PluginTheme.Protected, warning);
            if (_rules.ProtectionWarning(i, folder =>
                    state.ResolveFolderRule(folder).Rule == FolderProtectionRule.Protected) is { } protectedWarning)
                ImGui.TextColored(PluginTheme.Protected, protectedWarning);
            ImGui.PopID();

            if (remove)
            {
                _rules.Remove(i);
                persist = true;
                break;
            }
        }

        if (ImGui.Button("Add rule##add-sort-rule") && _rules.Add())
            persist = true;
        if (!_rules.CanAdd)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{RuleSet.MaxRules} rules is the limit.");
        }

        ImGui.EndDisabled();

        if (persist)
        {
            config.SortRules = _rules.ToConfig();
            config.SortRulesVersion++;
            saveConfig();
        }
    }

    // The checkboxes are an adapter over the category layouts: a change writes the matching split of its
    // category and, through the config, the legacy fields as a rollback mirror. The decision is the pure
    // SortPanelLayoutAdapter.Persist (tested); it compares against the cached profile, so a frame with no change
    // writes and allocates nothing. A read-only category's boxes are put back to what is saved.
    private void PersistSortChoices(Configuration config, Action saveConfig, SortStrategy strategy)
    {
        var (save, shown) = SortPanelLayoutAdapter.Persist(
            config, CurrentToggles(), strategy.ToString(), _raceEnabled, _multiRaceToMultiRace, _racePriority);
        ApplyToggles(shown);
        if (save)
            saveConfig();
    }

    // Categories customised beyond what the checkboxes can show, and the note naming them. Recomputed only when
    // the cache hands out a different profile, so not per frame.
    private LayoutProfile? _profileSeen;
    private IReadOnlySet<ModCategory> _readOnly = new HashSet<ModCategory>();
    private string? _customNote;

    private void RefreshReadOnly(Configuration config)
    {
        var profile = LayoutProfileCache.For(config);
        if (ReferenceEquals(profile, _profileSeen))
            return;
        _profileSeen = profile;
        _readOnly = SortPanelLayoutAdapter.ReadOnlyCategories(profile);
        _customNote = SortPanelLayoutAdapter.CustomNote(_readOnly);
    }

    private ControlState Gate(ControlState state, ModCategory category) =>
        _readOnly.Contains(category) ? state with { Enabled = false } : state;

    private string? ReasonFor(ModCategory category, string? reason) =>
        _readOnly.Contains(category) ? SortPanelLayoutAdapter.ReadOnlyReason : reason;
    private bool _splitGear;
    private bool _splitNpc = true;        // matches the always-on NPC subdivision it replaces
    private bool _splitHair;              // off, like gear: an unchanged install keeps flat Hair/
    private int _hairRaceIndex;              // 0 is None; see HairRaceLabels
    private bool _midlanderFallback;         // off: an unchanged install keeps the 0.6.1.0 rule
    private bool _splitGearBody;         // off: no body folders until asked, like gear and hair
    private bool _splitBodyMods;
    private bool _sharedHandsFeet;           // off: hands and feet stay under their body
    private bool _splitEmotes;       // off: emotes stay in one Emotes folder

    // The Race section. Master off and every row ticked is the fresh-install state; the master is a pure
    // gate that never changes the ticks. Saved the moment any of them changes, like the other Sort choices.
    private bool _raceEnabled;
    private RaceRows _raceRows = RaceRows.All;
    private bool _multiRaceToMultiRace = true;
    private RacePriority _racePriority = RacePriority.Empty;
    private const string BodyModsHelpPopup = "sort-body-mods-help";
    private SortSelection? _lastSorted;
    private bool _prefsLoaded;
    private int _whatToSortIndex;        // 0 whole library, 1 loose in root; session-only, never saved

    private static readonly string[] WhatToSortLabels = ["Whole library", "Only loose mods in the root"];

    /// <summary>The scope for the "What to sort" choice; null means the whole library, as before.</summary>
    public static SortScope? ScopeFor(bool rootOnly) => rootOnly ? new SortScope(true) : null;
    private BodyPriority _bodyPriority = BodyPriority.Default([]);
    private RulesEditor? _rules;      // the working copy of the Sort rules; saved on edit, never per frame
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

    private const string SelectAllPopupTitle = "Select all splits?";

    private SplitToggles CurrentToggles() => new(
        _splitGear, _splitNpc, _splitHair, _splitGearBody, _splitBodyMods, _sharedHandsFeet, _splitEmotes,
        _raceEnabled, _raceRows);

    private void ApplyToggles(SplitToggles t)
    {
        _splitGear = t.SplitGear;
        _splitNpc = t.SplitNpc;
        _splitHair = t.SplitHair;
        _splitGearBody = t.SplitGearBody;
        _splitBodyMods = t.SplitBodyMods;
        _sharedHandsFeet = t.SharedHandsFeet;
        _splitEmotes = t.SplitEmotes;
        _raceEnabled = t.RaceEnabled;
        _raceRows = t.RaceRows;
    }

    // One button above the split sections, with a confirmation: it changes many choices at once. It only
    // ticks boxes; nothing is sorted until Sort is pressed. Greyed when the splits are, with the same reason.
    private void DrawSelectAll(bool enabled, string? disabledReason)
    {
        ImGui.BeginDisabled(!enabled);
        if (ImGui.Button("Select all splits##select-all-splits"))
            ImGui.OpenPopup(SelectAllPopupTitle);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(enabled
                ? "Ticks every split and race row for the most detailed layout."
                : disabledReason ?? "Not available right now.");

        if (!ImGui.BeginPopupModal(SelectAllPopupTitle))
            return;

        ImGui.TextUnformatted("This turns on every split for the most detailed layout:");
        ImGui.BulletText("every gear, NPC, hair, body and emote split");
        ImGui.BulletText("the race split with every race row");
        ImGui.BulletText("hands and feet get their own folders (not shared)");
        ImGui.TextUnformatted("Your Group by, race priority and body priority stay as they are.");
        ImGui.TextUnformatted("Nothing moves until you press Sort, review the plan and Apply.");
        ImGui.Spacing();

        if (ImGui.Button("Select all##confirm-select-all"))
        {
            // Through the layout editor, so the change is saved once; the race master switch is global.
            _raceEnabled = _editor!.SelectAll();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##cancel-select-all"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

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

    /// <summary>
    /// The pinned top of the Sort tab: the Recommended | Advanced switch, the Sort button and its summary. MainWindow
    /// draws it OUTSIDE the scrolling region so it stays in view while the controls below scroll. It reads
    /// the other controls' values from the previous frame, which is invisible: a click on Sort cannot land
    /// in the same frame as a click on another control.
    /// </summary>
    public void DrawHeader(
        OrganizerState state,
        ActivityGates gates,
        Configuration config,             // the scraped-list opt-in is persistent, not session
        Action saveConfig,
        Func<string, string> canonicalizeCreator,
        Func<IReadOnlyList<string>> getBodyLabels,
        Action? onShowSortHelp = null,
        Action? onSortButtonDrawn = null)
    {
        var bodyLabels = getBodyLabels();

        if (!_prefsLoaded)
        {
            _prefsLoaded = true;
            var loaded = SortSelectionFactory.FromConfig(config, bodyLabels);
            _hairRaceIndex = HairRaceIndexFor(loaded.HairRaceCode);
            _midlanderFallback = loaded.MidlanderFallback;
            _groupingIndex = Array.FindIndex(Groupings, g => g.Strategy == loaded.Strategy);
            _splitGear = loaded.SplitGear;
            _splitNpc = loaded.SplitNpc;
            _splitHair = loaded.SplitHair;
            _splitGearBody = loaded.SplitGearBody;
            _splitBodyMods = loaded.SplitBodyMods;
            _sharedHandsFeet = loaded.Layout.SharedHandsAndFeet;
            // Read from the config, not from loaded.Layout.Race: the selection carries null when the race
            // split adds nothing beyond the legacy hair flag, which would lose the user's rows and priority.
            var race = RaceSplitOptions.FromConfig(config);
            _raceEnabled = race.Enabled;
            _raceRows = race.Rows;
            _multiRaceToMultiRace = race.MultiRaceToMultiRace;
            _racePriority = race.Priority;
            _splitEmotes = loaded.Layout.SplitEmotes;
            if (loaded.BodyPriority is { } loadedPriority)
                _bodyPriority = loadedPriority;
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

        // The pinned bar (spec 2, section 2): mode switch, Sort, the scope summary and ?. Group by and What to sort
        // moved into the screens below (DrawGroupingRow), so this reads them from the previous frame, as it already
        // did for every other control.
        if (!_modeChosen)
        {
            _modeChosen = true;
            _mode = CategoryLayoutEditor.InitialMode(LayoutProfileCache.For(config));
        }

        DrawModeSwitch();
        ImGui.SameLine();
        var rootOnly = _whatToSortIndex == 1;

        // The strategy is settled the moment the combo has been submitted. Every control below takes
        // its visible and enabled state from SortLayout, which is what the tests pin.
        var strategy = Groupings[_groupingIndex].Strategy;
        var states = SortLayout.Compute(
            strategy, _splitHair, Configuration.ScrapedNpcListFeatureEnabled, gates.CanStageProposals, _splitGear);
        PersistSortChoices(config, saveConfig, strategy);

        var raceNow = new RaceSplitOptions(_raceEnabled, _raceRows, _multiRaceToMultiRace, _racePriority);
        var selection = BuildSelection(config, strategy, _splitGear, _splitNpc, _splitHair,
            HairRaceCodeAt(_hairRaceIndex), _midlanderFallback, _splitGearBody, _splitBodyMods, _bodyPriority,
            _sharedHandsFeet, _splitEmotes, raceNow, rootOnly);

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
            SortRunner.Run(state, selection, canonicalizeCreator, ScopeFor(rootOnly));
            _lastSorted = selection;
        }

        if (state.LastSortSummary is { } summary)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(SortSummaryText.Describe(summary));
            Help.Tooltip(HelpTopics.SortScopeLine);
        }

        // Outside the activity gate on purpose: it only navigates to Help, so it works while a scan runs.
        ImGui.SameLine();
        if (IconButton.Draw("sort-help", FontAwesomeIcon.QuestionCircle))
            onShowSortHelp?.Invoke();
        Help.Tooltip(HelpTopics.SortHelpButton);

        if (_lastSorted is { } last && last != selection)
            ImGui.TextDisabled("Selection changed since the last sort.");

        ImGui.Separator();
    }

    /// <summary>
    /// Everything under the pinned header: the Recommended or Advanced screen, then the Rules and Sort settings
    /// cards. Drawn inside MainWindow's scrolling region.
    /// </summary>
    public void DrawBody(
        OrganizerState state,
        ActivityGates gates,
        Configuration config,
        Action saveConfig,
        Func<string, string> canonicalizeCreator,
        Func<IReadOnlyList<string>> getBodyLabels,
        Func<CollectionRead?>? readEnabledMods = null)
    {
        var bodyLabels = getBodyLabels();
        var strategy = Groupings[_groupingIndex].Strategy;
        var states = SortLayout.Compute(
            strategy, _splitHair, Configuration.ScrapedNpcListFeatureEnabled, gates.CanStageProposals, _splitGear);
        var splitReason = !gates.CanStageProposals ? ActivityGateReason
            : strategy == SortStrategy.CreatorOnly ? SplitsInapplicableReason
            : null;

        if (_mode == SortTabMode.Recommended)
            DrawRecommended(config, gates, strategy);
        else
            DrawAdvancedLayout(state, gates, config, saveConfig, strategy, states, splitReason, bodyLabels, readEnabledMods);

        DrawRulesCard(state, gates, config, saveConfig, canonicalizeCreator);
        DrawSortSettingsCard(config, saveConfig);
        PersistHairChoices(config, saveConfig);

        // The header persists at the start of the next frame; doing it here as well keeps a change made by
        // the last control of a frame from being lost if the window closes right after it.
        PersistSortChoices(config, saveConfig, strategy);
    }

    private static readonly string[] NewModActionLabels = ["Ask", "Sort automatically", "Do nothing"];

    // The Sort settings card (formerly the "Advanced" section; Advanced is now a mode). Collapsed by default. Saved
    // the moment it changes, like the other Sort tab choices.
    private static void DrawSortSettings(Configuration config, Action saveConfig)
    {
        if (!ImGui.CollapsingHeader("Sort settings###sort-settings"))
            return;

        var index = (int)config.NewModAction;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("When new mods are installed##new-mod-action", ref index,
                NewModActionLabels, NewModActionLabels.Length))
        {
            config.NewModAction = (Organizer.Collections.NewModAction)index;
            saveConfig();
        }
        Help.Tooltip(HelpTopics.SortNewModAction);

        // The same flag as the popup's own "Don't show this popup", inverted there.
        var showPopup = config.ShowNewModPopup;
        if (ImGui.Checkbox(Organizer.NewMods.NewModPromptText.ShowPopupSettingLabel + "##new-mod-popup", ref showPopup))
        {
            config.ShowNewModPopup = showPopup;
            saveConfig();
        }
        Help.Tooltip(HelpTopics.SortShowNewModPopup);
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

    private const string RaceNeedsBodySplitReason = "This row's folder only exists while body mods are split by body type.";
    private const string RaceRescanHint = "Scan again to use race split for faces, skin, tails and ears.";

    // The Race section: master switch, the six rows, the multi-race toggle and the ranked race list.
    // Every row is always visible. A row that needs the body split stays ticked while that split is off
    // and simply greys, so nothing the user chose is cleared. Under Creator grouping the whole section is
    // readable but not editable, with the reason in each tooltip.
    private void DrawRaceSplit(OrganizerState state, ControlState section, string? reason)
    {
        DrawCheckbox("Split supported categories by race", ref _raceEnabled, section, HelpTopics.SortRaceSplit, reason);

        foreach (var (row, _, label) in RaceRowNames.All)
        {
            var ticked = _raceRows.HasFlag(row);
            var needsBody = RaceSplitOptions.NeedsBodySplit(row);
            var readOnly = _readOnly.Contains(SortPanelLayoutAdapter.CategoryOf(row));
            var enabled = section.Enabled && (!needsBody || _splitBodyMods) && !readOnly;

            ImGui.Indent();
            ImGui.BeginDisabled(!enabled);
            if (ImGui.Checkbox($"{label}##race-row-{row}", ref ticked))
                _raceRows = ticked ? _raceRows | row : _raceRows & ~row;
            ImGui.EndDisabled();
            Help.Tooltip(
                row == RaceRows.Hair ? HelpTopics.SortSplitHairByRace : HelpTopics.SortRaceRows,
                enabled ? null
                : readOnly ? SortPanelLayoutAdapter.ReadOnlyReason
                : needsBody && section.Enabled ? RaceNeedsBodySplitReason : reason);
            ImGui.Unindent();
        }

        ImGui.Indent();
        DrawCheckbox("Send multi-race mods to Multi-race", ref _multiRaceToMultiRace, section, HelpTopics.SortMultiRace, reason);
        ImGui.Unindent();

        DrawRacePriority(section);

        if (_raceEnabled && state.Mods.Any(m => m.KeyRaces is null
                && m.Category is Core.Classification.ModCategory.Face or Core.Classification.ModCategory.Skin
                    or Core.Classification.ModCategory.Body))
            ImGui.TextDisabled(RaceRescanHint);
    }

    // Collapsed by default, like Body priority. Ranked races win in order; an unranked race never wins.
    private void DrawRacePriority(ControlState section)
    {
        var open = ImGui.CollapsingHeader("Race priority##race-priority");
        Help.Tooltip(HelpTopics.SortRacePriority);
        if (!open)
            return;

        ImGui.BeginDisabled(!section.Enabled);
        ImGui.TextDisabled("Highest first. Used when a mod supports more than one race and the Multi-race box above is off.");

        var ranked = _racePriority.Ranked; // immutable: safe to iterate while a click replaces the field
        for (var i = 0; i < ranked.Count; i++)
        {
            ImGui.PushID(i);

            ImGui.BeginDisabled(i == 0);
            if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                _racePriority = _racePriority.MoveUp(i);
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(i == ranked.Count - 1);
            if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                _racePriority = _racePriority.MoveDown(i);
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove##remove"))
                _racePriority = _racePriority.Remove(ranked[i]);

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. {ranked[i]}");

            ImGui.PopID();
        }

        var unranked = RaceNames.All.Where(r => _racePriority.RankOf(r) < 0).ToList();
        if (unranked.Count > 0)
        {
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("Add a race##race-priority-add", "Choose a race"))
            {
                foreach (var race in unranked)
                {
                    if (ImGui.Selectable($"{race}##race-add-{race}"))
                        _racePriority = _racePriority.Add(race);
                }
                ImGui.EndCombo();
            }
        }

        ImGui.EndDisabled();
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
