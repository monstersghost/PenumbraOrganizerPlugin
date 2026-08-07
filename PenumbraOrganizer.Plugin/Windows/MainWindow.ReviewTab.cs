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
    private void DrawReviewTab()
    {
        using var tab = ImRaii.TabItem("Review Changes");
        if (!tab)
            return;

        var result = _plugin.OrganizerState.Validate();

        if (!result.HasIssues)
            ImGui.TextColored(PluginTheme.ChangedGood, "No issues found.");

        foreach (var identifier in result.ProtectedViolations)
            TextColoredWrapped(PluginTheme.CollisionBad, $"Protected mod changed: {identifier}");

        foreach (var (path, identifiers) in result.PathCollisions)
            TextColoredWrapped(PluginTheme.CollisionBad, $"Collision at '{path}': {string.Join(", ", identifiers)}");

        ImGui.Spacing();
        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: true);

        ImGui.Spacing();
        if (ImGui.Button("Export"))
            _lastExportPath = _plugin.ExportReview();

        ImGui.SameLine();
        if (ImGui.Button("Show Config File"))
            OpenConfigFile();

        ImGui.SameLine();
        if (ImGui.Button("Create Diagnostic Dump"))
            _lastDiagnosticDumpPath = CreateDiagnosticDump();

        // File paths are unbounded in length (depend on the user's own Windows profile/folder
        // depth) - wrapped, and with any action button on its own line below rather than chained
        // via SameLine, so a long path can never push a button past the window edge.
        if (_lastExportPath is not null)
            ImGui.TextWrapped($"Exported to: {_lastExportPath}");

        if (_lastDiagnosticDumpPath is not null)
        {
            ImGui.TextWrapped($"Diagnostic dump written to: {_lastDiagnosticDumpPath}");
            if (ImGui.Button("Show Dump File"))
                OpenContainingFolder(_lastDiagnosticDumpPath);
        }

        ImGui.Spacing();
        var strategyLabels = WorkbookStrategyOptions.Select(o => o.Label).ToArray();
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Workbook destinations", ref _workbookStrategyIndex, strategyLabels, strategyLabels.Length);

        ImGui.SameLine();
        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(WorkbookStrategyOptions[_workbookStrategyIndex].Strategy);

        if (_lastWorkbookExportPath is not null)
        {
            // Own line, wrapped, and the button below it rather than SameLine'd - same reasoning
            // as the Export/Diagnostic Dump paths above: the path itself is unbounded in length.
            ImGui.TextWrapped($"Workbook exported to: {_lastWorkbookExportPath}");
            if (ImGui.Button("Open Workbook"))
                OpenFileWithDefaultApp(_lastWorkbookExportPath);
        }

        ImGui.Spacing();
        if (result.HasIssues && ImGui.Button("Protect & Skip All Blocking Mods"))
        {
            _plugin.ProtectAndSkipBlockingMods();
            result = _plugin.OrganizerState.Validate();
        }

        ImGui.Spacing();
        var touchedCount = _plugin.OrganizerState.Mods
            .Count(m => !m.Protected && !string.Equals(m.ProposedPath, m.CurrentPath, StringComparison.OrdinalIgnoreCase));

        var operationState = _plugin.OperationController.State;
        var gates = CurrentGates();
        if (_pendingApplyReminder)
        {
            _pendingApplyReminder = false;
            // In-scope with this tab's BeginPopupModal - see the field's comment for why the
            // consumer cannot call this itself.
            ImGui.OpenPopup("Apply complete - Rediscover Mods reminder");
        }

        ImGui.BeginDisabled(result.HasIssues || !gates.CanStartApply);
        var applyClicked = ImGui.Button("Apply");
        ImGui.EndDisabled();
        if (applyClicked)
            ImGui.OpenPopup("Apply changes?");

        ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("Apply changes?"))
        {
            ImGui.TextUnformatted($"Apply changes to {touchedCount} mods?");
            if (ImGui.Button("Yes, Apply"))
            {
                ApplyChanges();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.SetNextWindowSize(new Vector2(480, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopupModal("Apply complete - Rediscover Mods reminder"))
        {
            ImGui.TextUnformatted("Apply complete.");
            ImGui.TextWrapped(
                "Penumbra doesn't always write organization.json to disk immediately, so any folders "
                + "that are now empty may not be detected here yet, and may still show in Penumbra's own mod tree.");
            ImGui.TextWrapped(
                "Open Penumbra's Settings tab and click Rediscover Mods - this flushes the change to disk "
                + "and removes any stray empty folders from Penumbra's tree. Then Folder Cleanup below will "
                + "detect them accurately.");
            if (ImGui.Button("Understood"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        // Gated on Kind == Apply so an in-progress or just-completed Restore (sharing the same
        // OperationController) never renders here - CanStartApply/CanStartRestore are the same value
        // today, so Kind is the only field that actually distinguishes the two operations.
        if (operationState.Stage is not null && operationState.Kind == Organizer.Operations.OperationType.Apply)
        {
            if (!operationState.CanStartApply && !operationState.RequiresRecovery)
                DrawOperationProgress(operationState, "Applying", _plugin.RequestCancellation, "##cancel-apply");
            else if (operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Apply requires recovery - see the plugin log.");
            else
                ImGui.TextUnformatted($"Last Apply: {operationState.Stage} ({operationState.SuccessfulTargets}/{operationState.TotalTargets} succeeded).");
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawOrphanedFoldersSection();
    }

    private void DrawOrphanedFoldersSection()
    {
        var detection = _orphanedFolders;
        var gates = CurrentGates();
        if (detection is null || detection.Status == Organizer.FolderDetectionStatus.NotScanned)
            return; // nothing meaningful before the first scan

        if (detection.Status is Organizer.FolderDetectionStatus.UnsupportedVersion
            or Organizer.FolderDetectionStatus.MalformedJson)
        {
            TextColoredWrapped(ImGuiColors.DalamudYellow,
                "organization.json couldn't be read — folder cleanup unavailable "
                + (detection.Status == Organizer.FolderDetectionStatus.UnsupportedVersion
                    ? "(unsupported version)."
                    : "(unreadable file)."));
            return;
        }

        if (detection.Status == Organizer.FolderDetectionStatus.FileMissing)
        {
            // Ordinary state, but say so instead of hiding the whole section — a fresh Penumbra
            // install has no organization.json until its tree first gains folders, and an
            // invisible section reads as "the feature is gone" (real user report, 2026-07-19).
            ImGui.TextUnformatted("Orphaned Folders");
            TextDisabledWrapped(
                "Penumbra hasn't created organization.json yet — it appears once the mod tree has "
                + "folders (e.g. after your first Apply). Folder cleanup will activate then.");
            return;
        }

        var total = detection.PlainEmpty.Count + detection.CustomizedEmpty.Count;

        if (_folderReloadRequired)
            TextColoredWrapped(ImGuiColors.DalamudYellow,
                "Waiting on Rediscover Mods — the list below reflects organization.json on disk, "
                + "not Penumbra's confirmed live state. Click Rediscover Mods in Penumbra, then Scan here, to re-check.");

        ImGui.TextUnformatted($"Orphaned Folders ({total} detected)");

        if (ImGui.Button("Re-read organization.json##orphan-reread"))
            RefreshOrphanedFolders();

        ImGui.SameLine();
        ImGui.TextDisabled(_organizationJsonLastReadAt is { } readAt
            ? $"Last read: {readAt.ToLocalTime():HH:mm:ss} ({FormatElapsed(DateTimeOffset.Now - readAt)} ago)"
            : "Not yet read this session.");

        TextDisabledWrapped(
            "This reflects organization.json on disk as of the last read above, not Penumbra's live folder tree. "
            + "If Penumbra hasn't written a change to disk yet, re-reading won't show it — move a folder in "
            + "Penumbra's own UI (or use Rediscover Mods) to make Penumbra flush its tree, then re-read again.");

        if (total > 0)
        {
            ImGui.TextUnformatted($"Empty, no customization ({detection.PlainEmpty.Count}) — pre-checked");
            foreach (var path in detection.PlainEmpty)
                DrawOrphanCheckbox(path, path);

            if (detection.CustomizedEmpty.Count > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    $"Empty but customized ({detection.CustomizedEmpty.Count}) — unchecked, review before pruning");
                foreach (var folder in detection.CustomizedEmpty)
                    DrawOrphanCheckbox(folder.Path, $"{folder.Path}  ({folder.Description})");
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(_selectedOrphans.Count == 0 || !gates.CanRunFolderCleanup);
            var cleanClicked = ImGui.Button("Clean Up Selected Folders");
            ImGui.EndDisabled();
            // Gated on _selectedOrphans.Count > 0 so this tooltip only claims the reason is "another
            // operation" when that's actually why the button is disabled - with no selection at all,
            // the button is disabled for an unrelated, pre-existing reason (nothing chosen yet), and
            // this tooltip must not claim an operation is blocking it when none is.
            if (_selectedOrphans.Count > 0 && !gates.CanRunFolderCleanup && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
            if (cleanClicked)
                ImGui.OpenPopup("Clean up folders?");

            ImGui.SetNextWindowSize(new Vector2(DetailedPopupWidth, 0), ImGuiCond.Appearing);
            if (ImGui.BeginPopupModal("Clean up folders?"))
            {
                ImGui.TextUnformatted($"Remove {_selectedOrphans.Count} folder entries from Penumbra's organization.json?");
                // Height-capped and scrollable, not an unbounded list of TextUnformatted lines -
                // a real library has hit 229 orphaned folders in one run (see docs/HANDOFF_FOLDER_CLEANUP.md),
                // which would otherwise stretch this popup far past the screen's own height.
                using (var list = ImRaii.Child("CleanUpFoldersList", new Vector2(0, 300), border: true))
                {
                    if (list)
                        foreach (var path in _selectedOrphans.OrderBy(p => p, StringComparer.Ordinal))
                            ImGui.TextWrapped(path);
                }
                if (ImGui.Button("Yes, Clean Up"))
                {
                    CleanUpSelectedFolders();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (_plugin.FolderBackupExists)
        {
            // Deliberately its own line, not SameLine'd - whatever was drawn immediately above
            // (the wrapped explanatory paragraph, or nothing if total == 0) can end at an
            // unpredictable X position depending on window width and how many lines it wrapped
            // to, which could otherwise push this button most of the way off the window edge.
            ImGui.BeginDisabled(!gates.CanRunFolderCleanupRollback);
            if (ImGui.Button("Rollback Folder Cleanup"))
                RollbackFolderCleanup();
            ImGui.EndDisabled();
            if (!gates.CanRunFolderCleanupRollback && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
        }

        DrawFolderActionResults();
    }

    private void DrawOrphanCheckbox(string path, string label)
    {
        var selected = _selectedOrphans.Contains(path);
        if (ImGui.Checkbox($"{label}##orphan-{path}", ref selected))
        {
            if (selected)
                _selectedOrphans.Add(path);
            else
                _selectedOrphans.Remove(path);
        }
    }

    private void DrawFolderActionResults()
    {
        if (_lastCleanupResult is not null)
        {
            var r = _lastCleanupResult;
            switch (r.Status)
            {
                case Organizer.FolderCleanupStatus.Success:
                    ImGui.TextUnformatted($"{r.Pruned.Count} folder entries removed from organization.json.");
                    TextColoredWrapped(ImGuiColors.DalamudYellow,
                        "Penumbra hasn't loaded this change yet — open Penumbra's Settings tab and click "
                        + "Rediscover Mods before making any other folder changes there.");
                    if (r.SkippedStale.Count > 0)
                        ImGui.TextWrapped(
                            $"{r.SkippedStale.Count} selected folder(s) were no longer orphaned and were skipped.");
                    break;
                case Organizer.FolderCleanupStatus.SucceededBackupFailed:
                    TextColoredWrapped(PluginTheme.CollisionBad,
                        $"{r.Pruned.Count} folder entries removed, but the rollback backup could not be saved. "
                        + "Rediscover Mods in Penumbra now, then avoid running another cleanup until you've "
                        + "confirmed the result — there is no safety net for this action right now.");
                    if (_plugin.FolderBackupExists)
                        TextColoredWrapped(PluginTheme.CollisionBad,
                            "The Rollback button restores an OLDER backup that predates this cleanup — "
                            + "clicking it would undo more than just this action.");
                    break;
                case Organizer.FolderCleanupStatus.NothingStillValid:
                    ImGui.TextWrapped(
                        "Nothing was cleaned up — the selected folder(s) are no longer orphaned (or no longer exist). "
                        + "No files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.NothingSelected:
                    ImGui.TextUnformatted("Nothing selected — no files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.FileMissing:
                    ImGui.TextWrapped("organization.json does not exist on this install — nothing to clean up.");
                    break;
                case Organizer.FolderCleanupStatus.UnsupportedVersion:
                case Organizer.FolderCleanupStatus.MalformedJson:
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "organization.json couldn't be read — no files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.FileChangedDuringCleanup:
                    TextColoredWrapped(ImGuiColors.DalamudYellow,
                        "organization.json changed (likely Penumbra itself) while this cleanup was running — "
                        + "no files were touched. Scan and try again.");
                    break;
            }
        }

        if (_lastFolderRollbackResult is not null)
        {
            switch (_lastFolderRollbackResult.Status)
            {
                case Organizer.FolderRollbackStatus.Restored:
                    ImGui.TextUnformatted("Backup restored to organization.json.");
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "Penumbra hasn't loaded this change yet — click Rediscover Mods.");
                    break;
                case Organizer.FolderRollbackStatus.InvalidBackup:
                    TextColoredWrapped(PluginTheme.CollisionBad,
                        "The backup file is unreadable or unsupported — rollback aborted, organization.json was not touched.");
                    break;
                case Organizer.FolderRollbackStatus.NoBackup:
                    ImGui.TextUnformatted("No folder-cleanup backup exists.");
                    break;
            }
        }
    }
}
