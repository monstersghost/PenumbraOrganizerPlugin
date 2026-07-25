using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Infrastructure.Exports;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/porganizer";

    public readonly WindowSystem WindowSystem = new("Penumbra Organizer");

    private readonly MainWindow _mainWindow;

    internal readonly Penumbra.Api.IpcSubscribers.GetModListAdapter GetModListAdapterIpc;
    internal readonly Penumbra.Api.IpcSubscribers.SetModPath SetModPathIpc;
    internal readonly Organizer.Operations.OperationController OperationController;
    public readonly Organizer.OrganizerState OrganizerState = new();
    public LibrarySearch.ChangedItemIndex? LibraryIndex { get; private set; }
    public string? LibraryIndexError { get; private set; }
    internal Configuration Config = null!;
    private bool _operationInProgress;
    private readonly WorkbookWorkflowService _workbookService;
    private readonly HttpClient _npcHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly Organizer.NpcNames.NpcNameRefreshService _npcNameRefreshService;

    private readonly EventSubscriber<string> _modAdded;
    private readonly EventSubscriber<string> _modDeleted;
    private readonly EventSubscriber<string, string> _modMoved;

    public Plugin()
    {
        _mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(_mainWindow);

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GetModListAdapterIpc = new Penumbra.Api.IpcSubscribers.GetModListAdapter(PluginInterface);
        SetModPathIpc = new Penumbra.Api.IpcSubscribers.SetModPath(PluginInterface);
        var operationsAdapter = new Organizer.Operations.PenumbraOperationsAdapter(PluginInterface);
        var operationsDiagnosticsSink = new Organizer.Operations.FileDiagnosticsSink(
            Organizer.Operations.OperationBundlePaths.DiagnosticsLogPath(OperationsRoot));
        OperationController = new Organizer.Operations.OperationController(
            operationsAdapter, new Organizer.Operations.StopwatchElapsedTimeSource(),
            operationsDiagnosticsSink, TimeSpan.FromMilliseconds(2), OperationsRoot);
        var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
        OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
        _workbookService = new WorkbookWorkflowService(
            new CreatorCanonicalizer(), new Organizer.PluginLogAdapter<WorkbookWorkflowService>(Log));
        _npcHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PenumbraOrganizer.Plugin/1.0 (+https://github.com/monstersghost/PenumbraOrganizer.Plugin)");
        _npcNameRefreshService = new Organizer.NpcNames.NpcNameRefreshService(
            new Organizer.NpcNames.NpcWikiScraper(_npcHttpClient));

        // Observe live changes. SetModPath is now called from ApplyChanges/Restore only,
        // gated on OrganizerState.Validate() showing no issues (see those methods below).
        _modAdded = ModAdded.Subscriber(PluginInterface, dir => _mainWindow.LogEvent($"Mod added: {dir}"));
        _modDeleted = ModDeleted.Subscriber(PluginInterface, dir => _mainWindow.LogEvent($"Mod deleted: {dir}"));
        _modMoved = ModMoved.Subscriber(PluginInterface,
            (oldDir, newDir) => _mainWindow.LogEvent($"Mod moved: {oldDir} -> {newDir}"));

        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Penumbra Organizer (MVP) window.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        // No separate settings window; the installer's config button opens the main window.
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Log.Information("Penumbra Organizer (MVP) plugin loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        Framework.Update -= OnFrameworkUpdate;

        _modAdded.Dispose();
        _modDeleted.Dispose();
        _modMoved.Dispose();

        WindowSystem.RemoveAllWindows();
        _mainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        _npcHttpClient.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void ToggleMainUi() => _mainWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        OperationController.Update();
        if (_operationInProgress && OperationController.State.CanStartApply)
            _operationInProgress = false; // any async organizer operation (Apply or Restore) just reached
                                           // a terminal, non-recovery stage - CanStartApply/CanStartRestore
                                           // are guaranteed equal today (PublishState derives both from one
                                           // shared canStartNew), so checking either detects completion of
                                           // either operation type. If a future plan ever splits them apart
                                           // per-type, this check must be revisited.
    }

    public void RunScan()
    {
        // One bulk call for all mods' changed items (Approach B in the Phase 1c spec).
        // Plain dictionary, not disposable. If Penumbra is unavailable this throws and
        // surfaces through MainWindow's existing scan error handling.
        var allChangedItems = new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary(PluginInterface).Invoke();

        using var modList = GetModListAdapterIpc.Invoke();

        var npcNameListResult = NpcNameListStore.Load(NpcNameListPath, ReadEmbeddedNpcNameSeed());
        if (npcNameListResult.Warning is not null)
            Log.Warning(npcNameListResult.Warning);
        var npcNameMatcher = NpcNameListStore.BuildMatcher(npcNameListResult.Document);

        var rows = modList.Select(mod =>
        {
            var changedItemKeys = allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? changedItems.Keys
                : Enumerable.Empty<string>();
            var classification = ModTypeClassifier.Classify(mod.Name, changedItemKeys, npcNameMatcher);

            // Disk I/O only for mods the existing GetChangedItems-based rule already confirmed
            // are Gear — every other category never touches disk for this.
            var gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable;
            if (classification.Category == ModCategory.Gear)
            {
                var equipmentSlots = ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath);
                classification = ModTypeClassifier.EnrichGearSubCategory(classification, equipmentSlots);

                // Recorded per-row (not logged) so the Export button can surface a breakdown -
                // see GearSlotDiagnostic's doc comment for why this exists. ReadEquipmentSlots
                // itself can't distinguish "directory doesn't exist" from "directory exists but
                // has no equipment evidence" (by design, per its own tests) - checked here
                // instead, since it's a materially different root cause for diagnostics.
                gearSlotDiagnostic = equipmentSlots switch
                {
                    null => GearSlotDiagnostic.ReadFailure,
                    { Count: 0 } when !mod.ModPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                    { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                    { Count: 1 } => GearSlotDiagnostic.Single,
                    _ => GearSlotDiagnostic.Ambiguous,
                };
            }

            return new Organizer.OrganizerModRow
            {
                Identifier = mod.Identifier,
                Name = mod.Name,
                Author = mod.Author,
                CurrentPath = mod.FullPath,
                ProposedPath = mod.FullPath,
                HeliosphereManaged = Organizer.HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath),
                Category = classification.Category,
                SubCategory = classification.SubCategory,
                GearSlotDiagnostic = gearSlotDiagnostic,
            };
        }).ToList();

        OrganizerState.LoadScan(rows, Config.ProtectedModIdentifiers, Config.ProtectedFolderPaths);
        SaveProtectionState();
    }

    public void BuildChangedItemIndex()
    {
        try
        {
            var allChangedItems = new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary(PluginInterface).Invoke();
            using var modList = GetModListAdapterIpc.Invoke();

            var mods = modList
                .Select(mod => new LibrarySearch.LibraryModEntry(mod.Identifier, mod.Name, mod.Author, mod.ModPath))
                .ToList();

            var npcNameListResult = NpcNameListStore.Load(NpcNameListPath, ReadEmbeddedNpcNameSeed());
            if (npcNameListResult.Warning is not null)
                Log.Warning(npcNameListResult.Warning);
            var npcNameMatcher = NpcNameListStore.BuildMatcher(npcNameListResult.Document);

            var changedItemIdentifiers = allChangedItems.Keys.ToHashSet(StringComparer.Ordinal);

            LibraryIndex = LibrarySearch.ChangedItemIndexBuilder.Build(
                mods,
                changedItemIdentifiers,
                identifier => allChangedItems.TryGetValue(identifier, out var changedItems)
                    ? changedItems.Keys
                    : Enumerable.Empty<string>(),
                npcNameMatcher);
            LibraryIndexError = null;
        }
        catch (Exception ex)
        {
            // Atomic replacement: LibraryIndex is only ever reassigned above, after every step
            // succeeds. A thrown exception here (e.g. Penumbra unavailable) leaves the previous
            // index (and its BuiltAt timestamp) exactly as it was -- a failed refresh must not
            // discard a previously good result.
            LibraryIndexError = $"Refresh failed: {ex.Message}";
            Log.Warning(ex, "Library Search index refresh failed.");
        }
    }

    internal void SaveProtectionState()
    {
        Config.ProtectedModIdentifiers = OrganizerState.ProtectedModIdentifiers.ToHashSet();
        Config.ProtectedFolderPaths = OrganizerState.ProtectedFolders.ToHashSet();
        PluginInterface.SavePluginConfig(Config);
    }

    private PenumbraInstallation BuildInstallation() => new(
        ConfigurationPath: string.Empty,
        ConfigDirectory: PenumbraConfigDirectory,
        ModRoot: new Penumbra.Api.IpcSubscribers.GetModDirectory(PluginInterface).Invoke(),
        PluginAssemblyPath: null,
        PluginManifestPath: null,
        InstalledVersion: null,
        Confidence: DiscoveryConfidence.High,
        Evidence: [],
        Warnings: []);

    internal const string DefaultWorkbookFileName = "organizer-workbook.xlsx";

    internal string DefaultWorkbookFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, DefaultWorkbookFileName);

    private string NpcNameListPath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "npc-name-list.json");

    private static string ReadEmbeddedNpcNameSeed()
    {
        var assembly = typeof(Plugin).Assembly;
        const string resourceName = "PenumbraOrganizer.Plugin.Organizer.NpcNames.npc-name-list-seed.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal string ExportWorkbook(OrganizationStrategy strategy, string destinationPath)
    {
        var inventory = Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation());
        var proposals = Organizer.WorkbookAdapter.ToProposals(OrganizerState);
        var preferences = Organizer.WorkbookAdapter.ToOrganizationPreferences(strategy);

        // ClosedXML's SaveAs validates the file extension and rejects anything but
        // .xlsx/.xlsm/.xltx/.xltm, so the temp name must keep .xlsx as its actual extension -
        // "organizer-workbook.xlsx.tmp" fails, "organizer-workbook.tmp.xlsx" doesn't.
        var tempPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $"{Path.GetFileNameWithoutExtension(destinationPath)}.tmp{Path.GetExtension(destinationPath)}");
        var export = _workbookService.ExportAsync(inventory, proposals, preferences, tempPath, CancellationToken.None)
            .GetAwaiter().GetResult();
        File.Move(export.WorkbookPath, destinationPath, overwrite: true);
        return destinationPath;
    }

    internal WorkbookImportResult ImportWorkbook(string workbookPath)
    {
        var inventory = Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation());
        var result = _workbookService.ImportAsync(workbookPath, inventory, CancellationToken.None)
            .GetAwaiter().GetResult();
        Organizer.WorkbookAdapter.ApplyImportResult(OrganizerState, result);
        return result;
    }

    internal async Task<Organizer.NpcNames.NpcNameRefreshResult> RefreshNpcNamesAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5)); // generous: NPCs alone can span 50+ pages
        return await _npcNameRefreshService.RefreshAsync(NpcNameListPath, ReadEmbeddedNpcNameSeed(), timeoutCts.Token);
    }

    internal string ExportReview()
    {
        var content = Organizer.OrganizerExportFormatter.Format(OrganizerState.Mods, OrganizerState.Validate());
        var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-export.txt");
        Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
        File.WriteAllText(path, content);
        return path;
    }

    internal IReadOnlyList<Organizer.RollbackSnapshot> LoadHistory() =>
        Organizer.RollbackHistory.Load(HistoryFilePath);

    internal void CreateBackup(string? label)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var currentMods = ReadCurrentMods();
            var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label, "Manual backup");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    internal void DeleteHistorySnapshot(Guid id)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            Organizer.RollbackHistory.DeleteSnapshot(HistoryFilePath, id);
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private string HistoryFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-history.json");

    private string OperationsRoot => Path.Combine(PluginInterface.ConfigDirectory.FullName, "operations");

    // Penumbra's config dir is a sibling of this plugin's own under Dalamud's pluginConfigs
    // folder — no IPC exposes it (confirmed against the full Penumbra.Api 5.15.1 surface; see
    // the folder-cleanup design spec's Ground truth section).
    private static string PenumbraConfigDirectory =>
        Path.Combine(Directory.GetParent(PluginInterface.ConfigDirectory.FullName)!.FullName, "Penumbra");

    private static string OrganizationJsonPath =>
        Path.Combine(PenumbraConfigDirectory, "mod_filesystem", "organization.json");

    private string FolderBackupFilePath =>
        Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-folder-backup.json");

    internal bool FolderBackupExists => File.Exists(FolderBackupFilePath);

    [Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
    internal IReadOnlyList<Organizer.ApplyResult> ApplyChanges()
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var validation = OrganizerState.Validate();
            if (validation.HasIssues)
                throw new InvalidOperationException("Cannot Apply while Validate() reports issues.");

            // Equivalence, not raw string equality: a path differing only by a transient " (N)"
            // duplicate marker (or Penumbra's own name-trimming) is the same persisted location —
            // moving it would be a no-op write that Penumbra reshuffles on the next reload anyway.
            var touchedRows = OrganizerState.Mods
                .Where(m => !m.Protected && !Organizer.PenumbraPathSemantics.AreEquivalent(m.CurrentPath, m.ProposedPath, m.Name))
                .ToList();

            var folderCollisions = Organizer.ApplyPlanner.FolderPathCollisions(touchedRows, ReadExistingOrganizationFolderPaths());
            if (folderCollisions.Count > 0)
                throw new InvalidOperationException(
                    "Cannot Apply: the proposed path for the following mods matches an existing (likely orphaned) " +
                    "folder entry in Penumbra's organization.json, which Penumbra's own SetModPath will reject: " +
                    $"{string.Join(", ", folderCollisions)}. Run Folder Cleanup on the Review Changes tab to prune " +
                    "orphaned folders, then try Apply again.");

            var currentMods = ReadCurrentMods();
            var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label: null, $"{touchedRows.Count} mods moved");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);

            // From here on, a snapshot has already been captured - an unexpected exception must
            // still leave a diagnostic trail behind (tester report: prior-session Apply results
            // were silently lost on reload), not just bubble up with the outcome unrecorded.
            List<Organizer.ApplyResult> results;
            try
            {
                var moves = touchedRows
                    .Select(r => new Organizer.ModMove(r.Identifier, r.CurrentPath, r.ProposedPath))
                    .ToList();
                var failureByIdentifier = ExecuteOrderedMoves(moves);
                results = touchedRows
                    .Select(r => new Organizer.ApplyResult(
                        r.Identifier, !failureByIdentifier.ContainsKey(r.Identifier), failureByIdentifier.GetValueOrDefault(r.Identifier)))
                    .ToList();
            }
            catch (Exception)
            {
                Config.LastApply = new Organizer.ApplyOperationSummary(
                    DateTimeOffset.Now, Organizer.OperationCompletionStatus.Failed, Succeeded: 0, Failed: touchedRows.Count);
                PluginInterface.SavePluginConfig(Config);
                throw;
            }

            var applySucceeded = results.Count(r => r.Success);
            var applyStatus = results.Count == 0 || applySucceeded == results.Count
                ? Organizer.OperationCompletionStatus.Succeeded
                : applySucceeded == 0
                    ? Organizer.OperationCompletionStatus.Failed
                    : Organizer.OperationCompletionStatus.PartiallySucceeded;
            Config.LastApply = new Organizer.ApplyOperationSummary(
                DateTimeOffset.Now, applyStatus, applySucceeded, results.Count - applySucceeded);
            PluginInterface.SavePluginConfig(Config);

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    internal void StartApplyOperation()
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");

        var validation = OrganizerState.Validate();
        if (validation.HasIssues)
            throw new InvalidOperationException("Cannot Apply while Validate() reports issues.");

        // Equivalence, not raw string equality - a path differing only by a transient " (N)"
        // duplicate marker (or Penumbra's own name-trimming) is the same persisted location -
        // moving it would be a no-op write that Penumbra reshuffles on the next reload anyway.
        var touchedRows = OrganizerState.Mods
            .Where(m => !m.Protected && !Organizer.PenumbraPathSemantics.AreEquivalent(m.CurrentPath, m.ProposedPath, m.Name))
            .ToList();

        var folderCollisions = Organizer.ApplyPlanner.FolderPathCollisions(touchedRows, ReadExistingOrganizationFolderPaths());
        if (folderCollisions.Count > 0)
            throw new InvalidOperationException(
                "Cannot Apply: the proposed path for the following mods matches an existing (likely orphaned) " +
                "folder entry in Penumbra's organization.json, which Penumbra's own SetModPath will reject: " +
                $"{string.Join(", ", folderCollisions)}. Run Folder Cleanup on the Review Changes tab to prune " +
                "orphaned folders, then try Apply again.");

        var currentMods = ReadCurrentMods();
        var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label: null, $"{touchedRows.Count} mods moved");
        Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);

        var plan = Organizer.Operations.OperationPlanBuilder.BuildApplyPlan(touchedRows);
        var bundleDirectory = Organizer.Operations.OperationBundlePaths.BundleDirectory(OperationsRoot, active: true, plan.OperationId);
        Organizer.Operations.OperationPlanCodec.Save(Organizer.Operations.OperationBundlePaths.PlanPath(bundleDirectory), plan);
        Organizer.Operations.OperationSnapshotCodec.Save(Organizer.Operations.OperationBundlePaths.SnapshotPath(bundleDirectory), snapshot);

        _operationInProgress = true;
        try
        {
            OperationController.StartApply(plan, snapshot.Id, bundleDirectory);
        }
        catch
        {
            _operationInProgress = false;
            throw;
        }
    }

    internal void StartRestoreOperation(Guid snapshotId)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        // Defense-in-depth alongside _operationInProgress, not a replacement for it: reads the
        // controller's own authoritative state before any side effect below runs. A narrow TOCTOU gap
        // remains between this check and OperationController.StartRestore's own admission guard,
        // accepted rather than closed with a reservation API - both entry points only ever fire from
        // a button click on the single UI thread, so the gap has no live trigger today.
        if (!OperationController.State.CanStartRestore)
            throw new InvalidOperationException("Another organizer operation is already in progress or requires recovery.");

        var history = Organizer.RollbackHistory.Load(HistoryFilePath);
        var target = history.FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new InvalidOperationException("Snapshot not found.");

        var currentMods = ReadCurrentMods();

        // Current protection state is deliberately never passed to BuildRestorePlan - unchanged
        // reasoning from the synchronous Restore() path (tester report, Bug 3).
        var restorePlan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);
        var namedMoves = Organizer.Operations.OperationPlanBuilder.BuildNamedMoves(restorePlan.Moves, currentMods);
        var plan = Organizer.Operations.OperationPlanBuilder.BuildOperationPlan(Organizer.Operations.OperationType.Restore, namedMoves);

        var preRestoreLabel = target.Label ?? target.CreatedAt.ToString("u");
        var preRestoreSnapshot = Organizer.RollbackHistory.CaptureSnapshot(
            currentMods, label: null, autoDescription: $"Snapshot before restoring to \"{preRestoreLabel}\"");

        var resultSeed = new Organizer.Operations.RestoreResultSeed(
            target, restorePlan.UnchangedIdentifiers, restorePlan.SkippedUninstalledIdentifiers, restorePlan.RootRelocatedIdentifiers);

        var bundleDirectory = Organizer.Operations.OperationBundlePaths.BundleDirectory(OperationsRoot, active: true, plan.OperationId);
        Organizer.Operations.OperationPlanCodec.Save(Organizer.Operations.OperationBundlePaths.PlanPath(bundleDirectory), plan);
        Organizer.Operations.OperationSnapshotCodec.Save(Organizer.Operations.OperationBundlePaths.SnapshotPath(bundleDirectory), preRestoreSnapshot);
        Organizer.Operations.OperationRestoreResultSeedCodec.Save(
            Organizer.Operations.OperationBundlePaths.RestoreResultSeedPath(bundleDirectory), resultSeed);

        // Everything above is pure computation or a bundle-local write; only after all of it succeeds
        // does the operation become visible in the user-facing history file. This bounds the failure
        // window that can leave a "Snapshot before restoring..." entry with no accompanying restore
        // to failures below this line - a failure above can still leave partial bundle-local files
        // with no history entry and no active operation, which is accepted residue (see Task 1).
        Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, preRestoreSnapshot);

        _operationInProgress = true;
        try
        {
            OperationController.StartRestore(plan, preRestoreSnapshot.Id, bundleDirectory);
        }
        catch
        {
            _operationInProgress = false;
            throw;
        }
    }

    internal void ResolveKeepCurrent()
    {
        OperationController.ResolveKeepCurrent();
        RunScan();
    }

    internal void AcceptAllAndCloseInterruptedOperations()
    {
        OperationController.AcceptAllAndCloseInterruptedOperations();
        RunScan();
    }

    [Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
    internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var history = Organizer.RollbackHistory.Load(HistoryFilePath);
            var target = history.FirstOrDefault(s => s.Id == snapshotId)
                ?? throw new InvalidOperationException("Snapshot not found.");

            var currentMods = ReadCurrentMods();

            // Pre-restore snapshot makes the restore itself undoable - captured and persisted
            // before any moves happen, same as Apply's own pre-operation capture.
            var preRestoreLabel = target.Label ?? target.CreatedAt.ToString("u");
            var preRestoreSnapshot = Organizer.RollbackHistory.CaptureSnapshot(
                currentMods, label: null, autoDescription: $"Snapshot before restoring to \"{preRestoreLabel}\"");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, preRestoreSnapshot);

            // Current protection state (individual, folder, or Heliosphere) is deliberately
            // never passed to BuildRestorePlan for mods present in the snapshot - see its doc
            // comment and this plan's Global Constraints for why (tester report, Bug 3).
            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);

            // From here on, a pre-restore snapshot has already been captured - an unexpected
            // exception must still leave a diagnostic trail behind, same reasoning as ApplyChanges().
            List<Organizer.RestoreResult> results;
            try
            {
                var failureByIdentifier = ExecuteOrderedMoves(plan.Moves);

                results = new List<Organizer.RestoreResult>();
                foreach (var identifier in plan.UnchangedIdentifiers)
                    results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.Unchanged, null));
                foreach (var identifier in plan.SkippedUninstalledIdentifiers)
                    results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedUninstalled, null));

                var rootRelocatedIds = plan.RootRelocatedIdentifiers.ToHashSet(StringComparer.Ordinal);
                foreach (var move in plan.Moves)
                {
                    var failed = failureByIdentifier.TryGetValue(move.Identifier, out var reason);
                    var outcome = failed
                        ? Organizer.RestoreOutcome.Failed
                        : rootRelocatedIds.Contains(move.Identifier)
                            ? Organizer.RestoreOutcome.RootRelocated
                            : Organizer.RestoreOutcome.Moved;
                    results.Add(new Organizer.RestoreResult(move.Identifier, outcome, failed ? reason : null));
                }
            }
            catch (Exception)
            {
                // Failed: plan.Moves.Count is a coarse approximation, not a true per-move outcome -
                // some moves may have already succeeded before the exception. This diagnostic
                // summary treats the whole batch as failed rather than tracking a partial count.
                Config.LastRestore = new Organizer.RestoreOperationSummary(
                    DateTimeOffset.Now, Organizer.OperationCompletionStatus.Failed,
                    Moved: 0, Unchanged: 0, SkippedUninstalled: 0, RootRelocated: 0, Failed: plan.Moves.Count);
                PluginInterface.SavePluginConfig(Config);
                throw;
            }

            var failedCount = results.Count(r => r.Outcome == Organizer.RestoreOutcome.Failed);
            var restoreStatus = failedCount == 0
                ? Organizer.OperationCompletionStatus.Succeeded
                : failedCount == plan.Moves.Count
                    ? Organizer.OperationCompletionStatus.Failed
                    : Organizer.OperationCompletionStatus.PartiallySucceeded;
            Config.LastRestore = new Organizer.RestoreOperationSummary(
                DateTimeOffset.Now,
                restoreStatus,
                Moved: results.Count(r => r.Outcome == Organizer.RestoreOutcome.Moved),
                Unchanged: results.Count(r => r.Outcome == Organizer.RestoreOutcome.Unchanged),
                SkippedUninstalled: results.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedUninstalled),
                RootRelocated: results.Count(r => r.Outcome == Organizer.RestoreOutcome.RootRelocated),
                Failed: failedCount);
            PluginInterface.SavePluginConfig(Config);

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    // Read-only: computes what a Restore would do without capturing a snapshot or moving
    // anything, so the confirmation popup can show currently-protected/Heliosphere-managed mods
    // that will nevertheless move under this plan's Bug 3 fix, before the user commits to it.
    internal Organizer.RestorePlan PreviewRestore(Guid snapshotId)
    {
        var history = Organizer.RollbackHistory.Load(HistoryFilePath);
        var target = history.FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new InvalidOperationException("Snapshot not found.");
        var currentMods = ReadCurrentMods();
        return Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);
    }

    // Runs a cycle-safe ordered set of SetModPath calls and reports, per identifier, the first
    // failure it hit (skipping any later step for an identifier once one of its steps has failed,
    // since a mod parked mid-cycle can't reach its real target if its own earlier hop failed).
    [Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
    private Dictionary<string, string> ExecuteOrderedMoves(IReadOnlyList<Organizer.ModMove> moves)
    {
        var steps = Organizer.ApplyPlanner.OrderMovesForApply(moves);
        var failureByIdentifier = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (failureByIdentifier.ContainsKey(step.Identifier))
                continue;
            var ec = SetModPathIpc.Invoke(step.Identifier, step.TargetPath, "");
            if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success)
                failureByIdentifier[step.Identifier] = ec.ToString();
        }
        return failureByIdentifier;
    }

    private Dictionary<string, string> ReadCurrentModPaths()
    {
        using var modList = GetModListAdapterIpc.Invoke();
        return modList.ToDictionary(m => m.Identifier, m => m.FullPath, StringComparer.Ordinal);
    }

    private List<Organizer.LiveMod> ReadCurrentMods()
    {
        using var modList = GetModListAdapterIpc.Invoke();
        return modList.Select(mod => new Organizer.LiveMod(
                mod.Identifier,
                mod.Name,
                mod.FullPath,
                Organizer.HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath)))
            .ToList();
    }

    internal void ProtectAndSkipBlockingMods()
    {
        var rowsById = OrganizerState.Mods.ToDictionary(m => m.Identifier);
        foreach (var identifier in Organizer.ApplyPlanner.BlockingIdentifiers(OrganizerState.Validate()))
        {
            if (!rowsById.TryGetValue(identifier, out var mod))
                continue;
            OrganizerState.AssignManual(identifier, mod.CurrentPath);
            OrganizerState.SetProtected(identifier, true);
        }
        SaveProtectionState();
    }

    internal Organizer.FolderDetectionResult DetectOrphanedFolders()
    {
        // Before any scan, the occupied set would be empty and every real folder would look
        // orphaned — an active false positive. Distinct from "scanned, zero mods", which must
        // detect normally (an empty library is where everything may legitimately be orphaned).
        if (!OrganizerState.HasScanned)
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.NotScanned);

        if (!File.Exists(OrganizationJsonPath))
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.FileMissing);

        var parse = Organizer.OrganizationJsonCodec.Parse(File.ReadAllText(OrganizationJsonPath));
        if (parse.Status == Organizer.OrganizationJsonParseStatus.MalformedJson)
        {
            Log.Warning("organization.json is not valid JSON; folder cleanup unavailable.");
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.MalformedJson);
        }

        if (parse.Status == Organizer.OrganizationJsonParseStatus.UnsupportedVersion)
        {
            Log.Warning("organization.json has an unsupported Version; folder cleanup unavailable.");
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.UnsupportedVersion);
        }

        // Advisory list: last-scan occupancy is acceptable here — the write path re-derives
        // occupancy from a fresh IPC read and is the enforcement point.
        var occupied = OccupiedFolders(OrganizerState.Mods.Select(m => m.CurrentPath));
        var (plain, customized) = Organizer.OrganizationCleanupPlanner.DetectOrphaned(parse.Data!, occupied);
        return new Organizer.FolderDetectionResult(plain, customized, Organizer.FolderDetectionStatus.Detected);
    }

    internal Organizer.FolderCleanupResult CleanUpFolders(IReadOnlySet<string> selectedPaths)
    {
        // Fresh IPC read at write time — OrganizerState is only as fresh as the last scan and
        // can't see mods moved via Penumbra's own UI since then. Deliberately NOT RunScan(),
        // which would reset every ProposedPath and wipe staged sort proposals. If this throws
        // (Penumbra unavailable), nothing has been written: a clean abort surfaced by the
        // caller's error handling.
        using var modList = GetModListAdapterIpc.Invoke();
        var occupied = OccupiedFolders(modList.Select(m => m.FullPath));

        var result = Organizer.FolderCleanupExecutor.Execute(
            OrganizationJsonPath, FolderBackupFilePath, selectedPaths, occupied);

        Config.LastFolderCleanup = new Organizer.FolderCleanupOperationSummary(
            DateTimeOffset.Now, result.Status, result.Pruned.Count, result.SkippedStale.Count);
        PluginInterface.SavePluginConfig(Config);

        return result;
    }

    internal Organizer.FolderRollbackResult RollbackFolderCleanup()
    {
        var result = Organizer.FolderCleanupExecutor.ExecuteRollback(OrganizationJsonPath, FolderBackupFilePath);

        Config.LastFolderCleanupRollback = new Organizer.FolderCleanupRollbackOperationSummary(DateTimeOffset.Now, result.Status);
        PluginInterface.SavePluginConfig(Config);

        return result;
    }

    private static HashSet<string> OccupiedFolders(IEnumerable<string> fullPaths) =>
        fullPaths
            .Select(Organizer.OrganizationCleanupPlanner.GetVirtualParent)
            .Where(parent => parent is not null)
            .Select(parent => parent!)
            .ToHashSet(StringComparer.Ordinal);

    // Best-effort: a missing or unparseable organization.json means "no known folders to collide
    // with", not a reason to block Apply - Folder Cleanup already surfaces those failure modes
    // on its own tab.
    private static IReadOnlySet<string> ReadExistingOrganizationFolderPaths()
    {
        if (!File.Exists(OrganizationJsonPath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parse = Organizer.OrganizationJsonCodec.Parse(File.ReadAllText(OrganizationJsonPath));
        return parse.Data is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(parse.Data.Folders.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
