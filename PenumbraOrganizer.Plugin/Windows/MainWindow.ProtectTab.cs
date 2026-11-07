using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed partial class MainWindow
{
    private const string UnprotectFolderPopupTitle = "Unprotect everything inside this folder?";

    private void DrawProtectTab()
    {
        using var tab = ImRaii.TabItem("Protect", TutorialTabFlags(HighlightTarget.ProtectTab));
        OutlineLastItem(HighlightTarget.ProtectTab);
        if (!tab)
            return;

        // One snapshot per frame: Mods sorts and copies the whole list on every call.
        var allMods = _plugin.OrganizerState.Mods;
        var allProtected = allMods.All(m => m.Protected);
        if (ImGui.Button($"{ProtectToggleLabels.All(allProtected)}##toggle-protect-all"))
        {
            _plugin.OrganizerState.SetAllProtection(!allProtected);
            SaveProtectionStateSafely();
        }
        Help.Tooltip(HelpTopics.ProtectToggleAll);

        DrawToolModes(allMods);
        DrawSortInsideFolders(allMods);

        // A protected mod sitting at the library root is stuck there permanently: protection means
        // the plugin never proposes a path for it, so no amount of sorting will file it away. That is
        // invisible otherwise, and it reads as "the plugin dumped my mods at the root and won't fix
        // them" - which is how it was reported. Heliosphere puts freshly installed mods at the root,
        // and they are auto-protected on the same scan that first sees them.
        var strandedAtRoot = _plugin.OrganizerState.Mods
            .Where(m => m.Protected && !m.CurrentPath.Contains('/'))
            .ToList();
        if (strandedAtRoot.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"{strandedAtRoot.Count} protected mods sit at the top level of your library, outside "
                + "any folder. Because they are protected, sorting will never move them. Untick one "
                + "below to let the plugin file it away.");
            Help.Tooltip(HelpTopics.ProtectStrandedAtRoot);
        }

        ImGui.Spacing();
        ImGui.InputText("Search mods and folders", ref _protectFilter, 256);
        ImGui.Spacing();

        var filter = _protectFilter.Trim();
        var knownFolders = _plugin.OrganizerState.KnownFolders.ToHashSet(StringComparer.Ordinal);
        var protectedCountsByFolder = _plugin.OrganizerState.BuildProtectedModCountsByFolder();
        // Ruled folders are included even when empty, so a rule is never invisible: a folder whose
        // mods have all moved away still shows its own tick and can be untangled.
        var folderTree = FolderProtectionTree.Build(
            knownFolders.Union(_plugin.OrganizerState.FolderProtectionRules.Keys, StringComparer.Ordinal));

        ImGui.TextUnformatted("Folders");
        Help.Tooltip(HelpTopics.ProtectFolders);
        using (var folderChild = ImRaii.Child("ProtectedFolderList", new Vector2(0, _protectedFolderListHeight), border: true))
        {
            if (folderChild)
            {
                var expansionChanged = FolderProtectionTree.Draw(
                    folderTree,
                    _plugin.OrganizerState.ResolveFolderRule,
                    protectedCountsByFolder,
                    knownFolders,
                    filter,
                    _expandedProtectFolders,
                    (folder, rule) =>
                    {
                        _plugin.OrganizerState.SetFolderProtectionRule(folder, rule);
                        SaveProtectionStateSafely();
                    },
                    folder => _pendingUnprotectFolder = folder);

                if (expansionChanged)
                    SaveExpandedProtectFoldersSafely();
            }
        }

        // Drag-to-resize grip: a thin full-width button whose vertical drag delta adjusts the
        // child's height above (min/max clamped to keep it usable). ImGui has no built-in
        // resizable child, so this is the standard manual-splitter pattern.
        ImGui.Button("##protect-folder-list-resize", new Vector2(-1, 6));
        if (ImGui.IsItemActive())
            _protectedFolderListHeight = Math.Clamp(_protectedFolderListHeight + ImGui.GetIO().MouseDelta.Y, 80f, 600f);
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);

        // Opened and drawn here, not on the folder row that requested it: the folder list is a
        // scrolling child, so a clipped row submits nothing that frame and a popup owned by it
        // would blink out from under the user mid-click. The row only records the request.
        if (_pendingUnprotectFolder is { } requestedFolder)
        {
            if (!_unprotectFolderPopupOpened)
            {
                ImGui.OpenPopup(UnprotectFolderPopupTitle);
                _unprotectFolderPopupOpened = true;
            }

            if (ImGui.BeginPopupModal(UnprotectFolderPopupTitle))
            {
                var affected = protectedCountsByFolder.GetValueOrDefault(requestedFolder).Freeable;
                ImGui.TextWrapped($"Unprotect all {affected} mod(s) inside \"{requestedFolder}\", at any depth?");
                ImGui.TextWrapped(
                    "Their individual ticks are cleared too, so Sort and Apply will be free to move them.");
                TextColoredWrapped(
                    ImGuiColors.DalamudYellow,
                    "Heliosphere-managed mods inside this folder are unprotected as well. As with the "
                    + "Heliosphere toggle, that lasts this session only - the next Scan re-protects them.");

                if (ImGui.Button("Yes, unprotect them"))
                {
                    _plugin.OrganizerState.UnprotectFolderContents(requestedFolder);
                    SaveProtectionStateSafely();
                    // Closed here, inside the popup, where CloseCurrentPopup is meaningful - the
                    // field reset alone would only stop the block being submitted next frame.
                    ImGui.CloseCurrentPopup();
                    ClearPendingUnprotectFolder();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    ImGui.CloseCurrentPopup();
                    ClearPendingUnprotectFolder();
                }
                ImGui.EndPopup();
            }
            else
            {
                // Dismissed by Escape or a click outside, which never runs the buttons above.
                // ImGui has already closed it, so there is nothing here but the state to clear.
                ClearPendingUnprotectFolder();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Mods");
        Help.Tooltip(HelpTopics.ProtectMods);
        var explicitIdentifiers = _plugin.OrganizerState.ProtectedModIdentifiers.ToHashSet(StringComparer.Ordinal);
        // Fills whatever vertical space remains in the tab (height -1, ImGui's "leave 1px at the
        // bottom" convention) rather than a fixed height like the Folders list above - this is
        // the last section in the tab, so there's nothing below it to preserve room for, and this
        // way it adapts to the window size instead of needing its own manual resize handle.
        using (var modChild = ImRaii.Child("ProtectedModList", new Vector2(0, -1), border: true))
        {
            if (modChild)
            {
                // Heliosphere-managed mods first (stable within each group) - they're almost
                // always already protected and are the ones users check on most, per feedback.
                // Folders above are deliberately untouched by this ordering.
                foreach (var mod in _plugin.OrganizerState.Mods.OrderByDescending(m => m.IsToolManaged))
                {
                    if (filter.Length > 0
                        && !mod.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !mod.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !mod.Author.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !mod.CurrentPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isProtected = mod.Protected;
                    if (ImGui.Checkbox($"{mod.Name}##protect-{mod.Identifier}", ref isProtected))
                    {
                        _plugin.OrganizerState.SetProtected(mod.Identifier, isProtected);
                        SaveProtectionStateSafely();
                    }

                    if (mod.Protected && !explicitIdentifiers.Contains(mod.Identifier))
                    {
                        ImGui.SameLine();
                        if (mod.IsToolManaged)
                        {
                            var proteusMod = mod.ProteusRole != ProteusRole.None;
                            ImGui.TextDisabled(proteusMod ? "(Proteus)" : "(Heliosphere)");
                            Help.Tooltip(proteusMod ? HelpTopics.ProtectProteusNote : HelpTopics.ProtectHeliosphereNote);
                        }
                        else
                        {
                            var resolution = _plugin.OrganizerState.ResolveFolderRule(
                                Organizer.OrganizationCleanupPlanner.GetVirtualParent(mod.CurrentPath));
                            ImGui.TextDisabled(resolution.Rule == FolderProtectionRule.Protected
                                ? $"(via folder: {resolution.Source})"
                                : "(protected)");
                        }
                    }
                }
            }
        }
    }

    private string _sortInsidePick = "";

    // One list of folders the user keeps as the top level: pick a folder, Add, and its mods are sorted
    // beneath it. A saved setting pushed into the state through ApplyToolPolicy, like the tool modes.
    private void DrawSortInsideFolders(IReadOnlyList<OrganizerModRow> mods)
    {
        var gateOpen = CurrentGates().CanStageProposals;
        var config = _plugin.Config;
        var candidates = SortInsideFolderChoices.Candidates(mods, config.SortInsideFolders);

        if (!ImGui.CollapsingHeader("Sort inside folders##sort-inside"))
            return;
        Help.Tooltip(HelpTopics.ProtectSortInsideFolders, gateOpen ? null : ActivityGateReason);

        ImGui.BeginDisabled(!gateOpen);
        if (candidates.Count > 0)
        {
            if (!candidates.Contains(_sortInsidePick, StringComparer.OrdinalIgnoreCase))
                _sortInsidePick = candidates[0];
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##sort-inside-pick", _sortInsidePick))
            {
                foreach (var candidate in candidates)
                    if (ImGui.Selectable(candidate, string.Equals(candidate, _sortInsidePick, StringComparison.OrdinalIgnoreCase)))
                        _sortInsidePick = candidate;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("Add##sort-inside-add"))
            {
                config.SortInsideFolders.Add(_sortInsidePick);
                _plugin.ApplyToolPolicy();
            }
        }
        else
        {
            ImGui.TextDisabled("No other top-level folders to add.");
        }

        string? remove = null;
        foreach (var folder in config.SortInsideFolders)
        {
            ImGui.TextUnformatted(folder);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##sort-inside-{folder}"))
                remove = folder;
        }
        if (remove is not null)
        {
            config.SortInsideFolders.RemoveAll(f => string.Equals(f, remove, StringComparison.OrdinalIgnoreCase));
            _plugin.ApplyToolPolicy();
        }
        ImGui.EndDisabled();
    }

    // One mode dropdown per tool, each shown only while the library has that tool's mods. The mode is
    // a saved setting (Configuration), pushed into the state; a per-mod untick below is separate.
    private void DrawToolModes(IReadOnlyList<OrganizerModRow> mods)
    {
        var heliosphere = mods.Count(m => m.HeliosphereManaged && m.ProteusRole == ProteusRole.None);
        var proteus = mods.Count(m => m.ProteusRole == ProteusRole.Made);
        var managedExists = mods.Any(m => m.ProteusRole == ProteusRole.Managed);
        var gateOpen = CurrentGates().CanStageProposals;
        var config = _plugin.Config;

        if (heliosphere > 0)
        {
            var index = ToolModeLabels.IndexOf(config.HeliosphereMode);
            ImGui.BeginDisabled(!gateOpen);
            ImGui.SetNextItemWidth(220);
            var changed = ImGui.Combo($"Heliosphere mods ({heliosphere})##heliosphere-mode",
                ref index, [.. ToolModeLabels.All], ToolModeLabels.All.Count);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.ProtectHeliosphereMode, gateOpen ? null : ActivityGateReason);
            if (changed)
            {
                config.HeliosphereMode = ToolModeLabels.FromIndex(index);
                _plugin.ApplyToolPolicy();
            }
        }

        if (proteus > 0)
        {
            var index = ToolModeLabels.IndexOf(config.ProteusMode);
            ImGui.BeginDisabled(!gateOpen);
            ImGui.SetNextItemWidth(220);
            var changed = ImGui.Combo($"Proteus mods ({proteus})##proteus-mode",
                ref index, [.. ToolModeLabels.All], ToolModeLabels.All.Count);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.ProtectProteusMode, gateOpen ? null : ActivityGateReason);
            if (changed)
            {
                config.ProteusMode = ToolModeLabels.FromIndex(index);
                _plugin.ApplyToolPolicy();
            }
        }

        if (managedExists)
        {
            var atRoot = config.ProteusManagedAtRoot;
            ImGui.BeginDisabled(!gateOpen);
            var changed = ImGui.Checkbox("Place the Proteus managed mod at the root##proteus-managed-root", ref atRoot);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.ProtectProteusManagedRoot, gateOpen ? null : ActivityGateReason);
            if (changed)
            {
                config.ProteusManagedAtRoot = atRoot;
                _plugin.ApplyToolPolicy();
            }
        }
    }

    // State only. Closing the popup is the caller's job and is only valid from inside it:
    // IsPopupOpen resolves its id against the current window, which inside the popup is the popup
    // itself, so a guard here would never match the id OpenPopup registered in the parent window.
    private void ClearPendingUnprotectFolder()
    {
        _pendingUnprotectFolder = null;
        _unprotectFolderPopupOpened = false;
    }
}
