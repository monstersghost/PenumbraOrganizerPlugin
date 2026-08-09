using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed partial class MainWindow
{
    private void DrawSortTab()
    {
        using var tab = ImRaii.TabItem("Sort");
        if (!tab)
            return;

        var gates = CurrentGates();

        _sortPanel.Draw(
            _plugin.OrganizerState, gates, _plugin.Config, _plugin.SaveConfig,
            _creatorCanonicalizer.Canonicalize);

        ImGui.Spacing();

        // Re-established explicitly rather than inherited. Import Workbook was the eighth element of
        // the sort button row, sharing that row's BeginDisabled scope and its trailing tooltip; the
        // row is gone, so both are restated here. Its behaviour, including the re-check inside the
        // dialog callback, is unchanged.
        ImGui.BeginDisabled(!gates.CanStageProposals);
        if (ImGui.Button("Import Workbook"))
        {
            _fileDialogManager.OpenFileDialog(
                "Import Workbook",
                ".xlsx",
                (success, paths) =>
                {
                    // The dialog callback fires on a later frame, after this frame's BeginDisabled
                    // has already lapsed - a scan or index build can have started in between, and
                    // ReplaceScanAtomically would silently wipe whatever ImportWorkbook is about to
                    // stage. Re-check the gate here, at the moment the import would actually run.
                    if (!success || paths.Count == 0)
                        return;
                    if (!CurrentGates().CanStageProposals)
                    {
                        _lastError = "Import Workbook was cancelled because library work started before the file was chosen.";
                        return;
                    }
                    ImportWorkbook(paths[0]);
                },
                selectionCountMax: 1);
        }
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortImportWorkbook,
            gates.CanStageProposals ? null : "Another operation is in progress or requires recovery.");

        if (_lastWorkbookImportResult is not null)
        {
            ImGui.TextWrapped(_lastWorkbookImportResult.Summary);
            foreach (var error in _lastWorkbookImportResult.Errors)
                TextColoredWrapped(PluginTheme.CollisionBad, $"  {error}");
            foreach (var warning in _lastWorkbookImportResult.Warnings)
                TextColoredWrapped(ImGuiColors.DalamudYellow, $"  {warning}");
        }

        ImGui.Spacing();
        // Disabled in 0.5.3.1 rather than removed: a full scrape produces roughly 21,000 names, and
        // building the matcher from a list that size is implicated in reports of the game closing
        // instantly during Scan and the Search index build. Re-enabled once the matcher no longer
        // builds one giant compiled alternation per category.
        ImGui.BeginDisabled(true);
        ImGui.Button("Refresh NPC list from wiki");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Temporarily unavailable in this version. See the 0.5.3.1 release notes.");
        TextColoredWrapped(ImGuiColors.DalamudYellow,
            "Refreshing the NPC list is turned off in this version. A full refresh produced a list "
            + "large enough to crash the game on the next scan. NPC classification still works from "
            + "the list that ships with the plugin.");

        if (_npcRefreshResult is not null)
        {
            if (_npcRefreshResult.RecoveredFromCorruption)
                TextColoredWrapped(ImGuiColors.DalamudYellow,
                    "The existing NPC name list was unreadable and has been reset from the bundled "
                    + "seed list; the old file was preserved alongside it as a timestamped backup.");

            foreach (var category in _npcRefreshResult.Categories)
            {
                if (category.FailureReason is not null)
                    TextColoredWrapped(PluginTheme.CollisionBad, $"  {category.CategoryName} failed: {category.FailureReason}");
                else
                    // A plain total, not "+N": a refresh now writes a snapshot, so the number is
                    // how many names the category holds afterwards, and it can go down.
                    ImGui.TextUnformatted($"  {category.CategoryName}: {category.NameCount} names");
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: check mods below, type a folder, click Assign.");

        ImGui.InputText("Search mods##manual-assign", ref _manualAssignFilter, 256);
        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        ImGui.SameLine();
        ImGui.BeginDisabled(!gates.CanStageProposals);
        var assignClicked = ImGui.Button($"Assign {_selectedManualModIdentifiers.Count} selected mods");
        ImGui.EndDisabled();
        if (!gates.CanStageProposals && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Another operation is in progress or requires recovery.");
        if (assignClicked && _manualFolderInput.Length > 0 && _selectedManualModIdentifiers.Count > 0)
        {
            var batchResults = _plugin.OrganizerState.AssignManualBatch(_selectedManualModIdentifiers, _manualFolderInput);
            var succeeded = batchResults.Count(r => r.Success);
            _lastManualAssignSummary = $"{succeeded} assigned, {batchResults.Count - succeeded} skipped (no longer eligible)";
        }

        if (_lastManualAssignSummary is not null)
            ImGui.TextUnformatted(_lastManualAssignSummary);

        // Reconcile before rendering: drop any selected identifier that is no longer present or
        // has since become protected (by any source, including a folder rule toggled on the
        // Protect tab), so stale checkmarks never display and Assign never targets them.
        var eligibleIdentifiers = _plugin.OrganizerState.Mods
            .Where(m => !m.Protected)
            .Select(m => m.Identifier)
            .ToHashSet(StringComparer.Ordinal);
        _selectedManualModIdentifiers.IntersectWith(eligibleIdentifiers);

        ImGui.Spacing();
        var manualFilter = _manualAssignFilter.Trim();
        using (var child = ImRaii.Child("ManualModList", new Vector2(0, 300), border: true))
        {
            if (child)
            {
                foreach (var mod in _plugin.OrganizerState.Mods.Where(m => !m.Protected))
                {
                    if (manualFilter.Length > 0
                        && !mod.Name.Contains(manualFilter, StringComparison.OrdinalIgnoreCase)
                        && !mod.Identifier.Contains(manualFilter, StringComparison.OrdinalIgnoreCase)
                        && !mod.CurrentPath.Contains(manualFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isSelected = _selectedManualModIdentifiers.Contains(mod.Identifier);
                    if (ImGui.Checkbox($"{mod.Name} ({mod.CurrentPath})##manual-{mod.Identifier}", ref isSelected))
                    {
                        if (isSelected)
                            _selectedManualModIdentifiers.Add(mod.Identifier);
                        else
                            _selectedManualModIdentifiers.Remove(mod.Identifier);
                    }
                }
            }
        }
    }
}
