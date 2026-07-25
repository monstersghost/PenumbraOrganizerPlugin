# Plan E: Operation UI and Diagnostics Presentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire seven areas of already-computed-but-undisplayed `OperationController` data and
already-built-but-unwired backend capability into `MainWindow.cs`/`Plugin.cs`: capability lockout,
progress display, a Stop control, per-mod recovery detail, multi-root incremental resolution,
diagnostics dump v2, and operation history display — plus wiring `OperationBundleRetention.
RunRetentionPass`, found to have zero production call sites despite being fully implemented since
Plan A2.

**Architecture:** No execution-engine changes. `OperationController` gains three query accessors
(`GetPendingRecoveryArtifactStatus`, `GetPendingRecoveryJournal`, `GetBlockedOperations`) and one
genuinely new resolution method (`ResolveOneMultiRootOperation`, built by extracting a shared
per-journal Keep-Current helper out of the existing `AcceptAllAndCloseInterruptedOperations` and
re-running the existing `OperationBundleDiscovery.RunStartupDiscovery` → `RegisterDiscoveredRecovery`
pipeline over whatever remains after resolving one root — failure-atomically: the old blocked-graph
state is only replaced once a fresh discovery result is in hand, never cleared speculatively before
one exists). `OperationBundleDiscovery` gains one new read function (`LoadRecentCompletedJournals`).
Everything else is `MainWindow.cs`/`Plugin.cs` wiring, with two correctness details that aren't just
wiring: the `Plugin.cs` wrapper for `ResolveOneMultiRootOperation` only re-scans once recovery has
actually cleared (resolving one root doesn't always reach `Idle`), and the retention-pass call is
isolated in its own try/catch so a maintenance failure can't block plugin startup.

**Tech Stack:** C# / .NET, xUnit, Dalamud plugin (ImGui via `Dalamud.Bindings.ImGui`).

**Design spec:** `docs/superpowers/specs/2026-07-25-plan-e-operation-ui-and-diagnostics-design.md`
(read in full before starting — every task below implements a specific section of it).

## Global Constraints

- `dotnet build` must introduce no new warnings/errors beyond the accepted baseline at worktree setup
  — re-verify fresh, per established precedent every prior plan in this series has followed.
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC.
- `RecoveryClassifier`, `ContinuationPlanner`, `RollbackHistory`, `ApplyPlanner`,
  `OperationBundleRetention`'s existing retention algorithm are out of scope for behavior changes —
  this plan consumes their existing output/reads their existing data unchanged.
- `AcceptAllAndCloseInterruptedOperations`'s existing observable behavior (return value, which
  journals get resolved, when it unblocks) must be unchanged after its internal refactor (Task 2) —
  verified by its own existing test suite passing unmodified. (Task 2 does add one new internal-state
  line, clearing `_blockedMultiRootJournals` alongside `_blockedMultiRootGraph` on success — an
  internal-state correction Task 1 itself made necessary, not an externally observable behavior
  change, and not covered by the pre-existing test suite since it predates Task 1's new field.)
- No method reachable from `OperationController.Update()` may let an exception escape it — unchanged
  by this plan, but every new method must not violate it either.
- `PublishState()` remains the sole place `OperationController.State` is assigned.

---

## Task 1: `OperationController` — `GetPendingRecoveryArtifactStatus`, `GetPendingRecoveryJournal`, `GetBlockedOperations`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Produces: `OperationController.GetPendingRecoveryArtifactStatus() -> (ArtifactCheckStatus Plan,
  ArtifactCheckStatus Snapshot)?`, `OperationController.GetPendingRecoveryJournal() -> OperationJournal?`,
  `OperationController.GetBlockedOperations() -> IReadOnlyList<(Guid OperationId, OperationJournal
  Journal)>` — all three consumed by Task 9/10/11's `MainWindow` wiring, and
  `GetPendingRecoveryJournal` also by Task 3's cycle-resolution test.

Design doc §4/§5/§6. Three pure, read-only query accessors over already-existing state. No behavior
change to anything — additive only.

`GetBlockedOperations()` depends on a new field, `_blockedMultiRootJournals`, that doesn't exist yet
— this task adds it (populated in `RegisterDiscoveredRecovery`'s multi-root branch, alongside the
already-existing `_blockedMultiRootGraph` assignment) since both new accessors are naturally one
small, cohesive change to review together.

- [ ] **Step 1: Write the failing tests**

Add to `OperationControllerTests.cs`:

```csharp
    [Fact]
    public void GetPendingRecoveryArtifactStatus_NoPendingRecovery_ReturnsNull()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Null(controller.GetPendingRecoveryArtifactStatus());
    }

    [Fact]
    public void GetPendingRecoveryJournal_NoPendingRecovery_ReturnsNull()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Null(controller.GetPendingRecoveryJournal());
    }

    [Fact]
    public void GetPendingRecoveryJournal_PendingRecoveryExists_ReturnsItsJournal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);

            var journal = controller.GetPendingRecoveryJournal();

            Assert.NotNull(journal);
            Assert.Equal(journalId, journal!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetPendingRecoveryArtifactStatus_PendingRecoveryWithValidPlanMissingSnapshot_ReturnsBothStatuses()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            // snapshot.json intentionally not written
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update(); // populates PlanCheckStatus/SnapshotCheckStatus

            var status = controller.GetPendingRecoveryArtifactStatus();

            Assert.NotNull(status);
            Assert.Equal(ArtifactCheckStatus.Valid, status!.Value.Plan);
            Assert.Equal(ArtifactCheckStatus.Missing, status.Value.Snapshot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetBlockedOperations_NoBlockedGraph_ReturnsEmpty()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Empty(controller.GetBlockedOperations());
    }

    [Fact]
    public void GetBlockedOperations_BlockedMultiRoot_ReturnsOnlyAuthoritativeOperationsWithTheirJournals()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var journalA = InterruptedJournal(idA);
        var journalB = InterruptedJournal(idB);
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, [idA, idB], [idA, idB]),
            new Dictionary<Guid, OperationJournal> { [idA] = journalA, [idB] = journalB });

        controller.RegisterDiscoveredRecovery(discovery);
        var blocked = controller.GetBlockedOperations();

        Assert.Equal(2, blocked.Count);
        Assert.Contains(blocked, b => b.OperationId == idA && b.Journal == journalA);
        Assert.Contains(blocked, b => b.OperationId == idB && b.Journal == journalB);
    }

    [Fact]
    public void GetBlockedOperations_OnlyListsAuthoritativeIdsNotNonLeafAncestors()
    {
        // A cycle's AuthoritativeOperationIds is the full cycle-member set (OperationRecoveryGraph's
        // own semantics, unchanged by this plan) - this test just proves GetBlockedOperations
        // faithfully mirrors whatever the graph names as authoritative, not a hand-picked subset.
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid(); // not authoritative - simulates a non-leaf ancestor present in Journals but absent from AuthoritativeOperationIds
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, [idA, idB], [idA, idB, idC]),
            new Dictionary<Guid, OperationJournal> { [idA] = InterruptedJournal(idA), [idB] = InterruptedJournal(idB), [idC] = InterruptedJournal(idC) });

        controller.RegisterDiscoveredRecovery(discovery);
        var blocked = controller.GetBlockedOperations();

        Assert.Equal(2, blocked.Count);
        Assert.DoesNotContain(blocked, b => b.OperationId == idC);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OperationControllerTests`
Expected: build failure (`GetPendingRecoveryArtifactStatus`/`GetBlockedOperations` don't exist yet)

- [ ] **Step 3: Add the field, wire it in `RegisterDiscoveredRecovery`, add both accessors**

In `OperationController.cs`, add a field right after the existing `_blockedMultiRootGraph` declaration:

```csharp
    private OperationRecoveryGraphResult? _blockedMultiRootGraph;
    private IReadOnlyDictionary<Guid, OperationJournal>? _blockedMultiRootJournals;
```

Replace `RegisterDiscoveredRecovery`'s multi-root branch:

```csharp
            case OperationRecoveryGraphStatus.MultipleDisconnectedRoots:
            case OperationRecoveryGraphStatus.CycleDetected:
                _blockedMultiRootGraph = discovery.Graph;
                PublishState();
                return;
```

with:

```csharp
            case OperationRecoveryGraphStatus.MultipleDisconnectedRoots:
            case OperationRecoveryGraphStatus.CycleDetected:
                _blockedMultiRootGraph = discovery.Graph;
                _blockedMultiRootJournals = discovery.Journals;
                PublishState();
                return;
```

Add both new accessors right after the existing `public bool IsBlockedByMultipleRoots => ...` line:

```csharp
    public bool IsBlockedByMultipleRoots => _blockedMultiRootGraph is not null;

    public (ArtifactCheckStatus Plan, ArtifactCheckStatus Snapshot)? GetPendingRecoveryArtifactStatus() =>
        _pendingRecovery is { } pending ? (pending.PlanCheckStatus, pending.SnapshotCheckStatus) : null;

    public OperationJournal? GetPendingRecoveryJournal() => _pendingRecovery?.Journal;

    // Only AuthoritativeOperationIds (the ones actually independently resolvable - for disconnected
    // roots these are literal graph leaves, but for a cycle every member is authoritative), not
    // AllOperationIds - a non-authoritative ancestor isn't independently actionable; it gets folded in
    // automatically once its authoritative descendant resolves and discovery re-runs (Task 3).
    public IReadOnlyList<(Guid OperationId, OperationJournal Journal)> GetBlockedOperations() =>
        _blockedMultiRootGraph is not { } graph || _blockedMultiRootJournals is not { } journals
            ? []
            : graph.AuthoritativeOperationIds
                .Where(journals.ContainsKey)
                .Select(id => (id, journals[id]))
                .ToList();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter OperationControllerTests`
Expected: PASS — all pre-existing tests plus the seven new ones (two `GetPendingRecoveryJournal` tests
plus the five `GetPendingRecoveryArtifactStatus`/`GetBlockedOperations` tests above).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: add GetPendingRecoveryArtifactStatus/GetPendingRecoveryJournal/GetBlockedOperations query accessors"
```

---

## Task 2: `OperationController` — extract `TryResolveJournalAsKeepCurrent`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`

**Interfaces:**
- Produces: `private OperationController.TryResolveJournalAsKeepCurrent(Guid operationId) ->
  JournalResolutionOutcome` — consumed by the refactored `AcceptAllAndCloseInterruptedOperations`
  (this task) and Task 3's new `ResolveOneMultiRootOperation`.

Design doc §5: pure internal refactor. Extracts the per-journal Keep-Current resolution logic
`AcceptAllAndCloseInterruptedOperations` already has into a shared private helper, so Task 3's new
per-root resolution method isn't a third copy of the same "resolve a journal file, relocate it,
handle the already-resolved retry case" logic. **No test additions in this task** — the existing
`AcceptAllAndCloseInterruptedOperations_*` tests in `OperationControllerTests.cs` are the regression
suite for this refactor; they must pass completely unmodified, proving the extraction preserved exact
behavior.

- [ ] **Step 1: Record the baseline test output**

Run: `dotnet test --filter AcceptAllAndCloseInterruptedOperations`
Expected: PASS (3 tests) — record this output; it's what Step 3 must still show afterward.

- [ ] **Step 2: Extract the helper and rewrite `AcceptAllAndCloseInterruptedOperations` to use it**

In `OperationController.cs`, replace the whole `AcceptAllAndCloseInterruptedOperations` method:

```csharp
    // Resolves every journal in the blocked graph, not only the "authoritative" leaves - an
    // unresolved non-leaf ancestor journal would recreate this exact lockout at the next startup,
    // once its (now-terminal) child drops out of the non-terminal set and the ancestor becomes its
    // own new leaf/root. Only unblocks once every journal durably persisted its resolution.
    public IReadOnlyList<Guid> AcceptAllAndCloseInterruptedOperations()
    {
        if (_blockedMultiRootGraph is not { } graph)
            throw new InvalidOperationException("No blocked multi-root recovery to resolve.");

        var unresolved = new List<Guid>();
        foreach (var operationId in graph.AllOperationIds)
        {
            var activeBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, operationId);
            if (!Directory.Exists(activeBundleDirectory))
            {
                // Not present under active/ - either already resolved and relocated by a prior
                // partial attempt (retry case), or never existed. Verify which, rather than assuming
                // absence means success: a retry must not silently skip a genuinely-missing journal.
                var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, operationId);
                var alreadyResolved = OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var existing)
                    && existing is not null
                    && existing.OperationId == operationId
                    && existing.IsTerminal
                    && existing.Resolution == OperationResolution.AcceptedCurrentState;
                if (!alreadyResolved)
                    unresolved.Add(operationId);
                continue;
            }

            if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(activeBundleDirectory), out var journal) || journal is null)
            {
                unresolved.Add(operationId);
                continue;
            }

            var resolvedJournal = journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
            try
            {
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), resolvedJournal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Plugin.Log?.Warning(ex, $"Accept all: failed to persist resolution for {operationId}.");
                unresolved.Add(operationId);
                continue;
            }

            TryRelocateToCompleted(activeBundleDirectory, resolvedJournal); // best-effort, same rule as ResolveKeepCurrent
        }

        if (unresolved.Count > 0)
        {
            PublishState();
            return unresolved;
        }

        _blockedMultiRootGraph = null;
        PublishState();
        return [];
    }
```

with:

```csharp
    private enum JournalResolutionOutcome { Resolved, AlreadyResolved, Failed }

    // Extracted from AcceptAllAndCloseInterruptedOperations (this method's own logic is unchanged,
    // just no longer duplicated for Task 3's per-root resolution). Resolves one journal via
    // Keep-Current semantics: persists the resolution, best-effort relocates to completed/. Treats
    // "already resolved and relocated by a prior partial attempt" as its own outcome, not Failed -
    // a retry must not resurrect an already-successfully-resolved journal (see the existing
    // AcceptAllAndCloseInterruptedOperations_RetryAfterPartialFailure test, unchanged by this
    // extraction).
    private JournalResolutionOutcome TryResolveJournalAsKeepCurrent(Guid operationId)
    {
        var activeBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, operationId);
        if (!Directory.Exists(activeBundleDirectory))
        {
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, operationId);
            var alreadyResolved = OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var existing)
                && existing is not null
                && existing.OperationId == operationId
                && existing.IsTerminal
                && existing.Resolution == OperationResolution.AcceptedCurrentState;
            return alreadyResolved ? JournalResolutionOutcome.AlreadyResolved : JournalResolutionOutcome.Failed;
        }

        if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(activeBundleDirectory), out var journal) || journal is null)
            return JournalResolutionOutcome.Failed;

        var resolvedJournal = journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
        try
        {
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), resolvedJournal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log?.Warning(ex, $"Failed to persist Keep-Current resolution for {operationId}.");
            return JournalResolutionOutcome.Failed;
        }

        TryRelocateToCompleted(activeBundleDirectory, resolvedJournal); // best-effort, same rule as ResolveKeepCurrent
        return JournalResolutionOutcome.Resolved;
    }

    // Resolves every journal in the blocked graph, not only the "authoritative" leaves - an
    // unresolved non-leaf ancestor journal would recreate this exact lockout at the next startup,
    // once its (now-terminal) child drops out of the non-terminal set and the ancestor becomes its
    // own new leaf/root. Only unblocks once every journal durably persisted its resolution.
    public IReadOnlyList<Guid> AcceptAllAndCloseInterruptedOperations()
    {
        if (_blockedMultiRootGraph is not { } graph)
            throw new InvalidOperationException("No blocked multi-root recovery to resolve.");

        var unresolved = new List<Guid>();
        foreach (var operationId in graph.AllOperationIds)
        {
            if (TryResolveJournalAsKeepCurrent(operationId) == JournalResolutionOutcome.Failed)
                unresolved.Add(operationId);
        }

        if (unresolved.Count > 0)
        {
            PublishState();
            return unresolved;
        }

        _blockedMultiRootGraph = null;
        _blockedMultiRootJournals = null;
        PublishState();
        return [];
    }
```

Note `_blockedMultiRootJournals = null;` is a new line added to the success path — the field Task 1
introduced must be cleared alongside `_blockedMultiRootGraph`, or `GetBlockedOperations()` would keep
returning stale entries after a full Accept-All unblocks the controller.

- [ ] **Step 3: Run the tests to verify the refactor preserved exact behavior**

Run: `dotnet test --filter AcceptAllAndCloseInterruptedOperations`
Expected: PASS (3 tests) — identical to Step 1's baseline. If anything differs, the extraction changed
behavior; do not proceed until this is byte-for-byte the same pass/fail shape as Step 1.

Run: `dotnet test --filter OperationControllerTests`
Expected: PASS — full file, including Task 1's new tests.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs
git commit -m "refactor: extract TryResolveJournalAsKeepCurrent from AcceptAllAndCloseInterruptedOperations"
```

---

## Task 3: `OperationController` — `ResolveOneMultiRootOperation`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: Task 2's `TryResolveJournalAsKeepCurrent`, existing
  `OperationBundleDiscovery.RunStartupDiscovery`, existing `RegisterDiscoveredRecovery`.
- Produces: `OperationController.ResolveOneMultiRootOperation(Guid operationId)` — consumed by
  Task 5's `Plugin.cs` wiring.

Design doc §5 — the one genuinely new piece of `OperationController` logic in this whole plan.
Resolves exactly one operation from the blocked multi-root/cycle graph via Keep-Current, then
re-derives the graph from whatever remains on disk and feeds it through the existing discovery
pipeline, so the controller naturally transitions to `Idle`, ordinary single-root recovery, or a
smaller blocked set — whichever the fresh discovery pass produces.

**A genuine correctness property this task's tests must prove, not just exercise incidentally:**
resolving any single member of a `CycleDetected` set always breaks the cycle, never leaves a smaller
but still-cyclic remainder — see the design doc's own hand-traced proof (§5) before writing the cycle
test below; the test constructs the exact 3-node example that proof walks through.

- [ ] **Step 1: Write the failing tests**

Add to `OperationControllerTests.cs`. This test file already has `NewControllerWithBlockedMultiRoot`
(used by the existing `AcceptAllAndCloseInterruptedOperations_*` tests) — reuse it.

```csharp
    [Fact]
    public void ResolveOneMultiRootOperation_NoBlockedGraph_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveOneMultiRootOperation(Guid.NewGuid()));
    }

    [Fact]
    public void ResolveOneMultiRootOperation_IdNotInAuthoritativeSet_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);

            Assert.Throws<InvalidOperationException>(() => controller.ResolveOneMultiRootOperation(Guid.NewGuid()));
            Assert.True(controller.IsBlockedByMultipleRoots); // untouched
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_TwoDisconnectedRoots_ResolvingOneLeavesTheOtherAsOrdinarySingleRecovery()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            foreach (var id in new[] { idA, idB })
            {
                var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id);
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), InterruptedJournal(id));
            }

            controller.ResolveOneMultiRootOperation(idA);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.RequiresRecovery); // idB is now an ordinary single pending recovery
            Assert.True(controller.State.CanResolveRecovery);
            var completedDirA = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, idA);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedDirA), out var resolvedA));
            Assert.Equal(OperationResolution.AcceptedCurrentState, resolvedA!.Resolution);
            Assert.True(Directory.Exists(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idB))); // still there, now the sole pending recovery
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_LastRemainingRoot_TransitionsToIdle()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA]);
            var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idA);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), InterruptedJournal(idA));

            controller.ResolveOneMultiRootOperation(idA);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.False(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanStartApply);
            Assert.Empty(controller.GetBlockedOperations());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_ThreeDisconnectedRoots_ResolvingOneLeavesTwoStillBlocked()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var idC = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB, idC]);
            foreach (var id in new[] { idA, idB, idC })
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id)), InterruptedJournal(id));

            controller.ResolveOneMultiRootOperation(idA);

            Assert.True(controller.IsBlockedByMultipleRoots);
            var remaining = controller.GetBlockedOperations().Select(b => b.OperationId).ToList();
            Assert.Equal(2, remaining.Count);
            Assert.Contains(idB, remaining);
            Assert.Contains(idC, remaining);
            Assert.DoesNotContain(idA, remaining);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_ResolvingOneMemberOfAThreeNodeCycle_BreaksTheCycleCorrectly()
    {
        // Design doc section 5's hand-traced proof, made concrete: A -> B -> C -> A via
        // RecoveryOfOperationId. Resolving B removes it from the next non-terminal-journal load, so
        // the only surviving edge is C -> A (both still present); A is referenced as a parent by C
        // but has no parent itself in-set, so leaves = {C} once B drops out - SingleAuthoritative
        // with C as the sole remaining authoritative operation, not a smaller but still-cyclic set.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var idC = Guid.NewGuid();
            var journalA = InterruptedJournal(idA) with { RecoveryOfOperationId = idB };
            var journalB = InterruptedJournal(idB) with { RecoveryOfOperationId = idC };
            var journalC = InterruptedJournal(idC) with { RecoveryOfOperationId = idA };
            var cycleIds = new[] { idA, idB, idC };
            var controller = NewController(new FakePenumbraOperations(), new FakeClock(), operationsRoot: dir.FullName);
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.CycleDetected, cycleIds, cycleIds),
                new Dictionary<Guid, OperationJournal> { [idA] = journalA, [idB] = journalB, [idC] = journalC });
            controller.RegisterDiscoveredRecovery(discovery);
            foreach (var (id, journal) in new[] { (idA, journalA), (idB, journalB), (idC, journalC) })
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id)), journal);

            controller.ResolveOneMultiRootOperation(idB);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.RequiresRecovery); // now an ordinary single pending recovery, not still blocked
            // Directory presence alone wouldn't distinguish "C is correctly authoritative" from "the
            // controller latched onto the wrong id while C incidentally still sits under active/" -
            // assert via the controller's own authoritative accessor, not disk state.
            var pendingJournal = controller.GetPendingRecoveryJournal();
            Assert.NotNull(pendingJournal);
            Assert.Equal(idC, pendingJournal!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_RetriedAfterAlreadyResolved_SucceedsWithoutResolvingTwice()
    {
        // Stands in as the regression test for the failure-atomicity property (design doc section 5):
        // RunStartupDiscovery itself can't practically be forced to throw in a plain filesystem test
        // (every read failure it can hit - missing directory, corrupt/locked journal - is already
        // caught and treated as "skip this entry" one layer down), so this test instead exercises the
        // retry path the atomicity guarantee exists to make safe. Simulates the recovery path from a
        // prior partial failure: idA's journal is already resolved-and-relocated to completed/ (as if
        // a previous call got as far as resolving it, and this is a retry), but the controller's
        // in-memory blocked state still lists it as blocked - exactly the state the atomicity fix is
        // designed to leave behind if rediscovery didn't complete last time. The retry must not throw
        // or attempt to re-resolve an already-terminal journal - TryResolveJournalAsKeepCurrent's
        // AlreadyResolved outcome (proven by the existing AcceptAllAndCloseInterruptedOperations retry
        // test, unchanged by Task 2's extraction) makes this a normal, successful path.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            // idA: already resolved and relocated, as if by a prior partial attempt - nothing under
            // active/ for it.
            var completedDirA = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, idA);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(completedDirA), InterruptedJournal(idA) with { Resolution = OperationResolution.AcceptedCurrentState, Stage = OperationStage.Completed });
            // idB: still genuinely active.
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idB)), InterruptedJournal(idB));

            controller.ResolveOneMultiRootOperation(idA);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.RequiresRecovery); // idB is now the ordinary single pending recovery
            var pendingJournal = controller.GetPendingRecoveryJournal();
            Assert.NotNull(pendingJournal);
            Assert.Equal(idB, pendingJournal!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_JournalResolutionFails_ThrowsAndLeavesBlockStateIntact()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            // idA's bundle directory/journal is never written to disk - TryResolveJournalAsKeepCurrent
            // returns Failed for it (matches AcceptAllAndCloseInterruptedOperations_OneJournalUnloadable's
            // own established trigger).

            Assert.Throws<InvalidOperationException>(() => controller.ResolveOneMultiRootOperation(idA));

            Assert.True(controller.IsBlockedByMultipleRoots);
            Assert.Equal(2, controller.GetBlockedOperations().Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OperationControllerTests`
Expected: build failure (`ResolveOneMultiRootOperation` doesn't exist yet)

- [ ] **Step 3: Implement `ResolveOneMultiRootOperation`, failure-atomically**

**Why the obvious ordering is wrong:** clearing `_blockedMultiRootGraph`/`_blockedMultiRootJournals`
*before* calling `RunStartupDiscovery`, then registering whatever it returns, looks natural but isn't
safe. If `RunStartupDiscovery` throws, the selected journal has already been durably resolved and
relocated to `completed/` (that part already happened, in the line above), but the controller has
discarded its blocked-graph fields with nothing having replaced them yet — `State` still reports the
stale blocked snapshot while the fields backing it are gone, and there's no way to tell a caller what
actually happened. The fix: don't clear the old fields until a fresh discovery result is in hand to
replace them with. If discovery throws, the old (now slightly stale but coherent) blocked-graph fields
stay in place; a retry on the same operation id is safe because `TryResolveJournalAsKeepCurrent`
reports `AlreadyResolved` for it, not `Failed`, on a second attempt.

In `OperationController.cs`, add right after `AcceptAllAndCloseInterruptedOperations`:

```csharp
    // Clears every recovery-related field and re-registers a fresh discovery result in one place, so
    // a multi-root-to-single-root or multi-root-to-none transition can't leave a stale field from the
    // previous state behind. Called only once RunStartupDiscovery has already succeeded (see
    // ResolveOneMultiRootOperation below) - never call this before a fresh OperationDiscoveryResult is
    // in hand.
    private void ReplaceDiscoveredRecovery(OperationDiscoveryResult discovery)
    {
        _pendingRecovery = null;
        _blockedMultiRootGraph = null;
        _blockedMultiRootJournals = null;
        RegisterDiscoveredRecovery(discovery);

        // RegisterDiscoveredRecovery's NoRecoveryNeeded branch returns without calling PublishState()
        // (correct at startup, where State already defaults to Idle) - here we may be transitioning
        // OUT of a non-Idle blocked state, so publish unconditionally regardless of which branch fired.
        PublishState();
    }

    public void ResolveOneMultiRootOperation(Guid operationId)
    {
        if (_blockedMultiRootGraph is not { } graph)
            throw new InvalidOperationException("No blocked multi-root recovery to resolve.");
        if (!graph.AuthoritativeOperationIds.Contains(operationId))
            throw new InvalidOperationException("The requested operation is not an independently resolvable root of the blocked recovery graph.");

        if (TryResolveJournalAsKeepCurrent(operationId) == JournalResolutionOutcome.Failed)
            throw new InvalidOperationException($"Failed to resolve {operationId} - see the plugin log.");

        // Re-run discovery over whatever remains on disk now that operationId has dropped out (either
        // just resolved above, or already resolved by a prior partial attempt) - the same startup
        // discovery path Plugin.cs's constructor uses, reused here rather than hand-rolling a second
        // graph derivation. Deliberately NOT cleared before this call: if RunStartupDiscovery throws,
        // the old _blockedMultiRootGraph/_blockedMultiRootJournals stay exactly as they were rather
        // than being discarded with nothing to replace them - the journal we just resolved is already
        // durably terminal regardless of whether this line succeeds, so a retry is always safe.
        var discovery = OperationBundleDiscovery.RunStartupDiscovery(_operationsRoot);
        ReplaceDiscoveredRecovery(discovery);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter OperationControllerTests`
Expected: PASS — all pre-existing tests plus every new one from Tasks 1-3.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: add ResolveOneMultiRootOperation for incremental multi-root recovery"
```

---

## Task 4: `OperationBundleDiscovery` — `LoadRecentCompletedJournals`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundleDiscovery.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleDiscoveryTests.cs`

**Interfaces:**
- Produces: `OperationBundleDiscovery.LoadRecentCompletedJournals(string operationsRoot, int take) ->
  IReadOnlyList<OperationJournal>` — consumed by Task 11 (diagnostics dump) and Task 12 (operation
  history display); one function, two call sites, not duplicated.

Design doc §6/§7. Reads `completed/*/journal.json`, newest-first by `UpdatedAt`, capped at `take`.
Matches `LoadNonTerminalActiveJournals`'s own established "corrupt journal excluded, not fatal"
pattern and `OperationBundleRetention.RunRetentionPass`'s own "no completed/ directory yet" early
return. Contract: `take <= 0` returns `[]` (no negative or zero-length reads); only journals with
`IsTerminal == true` are included — a defensive check against a non-terminal journal somehow present
under `completed/`, mirroring the same "don't trust the directory over the journal's own state"
posture `LoadNonTerminalActiveJournals` already takes toward `active/`.

- [ ] **Step 1: Write the failing tests**

Add to `OperationBundleDiscoveryTests.cs`:

```csharp
    private static OperationJournal CompletedJournal(Guid id, DateTimeOffset updatedAt) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: id, Type: OperationType.Apply,
        Stage: OperationStage.Completed, Resolution: OperationResolution.None, SuccessorOperationId: null,
        CancellationRequested: false, StartedAt: updatedAt.AddSeconds(-5), TotalSteps: 1, ProcessedStepCount: 1,
        LastCompletedIdentifier: "mod-a", SnapshotId: Guid.NewGuid(), PlanId: Guid.NewGuid(), TargetHash: "irrelevant",
        RecoveryOfOperationId: null, UpdatedAt: updatedAt);

    [Fact]
    public void LoadRecentCompletedJournals_NoCompletedDirectory_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Empty(OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 10));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadRecentCompletedJournals_ReturnsNewestFirstRespectingTake()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                var journal = CompletedJournal(id, now.AddMinutes(-i)); // i=0 is newest
                var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id);
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), journal);
            }

            var result = OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 3);

            Assert.Equal(3, result.Count);
            Assert.Equal(ids[0], result[0].OperationId);
            Assert.Equal(ids[1], result[1].OperationId);
            Assert.Equal(ids[2], result[2].OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadRecentCompletedJournals_CorruptJournal_ExcludedNotFatal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var validId = Guid.NewGuid();
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, validId)),
                CompletedJournal(validId, DateTimeOffset.UtcNow));

            var corruptBundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, Guid.NewGuid());
            Directory.CreateDirectory(corruptBundleDir);
            File.WriteAllText(OperationBundlePaths.JournalPath(corruptBundleDir), "{ not valid json");

            var result = OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 10);

            Assert.Single(result);
            Assert.Equal(validId, result[0].OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LoadRecentCompletedJournals_TakeZeroOrNegative_ReturnsEmpty(int take)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id)),
                CompletedJournal(id, DateTimeOffset.UtcNow));

            Assert.Empty(OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadRecentCompletedJournals_NonTerminalJournalPresentUnderCompleted_Excluded()
    {
        // Shouldn't happen given how relocation works, but the read function shouldn't trust the
        // directory it's found in over the journal's own IsTerminal state - same defensive posture
        // LoadNonTerminalActiveJournals already takes toward active/.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var terminalId = Guid.NewGuid();
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, terminalId)),
                CompletedJournal(terminalId, DateTimeOffset.UtcNow));

            var nonTerminalId = Guid.NewGuid();
            var nonTerminalJournal = CompletedJournal(nonTerminalId, DateTimeOffset.UtcNow) with { Stage = OperationStage.Mutating, Resolution = OperationResolution.None };
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, nonTerminalId)),
                nonTerminalJournal);

            var result = OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 10);

            Assert.Single(result);
            Assert.Equal(terminalId, result[0].OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OperationBundleDiscoveryTests`
Expected: build failure (`LoadRecentCompletedJournals` doesn't exist yet)

- [ ] **Step 3: Implement**

In `OperationBundleDiscovery.cs`, add after `LoadNonTerminalActiveJournals`:

```csharp
    public static IReadOnlyList<OperationJournal> LoadRecentCompletedJournals(string operationsRoot, int take)
    {
        if (take <= 0)
            return [];

        var completedDir = OperationBundlePaths.CompletedDirectory(operationsRoot);
        if (!Directory.Exists(completedDir))
            return [];

        var journals = new List<OperationJournal>();
        foreach (var bundleDir in Directory.GetDirectories(completedDir))
        {
            if (OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) && journal is not null && journal.IsTerminal)
                journals.Add(journal);
        }

        return journals.OrderByDescending(j => j.UpdatedAt).Take(take).ToList();
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter OperationBundleDiscoveryTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundleDiscovery.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleDiscoveryTests.cs
git commit -m "feat: add OperationBundleDiscovery.LoadRecentCompletedJournals"
```

---

## Task 5: `Plugin.cs` wiring — retention pass, Cancel, multi-root resolution

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `OperationBundleRetention.RunRetentionPass` (existing), Task 3's
  `OperationController.ResolveOneMultiRootOperation`, existing `OperationController.
  RequestCancellation`.
- Produces: `Plugin.RequestCancellation()`, `Plugin.ResolveOneMultiRootOperation(Guid)` — consumed by
  Task 8/10's `MainWindow` wiring.

Design doc §3/§5/§7. Not unit-testable — Dalamud-coupled, same documented limitation as every prior
plan's `Plugin.cs` changes.

- [ ] **Step 1: Wire the retention pass into the constructor, isolated so it can't block startup**

Retention is maintenance, not a startup precondition — a permissions issue, locked directory, or
unexpected filesystem failure inside `RunRetentionPass` must not prevent the plugin from finishing
construction, especially since recovery discovery (the thing that actually matters for correctness) has
already completed on the line above. `RunRetentionPass` isn't verified to guarantee no exceptions
escape it, so this call site needs its own boundary.

In `Plugin.cs`, immediately after the existing discovery wiring:

```csharp
        var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
        OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
```

add:

```csharp
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
```

- [ ] **Step 2: Add the Cancel and multi-root wrapper methods**

Immediately after the existing `AcceptAllAndCloseInterruptedOperations()` wrapper:

```csharp
    internal void RequestCancellation() => OperationController.RequestCancellation();

    internal void ResolveOneMultiRootOperation(Guid operationId)
    {
        OperationController.ResolveOneMultiRootOperation(operationId);
        // Resolving one root can just as easily leave an ordinary single pending recovery (two roots
        // -> one) or a smaller blocked set (three roots -> two) as it can reach Idle (the last root
        // resolved) - in the first two outcomes CanScan is still false, so an unconditional RunScan()
        // would throw or record a misleading error while a recovery is still outstanding. Only scan
        // once recovery has actually cleared.
        if (!OperationController.State.RequiresRecovery)
            RunScan();
    }
```

`RequestCancellation` needs no `_operationInProgress` guard (it doesn't start anything, and
`OperationController.RequestCancellation()` is itself a no-op guarded internally by `Stage ==
Mutating`) and no try/catch (it cannot throw). `ResolveOneMultiRootOperation` resolves synchronously
(same as `ResolveKeepCurrent`/`AcceptAllAndCloseInterruptedOperations` — no new async operation
starts), so it follows their "no `_operationInProgress` guard" pattern, not `ResolveContinue`/
`ResolveRestorePreviousState`'s `_operationInProgress`-guarded async pattern — but unlike
`ResolveKeepCurrent`/`AcceptAll` (which always fully clear recovery), it cannot assume `RunScan()` is
always safe to call afterward, hence the `RequiresRecovery` check above.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: wire retention pass, RequestCancellation, ResolveOneMultiRootOperation"
```

---

## Task 6: `MainWindow` — capability lockout

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: existing `OperationStateSnapshot.CanScan`/`CanCreateBackup`/`CanStartRestore`/
  `CanRunFolderCleanup`/`CanRunFolderCleanupRollback`/`CanResolveRecovery`.

Design doc §1. Six buttons gain `ImGui.BeginDisabled`/`EndDisabled` plus a disabled-state tooltip.
Not unit-testable — pure ImGui UI code.

- [ ] **Step 1: Scan button**

In `DrawScanTab` (`MainWindow.cs`), replace:

```csharp
        using (PluginTheme.PrimaryButton())
        {
            if (ImGui.Button("Refresh mod list"))
                RunScan();
        }
```

with:

```csharp
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
```

- [ ] **Step 2: Create Backup and Restore buttons**

In `DrawHistoryTab`, replace:

```csharp
        ImGui.InputText("Label (optional)", ref _createBackupLabelInput, 200);
        ImGui.SameLine();
        if (ImGui.Button("Create Backup"))
        {
            var label = _createBackupLabelInput.Trim();
            CreateBackup(label.Length > 0 ? label : null);
            _createBackupLabelInput = string.Empty;
        }
```

with:

```csharp
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
```

(`operationState` is already in scope at the top of `DrawHistoryTab` — the existing `var
operationState = _plugin.OperationController.State;` line.)

Then, in the same method's per-snapshot loop, replace:

```csharp
            ImGui.SameLine();
            if (ImGui.Button($"Restore##restore-{snapshot.Id}"))
            {
                _pendingRestoreSnapshotId = snapshot.Id;
                // Compute the preview once, here, rather than every frame the popup is drawn -
                // PreviewRestore does a disk read (RollbackHistory.Load) and a Penumbra IPC call,
                // which is too expensive to repeat per-frame for large mod libraries.
                _pendingRestorePreview = _plugin.PreviewRestore(snapshot.Id);
                ImGui.OpenPopup("Restore snapshot?");
            }
```

with:

```csharp
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
```

This is a pure "capture the click, then act on it" transformation — the button-click detection moves
out of the `if` condition and into a captured `bool` (`restoreButtonClicked`) so it can sit behind
`BeginDisabled`/`EndDisabled` before the block that used to be the `if`'s body runs unchanged.

- [ ] **Step 3: Folder Cleanup and Rollback buttons**

In `DrawOrphanedFoldersSection`, add `var operationState = _plugin.OperationController.State;` right
after the method's opening `var detection = _orphanedFolders;` line (this method has no existing
local `operationState`, unlike `DrawHistoryTab`/`DrawReviewTab`).

Replace:

```csharp
            ImGui.Spacing();
            ImGui.BeginDisabled(_selectedOrphans.Count == 0);
            var cleanClicked = ImGui.Button("Clean Up Selected Folders");
            ImGui.EndDisabled();
```

with:

```csharp
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
```

Replace:

```csharp
        if (_plugin.FolderBackupExists)
        {
            ImGui.SameLine();
            if (ImGui.Button("Rollback Folder Cleanup"))
                RollbackFolderCleanup();
        }
```

with:

```csharp
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
```

- [ ] **Step 4: Keep Current State button**

In `DrawRecoveryPanelIfNeeded`, replace:

```csharp
        if (ImGui.Button("Keep Current State"))
            ImGui.OpenPopup("Keep current state?");
```

with:

```csharp
        ImGui.BeginDisabled(!operationState.CanResolveRecovery);
        if (ImGui.Button("Keep Current State"))
            ImGui.OpenPopup("Keep current state?");
        ImGui.EndDisabled();
```

(`operationState` is already in scope — the existing `var operationState =
_plugin.OperationController.State;` line at the top of this method. No tooltip added here: unlike the
other five buttons, `CanResolveRecovery` is only ever `false` while this whole panel isn't shown at
all — `RequiresRecovery` implies `CanResolveRecovery` in every reachable `PublishState()` branch — so
this is defensive-only and practically never renders disabled; adding a tooltip would document a
state that can't occur without adding confusion.)

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: disable Scan/Backup/Restore/FolderCleanup/KeepCurrent buttons per capability flags"
```

---

## Task 7: `MainWindow` — progress display

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: existing `OperationStateSnapshot.ProcessedTargets`/`SuccessfulTargets`/`TotalTargets`/
  `LastProcessedDisplayName`.

Design doc §2. Replaces the plain-text progress line in the Apply tab and Restore/History tab with a
real `ImGui.ProgressBar`, target-based not step-based. Not unit-testable — pure ImGui UI code.

- [ ] **Step 1: Apply tab**

In `DrawReviewTab`, replace:

```csharp
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
```

with:

```csharp
        // Gated on Kind == Apply so an in-progress or just-completed Restore (sharing the same
        // OperationController) never renders here - CanStartApply/CanStartRestore are the same value
        // today, so Kind is the only field that actually distinguishes the two operations.
        if (operationState.Stage is not null && operationState.Kind == Organizer.Operations.OperationType.Apply)
        {
            if (!operationState.CanStartApply && !operationState.RequiresRecovery)
                DrawOperationProgress(operationState, "Applying", null, "##cancel-apply"); // Task 8 wires the real cancel callback
            else if (operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Apply requires recovery - see the plugin log.");
            else
                ImGui.TextUnformatted($"Last Apply: {operationState.Stage} ({operationState.SuccessfulTargets}/{operationState.TotalTargets} succeeded).");
        }
```

- [ ] **Step 2: Restore/History tab**

Replace:

```csharp
        if (_restoreOperationActive)
        {
            if (operationState.Kind == Organizer.Operations.OperationType.Restore && !operationState.CanStartRestore && !operationState.RequiresRecovery)
                ImGui.TextUnformatted($"Restoring... {operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage}).");
            else if (operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");
        }
```

with:

```csharp
        if (_restoreOperationActive)
        {
            if (operationState.Kind == Organizer.Operations.OperationType.Restore && !operationState.CanStartRestore && !operationState.RequiresRecovery)
                DrawOperationProgress(operationState, "Restoring", null, "##cancel-restore"); // Task 8 wires the real cancel callback
            else if (operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");
        }
```

- [ ] **Step 3: Add the shared `DrawOperationProgress` helper**

Add a new private method near `DrawWrappingButtonRow` (the other small shared ImGui drawing helper in
this file). It takes an `onCancel` delegate up front (`null` from this task's two call sites — Task 8
passes the real callback) so the layout math for reserving room next to a full-width progress bar lives
in one place rather than being duplicated at both call sites:

```csharp
    // Target-based, not step-based: a cycle-breaking plan has more execution steps than recovery
    // targets (a temporary hop plus a final move both count as steps for one target), so a
    // step-based fraction misrepresents "how many mods are done" to a user whose mental model is
    // mods, not steps (design doc section 2). ProcessedTargets, not SuccessfulTargets, drives the
    // fraction - SuccessfulTargets is a subset of ProcessedTargets (attempted-and-succeeded, not
    // attempted), so a run with even one failure would otherwise leave the bar permanently short of
    // full even after the operation finishes processing everything. Completion (how much work is
    // done) and outcome (whether it succeeded) are separate concerns, shown on separate lines.
    //
    // onCancel is null from Task 7's two call sites (no cancel affordance yet) - Task 8 passes the
    // real callback. Cancel is drawn here, not at each call site, so the "reserve width for the
    // button before the full-width progress bar claims it" math isn't duplicated for Apply and
    // Restore separately. cancelButtonId carries a distinct ##-suffix per call site (ImGui requires
    // unique widget IDs across the whole window, not just within one tab, matching this file's own
    // established per-row uniqueness convention documented in DrawHistoryTab).
    private static void DrawOperationProgress(Organizer.Operations.OperationStateSnapshot operationState, string verb, Action? onCancel, string cancelButtonId)
    {
        var fraction = operationState.TotalTargets > 0
            ? (float)operationState.ProcessedTargets / operationState.TotalTargets
            : 1f;

        var showCancel = onCancel is not null && operationState.CanRequestCancellation;
        var barWidth = -1f;
        var buttonWidth = 0f;
        if (showCancel)
        {
            buttonWidth = ImGui.CalcTextSize("Cancel").X + ImGui.GetStyle().FramePadding.X * 2;
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            barWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonWidth - spacing);
        }

        ImGui.ProgressBar(fraction, new Vector2(barWidth, 0), $"{operationState.ProcessedTargets}/{operationState.TotalTargets} processed");
        if (showCancel)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Cancel{cancelButtonId}", new Vector2(buttonWidth, 0)))
                onCancel!();
        }

        var failedTargets = operationState.ProcessedTargets - operationState.SuccessfulTargets;
        ImGui.TextDisabled(failedTargets > 0
            ? $"{operationState.SuccessfulTargets} succeeded, {failedTargets} failed"
            : $"{operationState.SuccessfulTargets} succeeded");
        if (operationState.LastProcessedDisplayName is { } name)
            ImGui.TextDisabled($"{verb}: {name}");
        ImGui.TextDisabled($"{operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage})");
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: replace plain-text Apply/Restore progress with a target-based progress bar"
```

---

## Task 8: `MainWindow` — Stop control

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: Task 5's `Plugin.RequestCancellation()`, existing `OperationStateSnapshot.
  CanRequestCancellation`.

Design doc §3. A Cancel button next to the progress bar in both the Apply tab and Restore/History
tab, gated on `CanRequestCancellation`, deliberately **no confirmation popup** (reasoning: design doc
§3 — cancellation is the one genuinely low-stakes, reversible-in-intent action in this whole UI). Not
unit-testable — pure ImGui UI code.

Task 7 already built `DrawOperationProgress` with an `onCancel` slot and the width-reservation layout
math, called with `null` from both tabs (no cancel affordance yet). This task only needs to flip that
`null` to the real callback at each of the two call sites — the button itself, its layout, and its ID
uniqueness are already handled by the shared helper.

- [ ] **Step 1: Apply tab**

In `DrawReviewTab`, change the `DrawOperationProgress` call Task 7 added:

```csharp
                DrawOperationProgress(operationState, "Applying", null, "##cancel-apply");
```

to:

```csharp
                DrawOperationProgress(operationState, "Applying", _plugin.RequestCancellation, "##cancel-apply");
```

- [ ] **Step 2: Restore/History tab**

Change the `DrawOperationProgress` call Task 7 added:

```csharp
                DrawOperationProgress(operationState, "Restoring", null, "##cancel-restore");
```

to:

```csharp
                DrawOperationProgress(operationState, "Restoring", _plugin.RequestCancellation, "##cancel-restore");
```

(Passing the delegate unconditionally, not only when `CanRequestCancellation` is true, is safe and
simpler at the call site — `DrawOperationProgress` itself re-checks `CanRequestCancellation` before
deciding whether to reserve space for or draw the button.)

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add a Cancel button for in-progress Apply/Restore operations"
```

---

## Task 9: `MainWindow` — recovery dialog per-mod detail

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: existing `OperationController.GetRecoveryAssessment()`, Task 1's
  `GetPendingRecoveryArtifactStatus()`.

Design doc §4. A collapsible "Details" section in the single-root recovery panel showing artifact
status (if not both valid) and a color-coded per-mod classification table. Not unit-testable — pure
ImGui UI code.

- [ ] **Step 1: Add the section**

In `DrawRecoveryPanelIfNeeded`, immediately before the final `ImGui.Spacing(); ImGui.Separator();`
that closes the single-root branch:

```csharp
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Details"))
        {
            var artifactStatus = _plugin.OperationController.GetPendingRecoveryArtifactStatus();
            if (artifactStatus is { } status)
            {
                DrawArtifactLine(status.Plan, "Interrupted plan", "Continue");
                DrawArtifactLine(status.Snapshot, "Snapshot", "Restore Previous State");
            }

            var assessment = _plugin.OperationController.GetRecoveryAssessment();
            if (assessment is null)
            {
                // GetRecoveryAssessment() returning null has two distinct causes needing distinct
                // messages: classification genuinely hasn't settled yet (RecoveryClassificationPending
                // true - correct to say "still checking"), or it permanently failed to settle (an
                // invalid plan/live-read/provider per D2's own non-retryable settling design -
                // RecoveryClassificationPending is false, and "still checking" would be permanently,
                // silently wrong).
                if (operationState.RecoveryClassificationPending)
                    ImGui.TextDisabled("Still checking live mod state...");
                else
                    ImGui.TextColored(PluginTheme.CollisionBad, "Per-mod classification is unavailable - see the artifact status above.");
            }
            else if (assessment.Classifications.Count == 0)
            {
                ImGui.TextDisabled("No mods to classify.");
            }
            else
            {
                using var table = ImRaii.Table("RecoveryClassificationTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
                if (table)
                {
                    ImGui.TableSetupColumn("Mod");
                    ImGui.TableSetupColumn("State");
                    ImGui.TableHeadersRow();
                    foreach (var classification in assessment.Classifications.OrderBy(c => c.Identifier, StringComparer.Ordinal))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(classification.Identifier);
                        ImGui.TableNextColumn();
                        var color = classification.State switch
                        {
                            Organizer.Operations.ItemRecoveryState.AtNeither or Organizer.Operations.ItemRecoveryState.MissingLive => PluginTheme.CollisionBad,
                            Organizer.Operations.ItemRecoveryState.AtIntended or Organizer.Operations.ItemRecoveryState.AtBoth => ImGuiColors.HealerGreen,
                            _ => ImGuiColors.DalamudYellow,
                        };
                        ImGui.TextColored(color, classification.State.ToString());
                    }
                }
            }
        }
```

Add a small private static helper alongside `DrawOperationProgress` (Task 7) rendering the per-status
artifact line. `ArtifactCheckStatus` has four members — `Unchecked`, `Valid`, `Missing`, `Invalid` —
and `Unchecked` must not be treated as an error: it means the async check simply hasn't run yet, a
normal transient state early in a recovery's lifetime, not a problem.

```csharp
    private static void DrawArtifactLine(Organizer.Operations.ArtifactCheckStatus status, string artifactName, string unavailableAction)
    {
        switch (status)
        {
            case Organizer.Operations.ArtifactCheckStatus.Unchecked:
                ImGui.TextDisabled($"Checking {artifactName}...");
                break;
            case Organizer.Operations.ArtifactCheckStatus.Missing:
                ImGui.TextColored(PluginTheme.CollisionBad, $"{artifactName} is missing; {unavailableAction} is unavailable.");
                break;
            case Organizer.Operations.ArtifactCheckStatus.Invalid:
                ImGui.TextColored(PluginTheme.CollisionBad, $"{artifactName} is corrupt; {unavailableAction} is unavailable.");
                break;
            case Organizer.Operations.ArtifactCheckStatus.Valid:
                break; // nothing to report
        }
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: no new errors/warnings — confirm `ImGuiColors.HealerGreen` exists in this Dalamud version
(used elsewhere in this codebase already for e.g. protected-mod indicators; if it doesn't resolve,
substitute any existing green `ImGuiColors.*` constant already used in this file).

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: show per-mod classification detail in the recovery panel"
```

---

## Task 10: `MainWindow` — multi-root recovery list

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: Task 1's `GetBlockedOperations()`, Task 5's `Plugin.ResolveOneMultiRootOperation(Guid)`.

Design doc §5. Replaces the multi-root branch's complete black box with a per-operation list, each
row offering an individual "Keep Current" resolution; the existing bulk "Accept All" button stays as
the fast option. Not unit-testable — pure ImGui UI code.

- [ ] **Step 1: Replace the multi-root branch**

In `DrawRecoveryPanelIfNeeded`, replace:

```csharp
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
```

with:

```csharp
        if (_plugin.OperationController.IsBlockedByMultipleRoots)
        {
            // Precise about what clicking one row actually does: it does NOT turn that operation into
            // an ordinary single recovery - it permanently marks it Keep Current, abandoning it, and
            // ONE OF THE REMAINING operations may then become the ordinary single recovery. Getting
            // this wrong in the copy would understate how destructive the per-row action is.
            ImGui.TextWrapped(
                "Multiple interrupted operations were found. You can resolve one at a time below by " +
                "keeping its current state - the recovery graph is then recalculated for what's left, " +
                "which may become a smaller blocked set, a single recoverable operation, or fully " +
                "resolved. You can also abandon all of them at once and accept whatever Penumbra " +
                "currently has as correct - this does not undo or redo any moves for any of them, it " +
                "only stops the plugin from blocking further actions.");

            ImGui.Spacing();
            var blocked = _plugin.OperationController.GetBlockedOperations();
            foreach (var (operationId, journal) in blocked.OrderByDescending(b => b.Journal.UpdatedAt))
            {
                ImGui.TextUnformatted($"{journal.Type} - {journal.Stage} - interrupted {journal.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                ImGui.SameLine();
                if (ImGui.Button($"Keep Current State##multiroot-{operationId}"))
                    ImGui.OpenPopup($"Keep current state for {operationId}?");

                if (ImGui.BeginPopupModal($"Keep current state for {operationId}?"))
                {
                    ImGui.TextUnformatted("This selected operation cannot later be continued or restored - it will be permanently abandoned.");
                    ImGui.TextUnformatted("Any other interrupted operations found stay blocked until resolved separately.");
                    if (ImGui.Button("Yes, Keep Current"))
                    {
                        _plugin.ResolveOneMultiRootOperation(operationId);
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                        ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }
            }

            ImGui.Spacing();
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
```

Note the per-row popup ID is dynamically named (`$"Keep current state for {operationId}?"`) since
multiple rows can each need their own independently-open-able popup — unlike every other popup in
this file (all statically named, since only one instance of each can ever be relevant at a time), this
is the first place a variable number of rows each need one.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: replace multi-root Accept-All-only panel with a per-operation resolution list"
```

---

## Task 11: `MainWindow` — diagnostics dump v2

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: Task 4's `OperationBundleDiscovery.LoadRecentCompletedJournals`, Task 1's
  `GetPendingRecoveryJournal`/`GetBlockedOperations`, existing `DiagnosticsLog.ReadAll`, existing
  `OperationController.State`.

Design doc §6. Three new sections in `CreateDiagnosticDump()`. Not unit-testable — reads live
`OperationController.State` and writes a file via Dalamud's config directory, same limitation as the
existing dump. Each section is wrapped in its own try/catch: the dump's entire purpose is helping
diagnose a problem, so one unreadable source (a locked `completed/` directory, a corrupt diagnostics
log) must degrade that section's own output, not abort the whole dump.

- [ ] **Step 1: Add the three sections**

In `CreateDiagnosticDump()`, replace:

```csharp
        sb.AppendLine("== Session event log (most recent first) ==");
        foreach (var line in _eventLog)
            sb.AppendLine(line);

        var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "organizer-diagnostics.txt");
```

with:

```csharp
        sb.AppendLine("== Interrupted operation ==");
        try
        {
            var pendingJournal = _plugin.OperationController.GetPendingRecoveryJournal();
            if (pendingJournal is not null)
            {
                sb.AppendLine($"OperationId={pendingJournal.OperationId}, Type={pendingJournal.Type}, Stage={pendingJournal.Stage}, {pendingJournal.ProcessedStepCount}/{pendingJournal.TotalSteps} steps, UpdatedAt={pendingJournal.UpdatedAt.ToLocalTime():u}");
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
            var recentOperations = Organizer.Operations.OperationBundleDiscovery.LoadRecentCompletedJournals(_plugin.OperationsRoot, take: 20);
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
        foreach (var line in _eventLog)
            sb.AppendLine(line);

        var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "organizer-diagnostics.txt");
```

`_plugin.OperationsRoot` needs to become accessible from `MainWindow.cs` — check whether `Plugin.
OperationsRoot` (currently `private string OperationsRoot => ...`, `Plugin.cs:348`) is already
`internal`/`public`; if it's `private`, change it to `internal` as part of this step (a pure
visibility widening, no behavior change — `MainWindow` already has an internal-friends relationship
with `Plugin` throughout this whole codebase, e.g. `_plugin.OrganizerState`, `_plugin.Config`).

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: add interrupted-operation, recent-operations, and slow-call sections to the diagnostic dump"
```

---

## Task 12: `MainWindow` — operation history display

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: Task 4's `OperationBundleDiscovery.LoadRecentCompletedJournals` (same function Task 11
  already introduced a call site for — this task adds the second).

Design doc §7. A new collapsed-by-default "Recent Operations" section in the History tab, read-only,
visually distinct from and below the existing `RollbackSnapshot` list. Not unit-testable — pure ImGui
UI code.

**Must not re-read the filesystem every rendered frame.** `ImGui.CollapsingHeader`'s body runs on
every frame the section is expanded — a naive call to `LoadRecentCompletedJournals` from inside it
would enumerate `completed/`, open and parse every retained journal, sort, and allocate a new list
continuously at 60+ FPS while the header stays open, exactly the per-frame-disk-read pattern this
codebase has already deliberately avoided elsewhere (the Restore-preview computation is captured once,
on click, not recomputed every frame the confirmation popup is open — Task 6, Step 2). Cache the result
in fields, reload only when the section transitions from collapsed to expanded (not on every frame it
stays expanded), and give the user an explicit "Refresh" button for the case where they want the list
current without collapsing and reopening the section first.

- [ ] **Step 1: Add caching fields and the `RefreshRecentOperations` helper**

Add near this file's other per-window cached-state fields (e.g. `_pendingRestorePreview`):

```csharp
    private IReadOnlyList<Organizer.Operations.OperationJournal> _recentOperations = [];
    private bool _recentOperationsLoaded;
    private string? _recentOperationsError;
    private bool _recentOperationsSectionWasOpen;
```

Add a private helper method:

```csharp
    private void RefreshRecentOperations()
    {
        try
        {
            _recentOperations = Organizer.Operations.OperationBundleDiscovery.LoadRecentCompletedJournals(_plugin.OperationsRoot, take: 20);
            _recentOperationsError = null;
        }
        catch (Exception ex)
        {
            _recentOperations = [];
            _recentOperationsError = $"Could not load recent operations: {ex.Message}";
            Plugin.Log.Warning(ex, "Loading recent operations failed.");
        }
        finally
        {
            _recentOperationsLoaded = true;
        }
    }
```

- [ ] **Step 2: Add the section**

In `DrawHistoryTab`, immediately before the closing brace of the method (after the existing
`_restoreOperationActive` progress block):

```csharp
        ImGui.Spacing();
        ImGui.Separator();
        var recentOperationsOpen = ImGui.CollapsingHeader("Recent Operations");
        // Reload only on a collapsed -> expanded transition (or the very first expansion), not every
        // frame the header stays open - the naive "load every frame it's expanded" version is exactly
        // the per-frame-disk-read pattern this section must avoid.
        if (recentOperationsOpen && (!_recentOperationsLoaded || !_recentOperationsSectionWasOpen))
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
```

Note this section's own `RefreshRecentOperations` call is independent of Task 11's diagnostics-dump
call to the same `LoadRecentCompletedJournals` function — the dump runs once per explicit "Create
Diagnostic Dump" click (never per-frame), so its direct, uncached call is fine as-is and needs no
caching of its own.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add a Recent Operations section to the History tab"
```

---

## Task 13: Full-suite verification + manual checklist

**Files:** None modified — verification only.

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test`
Expected: PASS, 0 failures. Note the total test count for the final whole-branch review.

- [ ] **Step 2: Run a full build**

Run: `dotnet build`
Expected: no new warnings/errors beyond whatever baseline was recorded at worktree setup.

- [ ] **Step 3: Write the manual in-game verification checklist**

This plan's `Plugin.cs`/`MainWindow.cs` changes cannot be exercised by automated tests. Record the
following checklist for a human to run in-game before this plan is considered verified (do not
attempt to run it yourself — no game client is available in this environment):

1. With another operation already active (e.g. mid-Apply), confirm the Scan/Create Backup/Restore/
   Folder Cleanup/Rollback buttons are greyed out with a tooltip explaining why on hover — and confirm
   the Folder Cleanup button, when disabled purely because no folders are selected (no operation
   active), shows no misleading "another operation" tooltip.
2. Start an Apply on a real multi-mod library. Confirm the progress bar fills proportionally by mod
   count (not step count — a mod involved in a swap should not make the bar jump by more than its own
   share) and continues advancing even if a target fails partway through (processed count keeps
   climbing; only the separate succeeded/failed line reflects the failure), the "Applying: <mod name>"
   line updates, and a Cancel button appears and disappears correctly around the Mutating stage without
   clipping or overlapping the progress bar.
3. Click Cancel mid-Apply. Confirm the operation stops at the next safe boundary and settles as
   `Cancelled`, with no confirmation popup needed.
4. Force an interrupted single-root recovery (per D1/D2's own manual checklists). Confirm the new
   "Details" section shows the correct per-mod classification colors, distinguishes "still checking"
   from "classification permanently unavailable" correctly, and — if the plan or snapshot is corrupted —
   shows the correct artifact-status warning text (and shows "Checking..." rather than an error while
   the artifact check is still `Unchecked`, early in the recovery's lifetime).
5. Hand-construct (or otherwise produce) a multi-root/cycle blocked state. Confirm each blocked
   operation gets its own row with type/stage/timestamp, the warning copy correctly describes resolving
   one row as permanently abandoning that one operation, resolving one via its own "Keep Current State"
   button correctly shrinks the list (or, if it was the last one, unblocks entirely and shows the
   ordinary single-root panel or nothing at all), and "Accept Current State and Close All" still works
   as the bulk fallback.
6. Create a Diagnostic Dump. Confirm the new "Interrupted operation" section shows the real interruption
   timestamp (not the dump's own creation time), "Recent operations" and "Slow calls" (grouped by
   identifier with count/worst/total, not five raw events) appear with real data (or "(none)" where
   appropriate) rather than throwing or silently omitting.
7. Open the History tab's new "Recent Operations" section. Confirm it lists real completed Apply/
   Restore/Continue/Restore-Previous-State operations with correct type/stage/resolution, stays
   collapsed by default, is visually distinct from the snapshot list above it, does not visibly
   re-query or flicker while left open across many frames, and the "Refresh" button updates the list
   after a new operation completes without needing to collapse and re-expand the section.
8. Restart the plugin (or the game) with more than 50 completed operation bundles on disk (or an
   artificially-lowered retention count for testing). Confirm `completed/` is pruned to the retention
   window on the next startup and the plugin doesn't error — including simulating a retention failure
   (e.g. a locked file under `completed/`) and confirming the plugin still starts normally with a
   warning logged, not a crash.
9. Click a Restore row to open its confirmation popup, then (in another window/instance, or by editing
   files on disk to simulate a race) cause `CanStartRestore` to become false before confirming. Confirm
   the confirm handler's own admission recheck rejects the stale confirmation rather than proceeding.

- [ ] **Step 4: Report the test count and baseline comparison**

State the final `dotnet test` pass count and confirm it matches (prior count) + (new tests added
across Tasks 1, 3, and 4) with zero unrelated regressions, ready for the final whole-branch review per
`superpowers:subagent-driven-development`.
