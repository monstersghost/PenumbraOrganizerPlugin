using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Operations;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Windows;

// The Advanced screen (spec 2, section 4): a header row, the category list beside the editor for the selected
// category, and the panels that belong to some categories (Race, Body types, Hair priority, NPC list). Drawing only;
// the state is CategoryLayoutEditor's.
public sealed partial class SortPanel
{
    private static readonly string[] CreatorLabels = ["None", "Before type folders", "After type folders"];
    private const float CategoryListMaxHeight = 470f;

    // Created on the first Advanced frame from the saved layouts. Every change it makes is saved at once through
    // SaveLayoutProfile (which also writes the legacy mirror), never per frame.
    private CategoryLayoutEditor? _editor;

    private CategoryLayoutEditor EditorFor(Configuration config, Action saveConfig) =>
        _editor ??= new CategoryLayoutEditor(LayoutProfileCache.For(config), profile =>
        {
            config.SaveLayoutProfile(profile);
            // Until the old checkbox fields are removed, they mirror the editor, so PersistSortChoices and
            // BuildSelection see the same splits and write nothing back.
            ApplyToggles(SortPanelLayoutAdapter.TogglesFor(profile, _raceEnabled));
            saveConfig();
        });

    private void DrawAdvancedLayout(
        OrganizerState state, ActivityGates gates, Configuration config, Action saveConfig, SortStrategy strategy,
        SortControlStates states, string? splitReason, IReadOnlyList<string> bodyLabels,
        Func<CollectionRead?>? readEnabledMods)
    {
        var editor = EditorFor(config, saveConfig);
        var gateOpen = gates.CanStageProposals;
        var gateReason = gateOpen ? null : ActivityGateReason;

        using var card = Card.Begin("##sort-advanced-layout");
        if (!card)
            return;

        ImGui.TextUnformatted("Advanced layout");
        ImGui.TextDisabled($"{editor.AdvancedSummary}. Everything else follows Recommended.");
        DrawSelectAll(states.SplitGear.Enabled, splitReason);
        ImGui.SameLine();
        DrawAdvancedHeaderButtons(editor, gateOpen, gateReason);
        ImGui.Spacing();
        DrawGroupingRow(gates);
        ImGui.Spacing();

        var columns = WidgetLayout.Columns(ImGui.GetContentRegionAvail().X, ImGui.GetStyle().ItemSpacing.X);
        DrawCategoryList(editor, columns.ListWidth);
        if (!columns.Stacked)
            ImGui.SameLine();

        ImGui.BeginGroup();
        DrawCategoryEditor(state, editor, strategy, gateOpen, gateReason);
        DrawCategoryPanels(state, config, saveConfig, editor.Selected, states, splitReason, bodyLabels, readEnabledMods);
        ImGui.EndGroup();
    }

    private static void DrawAdvancedHeaderButtons(CategoryLayoutEditor editor, bool gateOpen, string? gateReason)
    {
        ImGui.BeginDisabled(!gateOpen || editor.CustomCount == 0);
        if (ImGui.Button("Reset all to Recommended##reset-all-layouts"))
            editor.ResetAll();
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortResetLayout, gateReason);
    }

    // Scrolls on its own when taller than the space it is given.
    private static void DrawCategoryList(CategoryLayoutEditor editor, float width)
    {
        var rowHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var height = MathF.Min(CategoryListMaxHeight,
            rowHeight * LayoutCatalog.Categories.Count + ImGui.GetStyle().WindowPadding.Y);
        using var list = ImRaii.Child("##sort-category-list", new Vector2(width, height), false);
        if (!list)
            return;

        foreach (var category in LayoutCatalog.Categories)
        {
            if (CategoryRow.Draw($"##category-{category}", category.ToString(), category == editor.Selected,
                    editor.IsCustom(category)))
                editor.Select(category);
            Help.Tooltip(HelpTopics.SortCategoryList);
        }
    }

    private void DrawCategoryEditor(
        OrganizerState state, CategoryLayoutEditor editor, SortStrategy strategy, bool gateOpen, string? gateReason)
    {
        var category = editor.Selected;
        ImGui.TextUnformatted(category.ToString());
        ImGui.SameLine();
        if (editor.IsCustom(category))
        {
            Badge.Draw("Custom", BadgeKind.Warning);
            ImGui.SameLine();
            ImGui.BeginDisabled(!gateOpen);
            if (ImGui.SmallButton("Reset to Recommended##reset-category"))
                editor.Reset(category);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.SortResetLayout, gateReason);
        }
        else
        {
            ImGui.TextDisabled("Using Recommended");
        }

        // Folder: validated as it is typed, saved when the box is left; an invalid value is never saved.
        ImGui.Spacing();
        ImGui.TextDisabled("Folder (nest with /, for example Equipment/Gear)");
        ImGui.BeginDisabled(!gateOpen);
        var text = editor.FolderText;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##category-folder", ref text, LayoutFolderBufferBytes))
            editor.SetFolderText(text);
        if (ImGui.IsItemDeactivatedAfterEdit())
            editor.Commit();
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortCategoryFolder, gateReason);
        if (editor.FolderError is { } error)
            ImGui.TextColored(PluginTheme.CollisionBad, error);
        else if (editor.FolderWarning(IsProtectedFolder(state), IsSortInsideFolder(state)) is { } warning)
            ImGui.TextColored(PluginTheme.Warning, warning);

        // Creator folder: the effective level is selected; picking Group by's own level keeps following it.
        ImGui.Spacing();
        ImGui.TextDisabled("Creator folder");
        var creatorOnly = strategy == SortStrategy.CreatorOnly;
        var creator = (int)(editor.CreatorShown(strategy) ?? CreatorLevel.None);
        ImGui.BeginDisabled(!gateOpen || creatorOnly);
        if (SegmentedControl.Draw("creator-folder", CreatorLabels, ref creator))
            editor.SetCreator((CreatorLevel)creator, strategy);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortCreatorFolder, gateReason ?? (creatorOnly ? SplitsInapplicableReason : null));
        if (editor.FollowsGroupBy && !creatorOnly)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Follows Group by");
        }

        // Splits, in the engine's fixed order. Folder splits are toggles with their depth; a switch that adds no
        // folder (hands and feet) is a checkbox with no number.
        ImGui.Spacing();
        ImGui.TextDisabled("Splits (numbers are the folder depth; the order is fixed)");
        Help.Tooltip(HelpTopics.SortSplits);
        var rows = editor.Splits(strategy, _raceEnabled, gateOpen);
        if (rows.Count == 0)
            ImGui.TextDisabled(CategoryLayoutEditor.NoSplitsText);
        foreach (var row in rows)
        {
            ImGui.PushID(row.Info.Split.ToString());
            var rowStart = ImGui.GetCursorPosX();
            ImGui.TextDisabled(row.Depth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? " ");
            // SetCursorPosX, not SameLine(offset): SameLine's offset is from the group start, and this editor sits in
            // a group beside the category list, so the local X read above would count that offset twice.
            ImGui.SameLine();
            ImGui.SetCursorPosX(rowStart + ImGui.GetFrameHeight());
            ImGui.BeginDisabled(!row.Enabled);
            var on = row.On;
            var changed = row.Info.IsFolder
                ? Toggle.Draw("##split", row.Label, ref on)
                : ImGui.Checkbox($"{row.Label}##split", ref on);
            ImGui.EndDisabled();
            Help.Tooltip(SplitHelp.TopicFor(category, row.Info.Split), row.DisabledReason);
            if (changed)
                editor.SetSplit(row.Info.Split, on);
            ImGui.Indent(ImGui.GetFrameHeight());
            ImGui.TextDisabled(row.Hint);
            ImGui.Unindent(ImGui.GetFrameHeight());
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Example");
        PathPreview.Draw(editor.Example(strategy, _raceEnabled));
        Help.Tooltip(HelpTopics.SortExamplePath);
    }

    // Four bytes per UTF-16 unit for the 260-character cap, so any name the check allows fits; longer text is
    // reported by the check instead.
    private const int LayoutFolderBufferBytes = 260 * 4 + 1;

    private static Func<string, bool> IsProtectedFolder(OrganizerState state) =>
        folder => state.ResolveFolderRule(folder).Rule == FolderProtectionRule.Protected;

    private static Func<string, bool> IsSortInsideFolder(OrganizerState state) =>
        folder => state.ToolPolicy.SortInsideFolders?.Any(anchor => CategoryLayoutEditor.IsInside(folder, anchor)) == true;

    // The settings that belong to some categories only (spec 2, section 5, resolved in the plan): Hair priority race
    // and Midlander fallback under Hair; the Race panel (master switch, multi-race, race priority, rescan hint) under
    // every category with a race split; Body types (body priority, Check my collection) under Gear and Body; the
    // scraped NPC list under NPC while that feature is switched on.
    private void DrawCategoryPanels(
        OrganizerState state, Configuration config, Action saveConfig, ModCategory category, SortControlStates states,
        string? splitReason, IReadOnlyList<string> bodyLabels, Func<CollectionRead?>? readEnabledMods)
    {
        if (category == ModCategory.Hair)
            DrawHairPanel(states, splitReason);
        if (LayoutCatalog.SplitsFor(category).Any(s => s.NeedsScanEvidence))
            DrawRacePanel(state, states.RaceSplit, splitReason);
        if (category is ModCategory.Gear or ModCategory.Body)
        {
            ImGui.Spacing();
            using var card = Card.Begin("##sort-body-types");
            if (card)
            {
                ImGui.TextUnformatted("Body types");
                DrawBodyPriority(config, saveConfig, bodyLabels, states,
                    anyBodySplit: _splitGearBody || _splitBodyMods, state, readEnabledMods);
            }
        }

        if (category == ModCategory.NPC && states.ScrapedNpcCheckbox.Visible)
            DrawScrapedNpcList(config, saveConfig, states);
    }

    // Shown only while Hair number is on (SortLayout's rule). Saved by PersistHairChoices, every frame it changes.
    private void DrawHairPanel(SortControlStates states, string? splitReason)
    {
        if (states.PriorityRace.Visible)
        {
            ImGui.Spacing();
            ImGui.BeginDisabled(!states.PriorityRace.Enabled);
            ImGui.SetNextItemWidth(220);
            ImGui.Combo("Priority race", ref _hairRaceIndex, HairRaceLabels, HairRaceLabels.Length);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.SortPriorityRace, splitReason);
        }

        if (states.MidlanderFallback.Visible)
            DrawCheckbox("Midlander fallback", ref _midlanderFallback, states.MidlanderFallback,
                HelpTopics.SortMidlanderFallback, splitReason);
    }

    // The hair priority race and Midlander fallback, written the moment they differ from the config. A comparison
    // per frame, no allocation; run whichever screen is showing, as the old Hair section did.
    private void PersistHairChoices(Configuration config, Action saveConfig)
    {
        var raceCode = HairRaceCodeAt(_hairRaceIndex);
        if (config.HairRaceCode == raceCode && config.MidlanderFallback == _midlanderFallback)
            return;
        config.HairRaceCode = raceCode;
        config.MidlanderFallback = _midlanderFallback;
        saveConfig();
    }

    // The global race settings. The per-category race splits are rows of the editor above.
    private void DrawRacePanel(OrganizerState state, ControlState section, string? reason)
    {
        ImGui.Spacing();
        using var card = Card.Begin("##sort-race-panel");
        if (!card)
            return;

        ImGui.TextUnformatted("Race");
        DrawCheckbox("Split by race", ref _raceEnabled, section, HelpTopics.SortRaceSplit, reason);
        DrawCheckbox("Send multi-race mods to Multi-race", ref _multiRaceToMultiRace, section, HelpTopics.SortMultiRace, reason);
        DrawRacePriority(section);

        if (_raceEnabled && state.Mods.Any(m => m.KeyRaces is null
                && m.Category is ModCategory.Face or ModCategory.Skin or ModCategory.Body))
            ImGui.TextDisabled(RaceRescanHint);
    }

    // Hidden while the feature is switched off at compile time (Configuration.ScrapedNpcListFeatureEnabled).
    private static void DrawScrapedNpcList(Configuration config, Action saveConfig, SortControlStates states)
    {
        ImGui.Spacing();
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
}
