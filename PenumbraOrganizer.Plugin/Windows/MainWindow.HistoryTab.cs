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
    private void DrawHistoryTab()
    {
        using var tab = ImRaii.TabItem("History");
        if (!tab)
            return;

        var operationState = _plugin.OperationController.State;
        var gates = CurrentGates();

        ImGui.InputText("Label (optional)", ref _createBackupLabelInput, 200);
        ImGui.SameLine();
        ImGui.BeginDisabled(!gates.CanCreateBackup);
        if (ImGui.Button("Create Backup"))
        {
            var label = _createBackupLabelInput.Trim();
            CreateBackup(label.Length > 0 ? label : null);
            _createBackupLabelInput = string.Empty;
        }
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.HistoryCreateBackup, gates.CanCreateBackup ? null : ActivityGateReason);

        ImGui.Spacing();
        ImGui.Separator();

        _historyCache ??= _plugin.LoadHistory();
        var history = _historyCache
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        if (history.Count == 0)
        {
            TextDisabledWrapped("No backups yet. Backups are created automatically before every Apply and Restore.");
        }

        foreach (var snapshot in history)
        {
            // Per-row widget uniqueness follows this codebase's existing convention (see
            // DrawProtectTab's "{mod.Name}##protect-{mod.Identifier}") rather than ImRaii.PushId,
            // whose exact signature in this Dalamud version wasn't worth gambling on.
            var title = snapshot.Label is { Length: > 0 } label
                ? $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {label}"
                : $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {snapshot.AutoDescription}";
            // Wrapped, and NOT SameLine'd with the buttons below - the label is user-supplied
            // free text up to 200 characters (see the "Label (optional)" input above), unbounded
            // enough that chaining it with the Restore/Delete buttons could push them past the
            // window edge.
            ImGui.TextWrapped($"{title} ({snapshot.ModPaths.Count} mods)");

            ImGui.BeginDisabled(!gates.CanStartRestore);
            var restoreButtonClicked = ImGui.Button($"Restore##restore-{snapshot.Id}");
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.HistoryRestore, gates.CanStartRestore ? null : ActivityGateReason);
            if (restoreButtonClicked)
            {
                _pendingRestoreSnapshotId = snapshot.Id;
                // Compute the preview once, here, rather than every frame the popup is drawn -
                // PreviewRestore does a disk read (RollbackHistory.Load) and a Penumbra IPC call,
                // which is too expensive to repeat per-frame for large mod libraries.
                _pendingRestorePreview = _plugin.PreviewRestore(snapshot.Id);
                ImGui.OpenPopup("Restore snapshot?");
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(!gates.CanCreateBackup);
            var deleteButtonClicked = ImGui.Button($"Delete##delete-{snapshot.Id}");
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.HistoryDeleteSnapshot, gates.CanCreateBackup ? null : ActivityGateReason);
            if (deleteButtonClicked)
            {
                DeleteHistorySnapshot(snapshot.Id);
            }

            if (_pendingRestoreSnapshotId == snapshot.Id)
                ImGui.SetNextWindowSize(new Vector2(DetailedPopupWidth, 0), ImGuiCond.Appearing);
            if (_pendingRestoreSnapshotId == snapshot.Id && ImGui.BeginPopupModal("Restore snapshot?"))
            {
                // Exact preview via PreviewRestore, not an ad-hoc estimate: the previous
                // "Up to N mods... may move" counts only checked snapshot membership, not whether
                // the historical path actually differs from the current one - this replaces that
                // with the real plan the Restore button will execute.
                // Cached at popup-open time (see the Restore button above) rather than recomputed
                // here - this body runs every frame the popup is visible, and PreviewRestore does a
                // disk read plus a Penumbra IPC call.
                if (_pendingRestorePreview is not { } preview)
                {
                    ImGui.EndPopup();
                    continue;
                }
                // OrganizerState.Mods reflects the last Scan, while preview.Moves comes from a
                // fresh IPC read taken when the popup opened - these could disagree if Penumbra
                // state changed since the last Scan, but that's an acceptable edge case here.
                var modsByIdentifier = _plugin.OrganizerState.Mods.ToDictionary(m => m.Identifier, m => m);
                var protectedMovingCount = preview.Moves.Count(move =>
                    modsByIdentifier.TryGetValue(move.Identifier, out var mod) && mod.Protected);
                var heliosphereMovingCount = preview.Moves.Count(move =>
                    modsByIdentifier.TryGetValue(move.Identifier, out var mod) && mod.HeliosphereManaged);

                ImGui.TextWrapped($"Restore to: {title}");
                ImGui.TextWrapped($"{preview.Moves.Count} mods will move to their snapshot path.");
                ImGui.TextWrapped($"{preview.UnchangedIdentifiers.Count} mods are already at their snapshot path.");
                ImGui.TextWrapped($"{preview.RootRelocatedIdentifiers.Count} mods installed since this snapshot will be moved to the Penumbra root.");
                ImGui.TextWrapped($"{preview.SkippedUninstalledIdentifiers.Count} mods from this snapshot are no longer installed and will be skipped.");
                TextColoredWrapped(ImGuiColors.DalamudYellow,
                    "Exact Restore: this reproduces the snapshot's historical paths, including for mods that are "
                    + "currently protected or Heliosphere-managed.");
                if (protectedMovingCount > 0 || heliosphereMovingCount > 0)
                    TextColoredWrapped(ImGuiColors.DalamudYellow,
                        $"{protectedMovingCount} currently protected and {heliosphereMovingCount} Heliosphere-managed "
                        + "mod(s) among these will move despite their current protection status.");

                if (ImGui.Button("Yes, Restore"))
                {
                    RestoreSnapshot(snapshot.Id);
                    _pendingRestoreSnapshotId = null;
                    _pendingRestorePreview = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _pendingRestoreSnapshotId = null;
                    _pendingRestorePreview = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        if (operationState.Kind == Organizer.Operations.OperationType.Restore && !operationState.CanStartRestore && !operationState.RequiresRecovery)
            DrawOperationProgress(operationState, "Restoring", _plugin.RequestCancellation, "##cancel-restore");
        else if (operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.RequiresRecovery)
            ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");

        ImGui.Spacing();
        ImGui.Separator();
        var recentOperationsOpen = ImGui.CollapsingHeader("Recent Operations");
        // Reload only on a collapsed -> expanded transition (or the very first expansion), not every
        // frame the header stays open - the naive "load every frame it's expanded" version is exactly
        // the per-frame-disk-read pattern this section must avoid.
        if (recentOperationsOpen && !_recentOperationsSectionWasOpen)
            RefreshRecentOperations();
        _recentOperationsSectionWasOpen = recentOperationsOpen;

        if (recentOperationsOpen)
        {
            if (ImGui.Button("Refresh##recent-operations"))
                RefreshRecentOperations();

            if (_recentOperationsError is { } error)
            {
                ImGui.TextColored(PluginTheme.CollisionBad, error);
            }
            else if (_recentOperations.Count == 0)
            {
                ImGui.TextDisabled("No completed operations yet.");
            }
            else
            {
                using var table = ImRaii.Table("RecentOperationsTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
                if (table)
                {
                    ImGui.TableSetupColumn("When");
                    ImGui.TableSetupColumn("Type");
                    ImGui.TableSetupColumn("Stage");
                    ImGui.TableSetupColumn("Resolution");
                    ImGui.TableHeadersRow();
                    foreach (var journal in _recentOperations)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.Type.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.Stage.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.Resolution.ToString());
                    }
                }
            }
        }
    }
}
