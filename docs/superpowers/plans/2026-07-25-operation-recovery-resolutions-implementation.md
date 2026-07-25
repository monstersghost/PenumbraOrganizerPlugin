# Operation Recovery Resolutions (Plan D2): Continue and Restore Previous State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the two remaining recovery resolutions — **Continue** (finish an interrupted Apply/Restore
from where it left off) and **Restore Previous State** (abandon it, roll every mod back to its
pre-operation state) — completing the three-way resolution set the recovery system's design always
intended (Keep Current shipped in Plan D1).

**Architecture:** A pure `ContinuationPlanner` computes Continue's residual move set from a
`RecoveryAssessment`. `OperationPlanBuilder.BuildRestoreOperationPlan` generalizes to `BuildOperationPlan`
so Continue can build either an Apply-type or Restore-type plan. `OperationController` decouples its
live-mods read from plan validity (so Restore Previous State works even with a corrupt interrupted plan),
caches classification/live-read results only as advisory UI hints, and gains a dedicated,
failure-atomic "recovery successor" start path that bypasses the normal admission guard internally
without ever exposing that bypass publicly. Both new resolution methods take their own fresh live-mods
read at the moment of resolution rather than reusing a potentially stale cached one.

**Tech Stack:** C# / .NET, xUnit, Dalamud plugin (Penumbra IPC via a narrow `IPenumbraOperations`
interface — no direct Penumbra.Api dependency in any tested code).

**Design spec:** `docs/superpowers/specs/2026-07-25-operation-recovery-resolutions-design.md` (read in
full before starting — every task below implements a specific section of it).

## Global Constraints

- `dotnet build` must introduce no new warnings/errors beyond whatever the accepted baseline is at
  worktree setup — re-verify the baseline fresh at setup time, don't assume a prior plan's baseline
  still holds.
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC. Use the existing
  `FakePenumbraOperations` test double and the existing `FakeClock` (`IElapsedTimeSource`) pattern from
  `OperationControllerTests.cs` — do not introduce a new mocking framework.
- Every path comparison must use `PenumbraPathSemantics.AreEquivalent`/`Normalize`, never raw string
  equality — though this plan's new code mostly delegates to existing, already-correct comparison logic
  in `RecoveryClassifier`/`ApplyPlanner`/`RollbackHistory` rather than doing new comparisons itself.
- `RollbackHistory.BuildRestorePlan`/`CaptureSnapshot`, `ApplyPlanner.OrderMovesForApply` are out of
  scope for behavior changes — this plan consumes their existing output unchanged.
- `OperationPlanBuilder.BuildRestoreOperationPlan`'s rename to `BuildOperationPlan` (Task 2) is the one
  sanctioned exception to "no behavior changes to existing methods," and it is a pure signature
  generalization with zero behavior change at its one existing call site.
- Every new/changed method that can be reached from `OperationController.Update()` must not let an
  exception escape `Update()` itself — match the existing pattern at `OperationController.cs:349-396`
  (the `_pendingRecovery` classification-advance branch already has its own try/catch).
- File-per-responsibility: `ContinuationPlanner` is a new file, `PenumbraOrganizer.Plugin/Organizer/Operations/ContinuationPlanner.cs`. Everything else is a change to an existing file — do not create new
  files for the `OperationController`/`OperationPlanBuilder`/`Plugin`/`MainWindow` changes.

---

## Task 1: `ContinuationPlanner`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/ContinuationPlanner.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/ContinuationPlannerTests.cs`

**Interfaces:**
- Consumes: `OperationPlan`/`OperationRecoveryTarget` (`OperationPlan.cs`), `RecoveryAssessment`/
  `ItemRecoveryClassification`/`ItemRecoveryState` (`RecoveryAssessment.cs`/`RecoveryClassifier.cs`),
  `LiveModSnapshot`/`LiveMod` (`LiveModSnapshot.cs`), `NamedModMove` (`OperationPlanBuilder.cs`),
  `ApplyPlanner.OrderMovesForApply`/`ModMove` (`ApplyPlanner.cs`).
- Produces: `ContinuationPlanStatus`, `ContinuationBlockReason`, `ContinuationPlanResult`,
  `ContinuationPlanner.TryBuildResidualMoves(OperationPlan, RecoveryAssessment) -> ContinuationPlanResult`
  — consumed by Task 4's `ResolveContinue` and Task 3's `RecomputeResolutionAvailability`.

This is a pure, static, dependency-free class — no `OperationController` changes needed for this task.
Per design doc §3, revised after review point 5: it must never throw for a malformed
`OperationPlan`/`RecoveryAssessment` pairing, even though today's shipped code can't actually produce
one (verified: `OperationPlan.Create` already rejects duplicate `RecoveryTargets` identifiers, and
`RecoveryAssessment.LiveSnapshot` and `Classifications` are always built together by
`RecoveryAssessmentBuilder.Build` from the same snapshot).

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class ContinuationPlannerTests
{
    private static OperationPlan Plan(params OperationRecoveryTarget[] targets)
    {
        var steps = targets
            .Select((t, i) => new OperationExecutionStep(i, t.Identifier, t.FinalRawPath, OperationStepKind.FinalMove, i))
            .ToList();
        return OperationPlan.Create(OperationType.Apply, steps, targets);
    }

    private static RecoveryAssessment Assessment(
        IReadOnlyDictionary<string, LiveMod> mods, params ItemRecoveryClassification[] classifications) =>
        new(new LiveModSnapshot(mods, new HashSet<string>()), classifications, "irrelevant-fingerprint");

    [Fact]
    public void AtIntendedAndAtBoth_ProduceNoResidualMoves()
    {
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtIntended));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Ready, result.Status);
        Assert.Empty(result.ResidualMoves);
    }

    [Fact]
    public void AtSnapshot_ProducesAMoveFromLivePathToFinalRawPath()
    {
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Ready, result.Status);
        var move = Assert.Single(result.ResidualMoves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Gear/A", move.CurrentPath);
        Assert.Equal("Weapons/A", move.TargetPath);
    }

    [Theory]
    [InlineData(ItemRecoveryState.AtNeither)]
    [InlineData(ItemRecoveryState.MissingLive)]
    public void BlockingClassification_BlocksTheWholeResultNotJustThatIdentifier(ItemRecoveryState blockingState)
    {
        var targetA = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var targetB = new OperationRecoveryTarget("mod-b", "Gear/B", "Weapons/B", "mod-b");
        var plan = Plan(targetA, targetB);
        var mods = new Dictionary<string, LiveMod>
        {
            ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false),
            ["mod-b"] = new("mod-b", "mod-b", "Gear/B", false),
        };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot), // would otherwise be a valid move
            new ItemRecoveryClassification("mod-b", blockingState));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.BlockingClassificationPresent, result.Reason);
        Assert.Empty(result.ResidualMoves);
    }

    [Fact]
    public void AtKnownIntermediateCollision_BlocksWithReplanFailedRatherThanThrowing()
    {
        // Two residual moves that would collide on the same CurrentPath - ApplyPlanner.OrderMovesForApply's
        // own ToDictionary(m => m.CurrentPath) throws ArgumentException on this; TryBuildResidualMoves
        // must catch it and report Blocked/ReplanFailed, not let the exception escape.
        var targetA = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var targetB = new OperationRecoveryTarget("mod-b", "Gear/B", "Weapons/B", "mod-b");
        var plan = Plan(targetA, targetB);
        var mods = new Dictionary<string, LiveMod>
        {
            ["mod-a"] = new("mod-a", "mod-a", "Collision/Path", false),
            ["mod-b"] = new("mod-b", "mod-b", "Collision/Path", false), // same live path as mod-a
        };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot),
            new ItemRecoveryClassification("mod-b", ItemRecoveryState.AtSnapshot));

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.ReplanFailed, result.Reason);
    }

    [Fact]
    public void AllTargetsAtIntendedOrAtBoth_ProducesReadyWithEmptyMoveList()
    {
        var targetA = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var targetB = new OperationRecoveryTarget("mod-b", "Gear/B", "Weapons/B", "mod-b");
        var plan = Plan(targetA, targetB);
        var mods = new Dictionary<string, LiveMod>
        {
            ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false),
            ["mod-b"] = new("mod-b", "mod-b", "Weapons/B", false),
        };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtIntended),
            new ItemRecoveryClassification("mod-b", ItemRecoveryState.AtBoth));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Ready, result.Status);
        Assert.Empty(result.ResidualMoves);
    }

    [Fact]
    public void ClassificationWithNoMatchingRecoveryTarget_BlocksWithInconsistentRecoveryTargetsRatherThanThrowing()
    {
        // A classification identifier absent from the plan's own RecoveryTargets isn't reachable from
        // RecoveryClassifier.Classify (it iterates plan.RecoveryTargets itself) but the method must
        // still be total against this hand-constructed, deliberately-mismatched input.
        var plan = Plan(new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a"));
        var mods = new Dictionary<string, LiveMod> { ["mod-x"] = new("mod-x", "mod-x", "Gear/X", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-x", ItemRecoveryState.AtSnapshot));

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.InconsistentRecoveryTargets, result.Reason);
    }

    [Fact]
    public void DuplicateClassificationForSameIdentifier_BlocksWithInconsistentRecoveryTargetsRatherThanThrowing()
    {
        // Review point 5: two classifications for the same identifier would (indirectly) collide in
        // ApplyPlanner.OrderMovesForApply's own CurrentPath dictionary since both produce an identical
        // NamedModMove - but that's an indirect guarantee this method's own totality shouldn't depend
        // on another method's internals for. Checked explicitly instead.
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false) };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot),
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot)); // duplicate

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.InconsistentRecoveryTargets, result.Reason);
    }

    [Fact]
    public void OutOfRangeClassificationState_BlocksRatherThanBeingTreatedLikeAtIntended()
    {
        // Not reachable from real RecoveryClassifier.Classify output (a closed 6-value enum), but the
        // per-item switch must be an explicit, exhaustive decision rather than an implicit fallthrough
        // that silently treats any unrecognized value the same as AtIntended/AtBoth (skip, no move).
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-a", (ItemRecoveryState)99));

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.InconsistentRecoveryTargets, result.Reason);
    }
}
```

(9 test cases across these 8 methods: 7 `[Fact]`s each contributing 1, plus `BlockingClassification_
BlocksTheWholeResultNotJustThatIdentifier`'s `[Theory]` contributing 2 via its two `InlineData` rows.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter ContinuationPlannerTests`
Expected: build failure (`ContinuationPlanner` doesn't exist yet) — that counts as "fails for the right
reason" here since the type under test doesn't exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ContinuationPlanStatus { Ready, Blocked }

public enum ContinuationBlockReason { None, BlockingClassificationPresent, InconsistentRecoveryTargets, ReplanFailed }

public sealed record ContinuationPlanResult(
    ContinuationPlanStatus Status, IReadOnlyList<NamedModMove> ResidualMoves,
    ContinuationBlockReason Reason = ContinuationBlockReason.None);

/// <summary>
/// Design doc section 3. Computes the residual move set for Continue from an already-built
/// RecoveryAssessment - never re-classifies, never re-reads live state itself (the caller supplies
/// whichever assessment it wants evaluated). AtKnownIntermediate's validity is proven by attempting
/// ApplyPlanner.OrderMovesForApply on the full candidate set, the exact operation Continue would
/// actually perform - not by hand-enumerated consistency rules. Never throws for malformed input.
/// </summary>
public static class ContinuationPlanner
{
    public static ContinuationPlanResult TryBuildResidualMoves(OperationPlan interruptedPlan, RecoveryAssessment assessment)
    {
        // "Any blocking classification present" is enforced by the per-item switch below returning
        // Blocked immediately (discarding whatever candidateMoves had accumulated so far) rather than
        // by a separate upfront scan - one pass, same all-or-nothing result.
        var targetByIdentifier = new Dictionary<string, OperationRecoveryTarget>(StringComparer.Ordinal);
        foreach (var target in interruptedPlan.RecoveryTargets)
        {
            if (!targetByIdentifier.TryAdd(target.Identifier, target))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);
        }

        // A duplicate identifier in Classifications would indirectly collide in
        // ApplyPlanner.OrderMovesForApply's own CurrentPath dictionary below (both duplicate entries
        // resolve to the identical live path/target path), but checking explicitly keeps this
        // method's totality independent of that other method's internals.
        var classifiedIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var candidateMoves = new List<NamedModMove>();
        foreach (var classification in assessment.Classifications)
        {
            if (!classifiedIdentifiers.Add(classification.Identifier))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);

            // Exhaustive (defensive - RecoveryClassifier.Classify only ever produces these six named
            // values, so `default` is not reachable from real classification output today, but an
            // explicit switch makes that an enumerated decision, not an implicit fallthrough).
            switch (classification.State)
            {
                case ItemRecoveryState.AtIntended:
                case ItemRecoveryState.AtBoth:
                    continue; // already at the final target, nothing to queue
                case ItemRecoveryState.AtSnapshot:
                case ItemRecoveryState.AtKnownIntermediate:
                    break;
                case ItemRecoveryState.AtNeither:
                case ItemRecoveryState.MissingLive:
                    return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.BlockingClassificationPresent);
                default:
                    return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);
            }

            if (!targetByIdentifier.TryGetValue(classification.Identifier, out var target) ||
                !assessment.LiveSnapshot.Mods.TryGetValue(classification.Identifier, out var live))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);

            candidateMoves.Add(new NamedModMove(classification.Identifier, target.ModName, live.FullPath, target.FinalRawPath));
        }

        if (candidateMoves.Count == 0)
            return new ContinuationPlanResult(ContinuationPlanStatus.Ready, []); // every target already at its final path - a valid, empty Continue

        try
        {
            // The exact operation Continue would perform - not evaluated for its result here, only
            // for whether it throws. OperationPlanBuilder.BuildOperationPlan (Task 2) re-runs this
            // same call for real when Continue is actually resolved; this is a dry run to decide
            // whether that later call would itself succeed.
            ApplyPlanner.OrderMovesForApply(candidateMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList());
        }
        catch (ArgumentException)
        {
            return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.ReplanFailed);
        }

        return new ContinuationPlanResult(ContinuationPlanStatus.Ready, candidateMoves);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter ContinuationPlannerTests`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/ContinuationPlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/ContinuationPlannerTests.cs
git commit -m "feat: add ContinuationPlanner for Continue's residual-move computation"
```

---

## Task 2: `OperationPlanBuilder.BuildOperationPlan` generalization

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlanBuilder.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (one call site, `StartRestoreOperation`)
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanBuilderTests.cs`

**Interfaces:**
- Consumes: `NamedModMove` (already exists in this same file), `ApplyPlanner.OrderMovesForApply`,
  `OperationPlan.Create`.
- Produces: `OperationPlanBuilder.BuildOperationPlan(OperationType, IReadOnlyList<NamedModMove>) -> OperationPlan`
  — consumed by Task 4's `ResolveContinue`/`ResolveRestorePreviousState`.

Design doc §4: `BuildRestoreOperationPlan` already contains 100% of the logic Continue needs; the only
thing hardcoded to `OperationType.Restore` is the final `OperationPlan.Create` call. Rename and add a
type parameter — pure signature generalization, zero behavior change at the one existing call site.

- [ ] **Step 1: Update the existing tests to call the new signature**

In `OperationPlanBuilderTests.cs`, replace every `OperationPlanBuilder.BuildRestoreOperationPlan(moves)`
call with `OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, moves)` — same assertions,
same test names except the `BuildRestoreOperationPlan_*` prefix becomes `BuildOperationPlan_Restore*`.
Concretely, replace these four tests:

```csharp
    [Fact]
    public void BuildOperationPlan_RestoreType_IndependentMoves_ProducesOneStepPerMod()
    {
        var moves = new[]
        {
            Named("mod-a", "Mod A", "Weapons/A", "Gear/A"),
            Named("mod-b", "Mod B", "Weapons/B", "Gear/B"),
        };

        var plan = OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, moves);

        Assert.Equal(OperationType.Restore, plan.Type);
        Assert.Equal(2, plan.ExecutionSteps.Count);
        Assert.Equal(2, plan.RecoveryTargets.Count);
        Assert.All(plan.ExecutionSteps, s => Assert.Equal(OperationStepKind.FinalMove, s.Kind));
        var targetA = plan.RecoveryTargets.Single(t => t.Identifier == "mod-a");
        Assert.Equal("Weapons/A", targetA.SnapshotRawPath);
        Assert.Equal("Gear/A", targetA.FinalRawPath);
        Assert.Equal("Mod A", targetA.ModName);
    }

    [Fact]
    public void BuildOperationPlan_RestoreType_TwoWayCycle_ProducesATemporaryHopStep()
    {
        var moves = new[]
        {
            Named("X", "Mod X", "Gear/A", "Gear/B"),
            Named("Y", "Mod Y", "Gear/B", "Gear/A"),
        };

        var plan = OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, moves);

        Assert.Equal(3, plan.ExecutionSteps.Count); // temp hop + 2 final moves
        Assert.Contains(plan.ExecutionSteps, s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove);
        Assert.Equal(2, plan.RecoveryTargets.Count);
        var targetX = plan.RecoveryTargets.Single(t => t.Identifier == "X");
        Assert.Equal("Gear/A", targetX.SnapshotRawPath);
        Assert.Equal("Gear/B", targetX.FinalRawPath);
    }

    [Fact]
    public void BuildOperationPlan_RestoreType_EmptyMoves_ProducesAValidZeroStepPlan()
    {
        var plan = OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, []);

        Assert.Empty(plan.ExecutionSteps);
        Assert.Empty(plan.RecoveryTargets);
        Assert.True(plan.Verify());
    }

    [Fact]
    public void BuildOperationPlan_RestoreType_DuplicateIdentifiers_ThrowsOperationPlansExistingDiagnostic()
    {
        var moves = new[]
        {
            Named("mod-a", "Mod A", "Gear/A", "Weapons/A"),
            Named("mod-a", "Mod A", "Gear/A", "Weapons/B"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, moves));
        Assert.Contains("Duplicate recovery target identifier", exception.Message);
    }
```

Also add one new test proving the type parameter is honored, not hardcoded:

```csharp
    [Fact]
    public void BuildOperationPlan_ApplyType_ProducesAnApplyTypePlan()
    {
        var moves = new[] { Named("mod-a", "Mod A", "Weapons/A", "Gear/A") };

        var plan = OperationPlanBuilder.BuildOperationPlan(OperationType.Apply, moves);

        Assert.Equal(OperationType.Apply, plan.Type);
        Assert.Single(plan.ExecutionSteps);
        Assert.Single(plan.RecoveryTargets);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OperationPlanBuilderTests`
Expected: build failure (`BuildOperationPlan` doesn't exist yet, `BuildRestoreOperationPlan` calls
removed from the test file)

- [ ] **Step 3: Rename and generalize the method**

In `OperationPlanBuilder.cs`, replace:

```csharp
    public static OperationPlan BuildRestoreOperationPlan(IReadOnlyList<NamedModMove> namedMoves)
    {
        // Check for duplicate identifiers before processing, so OperationPlan.Create's own
        // diagnostic is visible (not masked by ApplyPlanner's dictionary insert error).
        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in namedMoves)
        {
            if (!seenIdentifiers.Add(m.Identifier))
                throw new InvalidOperationException($"Duplicate recovery target identifier '{m.Identifier}'.");
        }

        var moves = namedMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList();
        var restoreSteps = ApplyPlanner.OrderMovesForApply(moves);

        var executionSteps = restoreSteps
            .Select((s, index) => new OperationExecutionStep(
                index, s.Identifier, s.TargetPath,
                s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
                s.GroupId))
            .ToList();

        var recoveryTargets = namedMoves
            .Select(m => new OperationRecoveryTarget(m.Identifier, m.CurrentPath, m.TargetPath, m.ModName))
            .ToList();

        return OperationPlan.Create(OperationType.Restore, executionSteps, recoveryTargets);
    }
```

with:

```csharp
    // Generalized from BuildRestoreOperationPlan (Plan C) to also serve Continue's residual-move
    // plans (design doc section 4), which must match the interrupted operation's own type - an
    // interrupted Apply's Continue is itself an Apply-type plan.
    public static OperationPlan BuildOperationPlan(OperationType type, IReadOnlyList<NamedModMove> namedMoves)
    {
        // Check for duplicate identifiers before processing, so OperationPlan.Create's own
        // diagnostic is visible (not masked by ApplyPlanner's dictionary insert error).
        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in namedMoves)
        {
            if (!seenIdentifiers.Add(m.Identifier))
                throw new InvalidOperationException($"Duplicate recovery target identifier '{m.Identifier}'.");
        }

        var moves = namedMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList();
        var steps = ApplyPlanner.OrderMovesForApply(moves);

        var executionSteps = steps
            .Select((s, index) => new OperationExecutionStep(
                index, s.Identifier, s.TargetPath,
                s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
                s.GroupId))
            .ToList();

        var recoveryTargets = namedMoves
            .Select(m => new OperationRecoveryTarget(m.Identifier, m.CurrentPath, m.TargetPath, m.ModName))
            .ToList();

        return OperationPlan.Create(type, executionSteps, recoveryTargets);
    }
```

- [ ] **Step 4: Update the one existing call site**

In `Plugin.cs`, in `StartRestoreOperation`, replace:

```csharp
        var plan = Organizer.Operations.OperationPlanBuilder.BuildRestoreOperationPlan(namedMoves);
```

with:

```csharp
        var plan = Organizer.Operations.OperationPlanBuilder.BuildOperationPlan(Organizer.Operations.OperationType.Restore, namedMoves);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter OperationPlanBuilderTests`
Expected: PASS (all renamed tests plus the new Apply-type test)

Then run the full suite once to confirm the `Plugin.cs` call-site change didn't break anything that
references `StartRestoreOperation` indirectly:

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlanBuilder.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanBuilderTests.cs
git commit -m "refactor: generalize BuildRestoreOperationPlan to BuildOperationPlan(type, moves)"
```

---

## Task 3: `OperationController` — decouple live-read from plan validity, cache resolution availability

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: Task 1's `ContinuationPlanner.TryBuildResidualMoves`.
- Produces: `OperationController.RecoveryLiveReadStatus` enum; `PendingRecoveryContext.LiveSnapshot`,
  `.LiveReadStatus`, `.CanContinueRecovery`, `.CanRestorePreviousState` (all internal to this class, but
  Task 4 reads them directly since it's the same class); `OperationStateSnapshot.CanContinueRecovery`/
  `.CanRestorePreviousState` (public, consumed by Task 6's `MainWindow` wiring).

Design doc §2, §5 (the availability-caching half only — Task 4 covers the resolution methods
themselves). This task implements the resolved "option 1" decision (decouple the live-mods read from
plan validity) plus the review's two required corrections: a separate `RecoveryLiveReadStatus` state
machine instead of overloading `ClassificationStatus`, and caching `CanContinueRecovery`/
`CanRestorePreviousState` once per classification advance rather than recomputing them every
`PublishState()` call.

- [ ] **Step 1: Write the failing tests**

Add to `OperationControllerTests.cs` (uses the existing `NewControllerWithPendingRecovery`/`FakeClock`/
`FakePenumbraOperations` helpers already in that file):

```csharp
    [Fact]
    public void Update_PlanInvalidSnapshotValid_PopulatesLiveSnapshotButNotAssessment()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // plan.json intentionally not written - PlanCheckStatus becomes Missing
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update();

            Assert.Null(controller.GetRecoveryAssessment()); // no valid plan - Assessment stays null
            Assert.True(controller.State.CanRestorePreviousState);
            Assert.False(controller.State.CanContinueRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_PlanInvalidSnapshotValid_LiveReadKeepsRetryingAfterClassificationSettlesPermanently()
    {
        // Review point 3: an earlier draft of this test tried to write snapshot.json AFTER a first
        // Update() call and expected a later Update() to discover it - impossible against the real
        // implementation, since ArtifactStatusChecker only ever runs once per artifact
        // (SnapshotCheckStatus/PlanCheckStatus permanently leave Unchecked on their first check,
        // matching ArtifactStatusChecker's own documented "checked at most once" contract). What this
        // test actually needs to prove - that Update()'s outer gate keeps retrying the live read after
        // ClassificationStatus alone has already settled - doesn't need the artifact files to change
        // at all; only the live read itself needs to fail once, then succeed on retry.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // plan.json intentionally not written - PlanCheckStatus becomes Missing, ClassificationStatus
            // settles permanently to ClassificationUnavailable on the first Update() call below.
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.TemporarilyUnavailable, null));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));

            controller.Update(); // PlanCheckStatus=Missing -> ClassificationUnavailable; SnapshotCheckStatus=Valid; live read attempted -> TemporarilyUnavailable, LiveReadStatus stays WaitingForProvider
            Assert.False(controller.State.CanContinueRecovery);
            Assert.False(controller.State.CanRestorePreviousState);

            clock.Advance(TimeSpan.FromSeconds(1));
            controller.Update(); // ClassificationStatus already settled, but LiveReadStatus is still WaitingForProvider - if Update()'s outer gate incorrectly stopped calling TryAdvanceClassification once ClassificationStatus alone settled, this queued response would never be consumed and the test would fail with "no queued result"

            Assert.False(controller.State.CanContinueRecovery);
            Assert.True(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_BothPlanAndSnapshotInvalid_SettlesPermanentlyAndNeverCallsGetLiveMods()
    {
        // Review point 4: LiveReadStatus must settle to Unavailable here, not stay WaitingForProvider
        // forever - otherwise Update()'s outer gate (ClassificationStatus == WaitingForProvider ||
        // LiveReadStatus == WaitingForProvider) never closes, and TryAdvanceClassification keeps
        // getting called every single tick indefinitely even though it can do no useful work. Proven
        // two ways: many repeated Update() calls never reach the adapter at all (FakePenumbraOperations
        // throws if GetLiveMods() is called with nothing queued, matching
        // Update_CalledManyTimesWithinSameSecond_CallsGetLiveModsAtMostOnce's own established pattern),
        // and the clock is advanced past the retry throttle between calls so a still-open gate would
        // have every opportunity to attempt a (would-throw) IPC call.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations(); // nothing enqueued - a GetLiveMods() call would throw
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out _);
            // Neither plan.json nor snapshot.json written - both artifact checks resolve to Missing.

            for (var i = 0; i < 20; i++)
            {
                controller.Update();
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.False(controller.State.CanContinueRecovery);
            Assert.False(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_ValidPlanAndSnapshotClassifiedReady_CanContinueRecoveryBecomesTrue()
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
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)]))); // AtIntended - a valid, empty Continue

            controller.Update();

            Assert.True(controller.State.CanContinueRecovery);
            Assert.True(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_ValidPlanButBlockingClassification_CanContinueRecoveryStaysFalse()
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
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([]))); // mod-a missing live -> MissingLive, blocking

            controller.Update();

            Assert.NotNull(controller.GetRecoveryAssessment()); // classification itself still succeeded
            Assert.False(controller.State.CanContinueRecovery);
            Assert.True(controller.State.CanRestorePreviousState); // Restore Previous State is unaffected by Continue's blocking rule
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_DuplicateLiveIdentifiers_BothCanContinueAndCanRestoreStayFalse()
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
            var duplicateSnapshot = new LiveModSnapshot(
                new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false) },
                new HashSet<string> { "mod-a" }); // DuplicateIdentifiers non-empty
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, duplicateSnapshot));

            controller.Update();

            Assert.False(controller.State.CanContinueRecovery);
            Assert.False(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OperationControllerTests`
Expected: build failure (`CanContinueRecovery`/`CanRestorePreviousState` don't exist on
`OperationStateSnapshot` yet)

- [ ] **Step 3: Implement the decoupling and availability caching**

In `OperationController.cs`, add the new enum right after `RecoveryClassificationStatus` (currently
line 67):

```csharp
    public enum RecoveryClassificationStatus { WaitingForProvider, Classified, ClassificationUnavailable }

    public enum RecoveryLiveReadStatus { WaitingForProvider, Available, Unavailable }
```

Add four new members to `PendingRecoveryContext` (currently lines 69-81):

```csharp
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
        public LiveModSnapshot? LiveSnapshot { get; set; }
        public RecoveryLiveReadStatus LiveReadStatus { get; set; } = RecoveryLiveReadStatus.WaitingForProvider;
        public bool CanContinueRecovery { get; set; }
        public bool CanRestorePreviousState { get; set; }
    }
```

Add two new fields to `OperationStateSnapshot`'s record declaration and `Idle` static (currently
lines 10-41):

```csharp
public sealed record OperationStateSnapshot(
    OperationStage? Stage,
    OperationType? Kind,
    int ProcessedSteps,
    int TotalSteps,
    int ProcessedTargets,
    int SuccessfulTargets,
    int TotalTargets,
    string? LastProcessedIdentifier,
    string? LastProcessedDisplayName,
    string? LastError,
    bool RequiresRecovery,
    bool RecoveryClassificationPending,
    bool CanStartApply,
    bool CanStartRestore,
    bool CanScan,
    bool CanIndex,
    bool CanRunFolderCleanup,
    bool CanRunFolderCleanupRollback,
    bool CanCreateBackup,
    bool CanResolveRecovery,
    bool CanContinueRecovery,
    bool CanRestorePreviousState,
    bool CanRequestCancellation)
{
    public static OperationStateSnapshot Idle { get; } = new(
        Stage: null, Kind: null, ProcessedSteps: 0, TotalSteps: 0,
        ProcessedTargets: 0, SuccessfulTargets: 0, TotalTargets: 0,
        LastProcessedIdentifier: null, LastProcessedDisplayName: null, LastError: null,
        RequiresRecovery: false, RecoveryClassificationPending: false,
        CanStartApply: true, CanStartRestore: true, CanScan: true, CanIndex: true,
        CanRunFolderCleanup: true, CanRunFolderCleanupRollback: true, CanCreateBackup: true,
        CanResolveRecovery: false, CanContinueRecovery: false, CanRestorePreviousState: false,
        CanRequestCancellation: false);
}
```

Replace `Update()`'s outer gate (currently lines 351-366):

```csharp
    public void Update()
    {
        if (_pendingRecovery is { } pending &&
            (pending.ClassificationStatus == RecoveryClassificationStatus.WaitingForProvider ||
             pending.LiveReadStatus == RecoveryLiveReadStatus.WaitingForProvider))
        {
            try
            {
                TryAdvanceClassification(pending);
            }
            catch (Exception)
            {
                // Mirrors the _active operation's own exception boundary below - an unmodeled failure
                // here must not propagate out of Update() (this method has no caller-side safety net;
                // Plugin.cs's Framework.Update subscription doesn't wrap it either), and must not leave
                // classification stuck retrying the same throw every second indefinitely.
                pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
                pending.LiveReadStatus = RecoveryLiveReadStatus.Unavailable;
                PublishState();
            }
        }
```

(leave the rest of `Update()`, from `if (_active is null || _active.RequiresRecovery) return;` onward,
unchanged).

Replace `TryAdvanceClassification` in full (currently lines 398-452):

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

        // Classification (Continue) needs a valid Plan only - a missing/invalid Snapshot does not
        // block it. Restore Previous State's own availability depends only on the live read below,
        // never on plan validity (design doc section 2).
        if (pending.PlanCheckStatus != ArtifactCheckStatus.Valid)
            pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;

        // If NEITHER artifact is valid, no resolution could ever consume a live read - settle
        // LiveReadStatus permanently here too (mirroring ClassificationStatus's own permanent settle
        // just above), rather than leaving it WaitingForProvider forever. Without this, Update()'s
        // outer gate (ClassificationStatus == WaitingForProvider || LiveReadStatus ==
        // WaitingForProvider) would stay true indefinitely once ClassificationStatus alone had
        // already settled, calling this method every tick forever for no purpose (review point 4).
        var anyLiveConsumerAvailable = pending.PlanCheckStatus == ArtifactCheckStatus.Valid || pending.SnapshotCheckStatus == ArtifactCheckStatus.Valid;
        if (!anyLiveConsumerAvailable)
        {
            pending.LiveReadStatus = RecoveryLiveReadStatus.Unavailable;
            RecomputeResolutionAvailability(pending);
            if (stateChanged)
                PublishState();
            return;
        }

        // The live read backs both Continue's classification and Restore Previous State's own
        // availability - attempt it whenever either resolution could still use it, but only if it
        // hasn't already settled (a prior attempt may have resolved it to Available/Unavailable while
        // ClassificationStatus was still the one field keeping Update()'s gate open).
        if (pending.LiveReadStatus != RecoveryLiveReadStatus.WaitingForProvider)
        {
            if (stateChanged)
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
                pending.LiveSnapshot = liveResult.Snapshot;
                pending.LiveReadStatus = RecoveryLiveReadStatus.Available;
                if (pending.PlanCheckStatus == ArtifactCheckStatus.Valid)
                {
                    pending.Assessment = RecoveryAssessmentBuilder.Build(pending.Plan!, liveResult.Snapshot);
                    pending.ClassificationStatus = RecoveryClassificationStatus.Classified;
                }
                break;

            case LiveModReadStatus.TemporarilyUnavailable:
            case LiveModReadStatus.ProviderUnavailable:
                // Retryable at startup specifically - Penumbra may simply not have finished loading
                // yet. Both statuses already WaitingForProvider; nothing to change.
                break;

            case LiveModReadStatus.InvalidData:
            default:
                // A response that parsed but doesn't make sense won't be fixed by asking again.
                pending.LiveReadStatus = RecoveryLiveReadStatus.Unavailable;
                if (pending.PlanCheckStatus == ArtifactCheckStatus.Valid)
                    pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
                break;
        }

        RecomputeResolutionAvailability(pending);
        PublishState();
    }

    // Cached once here (called only when classification/live-read state actually changes), not
    // recomputed on every PublishState() call - design doc section 5, review points 10/11. These
    // booleans are advisory for UI button-enablement only: ResolveContinue/ResolveRestorePreviousState
    // (Task 4) always take their own fresh read and re-derive everything from it before committing.
    private static void RecomputeResolutionAvailability(PendingRecoveryContext pending)
    {
        pending.CanContinueRecovery = pending.ClassificationStatus == RecoveryClassificationStatus.Classified
            && pending.Assessment is not null
            && pending.LiveSnapshot!.DuplicateIdentifiers.Count == 0
            && ContinuationPlanner.TryBuildResidualMoves(pending.Plan!, pending.Assessment).Status == ContinuationPlanStatus.Ready;

        pending.CanRestorePreviousState = pending.SnapshotCheckStatus == ArtifactCheckStatus.Valid
            && pending.LiveReadStatus == RecoveryLiveReadStatus.Available
            && pending.LiveSnapshot!.DuplicateIdentifiers.Count == 0;
    }
```

Finally, wire the two new fields into `PublishState()`'s three branches (currently lines 572-626):
`Idle`'s own static already carries `CanContinueRecovery: false, CanRestorePreviousState: false` (set
above), so the `_blockedMultiRootGraph` branch's `Idle with { ... }` needs no changes (it doesn't
override these, so it inherits `false`). The `_pendingRecovery` branch needs one addition — replace:

```csharp
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
```

with:

```csharp
        if (_active is null) // _pendingRecovery is not null
        {
            var pending = _pendingRecovery!;
            State = OperationStateSnapshot.Idle with
            {
                RequiresRecovery = true,
                RecoveryClassificationPending = pending.ClassificationStatus == RecoveryClassificationStatus.WaitingForProvider,
                CanResolveRecovery = true, // Keep Current needs neither classification nor a valid plan/snapshot
                CanContinueRecovery = pending.CanContinueRecovery,
                CanRestorePreviousState = pending.CanRestorePreviousState,
                CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
                CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
            };
            return;
        }
```

And the `_active`-populated branch's full positional constructor call needs the two new named args
(both `false` — Continue/Restore Previous State are only ever available while a recovery is pending,
never while an ordinary operation is active) — replace:

```csharp
        State = new OperationStateSnapshot(
            Stage: journal.Stage, Kind: journal.Type,
            ProcessedSteps: journal.ProcessedStepCount, TotalSteps: journal.TotalSteps,
            ProcessedTargets: processedTargets, SuccessfulTargets: successfulTargets, TotalTargets: _active.Plan.RecoveryTargets.Count,
            LastProcessedIdentifier: journal.LastCompletedIdentifier,
            LastProcessedDisplayName: journal.LastCompletedIdentifier is { } id ? modNameByIdentifier.GetValueOrDefault(id) : null,
            LastError: null,
            RequiresRecovery: _active.RequiresRecovery, RecoveryClassificationPending: false,
            CanStartApply: canStartNew, CanStartRestore: canStartNew, CanScan: canStartNew, CanIndex: canStartNew,
            CanRunFolderCleanup: canStartNew, CanRunFolderCleanupRollback: canStartNew, CanCreateBackup: canStartNew,
            CanResolveRecovery: _active.RequiresRecovery,
            CanRequestCancellation: journal.Stage == OperationStage.Mutating && !_active.RequiresRecovery);
```

with:

```csharp
        State = new OperationStateSnapshot(
            Stage: journal.Stage, Kind: journal.Type,
            ProcessedSteps: journal.ProcessedStepCount, TotalSteps: journal.TotalSteps,
            ProcessedTargets: processedTargets, SuccessfulTargets: successfulTargets, TotalTargets: _active.Plan.RecoveryTargets.Count,
            LastProcessedIdentifier: journal.LastCompletedIdentifier,
            LastProcessedDisplayName: journal.LastCompletedIdentifier is { } id ? modNameByIdentifier.GetValueOrDefault(id) : null,
            LastError: null,
            RequiresRecovery: _active.RequiresRecovery, RecoveryClassificationPending: false,
            CanStartApply: canStartNew, CanStartRestore: canStartNew, CanScan: canStartNew, CanIndex: canStartNew,
            CanRunFolderCleanup: canStartNew, CanRunFolderCleanupRollback: canStartNew, CanCreateBackup: canStartNew,
            CanResolveRecovery: _active.RequiresRecovery,
            CanContinueRecovery: false, CanRestorePreviousState: false,
            CanRequestCancellation: journal.Stage == OperationStage.Mutating && !_active.RequiresRecovery);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter OperationControllerTests`
Expected: PASS — this includes every pre-existing `OperationControllerTests` test too (they must still
pass unchanged; the new fields default to `false`/unaffected in every scenario those tests exercise).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: decouple OperationController's live-mods read from plan validity"
```

---

## Task 4: `OperationController` — failure-atomic successor start, `ResolveContinue`, `ResolveRestorePreviousState`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: Task 1's `ContinuationPlanner.TryBuildResidualMoves`, Task 2's
  `OperationPlanBuilder.BuildOperationPlan`, `ArtifactStatusChecker.CheckPlan`/`CheckSnapshot` (existing,
  D1), `RollbackHistory.CaptureSnapshot`/`BuildRestorePlan`, `OperationPlanBuilder.BuildNamedMoves`.
- Produces: `OperationController.ResolveContinue()`, `OperationController.ResolveRestorePreviousState()`
  — consumed by Task 5's `Plugin.cs` wiring.

Design doc §5 (the resolution-methods half). This is the task that implements the review's two most
critical required changes: a dedicated, failure-atomic recovery-successor start path (never clearing
`_pendingRecovery` until the successor is confirmed durably active), and a fresh `GetLiveMods()` read
taken at the moment of resolution rather than reused from classification time.

- [ ] **Step 1: Write the failing tests**

Add to `OperationControllerTests.cs`:

```csharp
    private static (OperationController Controller, Guid JournalId, string BundleDirectory) SetUpPendingContinue(
        FakePenumbraOperations adapter, FakeClock clock, string operationsRoot, OperationPlan interruptedPlan)
    {
        var controller = NewControllerWithPendingRecovery(adapter, clock, operationsRoot, out var journalId);
        var bundleDirectory = OperationBundlePaths.BundleDirectory(operationsRoot, active: true, journalId);
        OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), interruptedPlan);
        return (controller, journalId, bundleDirectory);
    }

    [Fact]
    public void ResolveContinue_NoPendingRecovery_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());
    }

    [Fact]
    public void ResolveContinue_HappyPath_StartsSuccessorAndResolvesTheInterruptedJournal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, journalId, bundleDirectory) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            // mod-a is still at the snapshot path - a real, non-empty residual move.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)])));

            controller.ResolveContinue();

            Assert.Equal(OperationStage.Mutating, controller.State.Stage);
            Assert.Equal(OperationType.Apply, controller.State.Kind); // same type as the interrupted operation
            Assert.False(controller.State.RequiresRecovery);
            Assert.False(Directory.Exists(bundleDirectory)); // relocated out of active/
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var resolved));
            Assert.Equal(OperationResolution.ContinuedByNewOperation, resolved!.Resolution);
            Assert.NotNull(resolved.SuccessorOperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_UsesAFreshLiveReadNotACachedOne()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, _) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);

            // Classification-time read: mod-a already at its final path (AtIntended) - a valid,
            // empty Continue, cached as CanContinueRecovery = true with zero residual moves.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
            controller.Update();
            Assert.True(controller.State.CanContinueRecovery);

            // Resolution-time read: mod-a has since moved back to the snapshot path - a real residual
            // move now exists. If ResolveContinue reused the cached (empty) result instead of taking
            // its own fresh read, the successor would incorrectly be a zero-step plan.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)])));

            controller.ResolveContinue();

            Assert.Equal(1, controller.State.TotalSteps); // the residual move the FRESH read implies, not zero
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_FreshReadShowsABlockingState_ThrowsEvenThoughCachedAvailabilityWasTrue()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, bundleDirectory) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);

            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
            controller.Update();
            Assert.True(controller.State.CanContinueRecovery);

            // mod-a has since been uninstalled entirely - MissingLive, blocking.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));

            Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());
            Assert.True(controller.State.RequiresRecovery); // _pendingRecovery untouched - still pending
            Assert.True(Directory.Exists(bundleDirectory)); // interrupted bundle never touched
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_FreshReadHasDuplicateIdentifiers_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, _) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            var duplicateSnapshot = new LiveModSnapshot(
                new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false) },
                new HashSet<string> { "mod-a" });
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, duplicateSnapshot));

            Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());
            Assert.True(controller.State.RequiresRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_LiveReadUnavailable_ThrowsAndLeavesPendingRecoveryIntact()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, bundleDirectory) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));

            Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());

            Assert.True(controller.State.RequiresRecovery);
            Assert.True(Directory.Exists(bundleDirectory));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_ZeroResidualMoves_StartsAndCompletesAZeroStepSuccessor()
    {
        // Every target already AtIntended - Continue is Ready with an empty move list. Proves the
        // same zero-step engine path Plan C's own StartRestore_ZeroStepPlan test already established
        // works correctly for Continue's own new call path too.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Restore,
                [new(0, "mod-a", "Gear/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Weapons/A", "Gear/A", "mod-a")]);
            var (controller, _, _) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)]))); // AtIntended
            adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));

            controller.ResolveContinue();
            Assert.Equal(OperationType.Restore, controller.State.Kind); // matches the interrupted operation's own type
            Assert.Equal(0, controller.State.TotalSteps);

            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Completed

            Assert.Equal(OperationStage.Completed, controller.State.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveRestorePreviousState_NoPendingRecovery_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveRestorePreviousState());
    }

    [Fact]
    public void ResolveRestorePreviousState_HappyPath_StartsARestoreSuccessorRegardlessOfInterruptedType()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // The interrupted operation was an Apply; Restore Previous State's successor must still
            // always be Restore-type (design doc section 1) - no plan.json needed at all.
            var targetSnapshot = new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto",
                new Dictionary<string, string> { ["mod-a"] = "Gear/A" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), targetSnapshot);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.ResolveRestorePreviousState();

            Assert.Equal(OperationType.Restore, controller.State.Kind);
            Assert.False(controller.State.RequiresRecovery);
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var resolved));
            Assert.Equal(OperationResolution.RestoredByNewOperation, resolved!.Resolution);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveRestorePreviousState_AvailableEvenWhenPlanIsInvalid()
    {
        // Proves section 2's decoupling holds all the way through to the actual resolution, not just
        // the cached CanRestorePreviousState flag.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // plan.json intentionally not written at all.
            var targetSnapshot = new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto",
                new Dictionary<string, string> { ["mod-a"] = "Gear/A" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), targetSnapshot);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            var exception = Record.Exception(() => controller.ResolveRestorePreviousState());

            Assert.Null(exception);
            Assert.Equal(OperationType.Restore, controller.State.Kind);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveRestorePreviousState_TargetHasIdentifierAbsentFromLive_SkipsItRatherThanFailing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var targetSnapshot = new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto",
                new Dictionary<string, string> { ["mod-a"] = "Gear/A", ["mod-gone"] = "Gear/Gone" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), targetSnapshot);
            // mod-gone is absent from live - RollbackHistory.BuildRestorePlan's SkippedUninstalledIdentifiers.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            var exception = Record.Exception(() => controller.ResolveRestorePreviousState());

            Assert.Null(exception);
            Assert.Equal(1, controller.State.TotalTargets); // only mod-a, mod-gone silently excluded (not failed)
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_StartOperationRejectsIt_DeletesTheOrphanedBundleAndLeavesPendingRecoveryIntact()
    {
        // Not reachable through the real engine today (an active, non-terminal operation and a
        // pending recovery are mutually exclusive in practice - recovery is only ever registered at
        // startup, before anything is active), but StartRecoverySuccessor's own admission guard
        // (StartOperation's "_active is not null && !CanStartNext" check, which
        // bypassPendingRecoveryLockout does NOT exempt) must still correctly reject this shape, and
        // its failure-cleanup path must
        // still run - forced here using the same "not reachable via the engine but must still be
        // correct on its own terms" pattern this file already uses for
        // CanStartNext_TerminalStageButRequiresRecovery_IsFalse.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewController(adapter, new FakeClock(), operationsRoot: dir.FullName);
            var otherPlan = SinglePlan("mod-z");
            controller.StartApply(otherPlan, Guid.NewGuid(), OperationBundlePaths.BundleDirectory(dir.FullName, active: true, otherPlan.OperationId)); // sets _active, non-terminal Mutating

            var journalId = Guid.NewGuid();
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = InterruptedJournal(journalId) });
            controller.RegisterDiscoveredRecovery(discovery); // sets _pendingRecovery alongside the already-set _active
            var interruptedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(interruptedBundleDirectory), interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)]))); // ResolveContinue's own fresh read

            var exception = Record.Exception(() => controller.ResolveContinue());

            Assert.IsType<InvalidOperationException>(exception);
            Assert.True(Directory.Exists(interruptedBundleDirectory)); // the interrupted bundle itself is never touched

            // The successor's own freshly-created bundle directory (plan.json/snapshot.json written
            // just before the rejected StartRecoverySuccessor call) must not survive the failure -
            // active/ must contain only otherPlan's bundle and the interrupted one, nothing extra.
            var activeDir = OperationBundlePaths.ActiveDirectory(dir.FullName);
            Assert.Equal(2, Directory.GetDirectories(activeDir).Length);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartRecoverySuccessor_BlockedMultiRootGraphPresent_StillRejects()
    {
        // Review point 1 (critical): the private bypass must exempt only _pendingRecovery, never
        // _blockedMultiRootGraph - D2 explicitly does not resolve the multi-root/cycle case (design
        // doc section 7), so a recovery successor must never be able to start while that lockout is
        // in effect. Not reachable through the real engine today (two separate
        // RegisterDiscoveredRecovery calls, one setting _blockedMultiRootGraph and another setting
        // _pendingRecovery, don't occur within one real startup discovery pass), but the guard must
        // still be correct on its own terms.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewController(adapter, new FakeClock(), operationsRoot: dir.FullName);

            var blockedIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var blockedDiscovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, blockedIds, blockedIds),
                blockedIds.ToDictionary(id => id, InterruptedJournal));
            controller.RegisterDiscoveredRecovery(blockedDiscovery); // sets _blockedMultiRootGraph

            var journalId = Guid.NewGuid();
            var pendingDiscovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = InterruptedJournal(journalId) });
            controller.RegisterDiscoveredRecovery(pendingDiscovery); // ALSO sets _pendingRecovery, alongside the still-set _blockedMultiRootGraph
            Assert.True(controller.IsBlockedByMultipleRoots);

            var interruptedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(interruptedBundleDirectory), interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)])));

            var exception = Record.Exception(() => controller.ResolveContinue());

            Assert.IsType<InvalidOperationException>(exception);
            Assert.True(controller.IsBlockedByMultipleRoots); // both lockouts remain exactly as they were
            Assert.True(Directory.Exists(interruptedBundleDirectory));
            var activeDir = OperationBundlePaths.ActiveDirectory(dir.FullName);
            Assert.Equal(1, Directory.GetDirectories(activeDir).Length); // only the interrupted bundle - no orphaned successor left behind
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OperationControllerTests`
Expected: build failure (`ResolveContinue`/`ResolveRestorePreviousState` don't exist yet)

- [ ] **Step 3: Implement the failure-atomic successor start and the two resolution methods**

In `OperationController.cs`, replace `StartApply`/`StartRestore`/`StartOperation` (currently lines
106-157):

```csharp
    public void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
        StartOperation(plan, snapshotId, bundleDirectory, OperationType.Apply);

    public void StartRestore(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
        StartOperation(plan, snapshotId, bundleDirectory, OperationType.Restore);

    // Shared by PublishState's canStartNew derivation and this admission guard, so the two can never
    // independently drift apart - previously each was written separately as its own inline boolean
    // expression. Public so it can be unit-tested directly against hand-constructed OperationJournal
    // values: the "terminal Stage co-occurring with RequiresRecovery" case this guards against is
    // not producible through the real engine today (every RequiresRecovery=true call site in this
    // class leaves Stage non-terminal), but the predicate must still be correct on its own terms,
    // not merely lucky given today's callers.
    public static bool CanStartNext(OperationJournal journal, bool requiresRecovery) =>
        journal.IsTerminal && !requiresRecovery;

    // bypassPendingRecoveryLockout is intentionally unreachable from StartApply/StartRestore's public
    // surface - only StartRecoverySuccessor (below) ever passes true, and only Task 4's
    // ResolveContinue/ResolveRestorePreviousState ever call that. An ordinary Apply/Restore must keep
    // being rejected while a recovery is pending; only the controlled recovery-resolution path itself
    // is allowed to bypass that lockout. Note this bypasses ONLY the _pendingRecovery half of the
    // guard - _blockedMultiRootGraph is never bypassable, by anything: D2 explicitly does not resolve
    // the multi-root/cycle case (design doc section 7), so a recovery successor must never be able to
    // start while that lockout is in effect.
    private void StartOperation(
        OperationPlan plan, Guid snapshotId, string bundleDirectory, OperationType expectedType,
        bool bypassPendingRecoveryLockout = false)
    {
        if (plan.Type != expectedType)
            throw new ArgumentException($"This entry point requires a {expectedType}-type plan; got {plan.Type}.", nameof(plan));

        var pendingRecoveryLocked = !bypassPendingRecoveryLockout && _pendingRecovery is not null;
        var blockedGraphLocked = _blockedMultiRootGraph is not null;
        if ((_active is not null && !CanStartNext(_active.Journal, _active.RequiresRecovery)) || pendingRecoveryLocked || blockedGraphLocked)
            throw new InvalidOperationException("Another organizer operation is already in progress or requires recovery.");

        var checkpointer = new OperationCheckpointer(_clock, bundleDirectory);

        // journal.OperationId and journal.PlanId both reuse plan.OperationId: a plan and the
        // journal that executes it are always constructed together when an operation is started, so there is
        // no meaningful distinction between "this operation's identity" and "this plan's identity"
        // for a freshly-started (non-resumed) operation.
        var preparedJournal = new OperationJournal(
            SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: plan.OperationId, Type: plan.Type,
            Stage: OperationStage.Prepared, Resolution: OperationResolution.None, SuccessorOperationId: null,
            CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: plan.ExecutionSteps.Count,
            ProcessedStepCount: 0, LastCompletedIdentifier: null, SnapshotId: snapshotId, PlanId: plan.OperationId,
            TargetHash: plan.IntegrityHash, RecoveryOfOperationId: null, UpdatedAt: DateTimeOffset.UtcNow);
        checkpointer.CheckpointIfDue(preparedJournal, force: true); // forced write on entering Prepared

        var mutatingJournal = preparedJournal with { Stage = OperationStage.Mutating, UpdatedAt = DateTimeOffset.UtcNow };
        checkpointer.CheckpointIfDue(mutatingJournal, force: true); // forced write on entering Mutating

        _active = new ActiveOperationContext
        {
            Journal = mutatingJournal,
            Plan = plan,
            Mutation = new PathMutationOperation(plan, _adapter, _clock, _diagnostics, bundleDirectory),
            Checkpointer = checkpointer,
            BundleDirectory = bundleDirectory,
        };
        _stopRequested = false;

        PublishState();
    }

    private void StartRecoverySuccessor(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
        StartOperation(plan, snapshotId, bundleDirectory, plan.Type, bypassPendingRecoveryLockout: true);
```

**Verified, not restructured (review point 6): `StartOperation`'s tail cannot throw after `_active` is
assigned, for a validly-constructed plan.** The only code after `_active = new ActiveOperationContext
{...}` is `_stopRequested = false; PublishState();`. `PublishState()` reads only already-in-memory data
(no I/O, no external calls); its one collision-prone call, `_active.Plan.RecoveryTargets.ToDictionary(t
=> t.Identifier, ...)`, can only throw on a duplicate identifier — an input shape `OperationPlan.
Create`'s own `Validate()` already rejects for every plan `newPlan` can be here (`OperationPlanBuilder.
BuildOperationPlan` always constructs it via `OperationPlan.Create`). This tail is unchanged from its
already-shipped Plan B1/C form — this plan only adds the guard parameter above it — so restructuring it
is out of scope; the invariant is documented here instead.

Add `ResolveContinue`, `ResolveRestorePreviousState`, `ReadFreshLiveModsOrThrow`,
`StartRecoverySuccessorOrThrow`, and `TryDeleteBundleDirectory` right after `ResolveKeepCurrent`
(currently ends at line 267, right before `AcceptAllAndCloseInterruptedOperations`):

Note the difference from a naive first draft: these re-check `ArtifactStatusChecker.CheckPlan`/
`CheckSnapshot` directly, rather than reading `pending.PlanCheckStatus`/`pending.Plan` (which are only
populated once `TryAdvanceClassification`'s async loop has run at least once). This removes any
dependency on classification having already advanced — matching the same "revalidate fresh, don't trust
a cache" principle already applied to the live-mods read below, and it's cheap (a small, synchronous,
side-effect-free file read):

```csharp
    public void ResolveContinue()
    {
        if (_pendingRecovery is not { } pending)
            throw new InvalidOperationException("No pending recovery to continue.");

        var (planStatus, plan) = ArtifactStatusChecker.CheckPlan(pending.BundleDirectory);
        if (planStatus != ArtifactCheckStatus.Valid || plan is null)
            throw new InvalidOperationException("No pending recovery with a valid plan to continue.");

        var freshSnapshot = ReadFreshLiveModsOrThrow();
        if (freshSnapshot.DuplicateIdentifiers.Count > 0)
            throw new InvalidOperationException("Continue is not available - live state has duplicate identifiers.");

        var freshAssessment = RecoveryAssessmentBuilder.Build(plan, freshSnapshot);
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, freshAssessment);
        if (result.Status != ContinuationPlanStatus.Ready)
            throw new InvalidOperationException("Continue is not available for the current live state.");

        var newPlan = OperationPlanBuilder.BuildOperationPlan(plan.Type, result.ResidualMoves);
        var newSnapshot = RollbackHistory.CaptureSnapshot(
            freshSnapshot.Mods.Values.ToList(), label: null,
            autoDescription: $"Snapshot before continuing interrupted operation {pending.Journal.OperationId}");

        StartRecoverySuccessorOrThrow(pending, newPlan, newSnapshot, OperationResolution.ContinuedByNewOperation);
    }

    public void ResolveRestorePreviousState()
    {
        if (_pendingRecovery is not { } pending)
            throw new InvalidOperationException("No pending recovery to restore.");

        var (snapshotStatus, targetSnapshot) = ArtifactStatusChecker.CheckSnapshot(pending.BundleDirectory);
        if (snapshotStatus != ArtifactCheckStatus.Valid || targetSnapshot is null)
            throw new InvalidOperationException("No pending recovery with a valid snapshot to restore.");

        var freshSnapshot = ReadFreshLiveModsOrThrow();
        if (freshSnapshot.DuplicateIdentifiers.Count > 0)
            throw new InvalidOperationException("Restore Previous State is not available - live state has duplicate identifiers.");

        var currentMods = freshSnapshot.Mods.Values.ToList();
        var restorePlan = RollbackHistory.BuildRestorePlan(targetSnapshot, currentMods);
        var namedMoves = OperationPlanBuilder.BuildNamedMoves(restorePlan.Moves, currentMods);
        var newPlan = OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, namedMoves);
        var newSnapshot = RollbackHistory.CaptureSnapshot(
            currentMods, label: null,
            autoDescription: $"Snapshot before restoring interrupted operation {pending.Journal.OperationId} to its prior state");

        StartRecoverySuccessorOrThrow(pending, newPlan, newSnapshot, OperationResolution.RestoredByNewOperation);
    }

    private LiveModSnapshot ReadFreshLiveModsOrThrow()
    {
        var result = _adapter.GetLiveMods();
        if (result.Status != LiveModReadStatus.Success || result.Snapshot is null)
            throw new InvalidOperationException("Live mod state is not currently available; try again shortly.");
        return result.Snapshot;
    }

    // Design doc section 5: the failure-atomic recovery-successor transaction. _pendingRecovery is
    // cleared only after StartRecoverySuccessor has durably activated the new operation - if anything
    // in the try block throws, _pendingRecovery is untouched and the interrupted operation is exactly
    // as recoverable as it was before this call.
    private void StartRecoverySuccessorOrThrow(
        PendingRecoveryContext expectedPending, OperationPlan newPlan, RollbackSnapshot newSnapshot,
        OperationResolution parentResolution)
    {
        // Defends the invariant, not a currently-reachable race: OperationController has no concurrent
        // entry points (same single-threaded Dalamud Update()/UI-callback model every other method
        // here already assumes). Guards a future refactor that introduces reentrancy.
        if (!ReferenceEquals(_pendingRecovery, expectedPending))
            throw new InvalidOperationException("The pending recovery changed before this resolution could start.");

        var newBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, newPlan.OperationId);
        try
        {
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(newBundleDirectory), newPlan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(newBundleDirectory), newSnapshot);
            StartRecoverySuccessor(newPlan, newSnapshot.Id, newBundleDirectory);
        }
        catch
        {
            TryDeleteBundleDirectory(newBundleDirectory);
            throw; // _pendingRecovery untouched - a failed attempt leaves recovery exactly as it was
        }

        // Reached only once the successor is durably active (StartOperation persisted Prepared and
        // Mutating checkpoints, force: true, before returning). Only now does clearing
        // _pendingRecovery become safe.
        var interruptedJournal = expectedPending.Journal;
        var interruptedBundleDirectory = expectedPending.BundleDirectory;
        _pendingRecovery = null;

        try
        {
            var resolvedInterruptedJournal = interruptedJournal with
            {
                Resolution = parentResolution,
                SuccessorOperationId = newPlan.OperationId,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(interruptedBundleDirectory), resolvedInterruptedJournal);
            TryRelocateToCompleted(interruptedBundleDirectory, resolvedInterruptedJournal);
        }
        catch (Exception ex)
        {
            // The successor is already durably running - the user's Continue/Restore request already
            // succeeded. Failing to decorate the parent journal is a housekeeping gap, not a
            // resolution failure: on next startup the successor's own RecoveryOfOperationId makes it,
            // not the stale parent, authoritative in OperationRecoveryGraph.Analyze regardless of
            // whether this write landed - nothing is silently lost, just not yet tidied up. Must not
            // rethrow: that would report "Continue failed" for a Continue that actually started.
            // Must not stay completely silent either though (review point 7) - logged via the same
            // Plugin.Log?.Warning pattern TryRelocateToCompleted already uses for its own best-effort
            // failures, below.
            Plugin.Log?.Warning(ex,
                $"{parentResolution} successor {newPlan.OperationId} started, but resolving the interrupted " +
                $"journal {interruptedJournal.OperationId} failed. It will be correctly picked up on next " +
                "startup via the successor's own RecoveryOfOperationId.");
        }
    }

    private static void TryDeleteBundleDirectory(string bundleDirectory)
    {
        try
        {
            if (Directory.Exists(bundleDirectory))
                Directory.Delete(bundleDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort. A bundle whose journal.json is missing or fails to load is skipped
            // outright by OperationBundleDiscovery.LoadNonTerminalActiveJournals (both call sites),
            // never treated as an interrupted operation needing recovery - a leftover journal-less
            // bundle here is inert disk clutter, not a correctness risk.
        }
    }
```

Also add `using PenumbraOrganizer.Plugin.Organizer;` at the top of `OperationController.cs` if not
already present (`RollbackHistory`/`RollbackSnapshot`/`LiveMod` live in the `Organizer` namespace, one
level up from `Organizer.Operations`) — check the existing `using` block first; `RollbackSnapshot` is
already referenced by `PendingRecoveryContext.Snapshot` today, so this should already be present.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter OperationControllerTests`
Expected: PASS — all pre-existing tests plus every new one from Task 3 and this task.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions anywhere else in the suite.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: add ResolveContinue/ResolveRestorePreviousState with a failure-atomic successor start"
```

---

## Task 5: `Plugin.cs` wiring

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: Task 4's `OperationController.ResolveContinue()`/`ResolveRestorePreviousState()`.
- Produces: `Plugin.ResolveContinue()`, `Plugin.ResolveRestorePreviousState()` — consumed by Task 6's
  `MainWindow` wiring.

Design doc §6 (the `Plugin.cs` half). Not unit-testable — this class has a direct Dalamud/`IFramework`
dependency, matching every prior plan's documented limitation (see Task 7's manual checklist instead).

- [ ] **Step 1: Add the two wrapper methods**

In `Plugin.cs`, immediately after `ResolveKeepCurrent()` (currently lines 534-538, right before
`AcceptAllAndCloseInterruptedOperations`):

```csharp
    internal void ResolveContinue()
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            OperationController.ResolveContinue();
        }
        catch
        {
            _operationInProgress = false;
            throw;
        }
        // No RunScan() here, unlike ResolveKeepCurrent/AcceptAll - this starts a new async operation,
        // which is polled to completion exactly like an ordinary Apply/Restore already is (Task 6's
        // MainWindow wiring) - RunScan() belongs there, not at the moment the operation merely starts.
    }

    internal void ResolveRestorePreviousState()
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            OperationController.ResolveRestorePreviousState();
        }
        catch
        {
            _operationInProgress = false;
            throw;
        }
    }
```

- [ ] **Step 2: Broaden `_operationInProgress`'s auto-clear condition**

Review point 2: today `_operationInProgress` only auto-clears once `OperationController.State.
CanStartApply` becomes true (an ordinary terminal completion). If the successor Continue/Restore
Previous State just started itself hits `RequiresRecovery` in-session (the same IPC-failure category
D1's `ResolveKeepCurrent` already handles for `_active` — `ResolveContinue`/`ResolveRestorePreviousState`
only ever target `_pendingRecovery`, never `_active.RequiresRecovery`, the same asymmetry D1 established:
Keep Current is the universal in-session fallback, Continue/Restore Previous State are not),
`CanStartApply` never becomes true on its own and `_operationInProgress` stays stuck until the user
resolves that in-session failure via Keep Current. While stuck, `CreateBackup`/`DeleteHistorySnapshot`
are incorrectly blocked for that same window even though the controller itself doesn't need them
blocked. `_operationInProgress` should mean "an operation is currently executing," not "some unresolved
operation history exists" — broaden the clear condition to match:

In `Plugin.cs`, in `OnFrameworkUpdate` (currently lines 118-128), replace:

```csharp
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
```

with:

```csharp
    private void OnFrameworkUpdate(IFramework framework)
    {
        OperationController.Update();
        if (_operationInProgress && (OperationController.State.CanStartApply || OperationController.State.RequiresRecovery))
            _operationInProgress = false; // any async organizer operation (Apply or Restore) just reached
                                           // a terminal, non-recovery stage - CanStartApply/CanStartRestore
                                           // are guaranteed equal today (PublishState derives both from one
                                           // shared canStartNew), so checking either detects completion of
                                           // either operation type. If a future plan ever splits them apart
                                           // per-type, this check must be revisited. Also clears on
                                           // RequiresRecovery (Plan D2, review point 2) - an operation that
                                           // needs recovery has stopped executing even though it isn't
                                           // terminal, and _operationInProgress should track "is something
                                           // executing," not "is there unresolved history."
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: wire Plugin.ResolveContinue/ResolveRestorePreviousState"
```

---

## Task 6: `MainWindow` wiring

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: Task 5's `Plugin.ResolveContinue()`/`ResolveRestorePreviousState()`, Task 3's
  `OperationStateSnapshot.CanContinueRecovery`/`.CanRestorePreviousState`, the existing
  `_applyOperationActive`/`_restoreOperationActive` fields and their existing completion blocks
  (`MainWindow.cs:576-584`, `:658-662`).

Design doc §6 (the `MainWindow` half, corrected after reading the real completion-detection wiring —
see the design doc's own note on this). Not unit-testable — `MainWindow` is pure Dalamud/ImGui UI code.
Verified manually per Task 7's checklist.

- [ ] **Step 1: Add the two wrapper methods**

In `MainWindow.cs`, immediately after `RestoreSnapshot` (currently ends around line 971, right before
`CreateBackup`):

Review point 8: these return `bool` rather than `void`, and the popup only closes on success — a
fresh-read rejection at click time (the button was enabled from a cached availability check, but the
revalidation done inside `ResolveContinue`/`ResolveRestorePreviousState` found a blocking condition)
should keep the confirmation dialog open with `_lastError` visible, not silently close it.

```csharp
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
```

- [ ] **Step 2: Add the two buttons to the recovery panel**

In `MainWindow.cs`'s `DrawRecoveryPanelIfNeeded()`, replace the single-resolution block (currently
lines 165-189):

```csharp
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

with:

```csharp
        ImGui.TextWrapped(
            "The plugin found a mod-organizing operation that didn't finish, likely from a crash or force-" +
            "quit mid-Apply or mid-Restore. You can accept whatever Penumbra currently has as the correct " +
            "state and move on, finish the interrupted operation from where it left off, or roll everything " +
            "back to how it was before the interrupted operation started.");

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
```

(`operationState` is already in scope at this point in the method — it's read at the top of
`DrawRecoveryPanelIfNeeded` as `var operationState = _plugin.OperationController.State;`.)

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: no new errors/warnings

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add Continue and Restore Previous State buttons to the recovery panel"
```

---

## Task 7: Full-suite verification + manual checklist

**Files:** None modified — verification only.

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test`
Expected: PASS, 0 failures. Note the total test count for the final whole-branch review.

- [ ] **Step 2: Run a full build**

Run: `dotnet build`
Expected: no new warnings/errors beyond whatever baseline was recorded at worktree setup.

- [ ] **Step 3: Write the manual in-game verification checklist**

This plan's `Plugin.cs`/`MainWindow.cs` wiring cannot be exercised by automated tests (no Dalamud host
in the test project, matching every prior plan's documented limitation). Record the following checklist
for a human to run in-game before this feature is considered verified (do not attempt to run it
yourself — no game client is available in this environment):

1. Start an Apply, force-quit the game mid-Mutating (or mid-Refreshing), relaunch. Confirm the recovery
   panel shows "Keep Current State", "Continue", and "Restore Previous State" buttons.
2. Click Continue. Confirm it finishes the interrupted Apply's remaining moves, the recovery panel
   disappears, and the Apply tab shows the successor's progress through to completion (proves Task 6's
   `_applyOperationActive` wiring fires `RunScan()` on the successor's own completion, not just the
   original).
3. Repeat step 1, but click Restore Previous State instead. Confirm every mod that was touched by the
   interrupted Apply ends up back at its pre-Apply path, and the History tab shows the successor's
   progress through to completion (proves the `_restoreOperationActive` wiring).
4. Repeat step 1 with an interrupted Restore instead of an Apply. Confirm Continue produces a
   Restore-type successor (not Apply) and the History tab (not the Apply tab) observes its completion.
5. Force-quit mid-Continue itself (after clicking Continue but before the successor finishes). Relaunch.
   Confirm the recovery panel now offers to resolve the *successor* operation, not the original
   interrupted one (proves the discovery graph correctly treats the successor as authoritative).
6. With Continue's or Restore Previous State's button visibly enabled, manually move a mod in Penumbra's
   own UI to create a duplicate-identifier or otherwise-blocking condition, then click the button.
   Confirm it fails cleanly with a visible error rather than silently doing nothing or corrupting state.

- [ ] **Step 4: Report the test count and baseline comparison**

State the final `dotnet test` pass count and confirm it matches (prior count) + (new tests added across
Tasks 1, 3, and 4) with zero unrelated regressions, ready for the final whole-branch review per
`superpowers:subagent-driven-development`.
