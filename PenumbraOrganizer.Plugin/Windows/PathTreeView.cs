using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

public static class PathTreeView
{
    public static void Draw(IReadOnlyList<OrganizerModRow> mods, bool showProposedColumn, Action<OrganizerModRow>? onRowSelected = null)
    {
        var columnCount = showProposedColumn ? 4 : 3;
        using var table = ImRaii.Table("PathTreeView", columnCount,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new System.Numerics.Vector2(0, 300));
        if (!table)
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Author");
        ImGui.TableSetupColumn("Current Path");
        if (showProposedColumn)
            ImGui.TableSetupColumn("Proposed Path");
        ImGui.TableHeadersRow();

        foreach (var mod in mods)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (mod.Protected)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, mod.Name);
            }
            else if (ImGui.Selectable(mod.Name))
            {
                onRowSelected?.Invoke(mod);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.Author);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.CurrentPath);

            if (showProposedColumn)
            {
                ImGui.TableNextColumn();
                var changed = mod.ProposedPath != mod.CurrentPath;
                if (changed)
                    ImGui.TextColored(ImGuiColors.HealerGreen, mod.ProposedPath);
                else
                    ImGui.TextUnformatted(mod.ProposedPath);
            }
        }
    }
}
