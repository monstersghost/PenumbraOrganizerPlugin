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
        using var tab = ImRaii.TabItem("Sort", TutorialTabFlags(HighlightTarget.SortTab));
        OutlineLastItem(HighlightTarget.SortTab);
        if (!tab)
            return;

        var gates = CurrentGates();

        _sortPanel.Draw(
            _plugin.OrganizerState, gates, _plugin.Config, _plugin.SaveConfig,
            _creatorCanonicalizer.Canonicalize,
            () => _plugin.OrganizerState.BodyLabels ?? _plugin.BundledBodyLabels,
            () => _sortHelpRequested = true,
            () => OutlineLastItem(HighlightTarget.SortButton));

        ImGui.Spacing();
        // Collapsed by default: most sorts never touch it. Same controls as before, unchanged.
        if (!ImGui.CollapsingHeader("Manual assignment##manual-assign"))
            return;

        // The wiki refresh controls return, gated on Configuration.ScrapedNpcListFeatureEnabled, when that feature ships.

        ImGui.TextUnformatted("Start Manually: check mods below, type a folder, click Assign.");

        ImGui.InputText("Search mods##manual-assign", ref _manualAssignFilter, 256);
        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        ImGui.SameLine();
        ImGui.BeginDisabled(!gates.CanStageProposals);
        var assignClicked = ImGui.Button($"Assign {_selectedManualModIdentifiers.Count} selected mods");
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortManualAssign, gates.CanStageProposals ? null : ActivityGateReason);
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
