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
    private void DrawScanTab()
    {
        using var tab = ImRaii.TabItem("Scan");
        if (!tab)
            return;

        var gates = CurrentGates();
        var scanState = _plugin.ScanWork.State;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!gates.CanScan);
            if (ImGui.Button("Refresh mod list"))
                RunScan();
            ImGui.EndDisabled();
        }
        Help.Tooltip(HelpTopics.ScanRefreshModList, gates.CanScan ? null : ActivityGateReason);

        // The mods-loaded text must stay SameLine'd with the button here, before the progress bar
        // and outcome message are drawn - DrawLibraryWorkOutcome renders text on Failed/StaleModList/
        // Cancelled outcomes (not just while a run is in progress), and a SameLine after it would
        // wrongly glue the mods-loaded count onto the end of that outcome line.
        ImGui.SameLine();
        ImGui.Text($"{_plugin.OrganizerState.Mods.Count} mods loaded");

        DrawLibraryWorkProgress(scanState, _plugin.ScanWork.RequestCancellation);
        DrawLibraryWorkOutcome(scanState);
        ImGui.Spacing();

        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: false);

        ImGui.Spacing();
        // On the label, not the child: a scrolling child is not a hoverable item, so the heading is
        // the only thing IsItemHovered can bind to here.
        ImGui.Text("Live events (ModAdded / ModDeleted / ModMoved):");
        Help.Tooltip(HelpTopics.ScanEventLog);
        using (var child = ImRaii.Child("EventLog", new Vector2(0, 150), border: true))
        {
            if (child)
                foreach (var line in _eventLog.Lines)
                    ImGui.TextUnformatted(line);
        }
    }
}
