# Operation Schema v2 Foundations (Plan A1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite the operation persistence schema to v2 (execution steps separated from recovery targets, canonical integrity hash, step-cursor journal) plus the pure supporting types the controller will need, all Dalamud-free and unit-tested.

**Architecture:** This is the first of the design's five implementation plans (Plan A of §13 in `docs/superpowers/specs/2026-07-22-operation-controller-design.md`), split into A1 (this plan — schema, step metadata, small fixes) and A2 (storage/logging/discovery, a follow-on plan). Everything here is pure C# unit-testable without a running game: no Dalamud, no Penumbra IPC. It rewrites three merged v1 files (`OperationPlan`, `OperationJournal`, `ApplyPlanner`) to their v2 shapes and deletes the v1 `RecoveryClassifier` (dead code — no production caller — to be rebuilt against the v2 schema in Plan D).

**Tech Stack:** .NET (project SDK per `PenumbraOrganizer.Plugin.csproj`), `System.Text.Json`, `System.Security.Cryptography` (SHA-256), xUnit 2.5.3.

## Global Constraints

Copied from the design doc (`2026-07-22-operation-controller-design.md`); every task's requirements implicitly include these:

- **Path comparisons use `PenumbraPathSemantics`** (`AreEquivalent` for comparison, `Normalize` for hashing), never raw string equality — a `" (N)"` duplicate-marker suffix is discarded on Penumbra save and reassigned arbitrarily on reload (`PenumbraPathSemantics.cs:1-19`).
- **Integrity hash is canonical and length-prefixed**, format `<utf8-byte-length>:<utf8-bytes>` per field concatenated with no separators, covering every execution-relevant field: `SchemaVersion`, `OperationType`, step count, then per step `StepIndex`/`Identifier`/normalized `TargetRawPath`/`Kind`/`GroupId`, then target count, then per target `Identifier`/normalized `SnapshotRawPath`/normalized `FinalRawPath`/`ModName`. `OperationId` and `CreatedAt` are **deliberately excluded** (the hash binds executable content, not identity).
- **Persisted enums serialize as strings** via `JsonStringEnumConverter` (already the pattern in the merged codecs).
- **`SchemaVersion` is 2**; `TryLoad` rejects any non-current schema exactly like corruption — **no v1 migration** (v1 never shipped to a user; this plugin has no public release).
- **Every dependency group's steps occupy one contiguous `StepIndex` range**, `GroupId` is 0-based and assigned in the order `OrderMovesForApply` emits each group.
- **Atomic writes via `AtomicFile`** (temp-write-flush-replace, already built).
- **`sealed record` for data types, `static class` for pure logic** — follow `RollbackHistory.cs`/`ApplyPlanner.cs`.

Run the full suite with `dotnet test` from the repo root. Commit with `git add` on specific files only (never `git add -A`).

---

### Task 1: AtomicFile — tolerate IOException on read

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/AtomicFile.cs:32-49`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/AtomicFileTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `AtomicFile.TryReadValidated(string, out string?)` unchanged signature — now returns `false` (not throws) when the file exists but can't be read due to an `IOException` (locked/sharing-violated file). Callers (`OperationPlanCodec.TryLoad`, `OperationJournalCodec.TryLoad`) already treat `false` as "no valid data," so this closes a hole where a locked file would throw straight through their `try/catch (JsonException)` (which doesn't catch `IOException`).

- [ ] **Step 1: Write the failing test**

Add to `AtomicFileTests.cs`:

```csharp
[Fact]
public void TryReadValidated_ReturnsFalseWhenFileIsLockedForReading()
{
    var dir = Directory.CreateTempSubdirectory();
    try
    {
        var path = Path.Combine(dir.FullName, "locked.json");
        File.WriteAllText(path, "{\"a\":1}");

        // Hold an exclusive lock so File.ReadAllText inside TryReadValidated throws IOException.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var found = AtomicFile.TryReadValidated(path, out var contents);

        Assert.False(found);
        Assert.Null(contents);
    }
    finally
    {
        dir.Delete(recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~AtomicFileTests.TryReadValidated_ReturnsFalseWhenFileIsLockedForReading`
Expected: FAIL — an `IOException` propagates out of `TryReadValidated` instead of being converted to `false`.

- [ ] **Step 3: Wrap the read in a try/catch**

Replace the body of `TryReadValidated` (`AtomicFile.cs:32-49`) with:

```csharp
    public static bool TryReadValidated(string path, out string? contents)
    {
        if (!File.Exists(path))
        {
            contents = null;
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // A locked or sharing-violated file is "no valid data right now", not a crash — the
            // Try contract callers rely on (OperationPlanCodec/OperationJournalCodec.TryLoad) only
            // catches JsonException, so an IOException here would otherwise escape them.
            contents = null;
            return false;
        }

        if (string.IsNullOrEmpty(text))
        {
            contents = null;
            return false;
        }

        contents = text;
        return true;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AtomicFileTests`
Expected: PASS (all existing AtomicFile tests plus the new one).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/AtomicFile.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/AtomicFileTests.cs
git commit -m "fix: tolerate IOException in AtomicFile.TryReadValidated"
```

---

### Task 2: Fix slash literals in Normalize tests

**Files:**
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/PenumbraPathSemanticsTests.cs:117-143`

**Interfaces:** none (test-data correctness fix only).

The `Normalize` tests added in the persistence foundations plan use backslash literals (`"Gear\\Foo"` → actual string `Gear\Foo`), but `PenumbraPathSemantics.SplitPath` splits only on `/` (`PenumbraPathSemantics.cs:110` — `path.LastIndexOf('/')`), and real Penumbra paths use `/`. So these tests never actually exercise folder-vs-leaf splitting — `Normalize_ProducesDifferentStringsForDifferentLocations` passes on whole-string difference, not the folder logic its name claims. The rest of the file already correctly uses `/` (e.g. `AreEquivalent` tests at lines 46/54/61). This aligns the `Normalize` tests with real path format and with the rest of the file.

- [ ] **Step 1: Replace the backslash literals with forward slashes**

In `PenumbraPathSemanticsTests.cs`, change the three `Normalize` tests (lines 117-143) so every path literal uses `/`:

```csharp
    [Theory]
    [InlineData("Gear/Foo", "Gear/Foo (2)", "Foo")]      // duplicate marker on the display name itself
    [InlineData("Gear/Foo", "Gear/Foo", "Foo")]           // already identical
    public void Normalize_ProducesSameStringForEquivalentPaths(string a, string b, string displayName)
    {
        var normalizedA = PenumbraPathSemantics.Normalize(a, displayName);
        var normalizedB = PenumbraPathSemantics.Normalize(b, displayName);

        Assert.Equal(normalizedA, normalizedB);
        Assert.True(PenumbraPathSemantics.AreEquivalent(a, b, displayName));
    }

    [Fact]
    public void Normalize_ProducesDifferentStringsForDifferentLocations()
    {
        var normalizedA = PenumbraPathSemantics.Normalize("Gear/Foo", "Foo");
        var normalizedB = PenumbraPathSemantics.Normalize("Weapons/Foo", "Foo");

        Assert.NotEqual(normalizedA, normalizedB);
    }

    [Fact]
    public void Normalize_IsCaseInsensitive()
    {
        var normalizedA = PenumbraPathSemantics.Normalize("Gear/Foo", "Foo");
        var normalizedB = PenumbraPathSemantics.Normalize("gear/foo", "Foo");

        Assert.Equal(normalizedA, normalizedB);
```

(Leave the closing lines of `Normalize_IsCaseInsensitive` and any following tests unchanged.)

- [ ] **Step 2: Run the tests to verify they still pass — now for the right reason**

Run: `dotnet test --filter FullyQualifiedName~PenumbraPathSemanticsTests`
Expected: PASS. `Normalize_ProducesDifferentStringsForDifferentLocations` now genuinely exercises folder splitting (`Gear` vs `Weapons` as folders of the same leaf `Foo`), rather than passing on whole-string inequality.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/Organizer/PenumbraPathSemanticsTests.cs
git commit -m "test: use forward-slash paths in Normalize tests so they exercise folder splitting"
```

---

### Task 3: IElapsedTimeSource + StopwatchElapsedTimeSource

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/IElapsedTimeSource.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/ElapsedTimeSourceTests.cs`

**Interfaces:**
- Produces:
  - `IElapsedTimeSource` with `long GetTimestamp()` and `TimeSpan GetElapsedTime(long startTimestamp)` — mirrors `System.Diagnostics.Stopwatch`'s static `GetTimestamp()`/`GetElapsedTime(long)` API so the production implementation is a near pass-through. Later plans inject a fake to make frame-budget and settlement timing deterministic (design §5/§6).
  - `StopwatchElapsedTimeSource` — the production implementation.

Both are in `Organizer/Operations/`. **Declare them `public`**, matching every sibling type in this folder (`AtomicFile`, `OperationPlan`, `OperationJournal` are all `public`, and the test project references them as public — there is no `InternalsVisibleTo` in this project). The design doc writes `internal interface IElapsedTimeSource` as shorthand, but this codebase's convention is `public` for these types; follow the codebase.

- [ ] **Step 1: Write the failing test**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class ElapsedTimeSourceTests
{
    [Fact]
    public void GetElapsedTime_ReturnsNonNegativeSpanForAPriorTimestamp()
    {
        var clock = new StopwatchElapsedTimeSource();

        var start = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(start);

        Assert.True(elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void GetElapsedTime_IncreasesAcrossTwoReadingsFromTheSameStart()
    {
        var clock = new StopwatchElapsedTimeSource();

        var start = clock.GetTimestamp();
        var first = clock.GetElapsedTime(start);
        // Busy-wait a tiny amount without sleeping the test thread on a timer.
        var spin = clock.GetTimestamp();
        while (clock.GetElapsedTime(spin) < TimeSpan.FromMilliseconds(1)) { }
        var second = clock.GetElapsedTime(start);

        Assert.True(second >= first);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~ElapsedTimeSourceTests`
Expected: FAIL — `IElapsedTimeSource`/`StopwatchElapsedTimeSource` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Diagnostics;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Minimal elapsed-time seam so frame-budget and settlement timing can be driven deterministically
/// in tests. Mirrors System.Diagnostics.Stopwatch's static GetTimestamp/GetElapsedTime shape, so the
/// production implementation is a pass-through and no DI framework or external dependency is needed.
/// Timestamps are process-relative ticks and must never be persisted (design doc section 6).
/// </summary>
public interface IElapsedTimeSource
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startTimestamp);
}

public sealed class StopwatchElapsedTimeSource : IElapsedTimeSource
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ElapsedTimeSourceTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/IElapsedTimeSource.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/ElapsedTimeSourceTests.cs
git commit -m "feat: add IElapsedTimeSource clock seam for deterministic budget timing"
```

---

### Task 4: ApplyPlanner — IsTemporary and GroupId on ApplyStep

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/ApplyPlanner.cs:9` (the `ApplyStep` record) and `:67-107` (`OrderMovesForApply`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/ApplyPlannerTests.cs:209-298`

**Interfaces:**
- Consumes: existing `ModMove(string Identifier, string CurrentPath, string TargetPath)`.
- Produces: `ApplyStep(string Identifier, string TargetPath, bool IsTemporary, int GroupId)` — every step now carries whether it is the cycle-breaking temporary hop and which dependency group it belongs to. `OrderMovesForApply` assigns `GroupId` as a 0-based counter incremented once per emitted connected component, so each group's steps are a contiguous block in emission order (Plan A's `OperationPlan.Create` validates this contiguity). `IsTemporary` is `true` only on a cycle's temporary-hop step.

`Plugin.cs`'s `ExecuteOrderedMoves` (`Plugin.cs:507-520`) reads only `step.Identifier`/`step.TargetPath`, so adding fields does not break it. Only `ApplyPlannerTests` constructs `ApplyStep` positionally and must be updated.

- [ ] **Step 1: Update the OrderMovesForApply assertions to expect the new fields**

In `ApplyPlannerTests.cs`, replace the five `OrderMovesForApply` result-shape tests (lines 209-298) with these (note the added `IsTemporary`/`GroupId` on every constructed `ApplyStep`, and a new contiguity assertion in the independent-groups test):

```csharp
    [Fact]
    public void OrderMovesForApply_SingleMoveToFreePath_ReturnsThatMoveUnchanged()
    {
        var move = new ModMove("Foo", "Gear/Foo", "Gear/Foo (2)");

        var result = ApplyPlanner.OrderMovesForApply([move]);

        Assert.Equal([new ApplyStep("Foo", "Gear/Foo (2)", IsTemporary: false, GroupId: 0)], result);
    }

    [Fact]
    public void OrderMovesForApply_ChainEndingAtFreePath_ProcessesInReverseSoTargetsAreVacatedFirst()
    {
        var a = new ModMove("A", "P1", "P2");
        var b = new ModMove("B", "P2", "P3");

        var result = ApplyPlanner.OrderMovesForApply([a, b]);

        Assert.Equal(
            [
                new ApplyStep("B", "P3", IsTemporary: false, GroupId: 0),
                new ApplyStep("A", "P2", IsTemporary: false, GroupId: 0),
            ],
            result);
    }

    [Fact]
    public void OrderMovesForApply_TwoWaySwap_BreaksCycleWithTemporaryPath()
    {
        var x = new ModMove("X", "P0", "P2");
        var y = new ModMove("Y", "P2", "P0");

        var result = ApplyPlanner.OrderMovesForApply([x, y], _ => "TEMP");

        Assert.Equal(
            [
                new ApplyStep("X", "TEMP", IsTemporary: true, GroupId: 0),
                new ApplyStep("Y", "P0", IsTemporary: false, GroupId: 0),
                new ApplyStep("X", "P2", IsTemporary: false, GroupId: 0),
            ],
            result);
    }

    [Fact]
    public void OrderMovesForApply_ThreeWayRotation_BreaksCycleAndDrainsRemainderInReverse()
    {
        var x = new ModMove("X", "P0", "P2");
        var y = new ModMove("Y", "P2", "P3");
        var z = new ModMove("Z", "P3", "P0");

        var result = ApplyPlanner.OrderMovesForApply([x, y, z], _ => "TEMP");

        Assert.Equal(
            [
                new ApplyStep("X", "TEMP", IsTemporary: true, GroupId: 0),
                new ApplyStep("Z", "P0", IsTemporary: false, GroupId: 0),
                new ApplyStep("Y", "P3", IsTemporary: false, GroupId: 0),
                new ApplyStep("X", "P2", IsTemporary: false, GroupId: 0),
            ],
            result);
    }

    [Fact]
    public void OrderMovesForApply_IndependentGroups_GetDistinctContiguousGroupIds()
    {
        var freeChainA = new ModMove("A", "P1", "P2");
        var freeChainB = new ModMove("B", "P2", "P3");
        var swapX = new ModMove("X", "Q0", "Q1");
        var swapY = new ModMove("Y", "Q1", "Q0");

        var result = ApplyPlanner.OrderMovesForApply([freeChainA, freeChainB, swapX, swapY], _ => "TEMP");

        // Group 0 is the A/B chain (emitted first because A sorts before X); group 1 is the X/Y swap.
        Assert.Equal(
            [
                new ApplyStep("B", "P3", IsTemporary: false, GroupId: 0),
                new ApplyStep("A", "P2", IsTemporary: false, GroupId: 0),
                new ApplyStep("X", "TEMP", IsTemporary: true, GroupId: 1),
                new ApplyStep("Y", "Q0", IsTemporary: false, GroupId: 1),
                new ApplyStep("X", "Q1", IsTemporary: false, GroupId: 1),
            ],
            result);
        // Each group's steps form one contiguous block, in 0-based emission order.
        Assert.Equal([0, 0, 1, 1, 1], result.Select(s => s.GroupId));
    }

    [Fact]
    public void OrderMovesForApply_DefaultTemporaryPathFactory_ProducesTemporaryStepFlaggedAndDistinct()
    {
        var x = new ModMove("X", "P0", "P2");
        var y = new ModMove("Y", "P2", "P0");

        var result = ApplyPlanner.OrderMovesForApply([x, y]);

        var realPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P0", "P2" };
        var tempStep = Assert.Single(result, s => s.IsTemporary);
        Assert.Equal("X", tempStep.Identifier);
        Assert.False(realPaths.Contains(tempStep.TargetPath));
        Assert.False(string.IsNullOrWhiteSpace(tempStep.TargetPath));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ApplyPlannerTests`
Expected: FAIL to build — `ApplyStep` has no `IsTemporary`/`GroupId` parameters yet.

- [ ] **Step 3: Add the fields to ApplyStep**

In `ApplyPlanner.cs`, change line 9 from:

```csharp
public sealed record ApplyStep(string Identifier, string TargetPath);
```

to:

```csharp
public sealed record ApplyStep(string Identifier, string TargetPath, bool IsTemporary, int GroupId);
```

- [ ] **Step 4: Assign IsTemporary/GroupId in OrderMovesForApply**

Replace the body of `OrderMovesForApply` (`ApplyPlanner.cs:67-107`) with:

```csharp
    public static IReadOnlyList<ApplyStep> OrderMovesForApply(
        IReadOnlyList<ModMove> moves, Func<ModMove, string>? temporaryPathFactory = null)
    {
        temporaryPathFactory ??= m => $"{m.CurrentPath}__organizer_apply_tmp__{Guid.NewGuid():N}";

        var byCurrentPath = moves.ToDictionary(m => m.CurrentPath, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<ApplyStep>();
        var groupId = 0;

        foreach (var start in moves.OrderBy(m => m.Identifier, StringComparer.Ordinal))
        {
            if (visited.Contains(start.CurrentPath))
                continue;

            var chain = new List<ModMove>();
            ModMove? cursor = start;
            while (cursor is not null && visited.Add(cursor.CurrentPath))
            {
                chain.Add(cursor);
                byCurrentPath.TryGetValue(cursor.TargetPath, out cursor);
            }

            // Each emitted component appends its steps as one contiguous block, then bumps groupId -
            // so GroupIds are 0-based and every group occupies a contiguous StepIndex range once these
            // steps are numbered by OperationPlan (design doc section 3).
            if (cursor is null)
            {
                for (var i = chain.Count - 1; i >= 0; i--)
                    steps.Add(new ApplyStep(chain[i].Identifier, chain[i].TargetPath, IsTemporary: false, GroupId: groupId));
            }
            else
            {
                steps.Add(new ApplyStep(chain[0].Identifier, temporaryPathFactory(chain[0]), IsTemporary: true, GroupId: groupId));
                for (var i = chain.Count - 1; i >= 1; i--)
                    steps.Add(new ApplyStep(chain[i].Identifier, chain[i].TargetPath, IsTemporary: false, GroupId: groupId));
                steps.Add(new ApplyStep(chain[0].Identifier, chain[0].TargetPath, IsTemporary: false, GroupId: groupId));
            }

            groupId++;
        }

        return steps;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ApplyPlannerTests`
Expected: PASS (all ApplyPlanner tests, including the updated shape tests and the new contiguity assertion).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/ApplyPlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/ApplyPlannerTests.cs
git commit -m "feat: tag ApplyStep with IsTemporary and a contiguous GroupId per dependency group"
```

---

### Task 5: OperationPlan v2 — ExecutionSteps, RecoveryTargets, canonical hash

**Files:**
- Delete: `PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs`, `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs`
- Replace: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlan.cs` (whole file)
- Replace: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanTests.cs` (whole file)

**Interfaces:**
- Consumes: `PenumbraPathSemantics.Normalize`/`AreEquivalent`; `AtomicFile.CreateOrReplace`/`TryReadValidated`.
- Produces:
  - `OperationType` enum: `Apply`, `Restore` (unchanged, stays in this file).
  - `OperationStepKind` enum: `FinalMove`, `CycleBreakingTemporaryMove`.
  - `OperationExecutionStep(int StepIndex, string Identifier, string TargetRawPath, OperationStepKind Kind, int GroupId)`.
  - `OperationRecoveryTarget(string Identifier, string SnapshotRawPath, string FinalRawPath, string ModName)`.
  - `OperationPlan(int SchemaVersion, Guid OperationId, OperationType Type, DateTimeOffset CreatedAt, IReadOnlyList<OperationExecutionStep> ExecutionSteps, IReadOnlyList<OperationRecoveryTarget> RecoveryTargets, string IntegrityHash)` with `CurrentSchemaVersion = 2`, `Create(type, steps, targets)`, `Verify()`, `ComputeIntegrityHash(type, steps, targets)`.
  - `OperationPlanCodec.Save`/`TryLoad`.

`RecoveryClassifier` v1 is deleted first because the v2 `OperationPlan` removes `OperationPlanItem`/`Items`, which `RecoveryClassifier` compiles against. It has no production caller (confirmed: only its own file and tests reference it) and will be rebuilt against the v2 state set in Plan D.

- [ ] **Step 1: Delete the v1 RecoveryClassifier and its tests**

```bash
git rm PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs
```

Run: `dotnet build`
Expected: SUCCEEDS — nothing outside those two files references `RecoveryClassifier`/`ItemRecoveryState`.

- [ ] **Step 2: Replace OperationPlanTests.cs with the v2 tests**

Overwrite `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanTests.cs` with:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationPlanTests
{
    // Two independent final moves, each its own group. A minimal valid plan.
    private static OperationExecutionStep[] TwoFinalSteps() =>
    [
        new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        new(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
    ];

    private static OperationRecoveryTarget[] TwoFinalTargets() =>
    [
        new("mod-a", "Gear/A", "Weapons/A", "A"),
        new("mod-b", "Gear/B", "Weapons/B", "B"),
    ];

    // A two-way swap resolved with a cycle-breaking temporary hop: X and Y trade slots.
    private static OperationExecutionStep[] SwapSteps() =>
    [
        new(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
        new(1, "Y", "P0", OperationStepKind.FinalMove, 0),
        new(2, "X", "P2", OperationStepKind.FinalMove, 0),
    ];

    private static OperationRecoveryTarget[] SwapTargets() =>
    [
        new("X", "P0", "P2", "X"),
        new("Y", "P2", "P0", "Y"),
    ];

    [Fact]
    public void Create_ValidPlan_VerifiesAndCarriesSchemaVersion2()
    {
        var plan = OperationPlan.Create(OperationType.Apply, TwoFinalSteps(), TwoFinalTargets());

        Assert.True(plan.Verify());
        Assert.Equal(2, plan.SchemaVersion);
        Assert.Equal(2, plan.ExecutionSteps.Count);
        Assert.Equal(2, plan.RecoveryTargets.Count);
    }

    [Fact]
    public void Create_ValidCyclePlan_Verifies()
    {
        var plan = OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets());

        Assert.True(plan.Verify());
    }

    [Fact]
    public void Create_ThrowsWhenStepIndicesAreNotContiguous()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            new(5, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1), // gap
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, TwoFinalTargets()));
    }

    [Fact]
    public void Create_ThrowsWhenGroupIdsAreNotZeroBasedContiguous()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            new(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 2), // jumps 0 -> 2
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, TwoFinalTargets()));
    }

    [Fact]
    public void Create_ThrowsWhenAnIdentifierAppearsInTwoGroups()
    {
        // mod-a in group 0 and group 1 - group ranges would not be contiguous per identifier.
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            new(1, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 1),
        };
        var targets = new OperationRecoveryTarget[] { new("mod-a", "Gear/A", "Weapons/A", "A") };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenLastStepForAnIdentifierIsTemporary()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0), // temp is the only/last step for X
        };
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X") };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenAStepHasNoRecoveryTarget()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "orphan", "Weapons/O", OperationStepKind.FinalMove, 0),
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, TwoFinalTargets()));
    }

    [Fact]
    public void Create_ThrowsWhenARecoveryTargetHasNoStep()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        };
        var targets = new OperationRecoveryTarget[]
        {
            new("mod-a", "Gear/A", "Weapons/A", "A"),
            new("mod-b", "Gear/B", "Weapons/B", "B"), // no step
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenTargetIdentifiersAreNotUnique()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        };
        var targets = new OperationRecoveryTarget[]
        {
            new("mod-a", "Gear/A", "Weapons/A", "A"),
            new("mod-a", "Gear/A", "Weapons/A", "A"), // duplicate identifier
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void ComputeIntegrityHash_ChangesWhenStepKindChanges()
    {
        // The exact regression the canonical hash closes: Kind must be bound, or a step could flip
        // FinalMove <-> CycleBreakingTemporaryMove with identical identifier/path and the same hash.
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X") };
        var asTemporary = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.CycleBreakingTemporaryMove, 0) };
        var asFinal = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.FinalMove, 0) };

        Assert.NotEqual(
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, asTemporary, targets),
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, asFinal, targets));
    }

    [Fact]
    public void ComputeIntegrityHash_ChangesWhenGroupIdChanges()
    {
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X") };
        var group0 = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.FinalMove, 0) };
        var group1 = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.FinalMove, 1) };

        Assert.NotEqual(
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, group0, targets),
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, group1, targets));
    }

    [Fact]
    public void ComputeIntegrityHash_IsUnchangedByPenumbraDuplicateMarkerReshuffling()
    {
        // "Weapons/A" and "Weapons/A (3)" are the same persisted location for a mod named "A".
        var targets = new OperationRecoveryTarget[] { new("mod-a", "Gear/A", "Weapons/A", "A") };
        var plain = new OperationExecutionStep[] { new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var marked = new OperationExecutionStep[] { new(0, "mod-a", "Weapons/A (3)", OperationStepKind.FinalMove, 0) };

        Assert.Equal(
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, plain, targets),
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, marked, targets));
    }

    [Fact]
    public void SaveThenTryLoad_RoundTripsAndVerifies()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Restore, SwapSteps(), SwapTargets());

            OperationPlanCodec.Save(path, plan);
            var loaded = OperationPlanCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.NotNull(result);
            Assert.Equal(plan.OperationId, result!.OperationId);
            Assert.Equal(plan.IntegrityHash, result.IntegrityHash);
            Assert.True(result.Verify());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_WritesEnumsAsStrings()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            OperationPlanCodec.Save(path, OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets()));

            var json = File.ReadAllText(path);
            Assert.Contains("\"CycleBreakingTemporaryMove\"", json);
            Assert.Contains("\"Apply\"", json);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenFileMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationPlanCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);
            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenIntegrityHashHasBeenTampered()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets());
            OperationPlanCodec.Save(path, plan);

            File.WriteAllText(path, File.ReadAllText(path).Replace(plan.IntegrityHash, "tampered-hash-value"));

            var loaded = OperationPlanCodec.TryLoad(path, out var result);
            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenSchemaVersionIsNotCurrent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            OperationPlanCodec.Save(path, OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets()));

            // Simulate an older on-disk schema: force SchemaVersion to 1. No migration - TryLoad rejects it.
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"SchemaVersion\":2", "\"SchemaVersion\":1"));

            var loaded = OperationPlanCodec.TryLoad(path, out var result);
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

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationPlanTests`
Expected: FAIL to build — the v2 types (`OperationExecutionStep`, `OperationRecoveryTarget`, the new `OperationPlan` shape) don't exist yet.

- [ ] **Step 4: Replace OperationPlan.cs with the v2 implementation**

Overwrite `PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlan.cs` with:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationType { Apply, Restore }

public enum OperationStepKind { FinalMove, CycleBreakingTemporaryMove }

/// <summary> One physical SetModPath action. Duplicates per identifier are allowed (a cycle emits a
/// temporary hop and then a final move for the same mod). </summary>
public sealed record OperationExecutionStep(
    int StepIndex, string Identifier, string TargetRawPath, OperationStepKind Kind, int GroupId);

/// <summary> The desired before/after state for one mod, one per identifier - what recovery compares
/// live state against. Carries the snapshot path explicitly so recovery never infers it from steps. </summary>
public sealed record OperationRecoveryTarget(
    string Identifier, string SnapshotRawPath, string FinalRawPath, string ModName);

public sealed record OperationPlan(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OperationExecutionStep> ExecutionSteps,
    IReadOnlyList<OperationRecoveryTarget> RecoveryTargets,
    string IntegrityHash)
{
    public const int CurrentSchemaVersion = 2;

    public static OperationPlan Create(
        OperationType type,
        IReadOnlyList<OperationExecutionStep> executionSteps,
        IReadOnlyList<OperationRecoveryTarget> recoveryTargets)
    {
        Validate(type, executionSteps, recoveryTargets);
        return new OperationPlan(
            CurrentSchemaVersion, Guid.NewGuid(), type, DateTimeOffset.UtcNow,
            executionSteps, recoveryTargets, ComputeIntegrityHash(type, executionSteps, recoveryTargets));
    }

    public bool Verify() => IntegrityHash == ComputeIntegrityHash(Type, ExecutionSteps, RecoveryTargets);

    // Throws InvalidOperationException on any structural violation - a plan must never be persisted
    // in a state it would reject on reload. See design doc section 3 for the full invariant list.
    private static void Validate(
        OperationType type,
        IReadOnlyList<OperationExecutionStep> steps,
        IReadOnlyList<OperationRecoveryTarget> targets)
    {
        var targetByIdentifier = new Dictionary<string, OperationRecoveryTarget>(StringComparer.Ordinal);
        foreach (var t in targets)
            if (!targetByIdentifier.TryAdd(t.Identifier, t))
                throw new InvalidOperationException($"Duplicate recovery target identifier '{t.Identifier}'.");

        for (var i = 0; i < steps.Count; i++)
            if (steps[i].StepIndex != i)
                throw new InvalidOperationException(
                    $"Execution steps must have contiguous indices from 0; position {i} has StepIndex {steps[i].StepIndex}.");

        // GroupId: non-negative, first is 0, stays same or increments by exactly 1 in index order.
        // This alone guarantees 0-based, contiguous, non-interleaved group blocks.
        int? prevGroup = null;
        foreach (var s in steps)
        {
            if (s.GroupId < 0)
                throw new InvalidOperationException($"Step {s.StepIndex} has a negative GroupId ({s.GroupId}).");
            if (prevGroup is null)
            {
                if (s.GroupId != 0)
                    throw new InvalidOperationException($"First step must have GroupId 0; found {s.GroupId}.");
            }
            else if (s.GroupId != prevGroup && s.GroupId != prevGroup + 1)
            {
                throw new InvalidOperationException(
                    $"GroupId must stay equal or increment by 1 across steps in index order; went from {prevGroup} to {s.GroupId}.");
            }

            prevGroup = s.GroupId;
        }

        var groupByIdentifier = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastStepByIdentifier = new Dictionary<string, OperationExecutionStep>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            if (!targetByIdentifier.ContainsKey(s.Identifier))
                throw new InvalidOperationException($"Execution step identifier '{s.Identifier}' has no recovery target.");
            if (groupByIdentifier.TryGetValue(s.Identifier, out var g))
            {
                if (g != s.GroupId)
                    throw new InvalidOperationException(
                        $"Identifier '{s.Identifier}' appears in more than one group ({g} and {s.GroupId}).");
            }
            else
            {
                groupByIdentifier[s.Identifier] = s.GroupId;
            }

            lastStepByIdentifier[s.Identifier] = s; // index-ordered, so the final write is the highest-index step
        }

        foreach (var t in targets)
        {
            if (!lastStepByIdentifier.TryGetValue(t.Identifier, out var last))
                throw new InvalidOperationException($"Recovery target '{t.Identifier}' has no execution step.");
            if (last.Kind != OperationStepKind.FinalMove)
                throw new InvalidOperationException($"The last step for '{t.Identifier}' must be a FinalMove.");
            if (!PenumbraPathSemantics.AreEquivalent(last.TargetRawPath, t.FinalRawPath, t.ModName))
                throw new InvalidOperationException($"The last step for '{t.Identifier}' must target its FinalRawPath.");
        }
    }

    // Canonical, length-prefixed encoding (<utf8-byte-length>:<utf8-bytes> per field, concatenated,
    // no separators - unambiguous without depending on any character being absent from the data).
    // Covers every execution-relevant field including Kind and GroupId; excludes OperationId and
    // CreatedAt (identity, not executable content). Paths are normalized so a Penumbra reload that
    // reshuffles a " (N)" suffix cannot change the hash. Assumes validated input (Create validates
    // first): every step identifier resolves to a recovery target for the display-name lookup.
    public static string ComputeIntegrityHash(
        OperationType type,
        IReadOnlyList<OperationExecutionStep> steps,
        IReadOnlyList<OperationRecoveryTarget> targets)
    {
        var nameByIdentifier = targets.ToDictionary(t => t.Identifier, t => t.ModName, StringComparer.Ordinal);
        var sb = new StringBuilder();

        void Field(string value)
        {
            sb.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
        }

        Field(CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Field(type.ToString());
        Field(steps.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var s in steps.OrderBy(s => s.StepIndex))
        {
            Field(s.StepIndex.ToString(CultureInfo.InvariantCulture));
            Field(s.Identifier);
            Field(PenumbraPathSemantics.Normalize(s.TargetRawPath, nameByIdentifier[s.Identifier]));
            Field(s.Kind.ToString());
            Field(s.GroupId.ToString(CultureInfo.InvariantCulture));
        }

        Field(targets.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var t in targets.OrderBy(t => t.Identifier, StringComparer.Ordinal))
        {
            Field(t.Identifier);
            Field(PenumbraPathSemantics.Normalize(t.SnapshotRawPath, t.ModName));
            Field(PenumbraPathSemantics.Normalize(t.FinalRawPath, t.ModName));
            Field(t.ModName);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

public static class OperationPlanCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, OperationPlan plan)
    {
        if (!plan.Verify())
            throw new InvalidOperationException("Refusing to persist an OperationPlan that fails its own integrity check.");

        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(plan, SerializerOptions));
    }

    public static bool TryLoad(string path, out OperationPlan? plan)
    {
        plan = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        OperationPlan? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<OperationPlan>(contents, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.SchemaVersion != OperationPlan.CurrentSchemaVersion || !candidate.Verify())
            return false;

        plan = candidate;
        return true;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationPlanTests`
Expected: PASS (all v2 OperationPlan tests).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlan.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanTests.cs
git commit -m "feat!: OperationPlan v2 with execution steps, recovery targets, canonical hash

Replaces the v1 single-Items shape. Deletes v1 RecoveryClassifier (dead
code, rebuilt against the v2 state set in Plan D)."
```

---

### Task 6: OperationJournal v2 — ProcessedStepCount, Resolution, Cancellation

**Files:**
- Replace: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationJournal.cs` (whole file)
- Replace: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationJournalTests.cs` (whole file)

**Interfaces:**
- Consumes: `OperationType` (from `OperationPlan.cs`); `AtomicFile`.
- Produces:
  - `OperationStage` enum: `Preparing, Prepared, Mutating, Refreshing, Verifying, Completed, CompletedWithItemFailures, FailedBeforeMutation, FailedPartiallyApplied, Cancelled` (note: `AcceptedCurrentState` is **removed** — it moves to `OperationResolution`).
  - `OperationResolution` enum: `None, AcceptedCurrentState, ContinuedByNewOperation, RestoredByNewOperation`.
  - `OperationJournal(int SchemaVersion, Guid OperationId, OperationType Type, OperationStage Stage, OperationResolution Resolution, Guid? SuccessorOperationId, bool CancellationRequested, DateTimeOffset StartedAt, int TotalSteps, int ProcessedStepCount, string? LastCompletedIdentifier, Guid SnapshotId, Guid PlanId, string TargetHash, Guid? RecoveryOfOperationId, DateTimeOffset UpdatedAt)` with `CurrentSchemaVersion = 2` and `IsTerminal` = `Resolution != None` OR `Stage` is a terminal execution outcome.
  - `OperationJournalCodec.Save`/`TryLoad`.
  - `CheckpointPolicy` — **unchanged** from v1, kept as-is in this file.

- [ ] **Step 1: Replace OperationJournalTests.cs with the v2 tests**

Overwrite `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationJournalTests.cs` with:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationJournalTests
{
    private static OperationJournal Sample(
        OperationStage stage = OperationStage.Mutating,
        OperationResolution resolution = OperationResolution.None) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: Guid.NewGuid(),
        Type: OperationType.Apply,
        Stage: stage,
        Resolution: resolution,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 401,
        ProcessedStepCount: 173,
        LastCompletedIdentifier: "mod-173",
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "abc123",
        RecoveryOfOperationId: null,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(OperationStage.Preparing, false)]
    [InlineData(OperationStage.Prepared, false)]
    [InlineData(OperationStage.Mutating, false)]
    [InlineData(OperationStage.Refreshing, false)]
    [InlineData(OperationStage.Verifying, false)]
    [InlineData(OperationStage.Completed, true)]
    [InlineData(OperationStage.CompletedWithItemFailures, true)]
    [InlineData(OperationStage.FailedBeforeMutation, true)]
    [InlineData(OperationStage.FailedPartiallyApplied, true)]
    [InlineData(OperationStage.Cancelled, true)]
    public void IsTerminal_FollowsTheStageTerminalSetWhenResolutionIsNone(OperationStage stage, bool expected)
    {
        Assert.Equal(expected, Sample(stage).IsTerminal);
    }

    [Fact]
    public void IsTerminal_TrueWhenResolutionIsSetEvenIfStageIsNonTerminal()
    {
        // A superseded journal keeps an honest frozen Stage (e.g. Mutating) but is terminal via Resolution.
        var journal = Sample(OperationStage.Mutating, OperationResolution.ContinuedByNewOperation);
        Assert.True(journal.IsTerminal);
    }

    [Fact]
    public void SaveThenTryLoad_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var journal = Sample();

            OperationJournalCodec.Save(path, journal);
            var loaded = OperationJournalCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.Equal(journal, result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_WritesStageAsAString()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            OperationJournalCodec.Save(path, Sample(OperationStage.Mutating));

            var json = File.ReadAllText(path);
            Assert.Contains("\"Mutating\"", json);
            Assert.DoesNotContain("\"Stage\":2", json);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenFileMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationJournalCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);
            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenSchemaVersionIsNotCurrent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            OperationJournalCodec.Save(path, Sample());

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"SchemaVersion\":2", "\"SchemaVersion\":1"));

            var loaded = OperationJournalCodec.TryLoad(path, out var result);
            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class CheckpointPolicyTests
{
    [Fact]
    public void IsDue_TrueWhenItemCountThresholdReached()
    {
        Assert.True(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 10, elapsedSinceLastCheckpoint: TimeSpan.Zero));
    }

    [Fact]
    public void IsDue_TrueWhenTimeThresholdReached()
    {
        Assert.True(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 1, elapsedSinceLastCheckpoint: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void IsDue_FalseWhenNeitherThresholdReached()
    {
        Assert.False(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 5, elapsedSinceLastCheckpoint: TimeSpan.FromMilliseconds(200)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationJournalTests`
Expected: FAIL to build — the v2 journal shape (`Stage`/`Resolution`/`ProcessedStepCount`/etc.) doesn't exist yet.

- [ ] **Step 3: Replace OperationJournal.cs with the v2 implementation**

Overwrite `PenumbraOrganizer.Plugin/Organizer/Operations/OperationJournal.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationStage
{
    Preparing,
    Prepared,
    Mutating,
    Refreshing,
    Verifying,
    Completed,
    CompletedWithItemFailures,
    FailedBeforeMutation,
    FailedPartiallyApplied,
    Cancelled,
}

// A later human/system decision applied on top of a frozen execution Stage. Kept separate so a
// superseded journal can keep an honest historical Stage while still being terminal (design doc §4).
public enum OperationResolution { None, AcceptedCurrentState, ContinuedByNewOperation, RestoredByNewOperation }

public sealed record OperationJournal(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    OperationStage Stage,
    OperationResolution Resolution,
    Guid? SuccessorOperationId,
    bool CancellationRequested,
    DateTimeOffset StartedAt,
    int TotalSteps,
    int ProcessedStepCount,
    string? LastCompletedIdentifier,
    Guid SnapshotId,
    Guid PlanId,
    string TargetHash,
    Guid? RecoveryOfOperationId,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 2;

    private static readonly HashSet<OperationStage> TerminalStages =
    [
        OperationStage.Completed,
        OperationStage.CompletedWithItemFailures,
        OperationStage.FailedBeforeMutation,
        OperationStage.FailedPartiallyApplied,
        OperationStage.Cancelled,
    ];

    // Terminal by either axis, independently: a later resolution, or an execution Stage that
    // itself concluded. See design doc section 4.
    public bool IsTerminal => Resolution != OperationResolution.None || TerminalStages.Contains(Stage);
}

public static class OperationJournalCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, OperationJournal journal) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(journal, SerializerOptions));

    public static bool TryLoad(string path, out OperationJournal? journal)
    {
        journal = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        OperationJournal? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<OperationJournal>(contents, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.SchemaVersion != OperationJournal.CurrentSchemaVersion)
            return false;

        journal = candidate;
        return true;
    }
}

/// <summary>
/// Design doc section 6: checkpoint on whichever threshold is reached first, so a large library
/// doesn't rewrite the journal after every single mutation (filesystem churn on HDDs) while a
/// stalled operation still checkpoints promptly on wall-clock time.
/// </summary>
public static class CheckpointPolicy
{
    private const int ItemThreshold = 10;
    private static readonly TimeSpan TimeThreshold = TimeSpan.FromMilliseconds(500);

    public static bool IsDue(int completedSinceLastCheckpoint, TimeSpan elapsedSinceLastCheckpoint) =>
        completedSinceLastCheckpoint >= ItemThreshold || elapsedSinceLastCheckpoint >= TimeThreshold;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationJournalTests|FullyQualifiedName~CheckpointPolicyTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationJournal.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationJournalTests.cs
git commit -m "feat!: OperationJournal v2 with ProcessedStepCount, Resolution, and cancellation"
```

---

### Task 7: LiveModSnapshot with duplicate-identifier detection

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/LiveModSnapshot.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/LiveModSnapshotTests.cs`

**Interfaces:**
- Consumes: existing `LiveMod(string Identifier, string Name, string FullPath, bool HeliosphereManaged)` from `Organizer/RollbackHistory.cs` (reused deliberately — it already carries exactly the identifier/name/path the settlement and classification logic need, so introducing a second near-identical `LiveModState` record would violate DRY; the parent `Organizer` namespace resolves without a `using`, same as `OperationPlan.cs` reaching `PenumbraPathSemantics`).
- Produces:
  - `LiveModSnapshot(IReadOnlyDictionary<string, LiveMod> Mods, IReadOnlySet<string> DuplicateIdentifiers)`.
  - `LiveModSnapshotBuilder.Build(IEnumerable<LiveMod> mods)` — first occurrence of each identifier wins in `Mods`; any identifier seen more than once is flagged in `DuplicateIdentifiers`. Consumers (verification/recovery, later plans) treat any non-empty `DuplicateIdentifiers` as "live state can't be trusted," so this replaces the throw-on-duplicate behavior `RollbackHistory.CaptureSnapshot` uses on the write side with a non-throwing read-side guard (design §8a / earlier decision).

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class LiveModSnapshotTests
{
    private static LiveMod Mod(string id, string path) => new(id, id, path, HeliosphereManaged: false);

    [Fact]
    public void Build_NoDuplicates_ProducesEmptyDuplicateSetAndAllMods()
    {
        var snapshot = LiveModSnapshotBuilder.Build([Mod("a", "Gear/A"), Mod("b", "Gear/B")]);

        Assert.Empty(snapshot.DuplicateIdentifiers);
        Assert.Equal(2, snapshot.Mods.Count);
        Assert.Equal("Gear/A", snapshot.Mods["a"].FullPath);
    }

    [Fact]
    public void Build_DuplicateIdentifier_FlagsItAndKeepsFirstOccurrence()
    {
        var snapshot = LiveModSnapshotBuilder.Build([Mod("a", "Gear/First"), Mod("a", "Gear/Second")]);

        Assert.Contains("a", snapshot.DuplicateIdentifiers);
        Assert.Single(snapshot.Mods);
        Assert.Equal("Gear/First", snapshot.Mods["a"].FullPath);
    }

    [Fact]
    public void Build_Empty_ProducesEmptySnapshot()
    {
        var snapshot = LiveModSnapshotBuilder.Build([]);

        Assert.Empty(snapshot.Mods);
        Assert.Empty(snapshot.DuplicateIdentifiers);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LiveModSnapshotTests`
Expected: FAIL — `LiveModSnapshot`/`LiveModSnapshotBuilder` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// An immutable read of Penumbra's live mod list, with duplicate identifiers surfaced rather than
/// thrown. Consumers treat any non-empty DuplicateIdentifiers as "live state can't be trusted"
/// (verification/recovery force RecoveryRequired), so the read-side guard is non-throwing - unlike
/// RollbackHistory.CaptureSnapshot's deliberate throw on the write side. Reuses the existing LiveMod
/// record so there is one live-mod shape, not two.
/// </summary>
public sealed record LiveModSnapshot(
    IReadOnlyDictionary<string, LiveMod> Mods,
    IReadOnlySet<string> DuplicateIdentifiers);

public static class LiveModSnapshotBuilder
{
    public static LiveModSnapshot Build(IEnumerable<LiveMod> mods)
    {
        var byIdentifier = new Dictionary<string, LiveMod>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mod in mods)
            if (!byIdentifier.TryAdd(mod.Identifier, mod))
                duplicates.Add(mod.Identifier);

        return new LiveModSnapshot(byIdentifier, duplicates);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LiveModSnapshotTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/LiveModSnapshot.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/LiveModSnapshotTests.cs
git commit -m "feat: add LiveModSnapshot with non-throwing duplicate-identifier detection"
```

---

### Task 8: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test plus everything added/rewritten in Tasks 1–7, zero failures. Note the total count will be lower than before by the deleted `RecoveryClassifierTests` (15 tests removed) and adjusted by the rewritten Plan/Journal tests; a clean "Failed: 0" is the gate, not a specific total.

- [ ] **Step 2: Confirm the working tree is clean and no stray temp dirs leaked**

Run: `git status --short`
Expected: no output (all tests write to `Directory.CreateTempSubdirectory()` outside the repo and delete in `finally`).

---

## What this plan does not cover

Deferred to **Plan A2** (the storage/logging half of the design's Plan A), a separate follow-on plan — depends on this plan's `OperationPlan`/`OperationJournal` v2:

- `StepResultLog` (append-only `results.jsonl`, truncated-trailing-line tolerance) and its journal reconciliation rule (design §5a).
- `DiagnosticsLog` (append-only `diagnostics.jsonl`, write-failure-swallowing sink, trim cap) (design §10).
- `OperationStorage` — the multi-operation directory layout, recovery-graph discovery (chains, leaves, cycles, disconnected roots), and fail-safe bundle-based retention (design §4a).
- The remaining adapter contract: `IPenumbraOperations` interface plus `LiveModReadResult`/`RefreshResult`/`SetModPathResult` return types (defined where first implemented, since nothing in A1/A2 consumes them).

Deferred to **Plans B–E** (design §13): the `OperationController`, `PathMutationOperation`, frame-budgeted execution, `Refreshing`/verification settlement, recovery classification/assessment and resolutions, and all `MainWindow`/diagnostics-dump UI wiring.
