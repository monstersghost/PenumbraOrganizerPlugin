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

public sealed partial class MainWindow : Window, IDisposable
{
    private const int RecentOperationsCount = 20;
    private const float StandardPopupWidth = 420f;
    private const float DetailedPopupWidth = 560f;

    // The one activity-gate explanation, shared by every control the gate can disable. It is passed
    // as Help.Tooltip's disabledReason rather than issued as a second SetTooltip against the same
    // widget - two tooltips submitted for one item in one frame fight over the same window.
    private const string ActivityGateReason = "Another operation is in progress or requires recovery.";

    private readonly Plugin _plugin;
    private readonly CreatorCanonicalizer _creatorCanonicalizer = new();
    private readonly EventLogBuffer _eventLog = new();
    // An instance, not a static call: the Group by dropdown and the two split checkboxes are
    // ImGui ref-widgets and need backing storage that survives between frames.
    private readonly SortPanel _sortPanel = new();
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
    private long _lastConsumedCompletion;

    // Set by the completion consumer, consumed inside the Review Changes tab's own draw. The
    // OpenPopup call CANNOT move into the consumer: BeginTabBar pushes an ID override, so the
    // matching BeginPopupModal inside the tab resolves the popup name against a different ID stack
    // than Draw()'s root - a root-level OpenPopup would mark a popup open that the tab's
    // BeginPopupModal never sees. The flag carries the decision across that scope boundary.
    private bool _pendingApplyReminder;
    private string _createBackupLabelInput = string.Empty;
    private Guid? _pendingRestoreSnapshotId;
    private Organizer.RestorePlan? _pendingRestorePreview;
    private IReadOnlyList<Organizer.Operations.OperationJournal> _recentOperations = [];
    private string? _recentOperationsError;
    private bool _recentOperationsSectionWasOpen;
    private IReadOnlyList<Organizer.RollbackSnapshot>? _historyCache;
    private Organizer.FolderDetectionResult? _orphanedFolders;
    private DateTimeOffset? _organizationJsonLastReadAt;
    private readonly HashSet<string> _selectedOrphans = new(StringComparer.Ordinal);
    private bool _folderReloadRequired;
    private Organizer.FolderCleanupResult? _lastCleanupResult;
    private Organizer.FolderRollbackResult? _lastFolderRollbackResult;
    // No _npcRefreshTask while the refresh button is disabled for 0.5.3.1; RefreshNpcNamesAsync and
    // the result panel below are kept so re-enabling is a one-line change.
    private Organizer.NpcNames.NpcNameRefreshResult? _npcRefreshResult;
    private readonly FileDialogManager _fileDialogManager = new();

    private Organizer.Templates.TemplateStoreListing? _templateListing;
    private Organizer.Templates.StoredTemplate? _selectedTemplate;
    private Organizer.Templates.TemplateApplicationPlan? _templatePlan;
    private Organizer.Templates.ValidatedOrganizationTemplate? _templatePlanTemplate;
    private int _templatePlanScanGeneration = -1;
    private string? _templateStatus;

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
        // Appended deliberately: _workbookStrategyIndex defaults to 2, so inserting this earlier
        // would silently change which strategy a fresh session exports with.
        ("Keep current folders (as-is)", PenumbraOrganizer.Core.Models.OrganizationStrategy.PreserveAndClean),
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

    /// <summary>
    /// Raised each time this window opens. Plugin uses it to offer the first-run walkthrough.
    /// </summary>
    /// <remarks>
    /// Deliberately on open rather than on plugin load: a window appearing over someone's gameplay
    /// because a plugin loaded is hostile, and the walkthrough describes controls that are only
    /// worth seeing once this window is actually in front of them.
    /// </remarks>
    internal Action? Opened { get; set; }

    public override void OnOpen() => Opened?.Invoke();

    /// <summary> Piece 5's Help tab button, wired by Plugin. Null until the walkthrough exists. </summary>
    internal Action? ShowWalkthrough { get; set; }

    // Called from Penumbra's IPC subscribers, which may be on any thread. The timestamp is captured
    // here rather than at drain time so it records when the callback fired; display order is queue
    // arrival order, which is not the same thing and does not claim to be.
    internal void LogEvent(string message) =>
        _eventLog.Add($"{DateTime.Now:HH:mm:ss} {message}");

    // Framework thread only, called once per update from Plugin.OnFrameworkUpdate.
    internal void DrainEventLog() => _eventLog.Drain();

    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        ConsumeCompletionIfNew();

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
                DrawTemplatesTab();

                // A standalone type rather than a MainWindow partial, so its content is reachable
                // from tests - see HelpTab's own remarks. Tab dispatch stays here regardless.
                HelpTab.Draw(ShowWalkthrough);
            }
        }

        _fileDialogManager.Draw();
    }

    // The single place an operation completion turns into UI consequences. Guarded by a generation
    // comparison rather than a per-kind latch, so a terminal snapshot that stays published for many
    // frames is consumed exactly once, and recovery successors are consumed by the same code as
    // ordinary operations rather than needing their own polling.
    //
    // DELIBERATE BEHAVIOUR CHANGE, the one exception to this plan's behaviour-preserving rule: the
    // old latches lived inside tab draw methods that early-return when their tab is not selected,
    // so completion consequences waited until the user visited the right tab. This consumer fires
    // on the frame completion is first observed, whatever tab is visible. Deferred consumption was
    // itself a latent staleness bug (a completed Apply's RunScan would not happen until a tab
    // visit), so immediate consumption is adopted knowingly rather than reproduced.
    private void ConsumeCompletionIfNew()
    {
        var state = _plugin.OperationController.State;
        if (state.CompletionGeneration <= _lastConsumedCompletion)
            return;

        _lastConsumedCompletion = state.CompletionGeneration;

        // A completion may mean history moved - most operations append a pre-operation snapshot
        // before they start, but recovery successors write theirs only into the operation bundle.
        // Invalidating unconditionally avoids having to reason about which kinds mutate it.
        _historyCache = null;

        switch (state.Kind)
        {
            case Organizer.Operations.OperationType.Apply:
                // Penumbra's own tree is now stale relative to what was just written, and
                // OrganizerState's cached CurrentPath values are stale too - RunScan re-reads both.
                RunScan();
                if (state.SuccessfulTargets > 0)
                    _pendingApplyReminder = true;
                break;

            case Organizer.Operations.OperationType.Restore:
                RunScan(); // matches today's Restore completion block: cache null + RunScan, no popup
                break;
        }
    }

    // Popups auto-size to their content by default, which makes a single long, unwrapped
    // TextColored sentence stretch the whole popup to match (a genuinely "too big" popup, often
    // wider than the main window's own minimum width). ImGui has no built-in colored+wrapped text
    // call, so this pairs PushStyleColor with TextWrapped, matching PluginTheme.cs's own
    // push-then-pop style for temporary color overrides.
    private static void TextColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    // Same rationale as TextColoredWrapped above, for the dimmed/disabled text color -
    // ImGui.TextDisabled has no built-in wrapped variant either, and several long explanatory
    // lines in this file use it.
    private static void TextDisabledWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private ActivityGates CurrentGates() => ActivityGates.Build(
        _plugin.OperationController.State, _plugin.ScanWork.State, _plugin.IndexWork.State);

    private void RefreshRecentOperations()
    {
        try
        {
            _recentOperations = Organizer.Operations.OperationBundleDiscovery.LoadRecentCompletedJournals(_plugin.OperationsRoot, take: RecentOperationsCount);
            _recentOperationsError = null;
        }
        catch (Exception ex)
        {
            _recentOperations = [];
            _recentOperationsError = $"Could not load recent operations: {ex.Message}";
            Plugin.Log.Warning(ex, "Loading recent operations failed.");
        }
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
            _historyCache = null; // the pre-operation snapshot was just appended
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
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Restore Previous State failed: {ex.Message}";
            Plugin.Log.Error(ex, "Restore Previous State failed.");
            return false;
        }
    }

    private bool ResolveOneMultiRoot(Guid operationId)
    {
        try
        {
            _plugin.ResolveOneMultiRootOperation(operationId);
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Keep Current State failed: {ex.Message}";
            Plugin.Log.Error(ex, "Keep Current State (multi-root) failed.");
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

    // Starts the scan; completion lands in OnScanPublished on a later frame. The catch covers a
    // rejected start (another library run in flight); every failure inside the run itself is
    // reported through ScanWork.State.LastError instead.
    private void RunScan()
    {
        try
        {
            _plugin.RunScan();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Could not start scan: {ex.Message}";
            Plugin.Log.Error(ex, "Scan could not be started.");
        }
    }

    // Starts the changed-item index build; the catch covers a rejected start (another library
    // run or operation in flight) so the exception cannot escape the draw callback.
    private void BuildChangedItemIndex()
    {
        try
        {
            _plugin.BuildChangedItemIndex();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Could not start index build: {ex.Message}";
            Plugin.Log.Error(ex, "Index build could not be started.");
        }
    }

    // Framework thread, called by ScanJob.Publish once results are live in OrganizerState.
    internal void OnScanPublished()
    {
        _folderReloadRequired = false; // the banner's instruction is "Rediscover Mods, then Scan here"
        Plugin.Log.Information($"Scan completed: {_plugin.OrganizerState.Mods.Count} mods loaded.");
        RefreshOrphanedFolders();
    }

    private void ApplyChanges()
    {
        try
        {
            _plugin.StartApplyOperation();
            _lastError = null;
            _historyCache = null; // the pre-operation snapshot was just appended
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
        // No session-results argument: the field that used to carry it was never assigned once the
        // async operation engine replaced the synchronous Apply path, so this section has been
        // driven entirely by the persisted Config.LastApply summary since then. Reconstructing a
        // live per-mod result list from the operation bundle is Plan E territory, not this cleanup's.
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatApplySection(null, _plugin.Config.LastApply));
        sb.AppendLine();

        sb.AppendLine("== Last Restore result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatRestoreSection(null, _plugin.Config.LastRestore));
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

        sb.AppendLine("== Interrupted operation ==");
        try
        {
            var pendingJournal = _plugin.OperationController.GetPendingRecoveryJournal();
            if (pendingJournal is not null)
            {
                sb.AppendLine($"  OperationId={pendingJournal.OperationId}, Type={pendingJournal.Type}, Stage={pendingJournal.Stage}, {pendingJournal.ProcessedStepCount}/{pendingJournal.TotalSteps} steps, UpdatedAt={pendingJournal.UpdatedAt.ToLocalTime():u}");
            }
            else
            {
                var blocked = _plugin.OperationController.GetBlockedOperations();
                if (blocked.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var (_, journal) in blocked)
                        sb.AppendLine($"  OperationId={journal.OperationId}, Type={journal.Type}, Stage={journal.Stage}, UpdatedAt={journal.UpdatedAt.ToLocalTime():u}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(failed to read: {ex.Message})");
            Plugin.Log.Warning(ex, "Diagnostic dump: reading interrupted operation state failed.");
        }
        sb.AppendLine();

        sb.AppendLine("== Recent operations ==");
        try
        {
            var recentOperations = Organizer.Operations.OperationBundleDiscovery.LoadRecentCompletedJournals(_plugin.OperationsRoot, take: RecentOperationsCount);
            if (recentOperations.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var journal in recentOperations)
                    sb.AppendLine($"  {journal.UpdatedAt.ToLocalTime():u} - {journal.Type} - {journal.Stage} - {journal.Resolution}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(failed to read: {ex.Message})");
            Plugin.Log.Warning(ex, "Diagnostic dump: reading recent operations failed.");
        }
        sb.AppendLine();

        sb.AppendLine("== Slow calls ==");
        try
        {
            var diagnosticsLogPath = Organizer.Operations.OperationBundlePaths.DiagnosticsLogPath(_plugin.OperationsRoot);
            var slowCalls = Organizer.Operations.DiagnosticsLog.ReadAll(diagnosticsLogPath)
                .Where(e => e.Kind == Organizer.Operations.DiagnosticEventKind.SlowCall)
                .ToList();
            if (slowCalls.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                // Grouped by identifier, not just the five longest raw events - five slow calls to the
                // same identifier would otherwise crowd out four other identifiers that are each slow
                // exactly once. Ranked by worst (max) duration per identifier.
                var grouped = slowCalls
                    .GroupBy(e => e.Identifier, StringComparer.Ordinal)
                    .Select(g => new { Identifier = g.Key, Count = g.Count(), WorstMs = g.Max(e => e.DurationMilliseconds), TotalMs = g.Sum(e => e.DurationMilliseconds) })
                    .OrderByDescending(x => x.WorstMs)
                    .ThenByDescending(x => x.Count)
                    .Take(5)
                    .ToList();
                sb.AppendLine($"{slowCalls.Count} recorded slow calls across {grouped.Count} displayed identifiers (ranked by worst duration):");
                foreach (var item in grouped)
                    sb.AppendLine($"  {item.Identifier}: {item.Count} calls, worst {item.WorstMs}ms, total {item.TotalMs}ms");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(failed to read: {ex.Message})");
            Plugin.Log.Warning(ex, "Diagnostic dump: reading slow-call log failed.");
        }
        sb.AppendLine();

        sb.AppendLine("== Session event log (most recent first) ==");
        foreach (var line in _eventLog.Lines)
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
