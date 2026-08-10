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

        ImGui.Spacing();
        ImGui.InputText("Search mods and folders", ref _protectFilter, 256);
        ImGui.Spacing();

        var filter = _protectFilter.Trim();
        var protectedFolders = _plugin.OrganizerState.ProtectedFolders.ToHashSet(StringComparer.Ordinal);
        var knownFolders = _plugin.OrganizerState.KnownFolders.ToHashSet(StringComparer.Ordinal);
        var folderRows = knownFolders
            .Union(protectedFolders, StringComparer.Ordinal)
            .Where(f => filter.Length == 0 || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        ImGui.TextUnformatted("Folders");
        Help.Tooltip(HelpTopics.ProtectFolders);
        using (var folderChild = ImRaii.Child("ProtectedFolderList", new Vector2(0, _protectedFolderListHeight), border: true))
        {
            if (folderChild)
            {
                foreach (var folder in folderRows)
                {
                    var isExactlyProtected = protectedFolders.Contains(folder);
                    var label = knownFolders.Contains(folder) ? folder : $"{folder} (currently empty)";
                    var isChecked = isExactlyProtected;
                    if (ImGui.Checkbox($"{label}##protect-folder-{folder}", ref isChecked))
                    {
                        _plugin.OrganizerState.SetFolderProtected(folder, isChecked);
                        SaveProtectionStateSafely();
                    }

                    if (!isExactlyProtected)
                    {
                        var ancestor = protectedFolders.FirstOrDefault(f =>
                            !f.Equals(folder, StringComparison.Ordinal)
                            && folder.StartsWith(f + "/", StringComparison.Ordinal));
                        if (ancestor is not null)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled($"(covered by protected folder \"{ancestor}\")");
                        }
                    }
                }
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
                            var parent = Organizer.OrganizationCleanupPlanner.GetVirtualParent(mod.CurrentPath);
                            var coveringFolder = parent is null
                                ? null
                                : protectedFolders.FirstOrDefault(f =>
                                    parent.Equals(f, StringComparison.Ordinal) || parent.StartsWith(f + "/", StringComparison.Ordinal));
                            ImGui.TextDisabled(coveringFolder is not null ? $"(via folder: {coveringFolder})" : "(protected)");
                        }
                    }
                }
            }
        }
    }
}
