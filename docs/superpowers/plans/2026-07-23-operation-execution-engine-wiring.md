# Operation Execution Engine Wiring (Plan B2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the already-built, fully-unit-tested operation execution engine (Plan B1 — `OperationController`, `PathMutationOperation`, etc., all under `PenumbraOrganizer.Plugin/Organizer/Operations/`) into real Dalamud/Penumbra IPC, replacing `Plugin.cs`'s current synchronous `ApplyChanges()`/`ExecuteOrderedMoves()` body for the Apply path specifically.

**Architecture:** A real `PenumbraOperationsAdapter : IPenumbraOperations` wraps the actual Penumbra IPC subscribers (`GetModListAdapter`, `SetModPath`, `RedrawAll`), translating `Penumbra.Api.Enums.PenumbraApiEc` into the plugin's own `SetModPathStatus` via a small pure mapping class. `Plugin.cs` becomes the composition root: it constructs the adapter, a `StopwatchElapsedTimeSource` (already built), a `FileDiagnosticsSink` (already built), and one `OperationController`, then wires `Framework.Update += controller.Update` (unsubscribed in `Dispose`). A new `Plugin.StartApplyOperation()` replaces `ApplyChanges()` as the Apply button's entry point: it validates, captures the pre-apply `RollbackSnapshot` (as today), builds an `OperationPlan` from `OrganizerState` via a new pure `OperationPlanBuilder`, persists `plan.json`/`snapshot.json` into the operation's own bundle directory, then calls `OperationController.StartApply(...)` and returns immediately — the operation itself advances frame-budgeted across many subsequent `Framework.Update` ticks. `MainWindow` gets a deliberately minimal polling stub (not the final progress UI — that is Plan E's job): it reads `OperationController.State` every `Draw()` call, shows basic text while non-terminal, and detects the transition to terminal to refresh the same caches the old synchronous path used to refresh inline.

**Scope decisions (made explicitly before writing this plan, not assumptions):**
- **Apply only.** `OperationController` currently only has `StartApply` (Apply-typed plans). Restore stays on its existing synchronous `ExecuteOrderedMoves` path in this plan — wiring Restore onto the new engine is a separate, later plan (matches the design doc's own §13 sequencing, which treats "Restore integration" as its own follow-on). `ExecuteOrderedMoves` is **not removed** — `Plugin.Restore()` keeps using it.
- **Minimal MainWindow polling stub, not the final UI.** The real progress display / recovery dialog is explicitly Plan E's job (per Plan B1's own "what this plan does not cover" section). This plan's MainWindow change is deliberately crude: enough to keep Apply usable and observable in-game, nothing more.

**Tech Stack:** .NET (project SDK per `PenumbraOrganizer.Plugin.csproj`), `Penumbra.Api` 5.15.1 (already referenced), Dalamud SDK (`Dalamud.NET.Sdk` 15.0.0, already referenced), xUnit 2.5.3 for the pieces that can be unit-tested without Dalamud.

## Global Constraints

- **`Penumbra.Api.Enums.PenumbraApiEc` → `SetModPathStatus` mapping** (the full 23-member enum, confirmed via the shipped `Penumbra.Api.dll` v5.15.1 metadata — no XML doc comments exist per-member in this package, so this table is the authoritative source, not a guess):
  ```
  Success                       -> Success
  NothingChanged                -> NothingChanged
  ModMissing                    -> ModMissing
  InvalidGamePath                -> InvalidArgument
  InvalidManipulation            -> InvalidArgument
  InvalidArgument                -> InvalidArgument
  PathRenameFailed               -> PathRenameFailed
  SystemDisposed                 -> ProviderUnavailable
  everything else (CollectionMissing, OptionGroupMissing, OptionMissing,
  CharacterCollectionExists, LowerPriority, FileMissing, CollectionExists,
  AssignmentCreationDisallowed, AssignmentDeletionDisallowed, InvalidIdentifier,
  AssignmentDeletionFailed, TemporarySettingDisallowed, TemporarySettingImpossible,
  InvalidCredentials, CollectionInactive, UnknownError)
                                  -> Rejected
  ```
  `SetModPathStatus.InvalidState` has no `PenumbraApiEc` source reachable from `SetModPath` specifically — it is never produced by this mapping, matching `SetModPathStatus`'s own design (it exists for other adapter-level conditions, not this translation).
- **The adapter catches exactly `Dalamud.Plugin.Ipc.Exceptions.IpcError`** (confirmed via `Dalamud.dll` metadata — this is the base class of `IpcNotReadyError`, thrown when the target IPC method hasn't been registered yet, i.e. Penumbra isn't loaded/ready) and translates it to `ProviderUnavailable` (`SetModPathStatus.ProviderUnavailable` / `LiveModReadStatus.ProviderUnavailable` / `RefreshStatus.ProviderUnavailable` as appropriate). **Any other exception type is left to propagate uncaught** — `PathMutationOperation.Advance`'s own try/catch (already built, Plan B1) classifies an uncaught exception as `MutationStopReason.UnexpectedFatalException`, which is the conservative-by-default behavior this whole engine already relies on. Do not add a catch-all `catch (Exception)` in the adapter — that would silently reclassify genuine bugs as `ProviderUnavailable`.
- **`RequestPostMutationRefresh()` uses `Penumbra.Api.IpcSubscribers.RedrawAll.Invoke(RedrawType.Redraw)`** — confirmed via metadata: `RedrawAll`/`RedrawObject`/`RedrawCollectionMembers` all return `void`, not `PenumbraApiEc` (no error code to translate). `RedrawAll` is the right choice since a mod-path mutation isn't scoped to one game object. The adapter therefore only ever produces `RefreshStatus.Success` (call didn't throw) or `RefreshStatus.ProviderUnavailable` (an `IpcError` was thrown) — `TemporarilyUnavailable`/`InvalidState` are not reachable from this call shape and no code should assume otherwise.
- **Restore is out of scope.** Do not modify `Plugin.Restore()`, `ExecuteOrderedMoves` (still used by Restore), or `Organizer.RestorePlan`/`RestoreResult`/`RestoreOutcome`.
- **Only pure logic gets xUnit tests.** `SetModPathStatusMapper`, `OperationSnapshotCodec`, and `OperationPlanBuilder` have no Dalamud dependency and are fully unit-tested. `PenumbraOperationsAdapter`, the `Plugin.cs` composition-root wiring, and the `MainWindow.cs` changes cannot be unit-tested in this repo (no Dalamud test-double infrastructure, same limitation Plan B1 documented) — those tasks are verified by a clean `dotnet build` plus the Task 8 manual in-game checklist, not automated tests. Do not attempt to mock `IDalamudPluginInterface`/`IFramework` to force unit tests on these — that was explicitly rejected for this exact reason when Plan B1 was designed.
- **`_operationInProgress` (existing `Plugin.cs` field) becomes the cross-feature mutual-exclusion guard for the whole async Apply duration**, not just its synchronous kickoff — it is set `true` when `StartApplyOperation()` begins and only reset to `false` once `OperationController.State.CanStartApply` becomes `true` again (checked once per `Framework.Update` tick). This is necessary so `CreateBackup`/`DeleteHistorySnapshot`/`Restore`/`CleanUpFolders` (which already guard on this flag) correctly keep blocking for the operation's *entire* multi-frame duration, not just the instant `StartApplyOperation()` returns.
- **Frame budget: `TimeSpan.FromMilliseconds(2)`**, a documented starting value (leaves headroom in a 60fps ~16.6ms frame budget for everything else Dalamud/the game is doing that tick) — pending in-game profiling per Task 8's checklist, not asserted as definitively correct here.
- **`sealed record` for data types, `static class` for pure stateless logic, `sealed class` for stateful engines** (carried forward from Plan B1).
- Run the full suite with `dotnet test` from the repo root. Commit with `git add` on specific files only (never `git add -A`).

---

### Task 1: SetModPathStatusMapper — pure `PenumbraApiEc` → `SetModPathStatus` translation

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/SetModPathStatusMapper.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/SetModPathStatusMapperTests.cs`

**Interfaces:**
- Consumes: `Penumbra.Api.Enums.PenumbraApiEc` (external package, already referenced by the main project; the test project picks it up transitively via its existing `ProjectReference` to `PenumbraOrganizer.Plugin.csproj` — no test-csproj changes needed).
- Produces: `SetModPathStatusMapper.Map(PenumbraApiEc ec) : SetModPathStatus`, per the Global Constraints table above.

- [ ] **Step 1: Write the failing tests**

```csharp
using Penumbra.Api.Enums;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class SetModPathStatusMapperTests
{
    [Theory]
    [InlineData(PenumbraApiEc.Success, SetModPathStatus.Success)]
    [InlineData(PenumbraApiEc.NothingChanged, SetModPathStatus.NothingChanged)]
    [InlineData(PenumbraApiEc.ModMissing, SetModPathStatus.ModMissing)]
    [InlineData(PenumbraApiEc.InvalidGamePath, SetModPathStatus.InvalidArgument)]
    [InlineData(PenumbraApiEc.InvalidManipulation, SetModPathStatus.InvalidArgument)]
    [InlineData(PenumbraApiEc.InvalidArgument, SetModPathStatus.InvalidArgument)]
    [InlineData(PenumbraApiEc.PathRenameFailed, SetModPathStatus.PathRenameFailed)]
    [InlineData(PenumbraApiEc.SystemDisposed, SetModPathStatus.ProviderUnavailable)]
    [InlineData(PenumbraApiEc.CollectionMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.OptionGroupMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.OptionMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.CharacterCollectionExists, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.LowerPriority, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.FileMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.CollectionExists, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.AssignmentCreationDisallowed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.AssignmentDeletionDisallowed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.InvalidIdentifier, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.AssignmentDeletionFailed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.TemporarySettingDisallowed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.TemporarySettingImpossible, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.InvalidCredentials, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.CollectionInactive, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.UnknownError, SetModPathStatus.Rejected)]
    public void Map_EveryPenumbraApiEcMember_ReturnsTheDocumentedStatus(PenumbraApiEc ec, SetModPathStatus expected)
    {
        Assert.Equal(expected, SetModPathStatusMapper.Map(ec));
    }

    [Fact]
    public void Map_CoversEveryDefinedPenumbraApiEcMember()
    {
        // Regression guard: if a future Penumbra.Api version adds a new enum member, this test
        // fails loudly (falls through to Rejected via the switch's default arm, which is safe,
        // but the count mismatch below forces a human to consciously add a dedicated test case
        // and confirm Rejected is really the right default for the new member).
        var allMembers = Enum.GetValues<PenumbraApiEc>();
        Assert.Equal(23, allMembers.Length);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SetModPathStatusMapperTests`
Expected: FAIL — `SetModPathStatusMapper` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using Penumbra.Api.Enums;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Pure translation from the raw Penumbra.Api.Enums.PenumbraApiEc (23 members, no per-member XML
/// docs in the shipped package - see this plan's Global Constraints for the authoritative mapping
/// table, confirmed against Penumbra.Api 5.15.1's metadata directly) into the plugin's own
/// SetModPathStatus. Kept as its own pure static class - unlike PenumbraOperationsAdapter, which
/// wraps real IPC and cannot be unit-tested in this repo, this translation has zero Dalamud
/// dependency and is fully covered by SetModPathStatusMapperTests.
/// </summary>
public static class SetModPathStatusMapper
{
    public static SetModPathStatus Map(PenumbraApiEc ec) => ec switch
    {
        PenumbraApiEc.Success => SetModPathStatus.Success,
        PenumbraApiEc.NothingChanged => SetModPathStatus.NothingChanged,
        PenumbraApiEc.ModMissing => SetModPathStatus.ModMissing,
        PenumbraApiEc.InvalidGamePath => SetModPathStatus.InvalidArgument,
        PenumbraApiEc.InvalidManipulation => SetModPathStatus.InvalidArgument,
        PenumbraApiEc.InvalidArgument => SetModPathStatus.InvalidArgument,
        PenumbraApiEc.PathRenameFailed => SetModPathStatus.PathRenameFailed,
        PenumbraApiEc.SystemDisposed => SetModPathStatus.ProviderUnavailable,
        _ => SetModPathStatus.Rejected,
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SetModPathStatusMapperTests`
Expected: PASS (24 tests — 23 `[Theory]` cases + 1 member-count guard).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/SetModPathStatusMapper.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/SetModPathStatusMapperTests.cs
git commit -m "feat: add SetModPathStatusMapper translating PenumbraApiEc to SetModPathStatus"
```

---

### Task 2: OperationSnapshotCodec — persisting the pre-apply RollbackSnapshot into the bundle

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationSnapshotCodec.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationSnapshotCodecTests.cs`

**Interfaces:**
- Consumes: `Organizer.RollbackSnapshot` (existing, `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs:3-8`), `AtomicFile` (Plan A1).
- Produces: `OperationSnapshotCodec.Save(string path, RollbackSnapshot snapshot) : void`, `OperationSnapshotCodec.TryLoad(string path, out RollbackSnapshot? snapshot) : bool`. This is the durable copy of the operation's pre-mutation snapshot inside its own bundle directory (`snapshot.json`, path already defined by `OperationBundlePaths.SnapshotPath`) — separate from, but content-identical in shape to, the plugin's own `organizer-history.json` rollback list. Existing solely so an operation's own recovery data doesn't depend on `organizer-history.json` staying available/consistent (design doc §4a).

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationSnapshotCodecTests
{
    private static RollbackSnapshot Sample() => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "a label",
        "auto description", new Dictionary<string, string> { ["mod-a"] = "Weapons/A" });

    [Fact]
    public void Save_ThenTryLoad_RoundTripsExactly()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "snapshot.json");
            var snapshot = Sample();

            OperationSnapshotCodec.Save(path, snapshot);
            var loaded = OperationSnapshotCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.Equal(snapshot, result);
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
            var loaded = OperationSnapshotCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

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
            var path = Path.Combine(dir.FullName, "snapshot.json");
            File.WriteAllText(path, "{ not valid json");

            var loaded = OperationSnapshotCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_CreatesTheParentDirectoryIfMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "bundle", "snapshot.json");

            OperationSnapshotCodec.Save(path, Sample());

            Assert.True(File.Exists(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationSnapshotCodecTests`
Expected: FAIL — `OperationSnapshotCodec` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Persists the pre-mutation RollbackSnapshot into an operation's own bundle directory
/// (OperationBundlePaths.SnapshotPath) - a durable copy independent of organizer-history.json, so
/// an operation's own recovery data never depends on that separate file staying available or
/// consistent (design doc section 4a). Mirrors OperationJournalCodec/OperationPlanCodec's shape:
/// atomic write, TryLoad never throws.
/// </summary>
public static class OperationSnapshotCodec
{
    public static void Save(string path, RollbackSnapshot snapshot) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(snapshot));

    public static bool TryLoad(string path, out RollbackSnapshot? snapshot)
    {
        snapshot = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        RollbackSnapshot? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<RollbackSnapshot>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null)
            return false;

        snapshot = candidate;
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationSnapshotCodecTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationSnapshotCodec.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationSnapshotCodecTests.cs
git commit -m "feat: add OperationSnapshotCodec for the bundle's own snapshot.json"
```

---

### Task 3: OperationPlanBuilder — pure glue from OrganizerState rows to an OperationPlan

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlanBuilder.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanBuilderTests.cs`

**Interfaces:**
- Consumes: `Organizer.OrganizerModRow` (existing), `Organizer.ModMove`/`Organizer.ApplyPlanner.OrderMovesForApply`/`Organizer.ApplyStep` (existing, `PenumbraOrganizer.Plugin/Organizer/ApplyPlanner.cs`), `OperationPlan.Create`/`OperationExecutionStep`/`OperationRecoveryTarget`/`OperationStepKind`/`OperationType` (Plan A1).
- Produces: `OperationPlanBuilder.BuildApplyPlan(IReadOnlyList<OrganizerModRow> touchedRows) : OperationPlan`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationPlanBuilderTests
{
    private static OrganizerModRow Row(string id, string name, string currentPath, string proposedPath) => new()
    {
        Identifier = id, Name = name, Author = "", CurrentPath = currentPath, ProposedPath = proposedPath,
    };

    [Fact]
    public void BuildApplyPlan_IndependentMoves_ProducesOneStepPerMod()
    {
        var rows = new[]
        {
            Row("mod-a", "Mod A", "Gear/A", "Weapons/A"),
            Row("mod-b", "Mod B", "Gear/B", "Weapons/B"),
        };

        var plan = OperationPlanBuilder.BuildApplyPlan(rows);

        Assert.Equal(OperationType.Apply, plan.Type);
        Assert.Equal(2, plan.ExecutionSteps.Count);
        Assert.Equal(2, plan.RecoveryTargets.Count);
        Assert.All(plan.ExecutionSteps, s => Assert.Equal(OperationStepKind.FinalMove, s.Kind));
        var targetA = plan.RecoveryTargets.Single(t => t.Identifier == "mod-a");
        Assert.Equal("Gear/A", targetA.SnapshotRawPath);
        Assert.Equal("Weapons/A", targetA.FinalRawPath);
        Assert.Equal("Mod A", targetA.ModName);
    }

    [Fact]
    public void BuildApplyPlan_TwoWayCycle_ProducesATemporaryHopStep()
    {
        // X wants Y's current path and Y wants X's current path - ApplyPlanner.OrderMovesForApply
        // must break this cycle with a temporary hop, which this builder must faithfully translate
        // into an OperationStepKind.CycleBreakingTemporaryMove step.
        var rows = new[]
        {
            Row("X", "Mod X", "Gear/A", "Gear/B"),
            Row("Y", "Mod Y", "Gear/B", "Gear/A"),
        };

        var plan = OperationPlanBuilder.BuildApplyPlan(rows);

        Assert.Equal(3, plan.ExecutionSteps.Count); // temp hop + 2 final moves
        Assert.Contains(plan.ExecutionSteps, s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove);
        Assert.Equal(2, plan.RecoveryTargets.Count); // still one recovery target per identifier, not per step
    }

    [Fact]
    public void BuildApplyPlan_StepIndicesAreSequentialFromZero()
    {
        var rows = new[]
        {
            Row("mod-a", "Mod A", "Gear/A", "Weapons/A"),
            Row("mod-b", "Mod B", "Gear/B", "Weapons/B"),
            Row("mod-c", "Mod C", "Gear/C", "Weapons/C"),
        };

        var plan = OperationPlanBuilder.BuildApplyPlan(rows);

        Assert.Equal([0, 1, 2], plan.ExecutionSteps.Select(s => s.StepIndex).ToArray());
    }

    [Fact]
    public void BuildApplyPlan_EmptyRows_ProducesAValidZeroStepPlan()
    {
        var plan = OperationPlanBuilder.BuildApplyPlan([]);

        Assert.Empty(plan.ExecutionSteps);
        Assert.Empty(plan.RecoveryTargets);
        Assert.True(plan.Verify()); // OperationPlan.Create's own integrity hash still checks out
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationPlanBuilderTests`
Expected: FAIL — `OperationPlanBuilder` does not exist.

- [ ] **Step 3: Write the implementation**

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
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationPlanBuilderTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlanBuilder.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanBuilderTests.cs
git commit -m "feat: add OperationPlanBuilder translating OrganizerState rows into an OperationPlan"
```

---

### Task 4: PenumbraOperationsAdapter — the real IPenumbraOperations implementation

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/PenumbraOperationsAdapter.cs`

**No automated test for this task** — it wraps real Penumbra IPC subscribers and cannot be exercised without a running Dalamud/Penumbra instance. Verified by a clean `dotnet build` in this task, and by the manual in-game checklist in Task 8. Do not write xUnit tests against this class or attempt to mock `IDalamudPluginInterface`.

**Interfaces:**
- Consumes: `IPenumbraOperations`/`SetModPathResult`/`LiveModReadResult`/`RefreshResult` (Plan B1 Task 1), `SetModPathStatusMapper` (Task 1 of this plan), `LiveModSnapshotBuilder` (Plan A1), `Organizer.LiveMod`/`Organizer.HeliosphereDetector` (existing), `Penumbra.Api.IpcSubscribers.GetModListAdapter`/`SetModPath`/`RedrawAll`, `Penumbra.Api.Enums.PenumbraApiEc`/`RedrawType`, `Dalamud.Plugin.Ipc.Exceptions.IpcError`, `Dalamud.Plugin.IDalamudPluginInterface`.
- Produces: `PenumbraOperationsAdapter : IPenumbraOperations`, constructed with `(IDalamudPluginInterface pluginInterface)`.

- [ ] **Step 1: Write the implementation**

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// The real IPenumbraOperations implementation, wrapping the actual Penumbra IPC subscribers -
/// design doc section 2. Deliberately the only file in Organizer/Operations that references
/// Penumbra.Api/Dalamud types directly, keeping every other file in this folder (and everything
/// they depend on) Dalamud-free and unit-testable, per Plan B1's Task 1 design intent.
///
/// Every method catches exactly Dalamud.Plugin.Ipc.Exceptions.IpcError (thrown when Penumbra
/// hasn't registered the IPC endpoint - not loaded, or unloaded mid-operation) and translates it
/// to the corresponding ProviderUnavailable status. Any OTHER exception is deliberately left to
/// propagate uncaught: PathMutationOperation.Advance's own boundary (already built, Plan B1)
/// classifies an uncaught exception as MutationStopReason.UnexpectedFatalException, the
/// conservative-by-default behavior this whole engine relies on - catching everything here would
/// silently reclassify genuine bugs as "the provider is unavailable."
/// </summary>
public sealed class PenumbraOperationsAdapter : IPenumbraOperations
{
    private readonly GetModListAdapter _getModListAdapterIpc;
    private readonly SetModPath _setModPathIpc;
    private readonly RedrawAll _redrawAllIpc;

    public PenumbraOperationsAdapter(IDalamudPluginInterface pluginInterface)
    {
        _getModListAdapterIpc = new GetModListAdapter(pluginInterface);
        _setModPathIpc = new SetModPath(pluginInterface);
        _redrawAllIpc = new RedrawAll(pluginInterface);
    }

    public LiveModReadResult GetLiveMods()
    {
        try
        {
            using var modList = _getModListAdapterIpc.Invoke();
            var mods = modList.Select(mod => new LiveMod(
                mod.Identifier, mod.Name, mod.FullPath,
                HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath)));
            return new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build(mods));
        }
        catch (IpcError)
        {
            return new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null);
        }
    }

    public SetModPathResult SetModPath(string identifier, string targetPath)
    {
        try
        {
            var ec = _setModPathIpc.Invoke(identifier, targetPath, "");
            return new SetModPathResult(
                SetModPathStatusMapper.Map(ec), ec.ToString(),
                ec == PenumbraApiEc.Success ? null : $"Penumbra returned {ec}.");
        }
        catch (IpcError ex)
        {
            return new SetModPathResult(SetModPathStatus.ProviderUnavailable, null, ex.Message);
        }
    }

    public RefreshResult RequestPostMutationRefresh()
    {
        try
        {
            _redrawAllIpc.Invoke(RedrawType.Redraw);
            return new RefreshResult(RefreshStatus.Success);
        }
        catch (IpcError)
        {
            return new RefreshResult(RefreshStatus.ProviderUnavailable);
        }
    }
}
```

- [ ] **Step 2: Build and confirm no compile errors**

Run: `dotnet build`
Expected: build succeeds with no new warnings or errors introduced by this file.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/PenumbraOperationsAdapter.cs
git commit -m "feat: add PenumbraOperationsAdapter wrapping real Penumbra IPC"
```

---

### Task 5: Plugin.cs composition root — construct the controller, wire Framework.Update

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**No automated test for this task** — touches the Dalamud plugin composition root directly. Verified by a clean `dotnet build` and Task 8's manual checklist.

**Interfaces:**
- Consumes: `Organizer.Operations.OperationController` (Plan B1 Task 6), `Organizer.Operations.PenumbraOperationsAdapter` (Task 4 of this plan), `Organizer.Operations.StopwatchElapsedTimeSource` (Plan A1, already exists — zero new work), `Organizer.Operations.FileDiagnosticsSink`/`OperationBundlePaths.DiagnosticsLogPath` (Plan B1 Task 2 / Plan A2), `Dalamud.Plugin.Services.IFramework`.
- Produces: `Plugin.OperationController` (internal readonly field, read by `MainWindow` in Task 7), `Plugin.OperationsRoot` (private computed property, consumed by Task 6).

- [ ] **Step 1: Add the `IFramework` service and the `OperationController` field**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add to the `[PluginService]` block (currently lines 21-23):

```csharp
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
```

Add a new field near the existing `GetModListAdapterIpc`/`SetModPathIpc` declarations (currently lines 31-32):

```csharp
    internal readonly Organizer.Operations.OperationController OperationController;
```

- [ ] **Step 2: Add the `OperationsRoot` path property**

Add alongside the other path properties (near `HistoryFilePath`, currently line 317):

```csharp
    private string OperationsRoot => Path.Combine(PluginInterface.ConfigDirectory.FullName, "operations");
```

- [ ] **Step 3: Construct the adapter, diagnostics sink, and controller in the constructor**

In `Plugin()`'s constructor, immediately after the existing `SetModPathIpc = new Penumbra.Api.IpcSubscribers.SetModPath(PluginInterface);` line (currently line 56), add:

```csharp
        var operationsAdapter = new Organizer.Operations.PenumbraOperationsAdapter(PluginInterface);
        var operationsDiagnosticsSink = new Organizer.Operations.FileDiagnosticsSink(
            Organizer.Operations.OperationBundlePaths.DiagnosticsLogPath(OperationsRoot));
        OperationController = new Organizer.Operations.OperationController(
            operationsAdapter, new Organizer.Operations.StopwatchElapsedTimeSource(),
            operationsDiagnosticsSink, TimeSpan.FromMilliseconds(2));
```

- [ ] **Step 4: Wire `Framework.Update` and the completion-reset logic**

Immediately after the block from Step 3 (still inside the constructor, before the existing `CommandManager.AddHandler(...)` call), add:

```csharp
        Framework.Update += OnFrameworkUpdate;
```

Add a new private method (near `OnCommand`/`ToggleMainUi`, currently lines 101-103):

```csharp
    private void OnFrameworkUpdate(IFramework framework)
    {
        OperationController.Update();
        if (_operationInProgress && OperationController.State.CanStartApply)
            _operationInProgress = false; // the async Apply operation just reached a terminal stage
    }
```

- [ ] **Step 5: Unsubscribe in `Dispose`**

In `Plugin.Dispose()`, add alongside the existing unsubscriptions (currently lines 86-88):

```csharp
        Framework.Update -= OnFrameworkUpdate;
```

- [ ] **Step 6: Build and confirm no compile errors**

Run: `dotnet build`
Expected: build succeeds. `IFramework`/`Dalamud.Plugin.Services` is already `using`'d at the top of `Plugin.cs` (line 5) — no new `using` directives needed for this task.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: construct OperationController and wire Framework.Update in Plugin.cs"
```

---

### Task 6: Plugin.StartApplyOperation() — the new Apply entry point

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**No automated test for this task** — depends on live `OrganizerState`/Penumbra IPC/`OperationController`. Verified by a clean `dotnet build` and Task 8's manual checklist.

**Interfaces:**
- Consumes: `OperationController` (Task 5 of this plan), `Organizer.Operations.OperationPlanBuilder` (Task 3), `Organizer.Operations.OperationSnapshotCodec` (Task 2), `Organizer.Operations.OperationPlanCodec`/`OperationBundlePaths` (Plan A1/A2), `Organizer.RollbackHistory.CaptureSnapshot`/`AppendSnapshot` (existing), `Organizer.ApplyPlanner.FolderPathCollisions` (existing), `ReadCurrentMods`/`ReadExistingOrganizationFolderPaths`/`HistoryFilePath` (existing `Plugin.cs` members).
- Produces: `internal void StartApplyOperation()` — the Apply button's new entry point (consumed by `MainWindow` in Task 7). Does **not** remove `ApplyChanges()`/`ExecuteOrderedMoves()` — `ExecuteOrderedMoves` stays, used by `Restore()` only; `ApplyChanges()` itself becomes dead code this task does not call anywhere, and Task 7 stops calling it from `MainWindow` — it is left in place (not deleted) since removing it is a separate cleanup decision outside this plan's minimal-footprint scope, and deleting working code that's merely no longer wired up is not what this task is for.

- [ ] **Step 1: Write `StartApplyOperation()`**

Add this new method to `PenumbraOrganizer.Plugin/Plugin.cs`, near the existing `ApplyChanges()` method (currently starting at line 333) — place it immediately before or after `ApplyChanges()`:

```csharp
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
        OperationController.StartApply(plan, snapshot.Id, bundleDirectory);
    }
```

- [ ] **Step 2: Build and confirm no compile errors**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: add StartApplyOperation, the new frame-budgeted Apply entry point"
```

---

### Task 7: MainWindow — minimal polling stub for the new Apply path

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**No automated test for this task** — ImGui rendering code. Verified by a clean `dotnet build` and Task 8's manual checklist. This is deliberately a crude, minimal stub — the real progress UI/recovery dialog is Plan E's job, not this plan's.

**Interfaces:**
- Consumes: `Plugin.StartApplyOperation()` (Task 6), `Plugin.OperationController.State` (`OperationStateSnapshot`, Plan B1 Task 6 — fields used: `Stage`, `CanStartApply`, `RequiresRecovery`, `ProcessedSteps`, `TotalSteps`, `SuccessfulTargets`, `TotalTargets`).

- [ ] **Step 1: Add a field tracking whether this window kicked off an Apply that hasn't concluded yet**

Near the existing `private IReadOnlyList<Organizer.ApplyResult>? _lastApplyResults;` field (currently line 32), add:

```csharp
    private bool _applyOperationActive;
```

(`_lastApplyResults`/`Config.LastApply` are left as-is, not actively updated by the new path — this plan does not touch the diagnostics-summary/export surface; see "What this plan does not cover.")

- [ ] **Step 2: Detect the Apply operation's completion each frame, and refresh the same caches the old synchronous path used to refresh inline**

In the method that draws the Apply tab, immediately before the existing `ImGui.BeginDisabled(result.HasIssues);`/`var applyClicked = ImGui.Button("Apply");` block (currently lines 481-482), add:

```csharp
        var operationState = _plugin.OperationController.State;
        if (_applyOperationActive && operationState.CanStartApply)
        {
            _applyOperationActive = false;
            _historyCache = null; // StartApplyOperation() also captures a pre-apply snapshot - history changed
            RefreshOrphanedFolders(); // the completed Apply moved mods - occupancy changed
        }
```

- [ ] **Step 3: Disable the Apply button while an operation is non-terminal, and replace the static result display with a live poll**

Replace the existing block (currently lines 481-508):

```csharp
        ImGui.BeginDisabled(result.HasIssues);
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

        if (_lastApplyResults is not null)
        {
            var succeeded = _lastApplyResults.Count(r => r.Success);
            var failed = _lastApplyResults.Count - succeeded;
            ImGui.TextUnformatted($"Apply: {succeeded} succeeded, {failed} failed.");
            foreach (var failure in _lastApplyResults.Where(r => !r.Success))
                ImGui.TextColored(PluginTheme.CollisionBad, $"  {failure.Identifier}: {failure.FailureReason}");
        }
```

with:

```csharp
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

        // Deliberately minimal - the real progress UI and recovery dialog are Plan E's job. This
        // just keeps Apply usable and observable in-game now that it spans multiple frames.
        if (operationState.Stage is not null)
        {
            if (!operationState.CanStartApply)
                ImGui.TextUnformatted($"Applying... {operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage}).");
            else if (operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Apply requires recovery - see the plugin log.");
            else
                ImGui.TextUnformatted($"Last Apply: {operationState.Stage} ({operationState.SuccessfulTargets}/{operationState.TotalTargets} succeeded).");
        }
```

- [ ] **Step 4: Replace the `ApplyChanges()` private method's body to call the new entry point**

Replace the existing `ApplyChanges()` private method (currently lines 1064-1083):

```csharp
    private void ApplyChanges()
    {
        try
        {
            _lastApplyResults = _plugin.ApplyChanges();
            _lastError = null;
            var succeeded = _lastApplyResults.Count(r => r.Success);
            Plugin.Log.Information($"Apply completed: {succeeded} succeeded, {_lastApplyResults.Count - succeeded} failed.");
            foreach (var failure in _lastApplyResults.Where(r => !r.Success))
                Plugin.Log.Warning($"Apply failure: {failure.Identifier}: {failure.FailureReason}");
        }
        catch (Exception ex)
        {
            _lastError = $"Apply failed: {ex.Message}";
            Plugin.Log.Error(ex, "Apply failed.");
        }

        _historyCache = null; // ApplyChanges() also captures a pre-apply snapshot — history changed
        RefreshOrphanedFolders(); // ApplyChanges() ran RunScan() internally — occupancy changed
    }
```

with:

```csharp
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
```

(The history-cache/orphaned-folders refresh moved to Step 2's per-frame completion check, since `StartApplyOperation()` now returns almost immediately, long before the operation is actually done.)

- [ ] **Step 5: Build and confirm no compile errors**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: wire MainWindow's Apply button to StartApplyOperation with a minimal progress stub"
```

---

### Task 8: Full-suite verification and manual in-game checklist

**Files:** none (verification only)

- [ ] **Step 1: Run the entire automated test suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test plus everything added in Tasks 1-3, zero failures. (Tasks 4-7 have no automated tests, per this plan's Global Constraints — this run does not exercise them.)

- [ ] **Step 2: Confirm a clean release-equivalent build**

Run: `dotnet build -c Release`
Expected: build succeeds with exactly one new, known, accepted warning — `CS0649` on `MainWindow._lastApplyResults` — and no others. Task 7 deliberately stops assigning `_lastApplyResults` (the new Apply path doesn't maintain it; its only remaining reader, `DiagnosticSummaryFormatter.FormatApplySection`, is part of the diagnostics-summary/export surface this plan explicitly leaves untouched — see "What this plan does not cover"). Deleting the field to silence the warning would require touching that out-of-scope formatter, so this one warning is accepted rather than suppressed. If any *other* new warning appears, that is a real regression and must be fixed.

- [ ] **Step 3: Manual in-game verification checklist**

This plan cannot be fully verified without a running FFXIV/Dalamud/Penumbra instance — write out this checklist for the user to run themselves (this repo has no Dalamud test-double infrastructure, same limitation Plan B1 documented). Confirm each item:

- [ ] Load the plugin in-game; confirm no errors in the Dalamud log at startup (composition root construction, `Framework.Update` subscription).
- [ ] Run a Scan, propose a simple independent (non-cycle) move for 2-3 mods, click Apply. Confirm: the button becomes disabled immediately, the "Applying... X/Y steps" text appears and updates across frames, and it settles to "Last Apply: Completed" with the button re-enabled.
- [ ] Propose a 2-way cycle swap (two mods trading paths) and Apply it. Confirm both mods end up at their correct final paths (not stuck at an intermediate `__organizer_apply_tmp__…` path) — this exercises the temp-hop/cascade machinery for real for the first time.
- [ ] Confirm the affected mods visually redraw in-game shortly after the steps complete (proves `RequestPostMutationRefresh`/`RedrawAll` is actually reaching Penumbra).
- [ ] While an Apply is in progress (multi-mod so it spans a few frames), try clicking Create Backup / Restore / Folder Cleanup. Confirm each is blocked with "Another organizer operation is already in progress" (or its button-disabled equivalent) until the Apply concludes — this is the `_operationInProgress`/`Framework.Update` reset interaction from Task 5, and cannot be verified any other way.
- [ ] Force a failure case if feasible (e.g. propose a move to a path that collides with an existing orphaned folder entry) and confirm `StartApplyOperation()` throws the expected `InvalidOperationException` with the folder-collision message, surfaced via `_lastError` in the UI, exactly as it did before this plan.
- [ ] Watch for any visible frame hitching during a larger Apply (10+ mods) — if present, note it; the `TimeSpan.FromMilliseconds(2)` frame budget (Task 5) is a starting value, not a profiled-and-confirmed one.
- [ ] Confirm `%APPDATA%\XIVLauncher\pluginConfigs\PenumbraOrganizer.Plugin\operations\active\<guid>\` (or `completed\<guid>\` after it settles) contains `journal.json`, `plan.json`, `snapshot.json`, and `results.jsonl` after an Apply — proves the bundle-directory wiring end-to-end.

---

## What this plan does not cover

Deferred to **Plan C** (design §13): the same execution engine configured for Restore. `Plugin.Restore()`/`ExecuteOrderedMoves` are untouched by this plan.

Deferred to **Plan D** (design §13): `RecoveryAssessment`, startup deferred classification, the three recovery resolutions (Continue/Restore Previous State/Keep Current — what actually *acts* on `RequiresRecovery`), multi-journal discovery wired into controller startup, `RecoveryDialogSnapshot` population. This plan's `MainWindow` stub only shows a static "Apply requires recovery" message when `RequiresRecovery` is true — nothing resolves it yet.

Deferred to **Plan E** (design §13): the real `MainWindow` progress UI (this plan's polling stub is deliberately crude and is expected to be replaced, not extended), the recovery dialog, and diagnostics dump changes. Specifically: `_lastApplyResults`/`Config.LastApply`/`DiagnosticSummaryFormatter.FormatApplySection` are **not** updated by the new path in this plan — they stay frozen at whatever their last synchronous-Apply-era value was, which Plan E's diagnostics-dump rework is expected to address by reading `StepResultLog`/`DiagnosticsLog` back out instead.

Also out of scope for this plan specifically:
- Deleting the now-unused `ApplyChanges()`/`Organizer.ApplyResult` code path — left in place, unused, as a minimal-footprint choice; a future cleanup pass can remove it once nothing references it.
- `RefreshStatus.TemporarilyUnavailable`/`InvalidState` are modeled in the enum (matching `RefreshSettlement`'s full vocabulary from Plan B1) but never produced by `PenumbraOperationsAdapter` in this plan — `RedrawAll.Invoke` only ever throws or doesn't, giving only `Success`/`ProviderUnavailable`.
- Tuning the `TimeSpan.FromMilliseconds(2)` frame budget based on real profiling data — Task 8's checklist asks the user to watch for hitching, but changing the constant based on what they observe is a follow-up, not part of this plan's own verification.
