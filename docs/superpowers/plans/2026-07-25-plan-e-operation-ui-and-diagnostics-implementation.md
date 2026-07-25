# Plan E: Operation UI and Diagnostics Presentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire seven areas of already-computed-but-undisplayed `OperationController` data and
already-built-but-unwired backend capability into `MainWindow.cs`/`Plugin.cs`: capability lockout,
progress display, a Stop control, per-mod recovery detail, multi-root incremental resolution,
diagnostics dump v2, and operation history display — plus wiring `OperationBundleRetention.
RunRetentionPass`, found to have zero production call sites despite being fully implemented since
Plan A2.

**Architecture:** No execution-engine changes. `OperationController` gains three query accessors
(`GetPendingRecoveryArtifactStatus`, `GetBlockedOperations`) and one genuinely new resolution method
(`ResolveOneMultiRootOperation`, built by extracting a shared per-journal Keep-Current helper out of
the existing `AcceptAllAndCloseInterruptedOperations` and re-running the existing
`OperationBundleDiscovery.RunStartupDiscovery` → `RegisterDiscoveredRecovery` pipeline over whatever
remains after resolving one root). `OperationBundleDiscovery` gains one new read function
(`LoadRecentCompletedJournals`). Everything else is `MainWindow.cs`/`Plugin.cs` wiring.

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
  journals get resolved, when it unblocks) must be bit-for-bit unchanged after its internal refactor
  (Task 2) — verified by its own existing test suite passing unmodified.
- No method reachable from `OperationController.Update()` may let an exception escape it — unchanged
  by this plan, but every new method must not violate it either.
- `PublishState()` remains the sole place `OperationController.State` is assigned.

---

## Task 1: `OperationController` — `GetPendingRecoveryArtifactStatus`, `GetBlockedOperations`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Produces: `OperationController.GetPendingRecoveryArtifactStatus() -> (ArtifactCheckStatus Plan,
  ArtifactCheckStatus Snapshot)?`, `OperationController.GetBlockedOperations() ->
  IReadOnlyList<(Guid OperationId, OperationJournal Journal)>` — both consumed by Task 9/10's
  `MainWindow` wiring.

Design doc §4/§5. Two pure, read-only query accessors over already-existing state. No behavior
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

    // Only AuthoritativeOperationIds (the leaves - the ones actually independently resolvable), not
    // AllOperationIds - a non-leaf ancestor isn't independently actionable; it gets folded in
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
Expected: PASS — all pre-existing tests plus the four new ones.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: add GetPendingRecoveryArtifactStatus/GetBlockedOperations query accessors"
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
            Assert.True(Directory.Exists(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idC))); // C is the sole remaining authoritative operation
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

- [ ] **Step 3: Implement `ResolveOneMultiRootOperation`**

In `OperationController.cs`, add right after `AcceptAllAndCloseInterruptedOperations`:

```csharp
    public void ResolveOneMultiRootOperation(Guid operationId)
    {
        if (_blockedMultiRootGraph is not { } graph)
            throw new InvalidOperationException("No blocked multi-root recovery to resolve.");
        if (!graph.AuthoritativeOperationIds.Contains(operationId))
            throw new InvalidOperationException("The requested operation is not an independently resolvable root of the blocked recovery graph.");

        if (TryResolveJournalAsKeepCurrent(operationId) == JournalResolutionOutcome.Failed)
            throw new InvalidOperationException($"Failed to resolve {operationId} - see the plugin log.");

        // Re-run discovery over whatever remains on disk now that operationId has dropped out
        // (either just resolved above, or already resolved by a prior partial attempt) - the same
        // startup discovery path Plugin.cs's constructor uses, reused here rather than hand-rolling
        // a second graph derivation. Cleared first so RegisterDiscoveredRecovery's NoRecoveryNeeded/
        // SingleAuthoritative branches don't see stale blocked-graph state while re-registering.
        _blockedMultiRootGraph = null;
        _blockedMultiRootJournals = null;
        var discovery = OperationBundleDiscovery.RunStartupDiscovery(_operationsRoot);
        RegisterDiscoveredRecovery(discovery);

        // RegisterDiscoveredRecovery's NoRecoveryNeeded branch returns without calling PublishState()
        // (correct at startup, where State already defaults to Idle) - here we may be transitioning
        // OUT of a non-Idle blocked state, so publish unconditionally regardless of which branch fired.
        PublishState();
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
return.

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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OperationBundleDiscoveryTests`
Expected: build failure (`LoadRecentCompletedJournals` doesn't exist yet)

- [ ] **Step 3: Implement**

In `OperationBundleDiscovery.cs`, add after `LoadNonTerminalActiveJournals`:

```csharp
    public static IReadOnlyList<OperationJournal> LoadRecentCompletedJournals(string operationsRoot, int take)
    {
        var completedDir = OperationBundlePaths.CompletedDirectory(operationsRoot);
        if (!Directory.Exists(completedDir))
            return [];

        var journals = new List<OperationJournal>();
        foreach (var bundleDir in Directory.GetDirectories(completedDir))
        {
            if (OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) && journal is not null)
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

- [ ] **Step 1: Wire the retention pass into the constructor**

In `Plugin.cs`, immediately after the existing discovery wiring:

```csharp
        var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
        OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
```

add:

```csharp
        var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
        OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
        Organizer.Operations.OperationBundleRetention.RunRetentionPass(OperationsRoot, DateTimeOffset.UtcNow);
```

- [ ] **Step 2: Add the Cancel and multi-root wrapper methods**

Immediately after the existing `AcceptAllAndCloseInterruptedOperations()` wrapper:

```csharp
    internal void RequestCancellation() => OperationController.RequestCancellation();

    internal void ResolveOneMultiRootOperation(Guid operationId)
    {
        OperationController.ResolveOneMultiRootOperation(operationId);
        RunScan(); // matches ResolveKeepCurrent/AcceptAll's own pattern - this resolves synchronously, no successor operation starts
    }
```

`RequestCancellation` needs no `_operationInProgress` guard (it doesn't start anything, and
`OperationController.RequestCancellation()` is itself a no-op guarded internally by `Stage ==
Mutating`) and no try/catch (it cannot throw). `ResolveOneMultiRootOperation` resolves synchronously
(same as `ResolveKeepCurrent`/`AcceptAllAndCloseInterruptedOperations` — no new async operation
starts), so it follows their `RunScan()`-after pattern, not `ResolveContinue`/
`ResolveRestorePreviousState`'s `_operationInProgress`-guarded async pattern.

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
                DrawOperationProgress(operationState, "Applying");
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
                DrawOperationProgress(operationState, "Restoring");
            else if (operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");
        }
```

- [ ] **Step 3: Add the shared `DrawOperationProgress` helper**

Add a new private method near `DrawWrappingButtonRow` (the other small shared ImGui drawing helper in
this file):

```csharp
    // Target-based, not step-based: a cycle-breaking plan has more execution steps than recovery
    // targets (a temporary hop plus a final move both count as steps for one target), so a
    // step-based fraction misrepresents "how many mods are done" to a user whose mental model is
    // mods, not steps (design doc section 2). SuccessfulTargets, not ProcessedTargets, drives the
    // fraction - ProcessedTargets includes attempted-and-failed targets, which would make the bar
    // appear to complete even on a run with real failures.
    private static void DrawOperationProgress(Organizer.Operations.OperationStateSnapshot operationState, string verb)
    {
        var fraction = operationState.TotalTargets > 0
            ? (float)operationState.SuccessfulTargets / operationState.TotalTargets
            : 1f;
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{operationState.SuccessfulTargets}/{operationState.TotalTargets} mods");
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

- [ ] **Step 1: Apply tab**

In `DrawReviewTab`, immediately after the `DrawOperationProgress(operationState, "Applying");` call
added in Task 7:

```csharp
                DrawOperationProgress(operationState, "Applying");
                if (operationState.CanRequestCancellation)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel##cancel-apply"))
                        _plugin.RequestCancellation();
                }
```

- [ ] **Step 2: Restore/History tab**

Immediately after the `DrawOperationProgress(operationState, "Restoring");` call added in Task 7:

```csharp
                DrawOperationProgress(operationState, "Restoring");
                if (operationState.CanRequestCancellation)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel##cancel-restore"))
                        _plugin.RequestCancellation();
                }
```

(Two separate `##cancel-apply`/`##cancel-restore` widget IDs since ImGui requires unique IDs across
the whole window, not just within one tab, matching this file's own established per-row uniqueness
convention documented in `DrawHistoryTab`.)

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
                if (status.Plan != Organizer.Operations.ArtifactCheckStatus.Valid)
                    ImGui.TextColored(PluginTheme.CollisionBad, $"Interrupted plan is {status.Plan} - Continue is unavailable.");
                if (status.Snapshot != Organizer.Operations.ArtifactCheckStatus.Valid)
                    ImGui.TextColored(PluginTheme.CollisionBad, $"Snapshot is {status.Snapshot} - Restore Previous State is unavailable.");
            }

            var assessment = _plugin.OperationController.GetRecoveryAssessment();
            if (assessment is null)
            {
                ImGui.TextDisabled("Still checking live mod state...");
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
            ImGui.TextWrapped(
                "Multiple interrupted operations were found. You can resolve them one at a time below " +
                "(each becomes an ordinary single recovery, or unblocks entirely, once the others are " +
                "handled), or abandon all of them at once and accept whatever Penumbra currently has as " +
                "correct - this does not undo or redo any moves for any of them, it only stops the plugin " +
                "from blocking further actions.");

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
                    ImGui.TextUnformatted("This will mark this one interrupted operation as resolved. Any others found stay blocked until resolved separately.");
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
- Consumes: Task 4's `OperationBundleDiscovery.LoadRecentCompletedJournals`, existing
  `DiagnosticsLog.ReadAll`, existing `OperationController.State`.

Design doc §6. Three new sections in `CreateDiagnosticDump()`. Not unit-testable — reads live
`OperationController.State` and writes a file via Dalamud's config directory, same limitation as the
existing dump.

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
        var operationState = _plugin.OperationController.State;
        if (operationState.RequiresRecovery)
            sb.AppendLine($"Stage={operationState.Stage}, {operationState.ProcessedSteps}/{operationState.TotalSteps} steps, last updated (approx.) now - see the recovery panel for details.");
        else
            sb.AppendLine("(none)");
        sb.AppendLine();

        sb.AppendLine("== Recent operations ==");
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
        sb.AppendLine();

        sb.AppendLine("== Slow calls ==");
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
            sb.AppendLine($"{slowCalls.Count} recorded slow calls. Worst 5 by duration:");
            foreach (var evt in slowCalls.OrderByDescending(e => e.DurationMilliseconds).Take(5))
                sb.AppendLine($"  {evt.Identifier}: {evt.DurationMilliseconds}ms at {evt.RecordedAt.ToLocalTime():u}");
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

- [ ] **Step 1: Add the section**

In `DrawHistoryTab`, immediately before the closing brace of the method (after the existing
`_restoreOperationActive` progress block):

```csharp
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Recent Operations"))
        {
            var recentOperations = Organizer.Operations.OperationBundleDiscovery.LoadRecentCompletedJournals(_plugin.OperationsRoot, take: 20);
            if (recentOperations.Count == 0)
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
                    foreach (var journal in recentOperations)
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

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 3: Commit**

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
   Folder Cleanup/Rollback buttons are greyed out with a tooltip explaining why on hover.
2. Start an Apply on a real multi-mod library. Confirm the progress bar fills proportionally by mod
   count (not step count — a mod involved in a swap should not make the bar jump by more than its own
   share), the "Applying: <mod name>" line updates, and a Cancel button appears and disappears
   correctly around the Mutating stage.
3. Click Cancel mid-Apply. Confirm the operation stops at the next safe boundary and settles as
   `Cancelled`, with no confirmation popup needed.
4. Force an interrupted single-root recovery (per D1/D2's own manual checklists). Confirm the new
   "Details" section shows the correct per-mod classification colors and, if the plan or snapshot is
   corrupted, the correct artifact-status warning text.
5. Hand-construct (or otherwise produce) a multi-root/cycle blocked state. Confirm each blocked
   operation gets its own row with type/stage/timestamp, resolving one via its own "Keep Current
   State" button correctly shrinks the list (or, if it was the last one, unblocks entirely and shows
   the ordinary single-root panel or nothing at all), and "Accept Current State and Close All" still
   works as the bulk fallback.
6. Create a Diagnostic Dump. Confirm the new "Interrupted operation," "Recent operations," and "Slow
   calls" sections appear with real data (or "(none)" where appropriate) rather than throwing or
   silently omitting.
7. Open the History tab's new "Recent Operations" section. Confirm it lists real completed Apply/
   Restore/Continue/Restore-Previous-State operations with correct type/stage/resolution, stays
   collapsed by default, and is visually distinct from the snapshot list above it.
8. Restart the plugin (or the game) with more than 50 completed operation bundles on disk (or an
   artificially-lowered retention count for testing). Confirm `completed/` is pruned to the retention
   window on the next startup and the plugin doesn't error.

- [ ] **Step 4: Report the test count and baseline comparison**

State the final `dotnet test` pass count and confirm it matches (prior count) + (new tests added
across Tasks 1, 3, and 4) with zero unrelated regressions, ready for the final whole-branch review per
`superpowers:subagent-driven-development`.
