using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

public static class PathTreeView
{
    public static void Draw(IReadOnlyList<OrganizerModRow> mods, bool showProposedColumn)
    {
        var columnCount = showProposedColumn ? 4 : 3;
        // NoSavedSettings: a Resizable table otherwise persists per-column widths (keyed by this
        // table's ID) across frames/sessions once a user drags a column - a column dragged down
        // to a sliver once would otherwise stay that way forever regardless of the stretch
        // weights below, since the persisted override takes precedence over TableSetupColumn's
        // weight argument.
        using var table = ImRaii.Table("PathTreeView", columnCount,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings,
            new System.Numerics.Vector2(0, 300));
        if (!table)
            return;

        // Explicit stretch weights, not the previous unweighted (effectively equal-width) columns
        // - path columns are consistently much longer than Name/Author in a real library (nested
        // folder structure vs. a short mod title), so equal widths meant paths got hard-clipped
        // mid-word even when Name/Author had room to spare.
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Current Path", ImGuiTableColumnFlags.WidthStretch, 3f);
        if (showProposedColumn)
            ImGui.TableSetupColumn("Proposed Path", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableHeadersRow();

        foreach (var mod in mods)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (mod.Protected)
                DrawCellText(mod.Name, PluginTheme.Protected);
            else
                DrawCellText(mod.Name);

            ImGui.TableNextColumn();
            DrawCellText(mod.Author);
            ImGui.TableNextColumn();
            DrawCellText(mod.CurrentPath);

            if (showProposedColumn)
            {
                ImGui.TableNextColumn();
                var changed = !string.Equals(mod.ProposedPath, mod.CurrentPath, StringComparison.OrdinalIgnoreCase);
                if (changed)
                    DrawCellText(mod.ProposedPath, PluginTheme.ChangedGood);
                else
                    DrawCellText(mod.ProposedPath);
            }
        }
    }

    // Wraps at the CURRENT column's width, not the whole window's - plain ImGui.TextWrapped
    // wraps at the window's own right edge (or bleeds past the column into whatever's next),
    // since a table cell doesn't otherwise change what "wrap position" text rendering uses. A
    // long mod name or deeply nested path now spans multiple lines instead of being clipped to a
    // sliver when the column is narrow, and rows grow taller automatically to fit - normal ImGui
    // table behavior once cell content is genuinely multi-line rather than a single long line.
    private static void DrawCellText(string text, System.Numerics.Vector4? color = null)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        if (color is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
