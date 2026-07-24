# Operation Recovery Classification and Keep Current (Plan D1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire Plan A2's already-shipped startup discovery (`OperationBundleDiscovery`,
`OperationRecoveryGraph`) into `Plugin.cs`, classify a discovered interrupted operation against live
Penumbra state, and give the user a real, always-available way to unblock the plugin (Keep Current for
the common single-journal case, a bulk "accept all and close" fallback for the rare multi-root/cycle
case) — with a crude but functional `MainWindow` panel, not a backend-only lockout.

**Architecture:** A discovered interrupted operation is tracked as its own `PendingRecoveryContext`,
separate from the existing `_active` slot (which exists to be advanced frame-by-frame by
`PathMutationOperation`; nothing progresses a discovered-but-unresolved recovery that way — it sits
frozen until a human picks a resolution). Classification against live state is lazy, throttled, and
status-aware (`WaitingForProvider`/`Classified`/`ClassificationUnavailable`), started from
`Plugin.cs`'s constructor via `OperationBundleDiscovery.RunStartupDiscovery` and serviced from
`OperationController.Update()` alongside the existing per-frame Apply/Restore advancement. Keep Current
and the bulk fallback share one commit-point rule (persist the resolved journal first; best-effort
relocate the bundle directory after) and one collision-safe relocation helper.

**Tech Stack:** C# / .NET, xUnit, Dalamud plugin framework, Penumbra IPC (`Penumbra.Api` 5.15.1).

## Global Constraints

- `dotnet build` must introduce no NEW warnings/errors beyond whatever the accepted baseline is at
  worktree setup (re-verify then — Plan C's own baseline already drifted once from what an earlier
  plan assumed; do not assume 0 warnings without checking).
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC to force unit coverage
  onto `Plugin.cs`/`MainWindow.cs` — same documented limitation carried forward from every prior plan.
  Those two files are verified by build + the manual checklist in Task 11 only.
- `PenumbraPathSemantics.AreEquivalent(currentPath, proposedPath, displayName)`/
  `PenumbraPathSemantics.Normalize(path, displayName)` for every path comparison in new code — never
  raw string equality.
- `sealed record` for data types, `static class` for pure stateless logic — carried forward convention.
- `IElapsedTimeSource` (`GetTimestamp()`/`GetElapsedTime(startTimestamp)`) for any in-process
  interval/throttle timing; `DateTimeOffset.UtcNow` for any persisted wall-clock journal field
  (`StartedAt`/`UpdatedAt`) — matching the existing convention in `OperationController.StartOperation`.
  The two are not interchangeable; `IElapsedTimeSource` has no `UtcNow` member.
- Tasks are ordered so the repository is buildable and the full test suite passes at every task
  boundary: the Plan A2 bug fix and the three new pure classes first (no interdependencies beyond what
  each task's own Interfaces section states), then the `OperationController` changes as four smaller
  sequential tasks (registration/state, classification advancement, single resolution, bulk
  resolution) rather than one large task, then `Plugin.cs` wiring, then `MainWindow`, then final
  verification. Full build + test run after every task; explicit full-suite checkpoints called out
  after the last controller task and after the UI task.

---

### Task 1: Fix the Plan A2 bug — `OperationRecoveryGraphStatus.NoRecoveryNeeded`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationRecoveryGraph.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationRecoveryGraphTests.cs` (update one existing test, add one new)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleDiscoveryTests.cs` (extend one existing test, add one new)

**Interfaces:**
- Consumes: nothing new.
- Produces: `OperationRecoveryGraphStatus.NoRecoveryNeeded`, consumed by Task 5's
  `OperationController.RegisterDiscoveredRecovery`.

**Context for whoever implements this:** `OperationRecoveryGraph.Analyze`'s existing
`leaves.Count switch { 1 => SingleAuthoritative, _ => MultipleDisconnectedRoots }` sends
`leaves.Count == 0` (the ordinary "nothing to recover" case, produced whenever `Analyze` is called with
an empty journal list) into the same branch as `leaves.Count >= 2` — the normal clean-startup path is
currently misclassified as `MultipleDisconnectedRoots`. There's already a real, if muted, sign of this
in the existing test suite: `OperationRecoveryGraphTests.cs`'s
`Analyze_EmptyList_SingleAuthoritativeIsMeaninglessButMustNotThrow` test only asserts the two ID lists
are empty — it deliberately never asserts `.Status`, because asserting it today would mean asserting
the wrong value. This task fixes that.

- [ ] **Step 1: Write the failing tests**

In `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationRecoveryGraphTests.cs`, replace the
existing test:

```csharp
    [Fact]
    public void Analyze_EmptyList_SingleAuthoritativeIsMeaninglessButMustNotThrow()
    {
        var result = OperationRecoveryGraph.Analyze([]);

        Assert.Empty(result.AuthoritativeOperationIds);
        Assert.Empty(result.AllOperationIds);
    }
```

with:

```csharp
    [Fact]
    public void Analyze_EmptyList_NoRecoveryNeeded()
    {
        var result = OperationRecoveryGraph.Analyze([]);

        Assert.Equal(OperationRecoveryGraphStatus.NoRecoveryNeeded, result.Status);
        Assert.Empty(result.AuthoritativeOperationIds);
        Assert.Empty(result.AllOperationIds);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter Analyze_EmptyList_NoRecoveryNeeded`
Expected: build error (`OperationRecoveryGraphStatus.NoRecoveryNeeded` does not exist yet).

- [ ] **Step 3: Fix `OperationRecoveryGraph.cs`**

In `PenumbraOrganizer.Plugin/Organizer/Operations/OperationRecoveryGraph.cs`, change:

```csharp
public enum OperationRecoveryGraphStatus { SingleAuthoritative, MultipleDisconnectedRoots, CycleDetected }
```

to:

```csharp
public enum OperationRecoveryGraphStatus { NoRecoveryNeeded, SingleAuthoritative, MultipleDisconnectedRoots, CycleDetected }
```

Then in `Analyze`, immediately after the `var allIds = idSet.ToList();` line, add:

```csharp
        if (allIds.Count == 0)
            return new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.NoRecoveryNeeded, [], []);
```

Nothing else in `Analyze`/`TryFindCycle`/`TryFindCoreCycle` changes.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationRecoveryGraphTests`
Expected: all pass, including the renamed test.

- [ ] **Step 5: Extend the discovery-level tests to prove the fix end-to-end**

In `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleDiscoveryTests.cs`, extend the
existing test:

```csharp
    [Fact]
    public void RunStartupDiscovery_NoActiveBundles_EmptyResult()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Empty(result.Journals);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

to add a `Status` assertion:

```csharp
    [Fact]
    public void RunStartupDiscovery_NoActiveBundles_EmptyResult()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Empty(result.Journals);
            Assert.Equal(OperationRecoveryGraphStatus.NoRecoveryNeeded, result.Graph.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

Then add a new test proving the fix composes correctly with the existing terminal-bundle relocation
pass (not just the graph method in isolation):

```csharp
    [Fact]
    public void RunStartupDiscovery_OnlyTerminalBundlesPresent_NoRecoveryNeeded()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(id, OperationStage.Completed));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Empty(result.Journals);
            Assert.Equal(OperationRecoveryGraphStatus.NoRecoveryNeeded, result.Graph.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationBundleDiscoveryTests`
Expected: all pass, including the two new/extended ones.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass.

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationRecoveryGraph.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationRecoveryGraphTests.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleDiscoveryTests.cs
git commit -m "fix: add NoRecoveryNeeded status - Analyze([]) was misclassified as MultipleDisconnectedRoots"
```

---

### Task 2: `ArtifactCheckStatus` and `ArtifactStatusChecker`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/ArtifactStatusChecker.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/ArtifactStatusCheckerTests.cs`

**Interfaces:**
- Consumes: `OperationBundlePaths.PlanPath`/`SnapshotPath` (existing), `OperationPlanCodec.TryLoad`/
  `OperationSnapshotCodec.TryLoad` (existing).
- Produces: `ArtifactCheckStatus` enum and `ArtifactStatusChecker.CheckPlan`/`CheckSnapshot`, consumed
  by Task 5's `PendingRecoveryContext` field types and Task 6's `TryAdvanceClassification`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/ArtifactStatusCheckerTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class ArtifactStatusCheckerTests
{
    [Fact]
    public void CheckPlan_FileMissing_ReturnsMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var (status, plan) = ArtifactStatusChecker.CheckPlan(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Missing, status);
            Assert.Null(plan);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckPlan_FileCorrupt_ReturnsInvalid()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(OperationBundlePaths.PlanPath(dir.FullName), "{ not valid json");

            var (status, plan) = ArtifactStatusChecker.CheckPlan(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Invalid, status);
            Assert.Null(plan);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckPlan_FileValid_ReturnsValidWithParsedPlan()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var plan = OperationPlan.Create(
                OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)],
                [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(dir.FullName), plan);

            var (status, loaded) = ArtifactStatusChecker.CheckPlan(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Valid, status);
            Assert.NotNull(loaded);
            Assert.Equal(plan.OperationId, loaded!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckSnapshot_FileMissing_ReturnsMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var (status, snapshot) = ArtifactStatusChecker.CheckSnapshot(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Missing, status);
            Assert.Null(snapshot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckSnapshot_FileCorrupt_ReturnsInvalid()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(OperationBundlePaths.SnapshotPath(dir.FullName), "{ not valid json");

            var (status, snapshot) = ArtifactStatusChecker.CheckSnapshot(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Invalid, status);
            Assert.Null(snapshot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckSnapshot_FileValid_ReturnsValidWithParsedSnapshot()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var snapshot = new RollbackSnapshot(
                Guid.NewGuid(), DateTimeOffset.UtcNow, "label", "auto",
                new Dictionary<string, string> { ["mod-a"] = "Weapons/A" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(dir.FullName), snapshot);

            var (status, loaded) = ArtifactStatusChecker.CheckSnapshot(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Valid, status);
            Assert.NotNull(loaded);
            Assert.Equal(snapshot.Id, loaded!.Id);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter ArtifactStatusCheckerTests`
Expected: build error (`ArtifactCheckStatus`/`ArtifactStatusChecker` do not exist yet).

- [ ] **Step 3: Implement**

Create `PenumbraOrganizer.Plugin/Organizer/Operations/ArtifactStatusChecker.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ArtifactCheckStatus { Unchecked, Valid, Missing, Invalid }

/// <summary>
/// Checked at most once per discovered recovery bundle (OperationController's PendingRecoveryContext
/// tracks the result so it's never re-checked) - a missing or corrupt artifact is permanent for that
/// bundle's lifetime, so repeating this file I/O every frame would do real work for no benefit.
/// </summary>
public static class ArtifactStatusChecker
{
    public static (ArtifactCheckStatus Status, OperationPlan? Plan) CheckPlan(string bundleDirectory)
    {
        var path = OperationBundlePaths.PlanPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactCheckStatus.Missing, null);
        return OperationPlanCodec.TryLoad(path, out var plan)
            ? (ArtifactCheckStatus.Valid, plan)
            : (ArtifactCheckStatus.Invalid, null);
    }

    public static (ArtifactCheckStatus Status, RollbackSnapshot? Snapshot) CheckSnapshot(string bundleDirectory)
    {
        var path = OperationBundlePaths.SnapshotPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactCheckStatus.Missing, null);
        return OperationSnapshotCodec.TryLoad(path, out var snapshot)
            ? (ArtifactCheckStatus.Valid, snapshot)
            : (ArtifactCheckStatus.Invalid, null);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter ArtifactStatusCheckerTests`
Expected: 6/6 pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/ArtifactStatusChecker.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/ArtifactStatusCheckerTests.cs
git commit -m "feat: add ArtifactStatusChecker for plan/snapshot artifact validity"
```

---

### Task 3: `RecoveryClassifier`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs`

**Interfaces:**
- Consumes: `OperationPlan`/`OperationRecoveryTarget`/`OperationExecutionStep`/`OperationStepKind`
  (existing), `LiveModSnapshot`/`LiveModSnapshotBuilder` (existing), `LiveMod` (existing, in
  `RollbackHistory.cs`), `PenumbraPathSemantics.AreEquivalent` (existing).
- Produces: `ItemRecoveryState`/`ItemRecoveryClassification`/`RecoveryClassifier.Classify`, consumed by
  Task 4's `RecoveryAssessmentBuilder`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RecoveryClassifierTests
{
    private static OperationPlan SimplePlan(string id = "mod-a", string snapshotPath = "Gear/A", string finalPath = "Weapons/A") =>
        OperationPlan.Create(
            OperationType.Apply, [new(0, id, finalPath, OperationStepKind.FinalMove, 0)],
            [new(id, snapshotPath, finalPath, id)]);

    private static LiveModSnapshot Live(params LiveMod[] mods) => LiveModSnapshotBuilder.Build(mods);

    [Fact]
    public void Classify_LiveAtFinalPathOnly_AtIntended()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Weapons/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtIntended, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_LiveAtSnapshotPathOnly_AtSnapshot()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Gear/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtSnapshot, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_SnapshotAndFinalCoincide_AtBoth()
    {
        var plan = SimplePlan(snapshotPath: "Gear/A", finalPath: "Gear/A");
        var live = Live(new LiveMod("mod-a", "mod-a", "Gear/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtBoth, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_LiveAtNeitherPath_AtNeither()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "SomewhereElse/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtNeither, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_IdentifierNotInLiveSnapshot_MissingLive()
    {
        var plan = SimplePlan();
        var live = Live();

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.MissingLive, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_LiveAtCycleBreakingTemporaryPath_AtKnownIntermediate()
    {
        // Build a plan with a genuine two-way cycle so a real CycleBreakingTemporaryMove step exists.
        var moves = new[] { new ModMove("X", "Gear/A", "Gear/B"), new ModMove("Y", "Gear/B", "Gear/A") };
        var steps = ApplyPlanner.OrderMovesForApply(moves);
        var executionSteps = steps.Select((s, i) => new OperationExecutionStep(
            i, s.Identifier, s.TargetPath,
            s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove, s.GroupId)).ToList();
        var recoveryTargets = new[] { new OperationRecoveryTarget("X", "Gear/A", "Gear/B", "X"), new OperationRecoveryTarget("Y", "Gear/B", "Gear/A", "Y") };
        var plan = OperationPlan.Create(OperationType.Apply, executionSteps, recoveryTargets);
        var temporaryStep = executionSteps.Single(s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove);

        var live = Live(new LiveMod(temporaryStep.Identifier, temporaryStep.Identifier, temporaryStep.TargetRawPath, false));

        var result = RecoveryClassifier.Classify(plan, live);

        var classification = result.Single(c => c.Identifier == temporaryStep.Identifier);
        Assert.Equal(ItemRecoveryState.AtKnownIntermediate, classification.State);
    }

    [Fact]
    public void Classify_DuplicateLiveIdentifiers_DoesNotSpecialCase()
    {
        // Classify itself stays unconditional regardless of DuplicateIdentifiers - that's a
        // resolution-layer concern (Keep Current tolerates it, D2's Continue/Restore won't), not
        // something this pure function decides.
        var plan = SimplePlan();
        var mods = new[] { new LiveMod("mod-a", "mod-a", "Weapons/A", false) };
        var liveWithDuplicates = new LiveModSnapshot(
            new Dictionary<string, LiveMod> { ["mod-a"] = mods[0] },
            new HashSet<string> { "some-other-id" });

        var result = RecoveryClassifier.Classify(plan, liveWithDuplicates);

        Assert.Equal(ItemRecoveryState.AtIntended, Assert.Single(result).State);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RecoveryClassifierTests`
Expected: build error (`ItemRecoveryState`/`RecoveryClassifier` do not exist yet).

- [ ] **Step 3: Implement**

Create `PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ItemRecoveryState { AtSnapshot, AtIntended, AtBoth, AtKnownIntermediate, AtNeither, MissingLive }

public sealed record ItemRecoveryClassification(string Identifier, ItemRecoveryState State);

/// <summary>
/// Design doc section 8, reconciled against shipped code. Classifies each of the interrupted plan's
/// RecoveryTargets against live state, using PenumbraPathSemantics.AreEquivalent (never raw string
/// equality) for every path comparison. Depends only on OperationPlan, never RollbackSnapshot - every
/// path this classifier needs (SnapshotRawPath, FinalRawPath, temporary hop targets) is already
/// embedded in the plan at construction time.
/// </summary>
public static class RecoveryClassifier
{
    public static IReadOnlyList<ItemRecoveryClassification> Classify(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
        // Exactly one temporary step per identifier is guaranteed by ApplyPlanner.OrderMovesForApply's
        // own structure: each identifier appears in exactly one chain (the algorithm's `visited` set
        // prevents an identifier's CurrentPath from being entered twice), and only chain[0] of a cycle
        // - entered once - ever receives IsTemporary: true.
        var temporaryTargetByIdentifier = plan.ExecutionSteps
            .Where(s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove)
            .ToDictionary(s => s.Identifier, s => s.TargetRawPath, StringComparer.Ordinal);

        return plan.RecoveryTargets
            .Select(t => new ItemRecoveryClassification(t.Identifier, ClassifyOne(t, liveSnapshot, temporaryTargetByIdentifier)))
            .ToList();
    }

    private static ItemRecoveryState ClassifyOne(
        OperationRecoveryTarget target, LiveModSnapshot liveSnapshot,
        IReadOnlyDictionary<string, string> temporaryTargetByIdentifier)
    {
        if (!liveSnapshot.Mods.TryGetValue(target.Identifier, out var live))
            return ItemRecoveryState.MissingLive;

        var atFinal = PenumbraPathSemantics.AreEquivalent(live.FullPath, target.FinalRawPath, target.ModName);
        var atSnapshot = PenumbraPathSemantics.AreEquivalent(live.FullPath, target.SnapshotRawPath, target.ModName);

        // AtBoth means live state is semantically equivalent to BOTH the snapshot and intended paths
        // (per PenumbraPathSemantics, not raw string identity) - not necessarily that the two raw
        // paths themselves are byte-identical.
        if (atFinal && atSnapshot)
            return ItemRecoveryState.AtBoth;
        if (atFinal)
            return ItemRecoveryState.AtIntended;
        if (atSnapshot)
            return ItemRecoveryState.AtSnapshot;
        if (temporaryTargetByIdentifier.TryGetValue(target.Identifier, out var tempPath)
            && PenumbraPathSemantics.AreEquivalent(live.FullPath, tempPath, target.ModName))
            return ItemRecoveryState.AtKnownIntermediate;

        return ItemRecoveryState.AtNeither;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RecoveryClassifierTests`
Expected: 7/7 pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs
git commit -m "feat: add RecoveryClassifier for per-target recovery state against live Penumbra state"
```

---

### Task 4: `RecoveryAssessment` and `RecoveryAssessmentBuilder`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryAssessment.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryAssessmentBuilderTests.cs`

**Interfaces:**
- Consumes: `RecoveryClassifier.Classify` (Task 3), `LiveModSnapshot` (existing),
  `PenumbraPathSemantics.Normalize` (existing).
- Produces: `RecoveryAssessment`/`RecoveryAssessmentBuilder.Build`, consumed by Task 6's
  `TryAdvanceClassification`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryAssessmentBuilderTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RecoveryAssessmentBuilderTests
{
    private static OperationPlan SimplePlan() => OperationPlan.Create(
        OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)],
        [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);

    private static LiveModSnapshot Live(params LiveMod[] mods) => LiveModSnapshotBuilder.Build(mods);

    [Fact]
    public void Build_ClassificationsMatchADirectClassifyCall()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Weapons/A", false));

        var assessment = RecoveryAssessmentBuilder.Build(plan, live);
        var direct = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(direct, assessment.Classifications);
    }

    [Fact]
    public void Build_Fingerprint_IsDeterministic()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Weapons/A", false));

        var first = RecoveryAssessmentBuilder.Build(plan, live).LiveStateFingerprint;
        var second = RecoveryAssessmentBuilder.Build(plan, live).LiveStateFingerprint;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Build_Fingerprint_IsOrderIndependent()
    {
        var plan = SimplePlan();
        var liveAB = Live(new LiveMod("mod-a", "A", "Weapons/A", false), new LiveMod("mod-b", "B", "Weapons/B", false));
        var liveBA = Live(new LiveMod("mod-b", "B", "Weapons/B", false), new LiveMod("mod-a", "A", "Weapons/A", false));

        Assert.Equal(RecoveryAssessmentBuilder.Build(plan, liveAB).LiveStateFingerprint, RecoveryAssessmentBuilder.Build(plan, liveBA).LiveStateFingerprint);
    }

    [Fact]
    public void Build_Fingerprint_DiffersWhenDuplicateIdentifiersDiffer()
    {
        var plan = SimplePlan();
        var mod = new LiveMod("mod-a", "mod-a", "Weapons/A", false);
        var withoutDuplicates = new LiveModSnapshot(new Dictionary<string, LiveMod> { ["mod-a"] = mod }, new HashSet<string>());
        var withDuplicates = new LiveModSnapshot(new Dictionary<string, LiveMod> { ["mod-a"] = mod }, new HashSet<string> { "some-other-id" });

        var first = RecoveryAssessmentBuilder.Build(plan, withoutDuplicates).LiveStateFingerprint;
        var second = RecoveryAssessmentBuilder.Build(plan, withDuplicates).LiveStateFingerprint;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Build_Fingerprint_DiffersWhenModNameDiffers()
    {
        var plan = SimplePlan();
        var liveNamedX = Live(new LiveMod("mod-a", "Name X", "Weapons/A", false));
        var liveNamedY = Live(new LiveMod("mod-a", "Name Y", "Weapons/A", false));

        var first = RecoveryAssessmentBuilder.Build(plan, liveNamedX).LiveStateFingerprint;
        var second = RecoveryAssessmentBuilder.Build(plan, liveNamedY).LiveStateFingerprint;

        Assert.NotEqual(first, second);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RecoveryAssessmentBuilderTests`
Expected: build error (`RecoveryAssessment`/`RecoveryAssessmentBuilder` do not exist yet).

- [ ] **Step 3: Implement**

Create `PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryAssessment.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// One atomic read feeding both classification and (in a later plan) Continue's residual replanning -
/// never two independent GetLiveMods() calls that could disagree if the library changes mid-flow.
/// LiveStateFingerprint hashes PenumbraPathSemantics-normalized paths, so it proves semantic live-
/// state continuity, not raw byte-for-byte path identity - a purely cosmetic raw-path change (e.g.
/// Penumbra's own " (N)" suffix reshuffling) will not change it, by design.
/// </summary>
public sealed record RecoveryAssessment(
    LiveModSnapshot LiveSnapshot,
    IReadOnlyList<ItemRecoveryClassification> Classifications,
    string LiveStateFingerprint);

public static class RecoveryAssessmentBuilder
{
    public static RecoveryAssessment Build(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
        var classifications = RecoveryClassifier.Classify(plan, liveSnapshot);
        var fingerprint = ComputeFingerprint(liveSnapshot);
        return new RecoveryAssessment(liveSnapshot, classifications, fingerprint);
    }

    private static string ComputeFingerprint(LiveModSnapshot liveSnapshot)
    {
        var sb = new System.Text.StringBuilder();
        void Field(string value) => sb.Append(System.Text.Encoding.UTF8.GetByteCount(value)
            .ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value);

        foreach (var (identifier, mod) in liveSnapshot.Mods.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Field(identifier);
            Field(mod.Name);
            Field(PenumbraPathSemantics.Normalize(mod.FullPath, mod.Name));
        }

        Field(liveSnapshot.DuplicateIdentifiers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var dup in liveSnapshot.DuplicateIdentifiers.OrderBy(d => d, StringComparer.Ordinal))
            Field(dup);

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RecoveryAssessmentBuilderTests`
Expected: 5/5 pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryAssessment.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryAssessmentBuilderTests.cs
git commit -m "feat: add RecoveryAssessment and its builder"
```

---

### Task 5: `OperationController` — `operationsRoot`, `PendingRecoveryContext`, registration, state publishing

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: `OperationDiscoveryResult`/`OperationRecoveryGraphResult`/`OperationRecoveryGraphStatus`
  (existing, Task 1's fix), `ArtifactCheckStatus` (Task 2), `RecoveryAssessment` (Task 4).
- Produces: `OperationController`'s new 5-arg constructor, `RegisterDiscoveredRecovery`,
  `GetRecoveryAssessment()`, `IsBlockedByMultipleRoots`, and the `RecoveryClassificationStatus` enum —
  all consumed by Task 6 (classification advancement), Task 7 (Keep Current), Task 8 (bulk resolution),
  and Task 9 (`Plugin.cs` wiring).

**Note:** this task does *not* yet make `Update()` advance classification (Task 6) or add any
resolution method (Tasks 7-8) — it only wires registration and exposes the resulting state. A
registered `_pendingRecovery`'s `ClassificationStatus` stays at its default `WaitingForProvider`
forever until Task 6 lands; that's expected and covered by this task's own tests.

- [ ] **Step 1: Write the failing tests**

In `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`, first update the
shared helper (this is the one place the new constructor parameter needs to be threaded through — every
other existing call site in this file keeps compiling unchanged):

```csharp
    private static OperationController NewController(
        IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink? diagnostics = null, string? operationsRoot = null) =>
        new(adapter, clock, diagnostics ?? new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4), operationsRoot ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
```

Then add new tests after the existing `StartRestore_*` tests:

```csharp
    [Fact]
    public void RegisterDiscoveredRecovery_NoRecoveryNeeded_StaysIdle()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.NoRecoveryNeeded, [], []),
            new Dictionary<Guid, OperationJournal>());

        controller.RegisterDiscoveredRecovery(discovery);

        Assert.Equal(OperationStateSnapshot.Idle, controller.State);
        Assert.False(controller.IsBlockedByMultipleRoots);
        Assert.Null(controller.GetRecoveryAssessment());
    }

    [Fact]
    public void RegisterDiscoveredRecovery_SingleAuthoritative_RequiresRecoveryAndCanResolveTrueImmediately()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock(), operationsRoot: dir.FullName);
            var journalId = Guid.NewGuid();
            var journal = InterruptedJournal(journalId);
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = journal });

            controller.RegisterDiscoveredRecovery(discovery);

            Assert.True(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanResolveRecovery);
            Assert.True(controller.State.RecoveryClassificationPending); // WaitingForProvider until Task 6's Update() logic advances it
            Assert.False(controller.State.CanStartApply);
            Assert.False(controller.State.CanScan);
            Assert.False(controller.IsBlockedByMultipleRoots);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RegisterDiscoveredRecovery_MultipleDisconnectedRoots_RequiresRecoveryAndCanResolveTrueButBlockedFlagSet()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, [idA, idB], [idA, idB]),
            new Dictionary<Guid, OperationJournal> { [idA] = InterruptedJournal(idA), [idB] = InterruptedJournal(idB) });

        controller.RegisterDiscoveredRecovery(discovery);

        Assert.True(controller.State.RequiresRecovery);
        Assert.True(controller.State.CanResolveRecovery); // AcceptAllAndCloseInterruptedOperations is a real resolution, Task 8
        Assert.False(controller.State.RecoveryClassificationPending);
        Assert.False(controller.State.CanStartApply);
        Assert.True(controller.IsBlockedByMultipleRoots);
        Assert.Null(controller.GetRecoveryAssessment());
    }

    [Fact]
    public void RegisterDiscoveredRecovery_CycleDetected_SameLockoutShapeAsMultipleDisconnectedRoots()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var id = Guid.NewGuid();
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.CycleDetected, [id], [id]),
            new Dictionary<Guid, OperationJournal> { [id] = InterruptedJournal(id) });

        controller.RegisterDiscoveredRecovery(discovery);

        Assert.True(controller.State.RequiresRecovery);
        Assert.True(controller.State.CanResolveRecovery);
        Assert.True(controller.IsBlockedByMultipleRoots);
    }
```

Add the shared helper `InterruptedJournal` near the top of the file, alongside `SinglePlan`:

```csharp
    private static OperationJournal InterruptedJournal(Guid id) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: id, Type: OperationType.Apply,
        Stage: OperationStage.Mutating, Resolution: OperationResolution.None, SuccessorOperationId: null,
        CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: 1, ProcessedStepCount: 0,
        LastCompletedIdentifier: null, SnapshotId: Guid.NewGuid(), PlanId: Guid.NewGuid(), TargetHash: "irrelevant",
        RecoveryOfOperationId: null, UpdatedAt: DateTimeOffset.UtcNow);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: build error (`RegisterDiscoveredRecovery`/`IsBlockedByMultipleRoots`/`GetRecoveryAssessment`/
the 5-arg constructor do not exist yet).

- [ ] **Step 3: Implement**

In `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`, add near the top of the
class, alongside the existing `ActiveOperationContext`:

```csharp
    public enum RecoveryClassificationStatus { WaitingForProvider, Classified, ClassificationUnavailable }

    private sealed class PendingRecoveryContext
    {
        public required OperationJournal Journal { get; set; }
        public required string BundleDirectory { get; init; }
        public required OperationRecoveryGraphResult Graph { get; init; }
        public ArtifactCheckStatus PlanCheckStatus { get; set; } = ArtifactCheckStatus.Unchecked;
        public OperationPlan? Plan { get; set; }
        public ArtifactCheckStatus SnapshotCheckStatus { get; set; } = ArtifactCheckStatus.Unchecked;
        public RollbackSnapshot? Snapshot { get; set; }
        public RecoveryClassificationStatus ClassificationStatus { get; set; } = RecoveryClassificationStatus.WaitingForProvider;
        public RecoveryAssessment? Assessment { get; set; }
        public long? LastClassificationAttemptTimestamp { get; set; }
    }
```

Change the constructor and fields:

```csharp
    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly TimeSpan _frameBudget;
    private readonly string _operationsRoot;
    private ActiveOperationContext? _active;
    private PendingRecoveryContext? _pendingRecovery;
    private OperationRecoveryGraphResult? _blockedMultiRootGraph;
    private bool _stopRequested;

    public OperationStateSnapshot State { get; private set; } = OperationStateSnapshot.Idle;

    public OperationController(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, TimeSpan frameBudget, string operationsRoot)
    {
        _adapter = adapter;
        _clock = clock;
        _diagnostics = diagnostics;
        _frameBudget = frameBudget;
        _operationsRoot = operationsRoot;
    }
```

Add registration, after `StartOperation`:

```csharp
    public void RegisterDiscoveredRecovery(OperationDiscoveryResult discovery)
    {
        switch (discovery.Graph.Status)
        {
            case OperationRecoveryGraphStatus.NoRecoveryNeeded:
                return; // controller stays Idle, exactly as today

            case OperationRecoveryGraphStatus.SingleAuthoritative:
                RegisterSingleAuthoritative(discovery);
                return;

            case OperationRecoveryGraphStatus.MultipleDisconnectedRoots:
            case OperationRecoveryGraphStatus.CycleDetected:
                _blockedMultiRootGraph = discovery.Graph;
                PublishState();
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(discovery), discovery.Graph.Status, "Unhandled OperationRecoveryGraphStatus.");
        }
    }

    private void RegisterSingleAuthoritative(OperationDiscoveryResult discovery)
    {
        var authoritativeId = discovery.Graph.AuthoritativeOperationIds[0];
        if (!discovery.Journals.TryGetValue(authoritativeId, out var journal))
            return; // defensive - graph and journals dictionary are built together by RunStartupDiscovery

        var bundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, authoritativeId);
        _pendingRecovery = new PendingRecoveryContext { Journal = journal, BundleDirectory = bundleDirectory, Graph = discovery.Graph };
        PublishState();
    }

    public RecoveryAssessment? GetRecoveryAssessment() => _pendingRecovery?.Assessment;

    public bool IsBlockedByMultipleRoots => _blockedMultiRootGraph is not null;
```

Update `PublishState()` — the existing `if (_active is null) { State = Idle; return; }` guard needs two
new branches before the unchanged `_active`-populated branch:

```csharp
    private void PublishState()
    {
        if (_active is null && _pendingRecovery is null && _blockedMultiRootGraph is null)
        {
            State = OperationStateSnapshot.Idle;
            return;
        }

        if (_active is null && _blockedMultiRootGraph is not null)
        {
            State = OperationStateSnapshot.Idle with
            {
                RequiresRecovery = true,
                RecoveryClassificationPending = false,
                CanResolveRecovery = true, // AcceptAllAndCloseInterruptedOperations, Task 8
                CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
                CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
            };
            return;
        }

        if (_active is null) // _pendingRecovery is not null
        {
            var pending = _pendingRecovery!;
            State = OperationStateSnapshot.Idle with
            {
                RequiresRecovery = true,
                RecoveryClassificationPending = pending.ClassificationStatus == RecoveryClassificationStatus.WaitingForProvider,
                CanResolveRecovery = true, // Keep Current needs neither classification nor a valid plan/snapshot
                CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
                CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
            };
            return;
        }

        // ...unchanged: everything from `var journal = _active.Journal;` onward...
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: all existing tests still pass (updated `NewController` helper compiles transparently for
every prior call site), plus the 4 new tests pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: OperationController registers discovered recovery, publishes its state"
```

---

### Task 6: `OperationController` — throttled, status-aware classification advancement

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: `ArtifactStatusChecker.CheckPlan`/`CheckSnapshot` (Task 2), `RecoveryAssessmentBuilder.Build`
  (Task 4), `IPenumbraOperations.GetLiveMods()`/`LiveModReadStatus` (existing), `PendingRecoveryContext`
  (Task 5).
- Produces: classification actually advances via `Update()`, consumed by Task 9's `Plugin.cs` (which
  calls `OperationController.Update()` every frame exactly as it already does for Apply/Restore) and by
  Task 10's `MainWindow` panel (via `RecoveryClassificationPending`).

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`:

```csharp
    private static OperationController NewControllerWithPendingRecovery(
        FakePenumbraOperations adapter, FakeClock clock, string operationsRoot, out Guid journalId)
    {
        var controller = NewController(adapter, clock, operationsRoot: operationsRoot);
        journalId = Guid.NewGuid();
        var journal = InterruptedJournal(journalId);
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
            new Dictionary<Guid, OperationJournal> { [journalId] = journal });
        controller.RegisterDiscoveredRecovery(discovery);
        return controller;
    }

    [Fact]
    public void Update_ValidPlanAndSnapshot_ClassifiesOnceIpcSucceeds()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update();

            Assert.False(controller.State.RecoveryClassificationPending);
            Assert.NotNull(controller.GetRecoveryAssessment());
            Assert.True(controller.State.CanResolveRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_PlanMissing_BecomesPermanentlyClassificationUnavailableWithoutCallingIpc()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations(); // nothing enqueued - a GetLiveMods() call would throw
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out _);

            controller.Update();
            controller.Update(); // a second call must not attempt GetLiveMods() either

            Assert.False(controller.State.RecoveryClassificationPending);
            Assert.Null(controller.GetRecoveryAssessment());
            Assert.True(controller.State.CanResolveRecovery); // Keep Current unaffected
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_SnapshotMissingButPlanValid_ClassificationStillSucceeds()
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

            controller.Update();

            Assert.NotNull(controller.GetRecoveryAssessment());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(LiveModReadStatus.TemporarilyUnavailable)]
    [InlineData(LiveModReadStatus.ProviderUnavailable)]
    public void Update_RetryableIpcStatus_StaysPendingAndRetriesAfterThrottleInterval(LiveModReadStatus status)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(status, null));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update(); // consumes the retryable response - still pending

            Assert.True(controller.State.RecoveryClassificationPending);
            Assert.Null(controller.GetRecoveryAssessment());

            clock.Advance(TimeSpan.FromSeconds(1));
            controller.Update(); // throttle interval elapsed - consumes the Success response

            Assert.False(controller.State.RecoveryClassificationPending);
            Assert.NotNull(controller.GetRecoveryAssessment());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_InvalidDataIpcStatus_BecomesPermanentlyClassificationUnavailable()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.InvalidData, null));

            controller.Update();

            Assert.False(controller.State.RecoveryClassificationPending); // permanently unavailable, not pending
            Assert.Null(controller.GetRecoveryAssessment());
            Assert.True(controller.State.CanResolveRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_CalledManyTimesWithinSameSecond_CallsGetLiveModsAtMostOnce()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            var callCount = 0;
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.TemporarilyUnavailable, null), onCall: () => callCount++);

            for (var i = 0; i < 20; i++)
                controller.Update(); // no clock advance between calls - only the first should reach the adapter

            Assert.Equal(1, callCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: the 7 new tests fail (classification never advances yet - `Update()` doesn't call
`TryAdvanceClassification` until this task's implementation lands).

- [ ] **Step 3: Implement**

In `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`, add the retry interval
constant near the top of the class:

```csharp
    private static readonly TimeSpan ClassificationRetryInterval = TimeSpan.FromSeconds(1);
```

Change `Update()`:

```csharp
    public void Update()
    {
        if (_pendingRecovery is { ClassificationStatus: RecoveryClassificationStatus.WaitingForProvider } pending)
            TryAdvanceClassification(pending);

        if (_active is null || _active.RequiresRecovery)
            return;

        try
        {
            AdvanceActiveOperation();
        }
        // ...unchanged catch block...
    }
```

Add the new private method, anywhere below `Update()`:

```csharp
    private void TryAdvanceClassification(PendingRecoveryContext pending)
    {
        var stateChanged = false;

        if (pending.PlanCheckStatus == ArtifactCheckStatus.Unchecked)
        {
            (pending.PlanCheckStatus, pending.Plan) = ArtifactStatusChecker.CheckPlan(pending.BundleDirectory);
            stateChanged = true;
        }
        if (pending.SnapshotCheckStatus == ArtifactCheckStatus.Unchecked)
        {
            (pending.SnapshotCheckStatus, pending.Snapshot) = ArtifactStatusChecker.CheckSnapshot(pending.BundleDirectory);
            stateChanged = true;
        }

        // Classification needs a valid Plan only - a missing/invalid Snapshot does not block it.
        if (pending.PlanCheckStatus != ArtifactCheckStatus.Valid)
        {
            pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
            PublishState();
            return;
        }

        if (pending.LastClassificationAttemptTimestamp is { } last && _clock.GetElapsedTime(last) < ClassificationRetryInterval)
        {
            if (stateChanged)
                PublishState();
            return; // throttle window not yet elapsed since the last attempt
        }

        pending.LastClassificationAttemptTimestamp = _clock.GetTimestamp(); // record this attempt regardless of outcome
        var liveResult = _adapter.GetLiveMods();

        switch (liveResult.Status)
        {
            case LiveModReadStatus.Success when liveResult.Snapshot is not null:
                pending.Assessment = RecoveryAssessmentBuilder.Build(pending.Plan!, liveResult.Snapshot);
                pending.ClassificationStatus = RecoveryClassificationStatus.Classified;
                break;

            case LiveModReadStatus.TemporarilyUnavailable:
            case LiveModReadStatus.ProviderUnavailable:
                // Retryable at startup specifically - Penumbra may simply not have finished loading
                // yet. pending.ClassificationStatus already is WaitingForProvider; nothing to change.
                break;

            case LiveModReadStatus.InvalidData:
            default:
                // A response that parsed but doesn't make sense won't be fixed by asking again.
                pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
                break;
        }

        PublishState();
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: all pass, including the 7 new ones.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: OperationController advances recovery classification, throttled and status-aware"
```

---

### Task 7: `OperationController` — `TryRelocateToCompleted`, `ResolveKeepCurrent`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: `OperationJournalCodec.Save` (existing), `Plugin.Log` (existing static, `[PluginService]
  internal static IPluginLog Log` on the `Plugin` class — `OperationController` gains its first
  reference to it here; this is a narrow, deliberate dependency, not routed through `IDiagnosticsSink`,
  since a relocation-failure warning is an ordinary operational log line, not a structured per-operation
  diagnostic event).
- Produces: `KeepCurrentResolutionResult`, `TryRelocateToCompleted` (private, shared with Task 8),
  `ResolveKeepCurrent` — consumed by Task 9's `Plugin.ResolveKeepCurrent()`.

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`:

```csharp
    [Fact]
    public void ResolveKeepCurrent_NoPendingRecovery_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveKeepCurrent());
    }

    [Fact]
    public void ResolveKeepCurrent_HappyPath_ResolvesRelocatesAndUnblocks()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);
            var activeBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));

            var result = controller.ResolveKeepCurrent();

            Assert.Equal(KeepCurrentResolutionResult.ResolvedAndArchived, result);
            Assert.True(controller.State.CanStartApply);
            Assert.True(controller.State.CanStartRestore);
            Assert.False(controller.State.RequiresRecovery);
            Assert.False(Directory.Exists(activeBundleDirectory));
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var resolved));
            Assert.True(resolved!.IsTerminal);
            Assert.Equal(OperationResolution.AcceptedCurrentState, resolved.Resolution);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveKeepCurrent_CalledAgainAfterAMatchingPriorSuccess_IsIdempotent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);
            var activeBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));
            controller.ResolveKeepCurrent(); // first call relocates active/ -> completed/

            // Simulate a retry: re-register the same journal as pending (as if a second discovery
            // pass found it again before the first resolution's relocation was known to have
            // succeeded) and resolve it again.
            Directory.CreateDirectory(activeBundleDirectory);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = InterruptedJournal(journalId) });
            controller.RegisterDiscoveredRecovery(discovery);

            var result = controller.ResolveKeepCurrent();

            // The completed/ destination from the first call already exists, matches (same
            // OperationId, terminal, same Resolution), so this exercises the "already relocated"
            // branch specifically, not a fresh move.
            Assert.Equal(KeepCurrentResolutionResult.ResolvedAndArchived, result);
            Assert.False(controller.State.RequiresRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveKeepCurrent_ExistingDestinationDoesNotMatch_ReturnsDeferredButStillUnblocks()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);
            var activeBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));

            // Simulate an anomalous pre-existing completed/ directory for the same OperationId that
            // does NOT match what this resolution is about to produce (still non-terminal here,
            // unlike the journal ResolveKeepCurrent is about to save) - the collision check must not
            // treat this as "already relocated."
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(completedBundleDirectory), InterruptedJournal(journalId));

            var result = controller.ResolveKeepCurrent();

            Assert.Equal(KeepCurrentResolutionResult.ResolvedArchiveDeferred, result);
            Assert.False(controller.State.RequiresRecovery); // commit-point rule: the journal save already succeeded
            Assert.True(Directory.Exists(activeBundleDirectory)); // left untouched, not moved or deleted
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(activeBundleDirectory), out var activeJournal));
            Assert.Equal(OperationResolution.AcceptedCurrentState, activeJournal!.Resolution); // the save itself still happened
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: build error (`KeepCurrentResolutionResult`/`ResolveKeepCurrent` do not exist yet).

- [ ] **Step 3: Implement**

In `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`, add after
`IsBlockedByMultipleRoots`:

```csharp
    public enum KeepCurrentResolutionResult { ResolvedAndArchived, ResolvedArchiveDeferred }

    // Once a resolved (terminal) journal is durably saved, the caller must return success and clear
    // the recovery lock even if relocation fails - the persisted journal alone is authoritative, and
    // OperationBundleDiscovery's own startup relocation pass will finish moving any terminal journal
    // it later finds still sitting under active/.
    private KeepCurrentResolutionResult TryRelocateToCompleted(string activeBundleDirectory, OperationJournal resolvedJournal)
    {
        var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, resolvedJournal.OperationId);
        try
        {
            if (Directory.Exists(completedBundleDirectory))
            {
                var matches = OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var existing)
                    && existing is not null
                    && existing.OperationId == resolvedJournal.OperationId
                    && existing.IsTerminal
                    && existing.Resolution == resolvedJournal.Resolution;
                if (matches)
                    return KeepCurrentResolutionResult.ResolvedAndArchived;

                Plugin.Log.Warning($"Keep Current: completed bundle directory for {resolvedJournal.OperationId} exists but doesn't match the resolved journal - leaving both copies in place.");
                return KeepCurrentResolutionResult.ResolvedArchiveDeferred;
            }

            Directory.CreateDirectory(OperationBundlePaths.CompletedDirectory(_operationsRoot));
            Directory.Move(activeBundleDirectory, completedBundleDirectory);
            return KeepCurrentResolutionResult.ResolvedAndArchived;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log.Warning(ex, $"Keep Current: journal resolved but bundle relocation failed for {resolvedJournal.OperationId}.");
            return KeepCurrentResolutionResult.ResolvedArchiveDeferred;
        }
    }

    public KeepCurrentResolutionResult ResolveKeepCurrent()
    {
        if (_pendingRecovery is not { } pending)
            throw new InvalidOperationException("No pending recovery to resolve.");

        var resolvedJournal = pending.Journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(pending.BundleDirectory), resolvedJournal);
        pending.Journal = resolvedJournal; // commit point - everything below is best-effort

        var result = TryRelocateToCompleted(pending.BundleDirectory, resolvedJournal);
        _pendingRecovery = null;
        PublishState();
        return result;
    }
```

**Note:** `Plugin.Log` requires this file to reference the `Plugin` class, which lives in the parent
`PenumbraOrganizer.Plugin` namespace (`OperationController` is in `PenumbraOrganizer.Plugin.Organizer.Operations`)
— use the fully-qualified `PenumbraOrganizer.Plugin.Plugin.Log` if `Plugin.Log` alone doesn't resolve,
matching whichever form the compiler accepts; check this doesn't introduce a circular using need at
implementation time.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: all pass, including the 4 new ones.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass. This
is the "after the controller stage" full-suite checkpoint before the last controller task.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: OperationController.ResolveKeepCurrent with collision-safe relocation"
```

---

### Task 8: `OperationController` — `AcceptAllAndCloseInterruptedOperations`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: `TryRelocateToCompleted` (Task 7, private, same class), `OperationJournalCodec.Save`/
  `TryLoad` (existing).
- Produces: `AcceptAllAndCloseInterruptedOperations`, consumed by Task 9's
  `Plugin.AcceptAllAndCloseInterruptedOperations()`.

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`:

```csharp
    private static OperationController NewControllerWithBlockedMultiRoot(
        FakePenumbraOperations adapter, FakeClock clock, string operationsRoot, IReadOnlyList<Guid> ids)
    {
        var controller = NewController(adapter, clock, operationsRoot: operationsRoot);
        var journals = ids.ToDictionary(id => id, InterruptedJournal);
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, ids, ids),
            journals);
        controller.RegisterDiscoveredRecovery(discovery);
        return controller;
    }

    [Fact]
    public void AcceptAllAndCloseInterruptedOperations_NoBlockedGraph_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.AcceptAllAndCloseInterruptedOperations());
    }

    [Fact]
    public void AcceptAllAndCloseInterruptedOperations_AllJournalsResolvable_ResolvesAllAndUnblocks()
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

            var unresolved = controller.AcceptAllAndCloseInterruptedOperations();

            Assert.Empty(unresolved);
            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.CanStartApply);
            foreach (var id in new[] { idA, idB })
            {
                var completedDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id);
                Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedDir), out var resolved));
                Assert.Equal(OperationResolution.AcceptedCurrentState, resolved!.Resolution);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AcceptAllAndCloseInterruptedOperations_OneJournalUnloadable_LeavesLockoutInPlace()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            // idA's bundle directory/journal is never written to disk - simulates an unloadable journal.
            var bundleDirB = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idB);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirB), InterruptedJournal(idB));

            var unresolved = controller.AcceptAllAndCloseInterruptedOperations();

            Assert.Equal([idA], unresolved);
            Assert.True(controller.IsBlockedByMultipleRoots); // partial success does not unblock
            Assert.True(controller.State.RequiresRecovery);
            Assert.False(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: build error (`AcceptAllAndCloseInterruptedOperations` does not exist yet).

- [ ] **Step 3: Implement**

In `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`, add after
`ResolveKeepCurrent`:

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
            var bundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, operationId);
            if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDirectory), out var journal) || journal is null)
            {
                unresolved.Add(operationId);
                continue;
            }

            var resolvedJournal = journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
            try
            {
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirectory), resolvedJournal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Plugin.Log.Warning(ex, $"Accept all: failed to persist resolution for {operationId}.");
                unresolved.Add(operationId);
                continue;
            }

            TryRelocateToCompleted(bundleDirectory, resolvedJournal); // best-effort, same rule as ResolveKeepCurrent
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

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: all pass, including the 3 new ones.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds with no new warnings/errors beyond the accepted baseline; all tests pass. This
is the "after each controller stage" full-suite checkpoint, now for the last controller task.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: OperationController.AcceptAllAndCloseInterruptedOperations bulk fallback"
```

---

### Task 9: `Plugin.cs` — startup wiring

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `OperationBundleDiscovery.RunStartupDiscovery` (existing, Plan A2),
  `OperationController.RegisterDiscoveredRecovery`/`ResolveKeepCurrent`/
  `AcceptAllAndCloseInterruptedOperations` (Tasks 5, 7, 8), `OperationController`'s new 5-arg
  constructor (Task 5).
- Produces: `Plugin.ResolveKeepCurrent()`/`Plugin.AcceptAllAndCloseInterruptedOperations()` (both
  `internal`), consumed by Task 10's `MainWindow` panel.

**No automated test for this task** — Dalamud-coupled, same documented limitation as every prior
plan's `Plugin.cs` task. Verified by a clean `dotnet build` here and the manual checklist in Task 11.

- [ ] **Step 1: Add the fifth constructor argument**

In `PenumbraOrganizer.Plugin/Plugin.cs`, in the `Plugin()` constructor, change:

```csharp
        OperationController = new Organizer.Operations.OperationController(
            operationsAdapter, new Organizer.Operations.StopwatchElapsedTimeSource(),
            operationsDiagnosticsSink, TimeSpan.FromMilliseconds(2));
```

to:

```csharp
        OperationController = new Organizer.Operations.OperationController(
            operationsAdapter, new Organizer.Operations.StopwatchElapsedTimeSource(),
            operationsDiagnosticsSink, TimeSpan.FromMilliseconds(2), OperationsRoot);
```

- [ ] **Step 2: Run startup discovery immediately after, in the same constructor**

Immediately after the `OperationController = new Organizer.Operations.OperationController(...)`
statement (before `_workbookService = new WorkbookWorkflowService(...)`), add:

```csharp
        var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
        OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
```

- [ ] **Step 3: Add the two wrapper methods**

Add near the existing `internal void StartRestoreOperation(Guid snapshotId)` method (same general area
as the other operation-engine entry points):

```csharp
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
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: 0 errors, no new warnings beyond the accepted baseline.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests still pass (this task adds no new tests but must not break existing ones).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: wire startup recovery discovery into Plugin.cs"
```

---

### Task 10: `MainWindow` — crude recovery panel

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.ResolveKeepCurrent()`/`Plugin.AcceptAllAndCloseInterruptedOperations()` (Task 9),
  `OperationController.IsBlockedByMultipleRoots` (Task 5), `OperationStateSnapshot.RequiresRecovery`/
  `CanResolveRecovery` (existing).
- Produces: none consumed by later tasks — this is the last functional-wiring task before Task 11's
  verification.

**No automated test for this task** — ImGui rendering code, same documented limitation as every prior
plan's `MainWindow` task. Verified by a clean `dotnet build` here and the manual checklist in Task 11.

- [ ] **Step 1: Add the panel method**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add a new private method (placement: anywhere
among the other `Draw*` methods, e.g. immediately before `DrawScanTab`):

```csharp
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
            "quit mid-Apply or mid-Restore. Continuing it or fully rolling it back isn't supported yet. For " +
            "now, you can accept whatever Penumbra currently has as the correct state and move on - this " +
            "does not undo or redo any moves, it only stops the plugin from blocking further actions.");

        if (ImGui.Button("Keep Current State"))
            ImGui.OpenPopup("Keep current state?");

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

        ImGui.Spacing();
        ImGui.Separator();
    }
```

`ImGuiColors` is already used elsewhere in this file (the History tab's Restore preview popup) — no
new `using` needed.

- [ ] **Step 2: Call it at the top of `Draw()`**

In the same file, change:

```csharp
    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        if (_lastError != null)
            ImGui.TextColored(PluginTheme.CollisionBad, _lastError);
```

to:

```csharp
    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        DrawRecoveryPanelIfNeeded();

        if (_lastError != null)
            ImGui.TextColored(PluginTheme.CollisionBad, _lastError);
```

The rest of `Draw()` (the tab bar and everything inside it) is unchanged — the existing `CanStartApply`/
`CanScan`/etc. `false` values already gray out the now-blocked controls via each tab's existing
`ImGui.BeginDisabled` pattern, so there's no need to hide the tabs outright.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: 0 errors, no new warnings beyond the accepted baseline.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests still pass. This is the "after the UI wiring stage" full-suite checkpoint.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add crude recovery panel to MainWindow"
```

---

### Task 11: Full-suite verification and manual in-game checklist

**Files:** None modified — verification only.

- [ ] **Step 1: Full clean build**

Run: `dotnet build --no-incremental`
Expected: 0 errors. Note the exact warning count and compare against the baseline recorded at
worktree setup — no new warnings should appear (Plan C's own experience: an incremental build can
under-report warnings for unchanged files, so use `--no-incremental` here for an authoritative count).

- [ ] **Step 2: Full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests pass (baseline count plus this plan's new tests: 2 in Task 1, 6 in Task 2, 7 in
Task 3, 5 in Task 4, 4 in Task 5, 7 in Task 6, 4 in Task 7, 3 in Task 8 — 38 new tests total).

- [ ] **Step 3: Confirm the working tree is clean**

Run: `git status --short`
Expected: no output (all tests write to `Directory.CreateTempSubdirectory()` outside the repo and
delete in `finally`).

- [ ] **Step 4: Write out the manual in-game checklist**

This plan cannot be fully verified without a running FFXIV/Dalamud/Penumbra instance — write out this
checklist for the user to run themselves (same limitation as every prior plan). Confirm each item:

1. **Clean startup, unaffected**: with no interrupted operations present, start the game. Confirm the
   plugin behaves exactly as before this plan — no recovery panel, all tabs usable.
2. **Crash-and-recover, single journal**: start an Apply or Restore on a real library, force-quit the
   game mid-operation (or otherwise interrupt it), then restart. Confirm the recovery panel appears
   with "Keep Current State," every other organizer action is disabled, and clicking it (through the
   confirmation popup) unblocks the plugin and refreshes the mod list.
3. **Panel persists across window toggles**: with a pending recovery, close and reopen the plugin
   window (or toggle other tabs) — confirm the panel keeps showing until resolved, not just on first
   open.
4. **Multi-root/cycle panel** (needs deliberately constructing this — hand-edit or duplicate a bundle
   directory under `operations/active/` to create two disconnected non-terminal journals, since this
   can't arise from ordinary use): confirm the panel shows the "Accept Current State and Close All
   Interrupted Operations" message and button instead of the single-journal one, and that clicking it
   resolves both and unblocks the plugin.
5. **Bundle relocation verification**: after either resolution above, inspect
   `%APPDATA%\XIVLauncher\pluginConfigs\PenumbraOrganizer.Plugin\operations\` and confirm the
   previously-`active/` bundle director(y/ies) now sit under `completed/` with `Resolution` set in
   their `journal.json`.
6. **Frame-hitch watching**: while a recovery is pending and classification is retrying (e.g. if
   Penumbra takes a moment to load), confirm the game does not freeze or stutter — the throttled,
   at-most-once-per-second `GetLiveMods()` call should be unnoticeable.

- [ ] **Step 5: Note the plan's own status**

No code change — this step is a reminder for whoever runs the checklist to report back before this
plan is considered fully done, matching every prior plan's "not yet in-game verified" pattern until
someone actually runs the list above.
