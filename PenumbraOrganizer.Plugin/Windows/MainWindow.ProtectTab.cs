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
        using var tab = ImRaii.TabItem("Protect");
        if (!tab)
            return;

        if (ImGui.Button("Toggle protect all"))
        {
            var allProtected = _plugin.OrganizerState.Mods.All(m => m.Protected);
            _plugin.OrganizerState.SetAllProtection(!allProtected);
            SaveProtectionStateSafely();
        }
        Help.Tooltip(HelpTopics.ProtectToggleAll);

        ImGui.SameLine();
        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            SaveProtectionStateSafely();
        }
        Help.Tooltip(HelpTopics.ProtectToggleHeliosphere);

        var heliosphereMods = _plugin.OrganizerState.Mods.Where(m => m.HeliosphereManaged).ToList();
        if (heliosphereMods.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({heliosphereMods.Count(m => m.Protected)}/{heliosphereMods.Count} Heliosphere mods protected)");
        }

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
                foreach (var mod in _plugin.OrganizerState.Mods.OrderByDescending(m => m.HeliosphereManaged))
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
                        if (mod.HeliosphereManaged)
                        {
                            // Inside the loop: ImGui binds IsItemHovered to the last submitted item,
                            // so one topic serves every row and the call has to sit with its row.
                            ImGui.TextDisabled("(Heliosphere)");
                            Help.Tooltip(HelpTopics.ProtectHeliosphereNote);
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

    // State only. Closing the popup is the caller's job and is only valid from inside it:
    // IsPopupOpen resolves its id against the current window, which inside the popup is the popup
    // itself, so a guard here would never match the id OpenPopup registered in the parent window.
    private void ClearPendingUnprotectFolder()
    {
        _pendingUnprotectFolder = null;
        _unprotectFolderPopupOpened = false;
    }
}
