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

public sealed class MainWindow : Window, IDisposable
{
    private const int MaxEventLogLines = 200;

    private readonly Plugin _plugin;
    private readonly CreatorCanonicalizer _creatorCanonicalizer = new();
    private readonly List<string> _eventLog = [];
    private string? _lastError;
    private string _protectFilter = string.Empty;
    private float _protectedFolderListHeight = 220f;
    private string _manualFolderInput = string.Empty;
    private readonly HashSet<string> _selectedManualModIdentifiers = new(StringComparer.Ordinal);
    private string _manualAssignFilter = string.Empty;
    private string? _lastManualAssignSummary;
    private string? _lastExportPath;
    private string? _lastDiagnosticDumpPath;
    private string? _lastWorkbookExportPath;
    private int _workbookStrategyIndex = 2; // "By Type Then Creator" default
    private Organizer.WorkbookImportResultView? _lastWorkbookImportResult;
    private IReadOnlyList<Organizer.ApplyResult>? _lastApplyResults;
    private bool _applyOperationActive;
    private bool _restoreOperationActive;
    private string _createBackupLabelInput = string.Empty;
    private IReadOnlyList<Organizer.RestoreResult>? _lastRestoreResults;
    private Guid? _pendingRestoreSnapshotId;
    private Organizer.RestorePlan? _pendingRestorePreview;
    private IReadOnlyList<Organizer.RollbackSnapshot>? _historyCache;
    private Organizer.FolderDetectionResult? _orphanedFolders;
    private DateTimeOffset? _organizationJsonLastReadAt;
    private readonly HashSet<string> _selectedOrphans = new(StringComparer.Ordinal);
    private bool _folderReloadRequired;
    private Organizer.FolderCleanupResult? _lastCleanupResult;
    private Organizer.FolderRollbackResult? _lastFolderRollbackResult;
    private Task? _npcRefreshTask;
    private Organizer.NpcNames.NpcNameRefreshResult? _npcRefreshResult;
    private readonly FileDialogManager _fileDialogManager = new();

    private string _librarySearchNameQuery = string.Empty;
    private string _librarySearchItemQuery = string.Empty;
    private readonly HashSet<ModCategory> _librarySearchCategories = new(SearchableCategories);
    private bool _librarySearchIncludeUnknown = true;
    private readonly HashSet<EquipmentSlot> _librarySearchSlots = new(Enum.GetValues<EquipmentSlot>());
    private bool _librarySearchIncludeUnresolved = true;
    private string? _librarySearchSelectedModIdentifier;

    private static readonly ModCategory[] SearchableCategories =
    [
        ModCategory.Gear, ModCategory.NPC, ModCategory.Mount, ModCategory.Minion,
        ModCategory.Animation, ModCategory.VFX, ModCategory.Furniture, ModCategory.Sound,
        ModCategory.Face, ModCategory.Hair, ModCategory.Body, ModCategory.Skin,
    ];

    private static readonly (string Label, PenumbraOrganizer.Core.Models.OrganizationStrategy Strategy)[] WorkbookStrategyOptions =
    [
        ("By Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorOnly),
        ("By Mod Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeOnly),
        ("By Type Then Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeThenCreator),
        ("By Creator Then Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorThenType),
    ];

    private static readonly string PluginVersion =
        typeof(MainWindow).Assembly.GetName().Version?.ToString(4) ?? "unknown";

    public MainWindow(Plugin plugin)
        : base($"Penumbra Organizer v{PluginVersion}###PenumbraOrganizerPluginMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        _plugin = plugin;
    }

    public void Dispose()
    {
    }

    internal void LogEvent(string message)
    {
        _eventLog.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
        if (_eventLog.Count > MaxEventLogLines)
            _eventLog.RemoveRange(MaxEventLogLines, _eventLog.Count - MaxEventLogLines);
    }

    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        DrawRecoveryPanelIfNeeded();

        if (_lastError != null)
            ImGui.TextColored(PluginTheme.CollisionBad, _lastError);

        using (var tabBar = ImRaii.TabBar("MainTabs"))
        {
            if (tabBar)
            {
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
                DrawHistoryTab();
                DrawSearchTab();
            }
        }

        _fileDialogManager.Draw();
    }

    private void DrawRecoveryPanelIfNeeded()
    {
        var operationState = _plugin.OperationController.State;
        if (!operationState.RequiresRecovery)
            return;

        ImGui.TextColored(PluginTheme.CollisionBad, "An interrupted organizer operation was found.");

        if (_plugin.OperationController.IsBlockedByMultipleRoots)
        {
            ImGui.TextWrapped(
                "Multiple interrupted operations were found, and picking which one to recover isn't " +
                "supported yet in this version. You can abandon all of them and accept whatever Penumbra " +
                "currently has as correct - this does not undo or redo any moves for any of them, it only " +
                "stops the plugin from blocking further actions. This is destructive: none of the " +
                "interrupted operations can be revisited afterward.");

            if (ImGui.Button("Accept Current State and Close All Interrupted Operations"))
                ImGui.OpenPopup("Close all interrupted operations?");

            if (ImGui.BeginPopupModal("Close all interrupted operations?"))
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "This abandons every interrupted operation the plugin found. None of them can be " +
                    "continued or rolled back after this - only Keep Current's outcome is possible for all of them.");
                if (ImGui.Button("Yes, Close All"))
                {
                    _plugin.AcceptAllAndCloseInterruptedOperations();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            ImGui.Spacing();
            ImGui.Separator();
            return;
        }

        ImGui.TextWrapped(
            "The plugin found a mod-organizing operation that didn't finish, likely from a crash or force-" +
            "quit mid-Apply or mid-Restore. You can accept whatever Penumbra currently has as the correct " +
            "state and move on, finish the interrupted operation from where it left off, or roll everything " +
            "back to how it was before the interrupted operation started.");

        ImGui.BeginDisabled(!operationState.CanResolveRecovery);
        if (ImGui.Button("Keep Current State"))
            ImGui.OpenPopup("Keep current state?");
        ImGui.EndDisabled();

        if (ImGui.BeginPopupModal("Keep current state?"))
        {
            ImGui.TextUnformatted("This will mark the interrupted operation as resolved and unblock the plugin.");
            if (ImGui.Button("Yes, Keep Current"))
            {
                _plugin.ResolveKeepCurrent();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!operationState.CanContinueRecovery);
        if (ImGui.Button("Continue"))
            ImGui.OpenPopup("Continue interrupted operation?");
        ImGui.EndDisabled();

        if (ImGui.BeginPopupModal("Continue interrupted operation?"))
        {
            ImGui.TextUnformatted("This will finish the interrupted operation from where it left off.");
            if (ImGui.Button("Yes, Continue") && ContinueRecovery())
                ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!operationState.CanRestorePreviousState);
        if (ImGui.Button("Restore Previous State"))
            ImGui.OpenPopup("Restore to state before the interrupted operation?");
        ImGui.EndDisabled();

        if (ImGui.BeginPopupModal("Restore to state before the interrupted operation?"))
        {
            ImGui.TextUnformatted("This will roll every mod back to how it was before the interrupted operation started.");
            if (ImGui.Button("Yes, Restore") && RestorePreviousState())
                ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawScanTab()
    {
        using var tab = ImRaii.TabItem("Scan");
        if (!tab)
            return;

        var scanOperationState = _plugin.OperationController.State;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!scanOperationState.CanScan);
            if (ImGui.Button("Refresh mod list"))
                RunScan();
            ImGui.EndDisabled();
        }
        if (!scanOperationState.CanScan && ImGui.IsItemHovered())
            ImGui.SetTooltip("Another operation is in progress or requires recovery.");

        ImGui.SameLine();
        ImGui.Text($"{_plugin.OrganizerState.Mods.Count} mods loaded");
        ImGui.Spacing();

        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: false);

        ImGui.Spacing();
        ImGui.Text("Live events (ModAdded / ModDeleted / ModMoved):");
        using (var child = ImRaii.Child("EventLog", new Vector2(0, 150), border: true))
        {
            if (child)
                foreach (var line in _eventLog)
                    ImGui.TextUnformatted(line);
        }
    }

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

        ImGui.SameLine();
        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            SaveProtectionStateSafely();
        }

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
                            ImGui.TextDisabled("(Heliosphere)");
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

    // Standard Dear ImGui button-wrapping idiom: draw each button, then only chain a SameLine if
    // the NEXT button (measured ahead of time via CalcTextSize) would still fit within the
    // window's visible content width - otherwise it naturally drops to a new line instead of
    // being clipped or spilling past the window edge on a narrower/unmaximized window.
    private static void DrawWrappingButtonRow(IReadOnlyList<(string Label, Action OnClick)> buttons)
    {
        var style = ImGui.GetStyle();
        var windowVisibleX2 = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, onClick) = buttons[i];
            if (ImGui.Button(label))
                onClick();

            if (i + 1 >= buttons.Count)
                continue;

            var nextButtonWidth = ImGui.CalcTextSize(buttons[i + 1].Label).X + style.FramePadding.X * 2;
            var lastButtonX2 = ImGui.GetItemRectMax().X;
            var nextButtonX2 = lastButtonX2 + style.ItemSpacing.X + nextButtonWidth;
            if (nextButtonX2 < windowVisibleX2)
                ImGui.SameLine();
        }
    }

    private void DrawSortTab()
    {
        using var tab = ImRaii.TabItem("Sort");
        if (!tab)
            return;

        DrawWrappingButtonRow(
        [
            ("By Creator", () => _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize)),
            ("By Mod Type", () => _plugin.OrganizerState.SortByModType()),
            ("By Mod Type Detailed", () => _plugin.OrganizerState.SortByModTypeDetailed()),
            ("By Type Then Creator", () => _plugin.OrganizerState.SortByTypeThenCreatorFlat(_creatorCanonicalizer.Canonicalize)),
            ("By Type Then Creator (Detailed)", () => _plugin.OrganizerState.SortByTypeThenCreator(_creatorCanonicalizer.Canonicalize)),
            ("By Creator Then Type", () => _plugin.OrganizerState.SortByCreatorThenTypeFlat(_creatorCanonicalizer.Canonicalize)),
            ("By Creator Then Type (Detailed)", () => _plugin.OrganizerState.SortByCreatorThenType(_creatorCanonicalizer.Canonicalize)),
            ("Import Workbook", () => _fileDialogManager.OpenFileDialog(
                "Import Workbook",
                ".xlsx",
                (success, paths) =>
                {
                    if (success && paths.Count > 0)
                        ImportWorkbook(paths[0]);
                },
                selectionCountMax: 1)),
        ]);

        if (_lastWorkbookImportResult is not null)
        {
            ImGui.TextUnformatted(_lastWorkbookImportResult.Summary);
            foreach (var error in _lastWorkbookImportResult.Errors)
                ImGui.TextColored(PluginTheme.CollisionBad, $"  {error}");
            foreach (var warning in _lastWorkbookImportResult.Warnings)
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {warning}");
        }

        ImGui.Spacing();
        var npcRefreshInFlight = _npcRefreshTask is { IsCompleted: false };
        ImGui.BeginDisabled(npcRefreshInFlight);
        if (ImGui.Button("Refresh NPC list from wiki"))
        {
            _npcRefreshResult = null;
            _lastError = null;
            _npcRefreshTask = RefreshNpcNamesAsync();
        }
        ImGui.EndDisabled();

        if (npcRefreshInFlight)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Refreshing... (this can take a few minutes for a full scrape)");
        }

        if (_npcRefreshResult is not null)
        {
            if (_npcRefreshResult.RecoveredFromCorruption)
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "The existing NPC name list was unreadable and has been reset from the bundled "
                    + "seed list; the old file was preserved alongside it as a timestamped backup.");

            foreach (var category in _npcRefreshResult.Categories)
            {
                if (category.FailureReason is not null)
                    ImGui.TextColored(PluginTheme.CollisionBad, $"  {category.CategoryName} failed: {category.FailureReason}");
                else
                    ImGui.TextUnformatted($"  {category.CategoryName}: +{category.AddedCount}");
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: check mods below, type a folder, click Assign.");

        ImGui.InputText("Search mods##manual-assign", ref _manualAssignFilter, 256);
        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        ImGui.SameLine();
        if (ImGui.Button($"Assign {_selectedManualModIdentifiers.Count} selected mods")
            && _manualFolderInput.Length > 0 && _selectedManualModIdentifiers.Count > 0)
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

    private void DrawReviewTab()
    {
        using var tab = ImRaii.TabItem("Review Changes");
        if (!tab)
            return;

        var result = _plugin.OrganizerState.Validate();

        if (!result.HasIssues)
            ImGui.TextColored(PluginTheme.ChangedGood, "No issues found.");

        foreach (var identifier in result.ProtectedViolations)
            ImGui.TextColored(PluginTheme.CollisionBad, $"Protected mod changed: {identifier}");

        foreach (var (path, identifiers) in result.PathCollisions)
            ImGui.TextColored(PluginTheme.CollisionBad, $"Collision at '{path}': {string.Join(", ", identifiers)}");

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

        if (_lastExportPath is not null)
            ImGui.TextUnformatted($"Exported to: {_lastExportPath}");

        if (_lastDiagnosticDumpPath is not null)
        {
            ImGui.TextUnformatted($"Diagnostic dump written to: {_lastDiagnosticDumpPath}");
            ImGui.SameLine();
            if (ImGui.Button("Show Dump File"))
                OpenContainingFolder(_lastDiagnosticDumpPath);
        }

        ImGui.Spacing();
        var strategyLabels = WorkbookStrategyOptions.Select(o => o.Label).ToArray();
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Workbook suggestion strategy", ref _workbookStrategyIndex, strategyLabels, strategyLabels.Length);

        ImGui.SameLine();
        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(WorkbookStrategyOptions[_workbookStrategyIndex].Strategy);

        if (_lastWorkbookExportPath is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Workbook exported to: {_lastWorkbookExportPath}");

            ImGui.SameLine();
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
        if (_applyOperationActive && operationState.Kind == Organizer.Operations.OperationType.Apply && operationState.CanStartApply)
        {
            _applyOperationActive = false;
            _historyCache = null; // StartApplyOperation() also captures a pre-apply snapshot - history changed
            // The completed Apply moved mods via IPC directly, bypassing OrganizerState - its
            // cached CurrentPath values are now stale. RunScan() re-reads from Penumbra and
            // internally calls RefreshOrphanedFolders() itself, matching the same
            // scan-after-mutation pattern Restore() already relies on.
            RunScan();

            // Penumbra doesn't always flush organization.json to disk immediately after SetModPath,
            // so a freshly-emptied folder can still look occupied to Folder Cleanup (and still show
            // in Penumbra's own tree) until the user triggers Rediscover Mods themselves - real
            // in-game report, 2026-07-24. Only worth mentioning if the Apply actually moved something.
            if (operationState.SuccessfulTargets > 0)
                ImGui.OpenPopup("Apply complete - Rediscover Mods reminder");
        }

        ImGui.BeginDisabled(result.HasIssues || !operationState.CanStartApply);
        var applyClicked = ImGui.Button("Apply");
        ImGui.EndDisabled();
        if (applyClicked)
            ImGui.OpenPopup("Apply changes?");

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

        // Deliberately minimal - the real progress UI and recovery dialog are Plan E's job. This
        // just keeps Apply usable and observable in-game now that it spans multiple frames. Gated on
        // Kind == Apply so an in-progress or just-completed Restore (sharing the same
        // OperationController) never renders here - CanStartApply/CanStartRestore are the same value
        // today, so Kind is the only field that actually distinguishes the two operations.
        if (operationState.Stage is not null && operationState.Kind == Organizer.Operations.OperationType.Apply)
        {
            if (!operationState.CanStartApply && !operationState.RequiresRecovery)
                ImGui.TextUnformatted($"Applying... {operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage}).");
            else if (operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Apply requires recovery - see the plugin log.");
            else
                ImGui.TextUnformatted($"Last Apply: {operationState.Stage} ({operationState.SuccessfulTargets}/{operationState.TotalTargets} succeeded).");
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawOrphanedFoldersSection();
    }

    private void DrawHistoryTab()
    {
        using var tab = ImRaii.TabItem("History");
        if (!tab)
            return;

        var operationState = _plugin.OperationController.State;
        if (_restoreOperationActive && operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.CanStartRestore)
        {
            _restoreOperationActive = false;
            _historyCache = null;
            RunScan();
        }

        ImGui.InputText("Label (optional)", ref _createBackupLabelInput, 200);
        ImGui.SameLine();
        ImGui.BeginDisabled(!operationState.CanCreateBackup);
        if (ImGui.Button("Create Backup"))
        {
            var label = _createBackupLabelInput.Trim();
            CreateBackup(label.Length > 0 ? label : null);
            _createBackupLabelInput = string.Empty;
        }
        ImGui.EndDisabled();
        if (!operationState.CanCreateBackup && ImGui.IsItemHovered())
            ImGui.SetTooltip("Another operation is in progress or requires recovery.");

        ImGui.Spacing();
        ImGui.Separator();

        _historyCache ??= _plugin.LoadHistory();
        var history = _historyCache
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        if (history.Count == 0)
        {
            ImGui.TextDisabled("No backups yet. Backups are created automatically before every Apply and Restore.");
        }

        foreach (var snapshot in history)
        {
            // Per-row widget uniqueness follows this codebase's existing convention (see
            // DrawProtectTab's "{mod.Name}##protect-{mod.Identifier}") rather than ImRaii.PushId,
            // whose exact signature in this Dalamud version wasn't worth gambling on.
            var title = snapshot.Label is { Length: > 0 } label
                ? $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {label}"
                : $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {snapshot.AutoDescription}";
            ImGui.TextUnformatted($"{title} ({snapshot.ModPaths.Count} mods)");

            ImGui.SameLine();
            ImGui.BeginDisabled(!operationState.CanStartRestore);
            var restoreButtonClicked = ImGui.Button($"Restore##restore-{snapshot.Id}");
            ImGui.EndDisabled();
            if (!operationState.CanStartRestore && ImGui.IsItemHovered())
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
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
            if (ImGui.Button($"Delete##delete-{snapshot.Id}"))
            {
                DeleteHistorySnapshot(snapshot.Id);
            }

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

                ImGui.TextUnformatted($"Restore to: {title}");
                ImGui.TextUnformatted($"{preview.Moves.Count} mods will move to their snapshot path.");
                ImGui.TextUnformatted($"{preview.UnchangedIdentifiers.Count} mods are already at their snapshot path.");
                ImGui.TextUnformatted($"{preview.RootRelocatedIdentifiers.Count} mods installed since this snapshot will be moved to the Penumbra root.");
                ImGui.TextUnformatted($"{preview.SkippedUninstalledIdentifiers.Count} mods from this snapshot are no longer installed and will be skipped.");
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "Exact Restore: this reproduces the snapshot's historical paths, including for mods that are "
                    + "currently protected or Heliosphere-managed.");
                if (protectedMovingCount > 0 || heliosphereMovingCount > 0)
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
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

        if (_lastRestoreResults is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            var moved = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Moved);
            var rootRelocated = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.RootRelocated);
            var skippedUninstalled = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedUninstalled);
            var failed = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Failed);
            ImGui.TextUnformatted(
                $"Restore: {moved} moved, {rootRelocated} relocated to root, {skippedUninstalled} skipped (uninstalled), " +
                $"{failed} failed.");
            foreach (var failure in _lastRestoreResults.Where(r => r.Outcome == Organizer.RestoreOutcome.Failed))
                ImGui.TextColored(PluginTheme.CollisionBad, $"  {failure.Identifier}: {failure.FailureReason}");
        }

        if (_restoreOperationActive)
        {
            if (operationState.Kind == Organizer.Operations.OperationType.Restore && !operationState.CanStartRestore && !operationState.RequiresRecovery)
                ImGui.TextUnformatted($"Restoring... {operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage}).");
            else if (operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");
        }
    }

    private void DrawSearchTab()
    {
        using var tab = ImRaii.TabItem("Search");
        if (!tab)
            return;

        ImGui.TextWrapped(
            "Since Penumbra 1.7, its own Mods tab supports a native search syntax (c:[item], t:[tag], "
            + "a:[author], etc.) that covers much of what this tab does. This tab stays available for "
            + "now - it may be retired later if Penumbra's own filtering fully supersedes it.");
        ImGui.Spacing();

        using (PluginTheme.PrimaryButton())
        {
            if (ImGui.Button("Build/Refresh Index"))
                _plugin.BuildChangedItemIndex();
        }

        if (_plugin.LibraryIndexError is { } error)
            ImGui.TextColored(PluginTheme.CollisionBad, error);

        if (_plugin.LibraryIndex is not { } index)
        {
            ImGui.TextUnformatted("Click Build/Refresh Index to search your mod library.");
            return;
        }

        ImGui.TextWrapped(ChangedItemIndexSummary.Describe(index));
        ImGui.Text($"Index built at {index.BuiltAt:HH:mm:ss}");
        ImGui.Spacing();

        ImGui.InputText("Mod name contains", ref _librarySearchNameQuery, 256);
        ImGui.InputText("Item contains", ref _librarySearchItemQuery, 256);
        ImGui.Spacing();

        ImGui.TextUnformatted("Categories:");
        foreach (var category in SearchableCategories)
            DrawCategoryToggle(category);
        DrawUnknownToggle();

        if (_librarySearchCategories.Contains(ModCategory.Gear))
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Slots:");
            foreach (var slot in Enum.GetValues<EquipmentSlot>())
                DrawSlotToggle(slot);
            var includeUnresolved = _librarySearchIncludeUnresolved;
            if (ImGui.Checkbox("Unresolved##slot-unresolved", ref includeUnresolved))
                _librarySearchIncludeUnresolved = includeUnresolved;
        }

        ImGui.Spacing();

        var filter = new LibrarySearchFilter(
            _librarySearchCategories, _librarySearchIncludeUnknown,
            _librarySearchSlots, _librarySearchIncludeUnresolved,
            _librarySearchNameQuery, _librarySearchItemQuery);

        var matches = index.Mods.Where(mod => LibrarySearchEngine.Matches(mod, filter)).ToList();

        // Same flag combination as PathTreeView.cs (the only other table in this codebase) --
        // Resizable | SizingStretchProp, no per-column width flags, for proportional stretch.
        using var columns = ImRaii.Table("SearchColumns", 2,
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 420));
        if (!columns)
            return;

        ImGui.TableSetupColumn("Mods");
        ImGui.TableSetupColumn("Changed items");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        using (var left = ImRaii.Child("SearchModList", new Vector2(0, 400), border: true))
        {
            if (left)
            {
                if (matches.Count == 0)
                {
                    ImGui.TextUnformatted("No mods found.");
                }
                else
                {
                    foreach (var mod in matches)
                    {
                        var isSelected = mod.Identifier == _librarySearchSelectedModIdentifier;
                        if (ImGui.Selectable($"{mod.Name} ({mod.Author})##search-{mod.Identifier}", isSelected))
                            _librarySearchSelectedModIdentifier = mod.Identifier;
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        using (var right = ImRaii.Child("SearchItemList", new Vector2(0, 400), border: true))
        {
            if (right)
            {
                var selectedMod = matches.FirstOrDefault(m => m.Identifier == _librarySearchSelectedModIdentifier);
                if (selectedMod is null)
                {
                    ImGui.TextUnformatted("Select a mod to see its changed items.");
                }
                else
                {
                    var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(selectedMod, filter);
                    if (matchedByNameOnly)
                        ImGui.TextColored(PluginTheme.CollisionBad, "Matched by mod name, not by item.");
                    foreach (var item in items)
                        ImGui.TextUnformatted(item.Key);
                }
            }
        }
    }

    private void DrawCategoryToggle(ModCategory category)
    {
        var isChecked = _librarySearchCategories.Contains(category);
        if (ImGui.Checkbox($"{category}##search-category-{category}", ref isChecked))
        {
            if (isChecked)
                _librarySearchCategories.Add(category);
            else
                _librarySearchCategories.Remove(category);
        }
        ImGui.SameLine();
    }

    private void DrawUnknownToggle()
    {
        var isChecked = _librarySearchIncludeUnknown;
        if (ImGui.Checkbox("Unknown##search-category-unknown", ref isChecked))
            _librarySearchIncludeUnknown = isChecked;
    }

    private void DrawSlotToggle(EquipmentSlot slot)
    {
        var isChecked = _librarySearchSlots.Contains(slot);
        if (ImGui.Checkbox($"{SlotLabel(slot)}##search-slot-{slot}", ref isChecked))
        {
            if (isChecked)
                _librarySearchSlots.Add(slot);
            else
                _librarySearchSlots.Remove(slot);
        }
        ImGui.SameLine();
    }

    private static string SlotLabel(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Head => "Hats",
        EquipmentSlot.Top => "Tops",
        EquipmentSlot.Hands => "Hands",
        EquipmentSlot.Legs => "Bottoms",
        EquipmentSlot.Feet => "Feet",
        EquipmentSlot.Ears => "Earrings",
        EquipmentSlot.Neck => "Necklaces",
        EquipmentSlot.Wrists => "Bracelets",
        EquipmentSlot.Rings => "Rings",
        _ => slot.ToString(),
    };

    private void RestoreSnapshot(Guid snapshotId)
    {
        try
        {
            _plugin.StartRestoreOperation(snapshotId);
            _lastError = null;
            // Cleared immediately, not left to display a previous restore's results while this one
            // is in flight - Config.LastRestore/a displayed RestoreResult list are Plan E's job to
            // populate from the new async path; this plan's job is only making sure the tab doesn't
            // show stale, misattributed data in the meantime.
            _lastRestoreResults = null;
            _restoreOperationActive = true;
        }
        catch (Exception ex)
        {
            _lastError = $"Restore failed: {ex.Message}";
            Plugin.Log.Error(ex, "Restore failed.");
        }
    }

    private bool ContinueRecovery()
    {
        try
        {
            _plugin.ResolveContinue();
            _lastError = null;
            // The successor's type isn't known until after it's started (an interrupted Apply's
            // Continue is Apply-type, an interrupted Restore's Continue is Restore-type) - read it
            // back from the now-active operation rather than guessing from the interrupted one.
            var kind = _plugin.OperationController.State.Kind;
            if (kind == Organizer.Operations.OperationType.Apply)
                _applyOperationActive = true;
            else if (kind == Organizer.Operations.OperationType.Restore)
                _restoreOperationActive = true;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Continue failed: {ex.Message}";
            Plugin.Log.Error(ex, "Continue failed.");
            return false;
        }
    }

    private bool RestorePreviousState()
    {
        try
        {
            _plugin.ResolveRestorePreviousState();
            _lastError = null;
            _restoreOperationActive = true; // always Restore-type regardless of the interrupted operation's own type
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Restore Previous State failed: {ex.Message}";
            Plugin.Log.Error(ex, "Restore Previous State failed.");
            return false;
        }
    }

    private void CreateBackup(string? label)
    {
        try
        {
            _plugin.CreateBackup(label);
            _lastError = null;
            Plugin.Log.Information($"Manual backup created{(label is { Length: > 0 } ? $" ({label})" : "")}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Create backup failed: {ex.Message}";
            Plugin.Log.Error(ex, "Create backup failed.");
        }

        _historyCache = null;
    }

    private void DeleteHistorySnapshot(Guid id)
    {
        try
        {
            _plugin.DeleteHistorySnapshot(id);
            _lastError = null;
            Plugin.Log.Information($"History snapshot deleted: {id}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Delete backup failed: {ex.Message}";
            Plugin.Log.Error(ex, "Delete backup failed.");
        }

        _historyCache = null;
    }

    private void DrawOrphanedFoldersSection()
    {
        var detection = _orphanedFolders;
        var operationState = _plugin.OperationController.State;
        if (detection is null || detection.Status == Organizer.FolderDetectionStatus.NotScanned)
            return; // nothing meaningful before the first scan

        if (detection.Status is Organizer.FolderDetectionStatus.UnsupportedVersion
            or Organizer.FolderDetectionStatus.MalformedJson)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
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
            ImGui.TextDisabled(
                "Penumbra hasn't created organization.json yet — it appears once the mod tree has "
                + "folders (e.g. after your first Apply). Folder cleanup will activate then.");
            return;
        }

        var total = detection.PlainEmpty.Count + detection.CustomizedEmpty.Count;

        if (_folderReloadRequired)
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "Waiting on Rediscover Mods — the list below reflects organization.json on disk, "
                + "not Penumbra's confirmed live state. Click Rediscover Mods in Penumbra, then Scan here, to re-check.");

        ImGui.TextUnformatted($"Orphaned Folders ({total} detected)");

        if (ImGui.Button("Re-read organization.json##orphan-reread"))
            RefreshOrphanedFolders();

        ImGui.SameLine();
        ImGui.TextDisabled(_organizationJsonLastReadAt is { } readAt
            ? $"Last read: {readAt.ToLocalTime():HH:mm:ss} ({FormatElapsed(DateTimeOffset.Now - readAt)} ago)"
            : "Not yet read this session.");

        ImGui.TextDisabled(
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
            ImGui.BeginDisabled(_selectedOrphans.Count == 0 || !operationState.CanRunFolderCleanup);
            var cleanClicked = ImGui.Button("Clean Up Selected Folders");
            ImGui.EndDisabled();
            // Gated on _selectedOrphans.Count > 0 so this tooltip only claims the reason is "another
            // operation" when that's actually why the button is disabled - with no selection at all,
            // the button is disabled for an unrelated, pre-existing reason (nothing chosen yet), and
            // this tooltip must not claim an operation is blocking it when none is.
            if (_selectedOrphans.Count > 0 && !operationState.CanRunFolderCleanup && ImGui.IsItemHovered())
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
            if (cleanClicked)
                ImGui.OpenPopup("Clean up folders?");

            if (ImGui.BeginPopupModal("Clean up folders?"))
            {
                ImGui.TextUnformatted($"Remove {_selectedOrphans.Count} folder entries from Penumbra's organization.json?");
                foreach (var path in _selectedOrphans.OrderBy(p => p, StringComparer.Ordinal))
                    ImGui.TextUnformatted($"  {path}");
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
            ImGui.SameLine();
            ImGui.BeginDisabled(!operationState.CanRunFolderCleanupRollback);
            if (ImGui.Button("Rollback Folder Cleanup"))
                RollbackFolderCleanup();
            ImGui.EndDisabled();
            if (!operationState.CanRunFolderCleanupRollback && ImGui.IsItemHovered())
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
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "Penumbra hasn't loaded this change yet — open Penumbra's Settings tab and click "
                        + "Rediscover Mods before making any other folder changes there.");
                    if (r.SkippedStale.Count > 0)
                        ImGui.TextUnformatted(
                            $"{r.SkippedStale.Count} selected folder(s) were no longer orphaned and were skipped.");
                    break;
                case Organizer.FolderCleanupStatus.SucceededBackupFailed:
                    ImGui.TextColored(PluginTheme.CollisionBad,
                        $"{r.Pruned.Count} folder entries removed, but the rollback backup could not be saved. "
                        + "Rediscover Mods in Penumbra now, then avoid running another cleanup until you've "
                        + "confirmed the result — there is no safety net for this action right now.");
                    if (_plugin.FolderBackupExists)
                        ImGui.TextColored(PluginTheme.CollisionBad,
                            "The Rollback button restores an OLDER backup that predates this cleanup — "
                            + "clicking it would undo more than just this action.");
                    break;
                case Organizer.FolderCleanupStatus.NothingStillValid:
                    ImGui.TextUnformatted(
                        "Nothing was cleaned up — the selected folder(s) are no longer orphaned (or no longer exist). "
                        + "No files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.NothingSelected:
                    ImGui.TextUnformatted("Nothing selected — no files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.FileMissing:
                    ImGui.TextUnformatted("organization.json does not exist on this install — nothing to clean up.");
                    break;
                case Organizer.FolderCleanupStatus.UnsupportedVersion:
                case Organizer.FolderCleanupStatus.MalformedJson:
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "organization.json couldn't be read — no files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.FileChangedDuringCleanup:
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
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
                    ImGui.TextColored(PluginTheme.CollisionBad,
                        "The backup file is unreadable or unsupported — rollback aborted, organization.json was not touched.");
                    break;
                case Organizer.FolderRollbackStatus.NoBackup:
                    ImGui.TextUnformatted("No folder-cleanup backup exists.");
                    break;
            }
        }
    }

    private void SaveProtectionStateSafely()
    {
        try
        {
            _plugin.SaveProtectionState();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to save protection settings: {ex.Message}";
            Plugin.Log.Error(ex, "Failed to save protection settings.");
        }
    }

    private void RunScan()
    {
        try
        {
            _plugin.RunScan();
            _lastError = null;
            _folderReloadRequired = false; // the banner's instruction is "Rediscover Mods, then Scan here"
            Plugin.Log.Information($"Scan completed: {_plugin.OrganizerState.Mods.Count} mods loaded.");
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to reach Penumbra IPC: {ex.Message}";
            Plugin.Log.Error(ex, "Scan failed.");
        }

        RefreshOrphanedFolders();
    }

    private void ApplyChanges()
    {
        try
        {
            _plugin.StartApplyOperation();
            _lastError = null;
            _applyOperationActive = true;
        }
        catch (Exception ex)
        {
            _lastError = $"Apply failed: {ex.Message}";
            Plugin.Log.Error(ex, "Apply failed.");
        }
    }

    private void OpenConfigFile() => OpenContainingFolder(Plugin.PluginInterface.ConfigFile.FullName);

    // Opens Explorer with the file pre-selected rather than launching it with its default app -
    // these two files exist for attaching to a bug report, so showing where they are (and letting
    // the tester copy/zip/inspect from Explorer) is more useful than opening them directly.
    private void OpenContainingFolder(string filePath)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch (Exception ex)
        {
            _lastError = $"Could not open folder for '{filePath}': {ex.Message}";
        }
    }

    // Non-invasive by design: no absolute filesystem paths (Penumbra's config dir and the mod
    // storage root both live under the user's Windows profile folder and would leak the local
    // account name), and no full mod name/author/path list (that's what the separate Export
    // button already produces) - this is a summary of what happened this session, meant to
    // complement Export, not duplicate it.
    private string CreateDiagnosticDump()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Penumbra Organizer diagnostic dump");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:u}");
        sb.AppendLine($"Plugin version: {PluginVersion}");
        sb.AppendLine();

        sb.AppendLine("== Last error ==");
        sb.AppendLine(_lastError ?? "(none)");
        sb.AppendLine();

        sb.AppendLine("== Organizer state ==");
        var mods = _plugin.OrganizerState.Mods;
        sb.AppendLine($"Mods scanned: {mods.Count}");
        sb.AppendLine($"Protected: {mods.Count(m => m.Protected)}");
        sb.AppendLine($"Heliosphere-managed: {mods.Count(m => m.HeliosphereManaged)}");
        var validation = _plugin.OrganizerState.Validate();
        sb.AppendLine(
            $"Validation: {validation.ProtectedViolations.Count} protected violations, {validation.PathCollisions.Count} path collisions");
        sb.AppendLine();

        sb.AppendLine("== Last Apply result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatApplySection(_lastApplyResults, _plugin.Config.LastApply));
        sb.AppendLine();

        sb.AppendLine("== Last Restore result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatRestoreSection(_lastRestoreResults, _plugin.Config.LastRestore));
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatFolderCleanupSection(_lastCleanupResult, _plugin.Config.LastFolderCleanup));
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup Rollback result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(
            _lastFolderRollbackResult, _plugin.Config.LastFolderCleanupRollback));
        sb.AppendLine();

        sb.AppendLine("== Orphaned folder detection ==");
        sb.AppendLine(_orphanedFolders is null
            ? "(not run this session)"
            : $"Status={_orphanedFolders.Status}, Plain={_orphanedFolders.PlainEmpty.Count}, Customized={_orphanedFolders.CustomizedEmpty.Count}");
        sb.AppendLine();

        sb.AppendLine("== Rollback history ==");
        var history = _historyCache ??= _plugin.LoadHistory();
        sb.AppendLine($"Snapshot count: {history.Count}");
        foreach (var snapshot in history.OrderByDescending(s => s.CreatedAt))
        {
            var label = snapshot.Label is { Length: > 0 } l ? l : snapshot.AutoDescription;
            sb.AppendLine($"  {snapshot.CreatedAt.ToLocalTime():u} - {label} ({snapshot.ModPaths.Count} mods)");
        }
        sb.AppendLine();

        sb.AppendLine("== Session event log (most recent first) ==");
        foreach (var line in _eventLog)
            sb.AppendLine(line);

        var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "organizer-diagnostics.txt");
        Directory.CreateDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private void ExportWorkbook(PenumbraOrganizer.Core.Models.OrganizationStrategy strategy)
    {
        _fileDialogManager.SaveFileDialog(
            "Save Workbook",
            ".xlsx",
            Plugin.DefaultWorkbookFileName,
            ".xlsx",
            (success, path) =>
            {
                if (!success)
                    return;
                try
                {
                    _lastWorkbookExportPath = _plugin.ExportWorkbook(strategy, path);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _lastError = $"Workbook export failed: {ex.Message}";
                }
            },
            Path.GetDirectoryName(_plugin.DefaultWorkbookFilePath));
    }

    private void OpenFileWithDefaultApp(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _lastError = $"Could not open '{path}': {ex.Message}";
        }
    }

    private void ImportWorkbook(string workbookPath)
    {
        try
        {
            var result = _plugin.ImportWorkbook(workbookPath);
            _lastWorkbookImportResult = new Organizer.WorkbookImportResultView(result.Summary, result.Errors, result.Warnings);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Workbook import failed: {ex.Message}";
        }
    }

    private async Task RefreshNpcNamesAsync()
    {
        try
        {
            _npcRefreshResult = await _plugin.RefreshNpcNamesAsync(CancellationToken.None);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"NPC list refresh failed: {ex.Message}";
        }
    }

    // Detection reads a file and parses JSON — never callable from a draw method directly
    // (DrawReviewTab runs every frame). Recomputed only on explicit triggers; selection resets
    // to defaults on every recompute: a refresh means the world changed, and a stale selection
    // surviving it is the failure mode the write-time re-verification exists to catch.
    private void RefreshOrphanedFolders()
    {
        try
        {
            _orphanedFolders = _plugin.DetectOrphanedFolders();
            _organizationJsonLastReadAt = DateTimeOffset.Now;
            _selectedOrphans.Clear();
            if (_orphanedFolders.Status == Organizer.FolderDetectionStatus.Detected)
                foreach (var path in _orphanedFolders.PlainEmpty)
                    _selectedOrphans.Add(path);
        }
        catch (Exception ex)
        {
            _lastError = $"Orphaned-folder detection failed: {ex.Message}";
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero; // defends against a backward system-clock adjustment

        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours}h";

        return $"{(int)elapsed.TotalDays}d";
    }

    private void CleanUpSelectedFolders()
    {
        try
        {
            _lastCleanupResult = _plugin.CleanUpFolders(_selectedOrphans.ToHashSet(StringComparer.Ordinal));
            _lastError = null;
            if (_lastCleanupResult.Status is Organizer.FolderCleanupStatus.Success
                or Organizer.FolderCleanupStatus.SucceededBackupFailed)
                _folderReloadRequired = true;
            Plugin.Log.Information(
                $"Folder cleanup: status={_lastCleanupResult.Status}, pruned={_lastCleanupResult.Pruned.Count}, skippedStale={_lastCleanupResult.SkippedStale.Count}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Folder cleanup failed: {ex.Message}";
            Plugin.Log.Error(ex, "Folder cleanup failed.");
        }

        RefreshOrphanedFolders();
    }

    private void RollbackFolderCleanup()
    {
        try
        {
            _lastFolderRollbackResult = _plugin.RollbackFolderCleanup();
            _lastError = null;
            if (_lastFolderRollbackResult.Status == Organizer.FolderRollbackStatus.Restored)
                _folderReloadRequired = true;
            Plugin.Log.Information($"Folder cleanup rollback: status={_lastFolderRollbackResult.Status}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Folder cleanup rollback failed: {ex.Message}";
            Plugin.Log.Error(ex, "Folder cleanup rollback failed.");
        }

        RefreshOrphanedFolders();
    }
}
