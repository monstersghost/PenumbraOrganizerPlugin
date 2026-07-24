# Operation Restore Integration (Plan C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire `Restore` onto the same frame-budgeted `OperationController` engine Plan B2 already
wired `Apply` onto, replacing `Plugin.cs`'s synchronous `Restore(Guid)`/`ExecuteOrderedMoves` as the
History tab's entry point.

**Architecture:** `OperationController.StartApply`'s body has no Apply-specific logic besides its
admission guard - Task 3 extracts that guard into one shared, unit-testable predicate and adds a thin
`StartRestore` wrapper alongside it. `OperationPlanBuilder` gains a Restore-specific plan-construction
path (Task 2) that joins `RollbackHistory.BuildRestorePlan`'s move list with live mod names before
building an `OperationPlan`. A new `RestoreResultSeed` (Task 1) persists the classification data
(`Unchanged`/`SkippedUninstalled`/`RootRelocated` identifier lists, plus the full target
`RollbackSnapshot`) that would otherwise be discarded the moment `Plugin.StartRestoreOperation`
(Task 4) returns - Plan E needs this later and cannot reconstruct it from the plan/journal alone.
`MainWindow` (Task 5) gets a completion-detection block mirroring Apply's, gated by operation `Kind`
to prevent cross-tab bleed (a real bug that would otherwise exist on both tabs once they share one
controller). Task 6 marks the now-fully-superseded synchronous methods obsolete-as-error, sequenced
last so the build stays green at every checkpoint.

**Tech Stack:** C# / .NET, xUnit, Dalamud plugin framework, Penumbra IPC (`Penumbra.Api` 5.15.1).

## Global Constraints

- `dotnet build` must remain 0 warnings/errors at the end of every task, including Task 6's
  `[Obsolete(error: true)]` attributes - this is the actual proof those methods have zero remaining
  callers, not an assertion.
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC to force unit
  coverage onto `Plugin.cs`/`MainWindow.cs` - same documented limitation carried forward from Plan
  B1/B2. Those two files are verified by build + the manual checklist in Task 7 only.
- `PreviewRestore` and `RollbackHistory.BuildRestorePlan` are out of scope for modification - this
  plan consumes their existing output unchanged.
- `sealed record` for data types, `static class` for pure stateless logic - carried forward from Plan
  B1/B2's own conventions.
- Tasks are ordered so the repository is buildable and the full test suite passes at every task
  boundary (explicit user requirement): additive-only tasks first (1, 2), then the controller change
  (3) with its own full verification, then orchestration (4), then UI wiring (5), then dead-code
  marking last (6) once Task 5 has removed the last external caller.

---

### Task 1: `RestoreResultSeed`, its codec, and a new bundle path

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/RestoreResultSeed.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundlePaths.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RestoreResultSeedCodecTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundlePathsTests.cs` (extend existing test)

**Interfaces:**
- Consumes: `RollbackSnapshot` (`PenumbraOrganizer.Plugin.Organizer`, existing, unqualified from
  `Organizer.Operations` files), `AtomicFile.CreateOrReplace`/`TryReadValidated` (existing).
- Produces: `RestoreResultSeed` record and `OperationRestoreResultSeedCodec.Save`/`TryLoad`, consumed
  by Task 4's `Plugin.StartRestoreOperation`. `OperationBundlePaths.RestoreResultSeedPath(string
  bundleDirectory)`, also consumed by Task 4.

- [ ] **Step 1: Write the failing codec round-trip test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RestoreResultSeedCodecTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RestoreResultSeedCodecTests
{
    private static RollbackSnapshot SampleSnapshot() => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "a label",
        "auto description", new Dictionary<string, string> { ["mod-a"] = "Weapons/A" });

    private static RestoreResultSeed Sample() => new(
        SampleSnapshot(), ["mod-b"], ["mod-c"], ["mod-d"]);

    [Fact]
    public void Save_ThenTryLoad_RoundTripsAllFieldsIncludingTheFullTargetSnapshot()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            var seed = Sample();

            OperationRestoreResultSeedCodec.Save(path, seed);
            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.NotNull(result);
            Assert.Equal(seed.TargetSnapshot.Id, result!.TargetSnapshot.Id);
            Assert.Equal(seed.TargetSnapshot.ModPaths, result.TargetSnapshot.ModPaths);
            Assert.Equal(seed.UnchangedIdentifiers, result.UnchangedIdentifiers);
            Assert.Equal(seed.SkippedUninstalledIdentifiers, result.SkippedUninstalledIdentifiers);
            Assert.Equal(seed.RootRelocatedIdentifiers, result.RootRelocatedIdentifiers);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_FileDoesNotExist_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationRestoreResultSeedCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_CorruptJson_ReturnsFalseRatherThanThrowing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            File.WriteAllText(path, "{ not valid json");

            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_NullTargetSnapshot_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            File.WriteAllText(path, """{"TargetSnapshot":null,"UnchangedIdentifiers":[],"SkippedUninstalledIdentifiers":[],"RootRelocatedIdentifiers":[]}""");

            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_NullClassificationList_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            var snapshotJson = System.Text.Json.JsonSerializer.Serialize(SampleSnapshot());
            File.WriteAllText(path,
                $$"""{"TargetSnapshot":{{snapshotJson}},"UnchangedIdentifiers":null,"SkippedUninstalledIdentifiers":[],"RootRelocatedIdentifiers":[]}""");

            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RestoreResultSeedCodecTests`
Expected: build error (`RestoreResultSeed`/`OperationRestoreResultSeedCodec` do not exist yet).

- [ ] **Step 3: Create `RestoreResultSeed.cs`**

Create `PenumbraOrganizer.Plugin/Organizer/Operations/RestoreResultSeed.cs`:

```csharp
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// The parts of RollbackHistory.BuildRestorePlan's classification that don't fit OperationPlan's
/// schema, persisted into the operation's own bundle directory so a later plan can reconstruct the
/// full Moved/Unchanged/SkippedUninstalled/RootRelocated picture without depending on
/// organizer-history.json still holding the target entry or on Plugin.cs's local state surviving a
/// restart. "Moved" identifiers aren't repeated here - they're every identifier already present in
/// the accompanying OperationPlan's RecoveryTargets. RootRelocatedIdentifiers is a subset of those,
/// marking which moves target Penumbra's plain root rather than the snapshot's exact stored path.
/// TargetSnapshot carries the full RollbackSnapshot (not just its Id) for the same reason
/// OperationSnapshotCodec's own pre-restore snapshot copy does: self-contained, independent of
/// organizer-history.json (whose Delete action could otherwise leave a dangling reference).
/// </summary>
public sealed record RestoreResultSeed(
    RollbackSnapshot TargetSnapshot,
    IReadOnlyList<string> UnchangedIdentifiers,
    IReadOnlyList<string> SkippedUninstalledIdentifiers,
    IReadOnlyList<string> RootRelocatedIdentifiers);

/// <summary>
/// Mirrors OperationSnapshotCodec's shape: atomic write, TryLoad never throws. Validates structural
/// completeness (no null target snapshot or classification list), not cross-field semantics - e.g.
/// "every RootRelocated identifier is also a moved identifier" is a future reader's concern when it
/// interprets this file against the accompanying OperationPlan, not this codec's.
/// </summary>
public static class OperationRestoreResultSeedCodec
{
    public static void Save(string path, RestoreResultSeed seed) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(seed));

    public static bool TryLoad(string path, out RestoreResultSeed? seed)
    {
        seed = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        RestoreResultSeed? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<RestoreResultSeed>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.TargetSnapshot is null || candidate.UnchangedIdentifiers is null
            || candidate.SkippedUninstalledIdentifiers is null || candidate.RootRelocatedIdentifiers is null)
            return false;

        seed = candidate;
        return true;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RestoreResultSeedCodecTests`
Expected: 5/5 PASS.

- [ ] **Step 5: Add the bundle path, with a failing test first**

In `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundlePathsTests.cs`, extend the
existing test:

```csharp
    [Fact]
    public void JournalPlanSnapshotResults_AreNamedFilesUnderTheBundleDirectory()
    {
        var bundleDir = OperationBundlePaths.BundleDirectory(Root, active: true, OperationId);

        Assert.Equal(Path.Combine(bundleDir, "journal.json"), OperationBundlePaths.JournalPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "plan.json"), OperationBundlePaths.PlanPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "snapshot.json"), OperationBundlePaths.SnapshotPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "results.jsonl"), OperationBundlePaths.ResultsPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "restore-result-seed.json"), OperationBundlePaths.RestoreResultSeedPath(bundleDir));
    }
```

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter JournalPlanSnapshotResults_AreNamedFilesUnderTheBundleDirectory`
Expected: build error (`RestoreResultSeedPath` does not exist yet).

- [ ] **Step 6: Add the method**

In `PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundlePaths.cs`, add after `ResultsPath`:

```csharp
    public static string RestoreResultSeedPath(string bundleDirectory) => Path.Combine(bundleDirectory, "restore-result-seed.json");
```

- [ ] **Step 7: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds 0 warnings/errors; all tests pass (existing count plus 6 new).

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/RestoreResultSeed.cs PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundlePaths.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RestoreResultSeedCodecTests.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundlePathsTests.cs
git commit -m "feat: add RestoreResultSeed and its codec for the Restore operation bundle"
```

---

### Task 2: `OperationPlanBuilder` — `NamedModMove`, `BuildNamedMoves`, `BuildRestoreOperationPlan`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlanBuilder.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanBuilderTests.cs`

**Interfaces:**
- Consumes: `ModMove` (`PenumbraOrganizer.Plugin.Organizer`, existing, in `ApplyPlanner.cs`), `LiveMod`
  (`PenumbraOrganizer.Plugin.Organizer`, existing, in `RollbackHistory.cs`), `ApplyPlanner.OrderMovesForApply`
  (existing), `OperationPlan.Create` (existing - already rejects duplicate recovery-target identifiers
  via `targetByIdentifier.TryAdd` in `OperationPlan.cs`, confirmed by reading its `Validate` method;
  no new duplicate check is needed at this layer for that case).
- Produces: `NamedModMove` record, `OperationPlanBuilder.BuildNamedMoves`/`BuildRestoreOperationPlan`,
  both consumed by Task 4's `Plugin.StartRestoreOperation`.

- [ ] **Step 1: Write the failing tests**

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanBuilderTests.cs`, before
the final closing `}`:

```csharp
    private static LiveMod Live(string id, string name) => new(id, name, "", false);

    [Fact]
    public void BuildNamedMoves_HappyPath_ResolvesNamesFromCurrentMods()
    {
        var moves = new[] { new ModMove("mod-a", "Gear/A", "Weapons/A") };
        var currentMods = new[] { Live("mod-a", "Mod A") };

        var named = OperationPlanBuilder.BuildNamedMoves(moves, currentMods);

        Assert.Single(named);
        Assert.Equal("mod-a", named[0].Identifier);
        Assert.Equal("Mod A", named[0].ModName);
        Assert.Equal("Gear/A", named[0].CurrentPath);
        Assert.Equal("Weapons/A", named[0].TargetPath);
    }

    [Fact]
    public void BuildNamedMoves_MoveIdentifierNotInCurrentMods_ThrowsNamingTheIdentifier()
    {
        var moves = new[] { new ModMove("mod-a", "Gear/A", "Weapons/A") };
        var currentMods = Array.Empty<LiveMod>();

        var exception = Assert.Throws<InvalidOperationException>(() => OperationPlanBuilder.BuildNamedMoves(moves, currentMods));
        Assert.Contains("mod-a", exception.Message);
    }

    [Fact]
    public void BuildNamedMoves_DuplicateIdentifierInCurrentMods_ThrowsNamingTheDuplicates()
    {
        var moves = new[] { new ModMove("mod-a", "Gear/A", "Weapons/A") };
        var currentMods = new[] { Live("mod-a", "Mod A"), Live("mod-a", "Mod A Duplicate") };

        var exception = Assert.Throws<InvalidOperationException>(() => OperationPlanBuilder.BuildNamedMoves(moves, currentMods));
        Assert.Contains("mod-a", exception.Message);
    }

    private static NamedModMove Named(string id, string name, string currentPath, string targetPath) =>
        new(id, name, currentPath, targetPath);

    [Fact]
    public void BuildRestoreOperationPlan_IndependentMoves_ProducesOneStepPerMod()
    {
        var moves = new[]
        {
            Named("mod-a", "Mod A", "Weapons/A", "Gear/A"),
            Named("mod-b", "Mod B", "Weapons/B", "Gear/B"),
        };

        var plan = OperationPlanBuilder.BuildRestoreOperationPlan(moves);

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
    public void BuildRestoreOperationPlan_TwoWayCycle_ProducesATemporaryHopStep()
    {
        var moves = new[]
        {
            Named("X", "Mod X", "Gear/A", "Gear/B"),
            Named("Y", "Mod Y", "Gear/B", "Gear/A"),
        };

        var plan = OperationPlanBuilder.BuildRestoreOperationPlan(moves);

        Assert.Equal(3, plan.ExecutionSteps.Count); // temp hop + 2 final moves
        Assert.Contains(plan.ExecutionSteps, s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove);
        Assert.Equal(2, plan.RecoveryTargets.Count);
        // Recovery targets carry the real current/target paths, never a temporary cycle-breaking hop path.
        var targetX = plan.RecoveryTargets.Single(t => t.Identifier == "X");
        Assert.Equal("Gear/A", targetX.SnapshotRawPath);
        Assert.Equal("Gear/B", targetX.FinalRawPath);
    }

    [Fact]
    public void BuildRestoreOperationPlan_EmptyMoves_ProducesAValidZeroStepPlan()
    {
        var plan = OperationPlanBuilder.BuildRestoreOperationPlan([]);

        Assert.Empty(plan.ExecutionSteps);
        Assert.Empty(plan.RecoveryTargets);
        Assert.True(plan.Verify());
    }

    [Fact]
    public void BuildRestoreOperationPlan_DuplicateIdentifiers_ThrowsOperationPlansExistingDiagnostic()
    {
        var moves = new[]
        {
            Named("mod-a", "Mod A", "Gear/A", "Weapons/A"),
            Named("mod-a", "Mod A", "Gear/A", "Weapons/B"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => OperationPlanBuilder.BuildRestoreOperationPlan(moves));
        Assert.Contains("Duplicate recovery target identifier", exception.Message);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationPlanBuilderTests`
Expected: build error (`NamedModMove`/`BuildNamedMoves`/`BuildRestoreOperationPlan` do not exist yet).

- [ ] **Step 3: Implement `NamedModMove`, `BuildNamedMoves`, `BuildRestoreOperationPlan`**

Replace `PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlanBuilder.cs` with:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Pure glue between OrganizerState's rows and an OperationPlan - reuses the already-battle-tested
/// ApplyPlanner.OrderMovesForApply for cycle-breaking/ordering, then translates its ApplyStep
/// output (Identifier, TargetPath, IsTemporary, GroupId) into OperationExecutionStep, and each
/// touched row into one OperationRecoveryTarget (one per identifier, not one per execution step -
/// a cycle-breaking plan has more steps than targets). No Dalamud dependency - fully unit-tested.
/// </summary>
public static class OperationPlanBuilder
{
    public static OperationPlan BuildApplyPlan(IReadOnlyList<OrganizerModRow> touchedRows)
    {
        var moves = touchedRows
            .Select(r => new ModMove(r.Identifier, r.CurrentPath, r.ProposedPath))
            .ToList();
        var applySteps = ApplyPlanner.OrderMovesForApply(moves);

        var executionSteps = applySteps
            .Select((s, index) => new OperationExecutionStep(
                index, s.Identifier, s.TargetPath,
                s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
                s.GroupId))
            .ToList();

        var recoveryTargets = touchedRows
            .Select(r => new OperationRecoveryTarget(r.Identifier, r.CurrentPath, r.ProposedPath, r.Name))
            .ToList();

        return OperationPlan.Create(OperationType.Apply, executionSteps, recoveryTargets);
    }

    // currentMods is expected identifier-unique - the same invariant Plugin.cs's own
    // ReadCurrentModPaths() already relies on elsewhere (GetModListAdapter keys by Penumbra's own
    // directory identifier), but enforced explicitly here (unlike that existing call site) so a
    // violation fails with a clear diagnostic naming the offending identifiers, not a bare LINQ
    // ArgumentException from ToDictionary. Every move's identifier is guaranteed present in
    // currentMods by construction: RollbackHistory.BuildRestorePlan only ever emits a move for a
    // mod found in both the target snapshot and currentMods. The lookup below still throws with a
    // named identifier if that invariant is ever violated, rather than failing later with a bare
    // KeyNotFoundException.
    public static IReadOnlyList<NamedModMove> BuildNamedMoves(IReadOnlyList<ModMove> moves, IReadOnlyList<LiveMod> currentMods)
    {
        var duplicates = currentMods
            .GroupBy(m => m.Identifier, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Current mod list contains duplicate identifiers: {string.Join(", ", duplicates)}");

        var nameByIdentifier = currentMods.ToDictionary(m => m.Identifier, m => m.Name, StringComparer.Ordinal);
        return moves
            .Select(m => new NamedModMove(
                m.Identifier,
                nameByIdentifier.TryGetValue(m.Identifier, out var name)
                    ? name
                    : throw new InvalidOperationException($"Restore move for '{m.Identifier}' has no matching live mod."),
                m.CurrentPath, m.TargetPath))
            .ToList();
    }

    public static OperationPlan BuildRestoreOperationPlan(IReadOnlyList<NamedModMove> namedMoves)
    {
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
}

public sealed record NamedModMove(string Identifier, string ModName, string CurrentPath, string TargetPath);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationPlanBuilderTests`
Expected: all pass (existing 4 plus 8 new = 12/12).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds 0 warnings/errors; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlanBuilder.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanBuilderTests.cs
git commit -m "feat: add Restore plan construction to OperationPlanBuilder"
```

---

### Task 3: `OperationController` — extract `CanStartNext`, generalize `StartApply`/`StartRestore`, fix the recovery-admission gap

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: `OperationJournal.IsTerminal` (existing), `OperationPlan`/`OperationType` (existing).
- Produces: `OperationController.StartRestore(OperationPlan, Guid, string)` (same signature shape as
  `StartApply`), consumed by Task 4's `Plugin.StartRestoreOperation`. `OperationController.CanStartNext(OperationJournal, bool)`
  (`public static`, used internally by `PublishState` and the admission guard; also directly
  unit-tested below since the state it guards against - a terminal `Stage` co-occurring with
  `RequiresRecovery` - is not reachable through the real engine today, confirmed by reading every
  `RequiresRecovery = true` call site in this file: all four leave `Stage` at a non-terminal value,
  and `Resolution` is never set to anything but `None` anywhere in current code, so `journal.IsTerminal`
  can currently only become true via `TerminalStages.Contains(Stage)`, which never coincides with
  `RequiresRecovery == true` today. This becomes reachable once a future plan adds real recovery
  resolution logic - `CanStartNext` is tested directly against hand-constructed inputs for exactly
  that reason, not against a live engine run).

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`, after the
existing `StartApply_AfterAPriorOperationTerminated_IsAllowedAndOverwritesTheTerminalState` test:

```csharp
    [Fact]
    public void CanStartNext_TerminalStageWithoutRecovery_IsTrue()
    {
        var journal = new OperationJournal(
            OperationJournal.CurrentSchemaVersion, Guid.NewGuid(), OperationType.Apply,
            OperationStage.Completed, OperationResolution.None, null, false, DateTimeOffset.UtcNow,
            1, 1, "mod-a", Guid.NewGuid(), Guid.NewGuid(), "hash", null, DateTimeOffset.UtcNow);

        Assert.True(OperationController.CanStartNext(journal, requiresRecovery: false));
    }

    [Fact]
    public void CanStartNext_NonTerminalStage_IsFalse()
    {
        var journal = new OperationJournal(
            OperationJournal.CurrentSchemaVersion, Guid.NewGuid(), OperationType.Apply,
            OperationStage.Mutating, OperationResolution.None, null, false, DateTimeOffset.UtcNow,
            1, 0, null, Guid.NewGuid(), Guid.NewGuid(), "hash", null, DateTimeOffset.UtcNow);

        Assert.False(OperationController.CanStartNext(journal, requiresRecovery: false));
    }

    [Fact]
    public void CanStartNext_TerminalStageButRequiresRecovery_IsFalse()
    {
        // Not reachable through the real engine today (see this task's own notes), but the
        // predicate must still be correct on its own terms - this is the regression test for the
        // admission guard fix below, exercised directly rather than via a live engine run.
        var journal = new OperationJournal(
            OperationJournal.CurrentSchemaVersion, Guid.NewGuid(), OperationType.Apply,
            OperationStage.FailedPartiallyApplied, OperationResolution.None, null, false, DateTimeOffset.UtcNow,
            1, 1, "mod-a", Guid.NewGuid(), Guid.NewGuid(), "hash", null, DateTimeOffset.UtcNow);

        Assert.False(OperationController.CanStartNext(journal, requiresRecovery: true));
    }

    [Fact]
    public void StartRestore_ApplyTypePlan_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());

            Assert.Throws<ArgumentException>(() => controller.StartRestore(SinglePlan(type: OperationType.Apply), Guid.NewGuid(), dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartRestore_RestoreTypePlan_Succeeds()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());

            var exception = Record.Exception(() => controller.StartRestore(SinglePlan(type: OperationType.Restore), Guid.NewGuid(), dir.FullName));

            Assert.Null(exception);
            Assert.Equal(OperationStage.Mutating, controller.State.Stage);
            Assert.Equal(OperationType.Restore, controller.State.Kind);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartRestore_ZeroStepPlan_ReachesTerminalUiConsumedStateAfterTheUsualThreeUpdates()
    {
        // Update() advances at most one stage per call (Mutating, then Refreshing, then Verifying -
        // each its own "if (Stage == X) { ...; return; }" block in AdvanceActiveOperation), the same
        // as every non-empty-plan test in this file - a zero-step plan still needs all three calls,
        // it just has nothing to do during the Mutating one. Refreshing/Verifying still call into the
        // adapter even with zero recovery targets (confirmed empirically: with no adapter responses
        // enqueued, this reaches FailedBeforeMutation, not Completed), so both still need enqueuing,
        // just with an empty live-mod list since there's nothing to verify against.
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var emptyPlan = OperationPlan.Create(OperationType.Restore, [], []);
            var controller = NewController(adapter, new FakeClock());
            controller.StartRestore(emptyPlan, Guid.NewGuid(), dir.FullName);

            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Completed

            Assert.Equal(OperationStage.Completed, controller.State.Stage);
            Assert.Equal(OperationType.Restore, controller.State.Kind);
            Assert.True(controller.State.CanStartRestore);
            Assert.False(controller.State.RequiresRecovery);
            Assert.Equal(0, controller.State.ProcessedSteps);
            Assert.Equal(0, controller.State.TotalSteps);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: build error (`CanStartNext`/`StartRestore` do not exist yet).

- [ ] **Step 3: Extract `CanStartNext`, generalize the entry points, fix the guard**

In `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`, replace the existing
`StartApply` method (currently the sole public start method) with:

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

    private void StartOperation(OperationPlan plan, Guid snapshotId, string bundleDirectory, OperationType expectedType)
    {
        if (plan.Type != expectedType)
            throw new ArgumentException($"This entry point requires a {expectedType}-type plan; got {plan.Type}.", nameof(plan));
        if (_active is not null && !CanStartNext(_active.Journal, _active.RequiresRecovery))
            throw new InvalidOperationException("Another organizer operation is already in progress or requires recovery.");

        var checkpointer = new OperationCheckpointer(_clock, bundleDirectory);

        // journal.OperationId and journal.PlanId both reuse plan.OperationId: a plan and the
        // journal that executes it are always constructed together at StartApply time, so there is
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
        };
        _stopRequested = false;

        PublishState();
    }
```

This also fixes a pre-existing gap in the shipped guard, not something this task introduces: the
previous guard checked only `!_active.Journal.IsTerminal`, never `_active.RequiresRecovery`, even
though `PublishState`'s own `canStartNew` always combined both. Not live-reachable today (see this
task's Interfaces note), but the controller's own admission guard should be correct on its own terms.

- [ ] **Step 4: Update `PublishState` to use the shared predicate**

In the same file, in `PublishState()`, replace:

```csharp
        var canStartNew = journal.IsTerminal && !_active.RequiresRecovery;
```

with:

```csharp
        var canStartNew = CanStartNext(journal, _active.RequiresRecovery);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter OperationControllerTests`
Expected: all existing `StartApply_*` tests still pass unchanged, plus the 6 new tests pass.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds 0 warnings/errors; all tests pass (this is the "after each controller stage"
checkpoint - the repository must be fully green here before Task 4 begins).

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: generalize OperationController to StartRestore, fix recovery-admission guard"
```

---

### Task 4: `Plugin.cs` — `StartRestoreOperation`, exception-safety retrofit, `OnFrameworkUpdate` comment

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `OperationPlanBuilder.BuildNamedMoves`/`BuildRestoreOperationPlan` (Task 2),
  `OperationController.StartRestore` (Task 3), `RestoreResultSeed`/`OperationRestoreResultSeedCodec`/
  `OperationBundlePaths.RestoreResultSeedPath` (Task 1), `RollbackHistory.BuildRestorePlan`/
  `CaptureSnapshot`/`AppendSnapshot`/`Load` (existing, unchanged), `OperationController.State`
  (existing).
- Produces: `Plugin.StartRestoreOperation(Guid snapshotId)` (`internal`), consumed by Task 5's
  `MainWindow.RestoreSnapshot`.

**No automated test for this task** - Dalamud-coupled orchestration, same documented limitation as
`StartApplyOperation`. Verified by a clean `dotnet build` here and the manual checklist in Task 7.

- [ ] **Step 1: Add `StartRestoreOperation`**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add a new method immediately after the existing
`StartApplyOperation()` method (before `internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId)`):

```csharp
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
        var plan = Organizer.Operations.OperationPlanBuilder.BuildRestoreOperationPlan(namedMoves);

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

```

- [ ] **Step 2: Retrofit exception safety onto `StartApplyOperation`**

In the same file, in the existing `StartApplyOperation()` method, replace:

```csharp
        _operationInProgress = true;
        OperationController.StartApply(plan, snapshot.Id, bundleDirectory);
```

with:

```csharp
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
```

- [ ] **Step 3: Fix the `OnFrameworkUpdate` comment**

In the same file, in `OnFrameworkUpdate`, replace:

```csharp
    private void OnFrameworkUpdate(IFramework framework)
    {
        OperationController.Update();
        if (_operationInProgress && OperationController.State.CanStartApply)
            _operationInProgress = false; // the async Apply operation just reached a terminal stage
    }
```

with:

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

No logic changes in this step - `CanStartApply`/`CanStartRestore` are already provably the same value
(confirmed by reading `PublishState` in Task 3), so this reset already worked correctly for Restore
before this step; only the misleading comment is being fixed.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: 0 warnings, 0 errors. (No automated tests for this task - `Plugin.cs` is Dalamud-coupled.)

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests still pass (this task adds no new tests but must not break existing ones).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: add Plugin.StartRestoreOperation, retrofit Apply's exception safety"
```

---

### Task 5: `MainWindow` — wire the History tab, fix cross-tab status-text bleed on both tabs

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.StartRestoreOperation` (Task 4), `OperationStateSnapshot.Kind`/`CanStartRestore`/
  `CanStartApply`/`RequiresRecovery`/`ProcessedSteps`/`TotalSteps`/`Stage` (existing, `Kind` newly
  relied upon by this task).
- Produces: none consumed by later tasks - this is the last functional-wiring task before Task 6's
  cleanup.

**No automated test for this task** - ImGui rendering code, same documented limitation as Plan B2's
MainWindow task. Verified by a clean `dotnet build` here and the manual checklist in Task 7.

- [ ] **Step 1: Add the `_restoreOperationActive` field**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, near the existing `_applyOperationActive` field
declaration (currently `private bool _applyOperationActive;`), add immediately after it:

```csharp
    private bool _restoreOperationActive;
```

- [ ] **Step 2: Fix the Apply tab's existing status text to gate on `Kind`**

**This is a real regression this plan would otherwise introduce**, found while writing this task: the
existing Apply-tab status-text block only checks `operationState.Stage is not null`, never `Kind` -
once Restore shares the same `OperationController`, an in-progress or just-completed Restore would
make this block wrongly render as Apply progress/completion text. In the same file, replace:

```csharp
        // Deliberately minimal - the real progress UI and recovery dialog are Plan E's job. This
        // just keeps Apply usable and observable in-game now that it spans multiple frames.
        if (operationState.Stage is not null)
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

- [ ] **Step 3: Add the History tab's completion-detection block and status text**

In the same file, in `DrawHistoryTab()`, immediately after the opening guard clause:

```csharp
    private void DrawHistoryTab()
    {
        using var tab = ImRaii.TabItem("History");
        if (!tab)
            return;

```

add:

```csharp
        var operationState = _plugin.OperationController.State;
        if (_restoreOperationActive && operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.CanStartRestore)
        {
            _restoreOperationActive = false;
            _historyCache = null;
            RunScan();
        }

```

Then, at the end of `DrawHistoryTab()`, immediately before its closing `}` (after the existing
`_lastRestoreResults` display block), add:

```csharp

        if (_restoreOperationActive)
        {
            if (operationState.Kind == Organizer.Operations.OperationType.Restore && !operationState.CanStartRestore && !operationState.RequiresRecovery)
                ImGui.TextUnformatted($"Restoring... {operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage}).");
            else if (operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");
        }
```

- [ ] **Step 4: Rewrite `RestoreSnapshot` to call the async entry point**

In the same file, replace:

```csharp
    private void RestoreSnapshot(Guid snapshotId)
    {
        try
        {
            _lastRestoreResults = _plugin.Restore(snapshotId);
            _lastError = null;
            var byOutcome = _lastRestoreResults.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
            Plugin.Log.Information(
                "Restore completed: " + string.Join(", ", byOutcome.Select(kv => $"{kv.Value} {kv.Key}")));
            foreach (var failure in _lastRestoreResults.Where(r => r.Outcome == Organizer.RestoreOutcome.Failed))
                Plugin.Log.Warning($"Restore failure: {failure.Identifier}: {failure.FailureReason}");
        }
        catch (Exception ex)
        {
            _lastError = $"Restore failed: {ex.Message}";
            Plugin.Log.Error(ex, "Restore failed.");
        }

        _historyCache = null; // Restore() also captures a pre-restore snapshot — history changed
        RefreshOrphanedFolders(); // Restore() ran RunScan() internally — occupancy changed
    }
```

with:

```csharp
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
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests still pass (this is the "after each UI wiring stage" checkpoint).

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: wire Restore onto the async operation engine in MainWindow"
```

---

### Task 6: Mark the legacy synchronous methods `[Obsolete(error: true)]`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:** None - this task changes no signatures or behavior, only attributes on methods that,
after Task 5, have zero remaining callers outside each other.

- [ ] **Step 1: Confirm zero external callers**

Run: `grep -rn "\.ApplyChanges()\|\.Restore(" PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin/Plugin.cs`
Expected: no remaining call to `Plugin.ApplyChanges()` or `Plugin.Restore(Guid)` from `MainWindow.cs`
(both were replaced by `StartApplyOperation`/`StartRestoreOperation` in Plan B2 and this plan's Task
5 respectively). `Restore(Guid)`'s own body still calls `ExecuteOrderedMoves` internally - that is
expected and handled by Step 3 below.

- [ ] **Step 2: Add the attributes**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add `[Obsolete(...)]` immediately above each of the three
method declarations (do not change their bodies):

```csharp
    [Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
    internal IReadOnlyList<Organizer.ApplyResult> ApplyChanges()
```

```csharp
    [Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
    internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId)
```

```csharp
    [Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
    private Dictionary<string, string> ExecuteOrderedMoves(IReadOnlyList<Organizer.ModMove> moves)
```

- [ ] **Step 3: Build - confirm obsolete-calling-obsolete does not error**

Run: `dotnet build`
Expected: 0 warnings, 0 errors. `ApplyChanges()`'s and `Restore(Guid)`'s internal calls to
`ExecuteOrderedMoves(...)` do not trigger CS0619, because the C# compiler exempts a call from one
`[Obsolete(error: true)]` member to another (verified empirically during this plan's design phase via
a standalone repro compiled with `dotnet build`, not assumed). If this build step surprises that
expectation and produces CS0619 errors instead, do not suppress them - stop and report back rather
than adding `#pragma warning disable` or an `error: false` downgrade, since that would mean the
verified assumption was wrong somewhere specific to this codebase's actual call shape and needs
investigation, not silencing.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests still pass.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "chore: mark legacy synchronous Apply/Restore path obsolete-as-error"
```

---

### Task 7: Full-suite verification and manual in-game checklist

**Files:** None modified - verification only.

- [ ] **Step 1: Full clean build**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests pass (existing count plus this plan's ~19 new tests: 6 in Task 1, 8 in Task 2, 6
in Task 3, minus any overlap already accounted for above).

- [ ] **Step 3: Confirm the working tree is clean**

Run: `git status --short`
Expected: no output (all tests write to `Directory.CreateTempSubdirectory()` outside the repo and
delete in `finally`).

- [ ] **Step 4: Write out the manual in-game checklist**

This plan cannot be fully verified without a running FFXIV/Dalamud/Penumbra instance - write out this
checklist for the user to run themselves (same limitation as Plan B1/B2). Confirm each item:

1. **Restore happy path**: create a backup, sort mods to change several paths, Restore to the backup.
   Confirm mods move back, the History tab shows "Restoring... X/Y steps" while in flight, and the
   tab returns to normal (no stuck progress text) once complete.
2. **Restore with zero moves**: Restore to a snapshot that already matches current state exactly.
   Confirm it completes without hanging and without any other organizer operation staying blocked
   afterward (this is the zero-step path Task 3's test proved at the engine level - confirm it holds
   through the real Plugin.cs/MainWindow.cs wiring too).
3. **Restore mid-cycle**: Restore a snapshot that requires a two-way path swap (cycle-breaking).
   Confirm both mods end up at their correct final snapshot paths.
4. **Cross-feature blocking during an in-flight Restore**: while a Restore is in progress, attempt
   Apply, Scan, and Folder Cleanup from other tabs. Confirm all are blocked/disabled until the Restore
   reaches a terminal stage.
5. **Cross-tab status text**: start a Restore and immediately switch to the Apply tab while it's in
   flight. Confirm the Apply tab shows no progress/completion text (Task 5 Step 2's fix). Then do the
   reverse: start an Apply, switch to the History tab while it's in flight, confirm the History tab
   shows no Restore progress text.
6. **Frame-hitch watching**: during a Restore touching many mods, confirm the game does not freeze or
   stutter noticeably (same frame-budget behavior already confirmed for Apply in Plan B2).
7. **Bundle directory verification**: after a real Restore, inspect
   `%APPDATA%\XIVLauncher\pluginConfigs\PenumbraOrganizer.Plugin\operations\` for the operation's
   bundle directory and confirm `plan.json`, `snapshot.json`, and `restore-result-seed.json` all
   exist and are non-empty (Task 1/4's durability guarantee, holding up in a real run, not just in
   xUnit against a fake adapter).

- [ ] **Step 5: Update the plan doc's own status line**

No code change - this step is a reminder for whoever runs the checklist to report back before this
plan is considered fully done, matching Plan B2's own pattern of an explicit "not yet in-game
verified" note until someone actually runs the list above.
