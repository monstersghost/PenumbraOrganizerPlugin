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
using PenumbraOrganizer.Plugin.LibraryWork;
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

    internal void SetLibraryIndex(LibrarySearch.ChangedItemIndex index)
    {
        LibraryIndex = index;
        LibraryIndexError = null;
    }
    internal Configuration Config = null!;
    private readonly WorkbookWorkflowService _workbookService;
    private readonly HttpClient _npcHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly Organizer.NpcNames.NpcNameRefreshService _npcNameRefreshService;

    internal ModEventEpoch ModEvents { get; } = new();
    internal LibraryWorkCoordinator<LibraryWork.Pure.ScanSeed, Organizer.OrganizerModRow> ScanWork { get; }
    internal LibraryWorkCoordinator<LibraryWork.Pure.IndexSeed, LibrarySearch.IndexedMod> IndexWork { get; }

    private readonly EventSubscriber<string> _modAdded;
    private readonly EventSubscriber<string> _modDeleted;
    private readonly EventSubscriber<string, string> _modMoved;
    private readonly EventSubscriber<string, bool> _modDirectoryChanged;
    private readonly EventSubscriber _penumbraDisposed;

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
            operationsDiagnosticsSink, TimeSpan.FromMilliseconds(2), OperationsRoot,
            // Late-bound on purpose: the delegate reads the coordinator property at invoke time,
            // never at construction. OperationController is constructed before ScanWork and
            // IndexWork are assigned, and no admission check can run during the constructor - but
            // the null guard makes that ordering a non-issue rather than an invariant to remember.
            externalActivityGate: () => ScanWork is null || IndexWork is null
                ? null
                : LibraryWork.LibraryActivityGate.Reason(ScanWork.State, IndexWork.State));
        ScanWork = new LibraryWorkCoordinator<LibraryWork.Pure.ScanSeed, Organizer.OrganizerModRow>(
            () => ModEvents.Current, logWarning: message => Log.Warning(message));
        IndexWork = new LibraryWorkCoordinator<LibraryWork.Pure.IndexSeed, LibrarySearch.IndexedMod>(
            () => ModEvents.Current, logWarning: message => Log.Warning(message));
        var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
        OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
        try
        {
            Organizer.Operations.OperationBundleRetention.RunRetentionPass(OperationsRoot, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Operation bundle retention failed; plugin startup will continue.");
        }
        _workbookService = new WorkbookWorkflowService(
            new CreatorCanonicalizer(), new Organizer.PluginLogAdapter<WorkbookWorkflowService>(Log));
        _npcHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PenumbraOrganizer.Plugin/1.0 (+https://github.com/monstersghost/PenumbraOrganizer.Plugin)");
        _npcNameRefreshService = new Organizer.NpcNames.NpcNameRefreshService(
            new Organizer.NpcNames.NpcWikiScraper(_npcHttpClient));

        // Observe live changes. SetModPath is now reached only through the operation engine
        // (StartApplyOperation/StartRestoreOperation -> OperationController -> PathMutationOperation),
        // gated on OrganizerState.Validate() showing no issues.
        _modAdded = ModAdded.Subscriber(PluginInterface, dir =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod added: {dir}");
        });
        _modDeleted = ModDeleted.Subscriber(PluginInterface, dir =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod deleted: {dir}");
        });
        _modMoved = ModMoved.Subscriber(PluginInterface, (oldDir, newDir) =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod moved: {oldDir} -> {newDir}");
        });
        _modDirectoryChanged = ModDirectoryChanged.Subscriber(PluginInterface, (dir, locked) =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod directory changed: {dir}");
        });
        // Penumbra's own docs for GetChangedItemAdapterDictionary say to clear it on Disposed, so a
        // run holding one across this event is working from storage that is no longer valid.
        _penumbraDisposed = Disposed.Subscriber(PluginInterface, () =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent("Penumbra unloaded.");
        });

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
        _modDirectoryChanged.Dispose();
        _penumbraDisposed.Dispose();

        ScanWork.Dispose();
        IndexWork.Dispose();

        WindowSystem.RemoveAllWindows();
        _mainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        _npcHttpClient.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void ToggleMainUi() => _mainWindow.Toggle();

    // Everything that must happen once new scan data is live, none of which can be rolled back.
    // Each is isolated: one failing must not skip the others, and none may fail the run - the data
    // is already published by the time this is called.
    internal void RunPostScanSideEffects()
    {
        try
        {
            SaveProtectionState();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving protection state after a scan failed; the scan itself succeeded.");
        }

        try
        {
            _mainWindow.OnScanPublished();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Post-scan refresh failed; the scan itself succeeded.");
        }
    }

    private bool _libraryUpdateFaulted;

    private void OnFrameworkUpdate(IFramework framework)
    {
        OperationController.Update(); // has its own internal exception boundary

        try
        {
            _mainWindow.DrainEventLog();
            ScanWork.Update();
            IndexWork.Update();
        }
        catch (Exception ex)
        {
            // Latched: a fault that recurs every frame must not log every frame. Library work is
            // non-critical - the plugin stays usable, the user re-runs the scan after a reload.
            if (!_libraryUpdateFaulted)
            {
                _libraryUpdateFaulted = true;
                Log.Error(ex, "Library work update failed; background scans are disabled until the plugin reloads.");
            }
        }
    }

    /// <summary>
    /// Starts a scan. Returns as soon as the Penumbra reads are done; classification and the
    /// per-mod disk walk run on a background thread and publish through ScanJob.Publish.
    /// Throws InvalidOperationException if any conflicting activity is running.
    /// </summary>
    public void RunScan()
    {
        // EnsureAdmitted is the state-authority plan's shared admission point: it consults
        // AdmissionRejectionReason, which covers an active operation, a pending recovery, a
        // starting operation, AND (via the Task 8 gate) the other library coordinator.
        EnsureAdmitted();
        ScanWork.Start(new ScanJob(this, NpcNameListPath, ReadEmbeddedNpcNameSeed()));
    }

    /// <summary>
    /// Best-effort scan for callers that must not fail if one cannot start right now - specifically
    /// the recovery-resolution paths, whose scan is a refresh after the fact, not a correctness
    /// requirement. Returns false and logs rather than throwing out of a committed recovery.
    /// </summary>
    internal bool TryRequestScan()
    {
        if (OperationController.AdmissionRejectionReason() is { } reason)
        {
            Log.Information($"Post-recovery scan skipped: {reason} Use Refresh mod list when it clears.");
            return false;
        }

        RunScan();
        return true;
    }

    /// <summary>
    /// Starts a Search index build. Same three-phase shape as RunScan; a failed or discarded run
    /// leaves the previous LibraryIndex untouched. Throws InvalidOperationException if a library run
    /// is already in flight.
    /// </summary>
    public void BuildChangedItemIndex()
    {
        EnsureAdmitted(); // same shared admission point as RunScan - see Task 9
        IndexWork.Start(new LibraryWork.IndexJob(this, NpcNameListPath, ReadEmbeddedNpcNameSeed()));
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

    internal string NpcNameListPath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "npc-name-list.json");

    internal static string ReadEmbeddedNpcNameSeed()
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
        EnsureAdmitted();
        var currentMods = ReadCurrentMods();
        var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label, "Manual backup");
        Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);
    }

    internal void DeleteHistorySnapshot(Guid id)
    {
        EnsureAdmitted();
        Organizer.RollbackHistory.DeleteSnapshot(HistoryFilePath, id);
    }

    private string HistoryFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-history.json");

    internal string OperationsRoot => Path.Combine(PluginInterface.ConfigDirectory.FullName, "operations");

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

    internal void StartApplyOperation()
    {
        var result = OperationController.TryStart(Organizer.Operations.OperationType.Apply, () =>
        {
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

            return new Organizer.Operations.PreparedOperation(plan, snapshot.Id, bundleDirectory);
        });

        if (!result.Started)
            throw new InvalidOperationException(result.RejectionReason!);
    }

    internal void StartRestoreOperation(Guid snapshotId)
    {
        var result = OperationController.TryStart(Organizer.Operations.OperationType.Restore, () =>
        {
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

            return new Organizer.Operations.PreparedOperation(plan, preRestoreSnapshot.Id, bundleDirectory);
        });

        if (!result.Started)
            throw new InvalidOperationException(result.RejectionReason!);
    }

    internal void ResolveKeepCurrent()
    {
        OperationController.ResolveKeepCurrent();
        TryRequestScan();
    }

    internal void ResolveContinue()
    {
        EnsureAdmittedForRecoveryResolution();
        OperationController.ResolveContinue();
        // No RunScan() here, unlike ResolveKeepCurrent/AcceptAll - this starts a new async operation,
        // which is polled to completion exactly like an ordinary Apply/Restore already is (Task 6's
        // MainWindow wiring) - RunScan() belongs there, not at the moment the operation merely starts.
    }

    internal void ResolveRestorePreviousState()
    {
        EnsureAdmittedForRecoveryResolution();
        OperationController.ResolveRestorePreviousState();
    }

    internal void AcceptAllAndCloseInterruptedOperations()
    {
        OperationController.AcceptAllAndCloseInterruptedOperations();
        TryRequestScan();
    }

    internal void RequestCancellation() => OperationController.RequestCancellation();

    // Throws rather than returning the result, deliberately: MainWindow's handlers already catch
    // InvalidOperationException and surface the message via _lastError, so preserving the exception
    // keeps this a behaviour-preserving refactor. The structured OperationStartResult is available
    // for the UI to adopt later, when changing what the user sees is actually in scope.
    internal void EnsureAdmitted()
    {
        if (OperationController.AdmissionRejectionReason() is { } reason)
            throw new InvalidOperationException(reason);
    }

    // Recovery resolution is admitted despite _pendingRecovery - that is the state it exists to
    // clear. It must still be excluded by an in-flight operation or another starting caller.
    internal void EnsureAdmittedForRecoveryResolution()
    {
        if (OperationController.RecoveryResolutionRejectionReason() is { } reason)
            throw new InvalidOperationException(reason);
    }

    internal void ResolveOneMultiRootOperation(Guid operationId)
    {
        OperationController.ResolveOneMultiRootOperation(operationId);
        // Resolving one root can just as easily leave an ordinary single pending recovery (two roots
        // -> one) or a smaller blocked set (three roots -> two) as it can reach Idle (the last root
        // resolved) - in the first two outcomes CanScan is still false, so an unconditional RunScan()
        // would throw or record a misleading error while a recovery is still outstanding. Only scan
        // once recovery has actually cleared.
        if (!OperationController.State.RequiresRecovery)
            TryRequestScan();
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
        EnsureAdmitted();
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
        EnsureAdmitted();
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
