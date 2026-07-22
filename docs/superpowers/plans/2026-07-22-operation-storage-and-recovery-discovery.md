# Operation Storage and Recovery Discovery (Plan A2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the durable per-step result log, the diagnostics log, and the multi-operation storage/recovery-discovery/retention layer — the second and final half of design Plan A, all Dalamud-free and unit-tested.

**Architecture:** Continues directly from Plan A1 (merged: `OperationPlan` v2, `OperationJournal` v2, `AtomicFile`, `IElapsedTimeSource`, `LiveModSnapshot`). This plan adds: (1) `StepResultLog` + `StepResultReconciler` — the durable per-step evidence that explains what happened within a journal's `ProcessedStepCount`, and the startup rule for reconciling it against the journal; (2) `DiagnosticsLog` — the durable, crash-surviving source the eventual diagnostics dump reads from; (3) `OperationBundlePaths` / `OperationRecoveryGraph` / `OperationBundleDiscovery` / `OperationBundleRetention` — the directory-per-operation storage layout, the pure graph algorithm that finds which interrupted operation is authoritative after a crash, and fail-safe retention. Nothing here wires into Dalamud, the framework update loop, or `Plugin.cs`/`MainWindow.cs` — that's Plans B–E.

**Tech Stack:** .NET (project SDK per `PenumbraOrganizer.Plugin.csproj`), `System.Text.Json`, xUnit 2.5.3.

## Global Constraints

Copied from `docs/superpowers/specs/2026-07-22-operation-controller-design.md`; every task's requirements implicitly include these:

- **Storage layout**: `ConfigDirectory/operations/active/<operationId>/{journal.json,plan.json,snapshot.json,results.jsonl}`, `ConfigDirectory/operations/completed/<operationId>/{...same...}`, `ConfigDirectory/operations/diagnostics.jsonl` (global, not per-operation).
- **`results.jsonl` and `diagnostics.jsonl` are append-only** — one JSON object per line, never a whole-file rewrite on every write (that cost is exactly what this design avoids). A truncated or otherwise unparseable line (including a mid-file one) is skipped on read, never fails the whole read.
- **Ordering guarantee**: a step's result line is appended *before* the journal checkpoint advances `ProcessedStepCount` past it — enforced by callers in a later plan, but this plan's reconciliation logic must assume and check for it, never assume the reverse.
- **Journal is authoritative on committed progress, never the result log.** A result at or past `ProcessedStepCount` is expected and preserved for diagnostics but never used to advance anything. A *missing* result below `ProcessedStepCount`, a *duplicate* result for the same `StepIndex` below the cursor, or an *identifier mismatch* against the plan's step at that index — each makes the operation `Indeterminate`, not silently patched over.
- **A diagnostics write failure must never propagate** — the log swallows its own I/O exceptions.
- **Directory-per-operation-ID makes duplicate-ID collisions structurally impossible** — no runtime uniqueness check needed for that specific failure mode.
- **Startup cleanup pass runs before recovery-graph discovery**, and discovery runs before any retention deletion.
- **Retention never deletes when reference analysis is inconclusive** (a bundle that fails to load is simply excluded from consideration — protected by omission, not by an explicit check).
- **Retention is fail-safe**: one undeletable/unmovable bundle (`IOException`/`UnauthorizedAccessException`) must not block cleanup of the rest or plugin startup.
- **`sealed record` for data types, `static class` for pure logic** — follow `AtomicFile.cs`/`OperationPlan.cs`.

Run the full suite with `dotnet test` from the repo root. Commit with `git add` on specific files only (never `git add -A`).

---

### Task 1: OperationStepResult and StepResultLog

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/StepResultLog.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/StepResultLogTests.cs`

**Interfaces:**
- Produces:
  - `OperationStepDisposition` enum: `Succeeded, Failed, SkippedAfterEarlierFailure, SkippedAlreadySatisfied`.
  - `OperationStepResult(int StepIndex, string Identifier, OperationStepDisposition Disposition, string? IpcResultName, string? FailureDetail, DateTimeOffset RecordedAt, long? DurationMilliseconds)`.
  - `StepResultLog.Append(string path, OperationStepResult result)` (void) — a single durable append (open in append mode, write one JSON line, flush to disk), creating the file and its directory if they don't exist.
  - `StepResultLog.ReadAll(string path)` returns `IReadOnlyList<OperationStepResult>` — `[]` if the file doesn't exist; otherwise parses line-by-line, silently skipping any line (not only the last) that fails to deserialize, never throwing on a partial/corrupt line.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class StepResultLogTests
{
    private static OperationStepResult Sample(int stepIndex, string identifier, OperationStepDisposition disposition) => new(
        stepIndex, identifier, disposition,
        IpcResultName: disposition == OperationStepDisposition.Succeeded ? "Success" : "PathRenameFailed",
        FailureDetail: disposition == OperationStepDisposition.Succeeded ? null : "collision",
        RecordedAt: DateTimeOffset.UtcNow,
        DurationMilliseconds: disposition == OperationStepDisposition.Succeeded ? 5 : null);

    [Fact]
    public void ReadAll_FileDoesNotExist_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = StepResultLog.ReadAll(Path.Combine(dir.FullName, "missing.jsonl"));
            Assert.Empty(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppendThenReadAll_SingleResult_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            var sample = Sample(0, "mod-a", OperationStepDisposition.Succeeded);

            StepResultLog.Append(path, sample);
            var results = StepResultLog.ReadAll(path);

            var single = Assert.Single(results);
            Assert.Equal(sample, single);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppendMultipleTimes_ReadAll_ReturnsAllInAppendOrder()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));
            StepResultLog.Append(path, Sample(1, "mod-b", OperationStepDisposition.Failed));
            StepResultLog.Append(path, Sample(2, "mod-c", OperationStepDisposition.SkippedAfterEarlierFailure));

            var results = StepResultLog.ReadAll(path);

            Assert.Equal([0, 1, 2], results.Select(r => r.StepIndex));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_CreatesDestinationDirectoryIfMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));

            Assert.True(File.Exists(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadAll_ToleratesTruncatedTrailingLine()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));
            // Simulate a crash mid-write of the second line: a truncated JSON fragment, no closing brace.
            File.AppendAllText(path, "{\"StepIndex\":1,\"Identifier\":\"mod-b\",\"Disposi");

            var results = StepResultLog.ReadAll(path);

            var single = Assert.Single(results);
            Assert.Equal(0, single.StepIndex);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadAll_ToleratesCorruptMiddleLine()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));
            File.AppendAllText(path, "not valid json at all" + Environment.NewLine);
            StepResultLog.Append(path, Sample(2, "mod-c", OperationStepDisposition.Succeeded));

            var results = StepResultLog.ReadAll(path);

            Assert.Equal([0, 2], results.Select(r => r.StepIndex));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_WritesDispositionAsAString()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.SkippedAfterEarlierFailure));

            var text = File.ReadAllText(path);
            Assert.Contains("\"SkippedAfterEarlierFailure\"", text);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~StepResultLogTests`
Expected: FAIL — `StepResultLog`, `OperationStepResult`, `OperationStepDisposition` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationStepDisposition { Succeeded, Failed, SkippedAfterEarlierFailure, SkippedAlreadySatisfied }

/// <summary> One execution step's durable outcome. IpcResultName/DurationMilliseconds are null for
/// skipped dispositions - no IPC call was ever attempted for those. Design doc section 5a. </summary>
public sealed record OperationStepResult(
    int StepIndex,
    string Identifier,
    OperationStepDisposition Disposition,
    string? IpcResultName,
    string? FailureDetail,
    DateTimeOffset RecordedAt,
    long? DurationMilliseconds);

/// <summary>
/// Append-only results.jsonl - one JSON object per line. Never rewrites the whole file on write
/// (that cost grows with operation size, which is exactly what this design avoids); a corrupt or
/// truncated line anywhere in the file (not just the last one) is skipped on read, never fails the
/// whole read. Design doc section 5a.
/// </summary>
public static class StepResultLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Append(string path, OperationStepResult result)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var line = JsonSerializer.Serialize(result, SerializerOptions);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static IReadOnlyList<OperationStepResult> ReadAll(string path)
    {
        if (!File.Exists(path))
            return [];

        var results = new List<OperationStepResult>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            OperationStepResult? result;
            try
            {
                result = JsonSerializer.Deserialize<OperationStepResult>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue; // corrupt or truncated line - skip, don't fail the whole read
            }

            if (result is not null)
                results.Add(result);
        }

        return results;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~StepResultLogTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/StepResultLog.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/StepResultLogTests.cs
git commit -m "feat: add durable append-only per-step result log"
```

---

### Task 2: StepResultReconciler

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/StepResultReconciler.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/StepResultReconcilerTests.cs`

**Interfaces:**
- Consumes: `OperationJournal` (`ProcessedStepCount`), `OperationPlan`/`OperationExecutionStep` (`ExecutionSteps`, `Identifier`), `OperationStepResult` (Task 1).
- Produces:
  - `StepResultReconciliationStatus` enum: `Consistent, Indeterminate`.
  - `StepResultReconciliationResult(StepResultReconciliationStatus Status, string? Reason)`.
  - `StepResultReconciler.Reconcile(OperationJournal journal, OperationPlan plan, IReadOnlyList<OperationStepResult> results)` returns `StepResultReconciliationResult`.

This is the pure logic half of design §5a's reconciliation rule (Task 1 is the raw log I/O; this is "does the log actually substantiate the journal's claimed progress"). For every `StepIndex` in `[0, journal.ProcessedStepCount)`: require exactly one result, and that result's `Identifier` must equal `plan.ExecutionSteps[StepIndex].Identifier`. Any violation is `Indeterminate` with a specific reason. Results at or past `ProcessedStepCount` are ignored entirely by this method — never inspected, never used to relax or extend what's required below the cursor.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class StepResultReconcilerTests
{
    private static readonly OperationExecutionStep[] ThreeSteps =
    [
        new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        new(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        new(2, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 2),
    ];

    private static readonly OperationRecoveryTarget[] ThreeTargets =
    [
        new("mod-a", "Gear/A", "Weapons/A", "A"),
        new("mod-b", "Gear/B", "Weapons/B", "B"),
        new("mod-c", "Gear/C", "Weapons/C", "C"),
    ];

    private static OperationPlan SamplePlan() =>
        OperationPlan.Create(OperationType.Apply, ThreeSteps, ThreeTargets);

    private static OperationJournal SampleJournal(int processedStepCount, Guid planId) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: Guid.NewGuid(),
        Type: OperationType.Apply,
        Stage: OperationStage.Mutating,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 3,
        ProcessedStepCount: processedStepCount,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: planId,
        TargetHash: "irrelevant-to-this-test",
        RecoveryOfOperationId: null,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static OperationStepResult Result(int stepIndex, string identifier) => new(
        stepIndex, identifier, OperationStepDisposition.Succeeded, "Success", null, DateTimeOffset.UtcNow, 5);

    [Fact]
    public void Reconcile_ProcessedStepCountZero_ConsistentEvenWithNoResults()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 0, plan.OperationId);

        var result = StepResultReconciler.Reconcile(journal, plan, []);

        Assert.Equal(StepResultReconciliationStatus.Consistent, result.Status);
    }

    [Fact]
    public void Reconcile_ExactCoverageWithMatchingIdentifiers_Consistent()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 2, plan.OperationId);
        var results = new[] { Result(0, "mod-a"), Result(1, "mod-b") };

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Consistent, result.Status);
    }

    [Fact]
    public void Reconcile_ExtraResultsAheadOfCursor_StillConsistent()
    {
        // The append-before-checkpoint ordering means the log can legitimately be ahead of the
        // journal after a crash - this must not be treated as inconsistent.
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 1, plan.OperationId);
        var results = new[] { Result(0, "mod-a"), Result(1, "mod-b"), Result(2, "mod-c") };

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Consistent, result.Status);
    }

    [Fact]
    public void Reconcile_MissingResultBelowCursor_Indeterminate()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 2, plan.OperationId);
        var results = new[] { Result(0, "mod-a") }; // step 1 missing, but cursor claims 2 processed

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Indeterminate, result.Status);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Reconcile_DuplicateResultForSameStepIndexBelowCursor_Indeterminate()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 1, plan.OperationId);
        var results = new[] { Result(0, "mod-a"), Result(0, "mod-a") };

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Indeterminate, result.Status);
    }

    [Fact]
    public void Reconcile_IdentifierMismatchBelowCursor_Indeterminate()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 1, plan.OperationId);
        var results = new[] { Result(0, "wrong-identifier") }; // step 0's real identifier is "mod-a"

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Indeterminate, result.Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~StepResultReconcilerTests`
Expected: FAIL — `StepResultReconciler`, `StepResultReconciliationStatus`, `StepResultReconciliationResult` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum StepResultReconciliationStatus { Consistent, Indeterminate }

public sealed record StepResultReconciliationResult(StepResultReconciliationStatus Status, string? Reason);

/// <summary>
/// The journal, not the result log, is authoritative on committed progress (design doc section 5a).
/// This checks whether results.jsonl actually substantiates journal.ProcessedStepCount: every step
/// below the cursor must have exactly one result, with a matching identifier. Results at or past the
/// cursor are expected (append-before-checkpoint can leave the log ahead after a crash) and are
/// never inspected here - they don't relax or extend what's required below the cursor.
/// </summary>
public static class StepResultReconciler
{
    public static StepResultReconciliationResult Reconcile(
        OperationJournal journal, OperationPlan plan, IReadOnlyList<OperationStepResult> results)
    {
        var resultsByStepIndex = new Dictionary<int, List<OperationStepResult>>();
        foreach (var r in results)
        {
            if (r.StepIndex >= journal.ProcessedStepCount)
                continue; // ahead of the cursor - expected, not inspected

            if (!resultsByStepIndex.TryGetValue(r.StepIndex, out var list))
            {
                list = [];
                resultsByStepIndex[r.StepIndex] = list;
            }

            list.Add(r);
        }

        var stepByIndex = plan.ExecutionSteps.ToDictionary(s => s.StepIndex);

        for (var i = 0; i < journal.ProcessedStepCount; i++)
        {
            if (!resultsByStepIndex.TryGetValue(i, out var matches) || matches.Count == 0)
                return new StepResultReconciliationResult(
                    StepResultReconciliationStatus.Indeterminate, $"Missing result for step {i}.");

            if (matches.Count > 1)
                return new StepResultReconciliationResult(
                    StepResultReconciliationStatus.Indeterminate, $"Duplicate result for step {i}.");

            var expectedIdentifier = stepByIndex.TryGetValue(i, out var step) ? step.Identifier : null;
            if (matches[0].Identifier != expectedIdentifier)
                return new StepResultReconciliationResult(
                    StepResultReconciliationStatus.Indeterminate,
                    $"Result identifier '{matches[0].Identifier}' for step {i} does not match plan identifier '{expectedIdentifier}'.");
        }

        return new StepResultReconciliationResult(StepResultReconciliationStatus.Consistent, null);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~StepResultReconcilerTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/StepResultReconciler.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/StepResultReconcilerTests.cs
git commit -m "feat: add step result reconciliation against journal ProcessedStepCount"
```

---

### Task 3: DiagnosticsLog

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/DiagnosticsLog.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsLogTests.cs`

**Interfaces:**
- Produces:
  - `DiagnosticEventKind` enum: `SlowCall, SlowLiveSnapshot, Exception`.
  - `DiagnosticEvent(Guid? OperationId, DiagnosticEventKind Kind, DateTimeOffset RecordedAt, long? DurationMilliseconds, string? ExceptionTypeName, string? ExceptionMessage, string? TruncatedStackTrace)`.
  - `DiagnosticsLog.Append(string path, DiagnosticEvent evt)` (void) — never throws, even if the underlying write fails; truncates `TruncatedStackTrace` to 2000 characters if longer; after appending, if the file exceeds the retention cap, rewrites it keeping only the newest events.
  - `DiagnosticsLog.ReadAll(string path)` returns `IReadOnlyList<DiagnosticEvent>` — same truncated/corrupt-line tolerance as `StepResultLog.ReadAll`.

Design §10: "A diagnostics write failure must never stop or fail an operation. The sink swallows its own I/O exceptions internally (logs to the ordinary Dalamud log as a fallback, does not propagate)." This class is the pure, Dalamud-free primitive — it swallows failures silently (never throws), but does **not** itself log to Dalamud (there is no Dalamud dependency available at this layer). The "log to Dalamud as fallback" behavior belongs to a higher-level sink that wraps this in a later plan (Plan B/E), which can catch a bool/observe a failure and forward it to `IPluginLog`. Note this explicitly in the class's doc comment so a later reader isn't surprised `Append` has no logging side effect of its own.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class DiagnosticsLogTests
{
    private static DiagnosticEvent SlowCallEvent(Guid? operationId = null) => new(
        operationId, DiagnosticEventKind.SlowCall, DateTimeOffset.UtcNow,
        DurationMilliseconds: 75, ExceptionTypeName: null, ExceptionMessage: null, TruncatedStackTrace: null);

    [Fact]
    public void ReadAll_FileDoesNotExist_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Empty(DiagnosticsLog.ReadAll(Path.Combine(dir.FullName, "missing.jsonl")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppendThenReadAll_SingleEvent_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var evt = SlowCallEvent(Guid.NewGuid());

            DiagnosticsLog.Append(path, evt);
            var events = DiagnosticsLog.ReadAll(path);

            var single = Assert.Single(events);
            Assert.Equal(evt, single);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_NullOperationId_RoundTripsAsNull()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            DiagnosticsLog.Append(path, SlowCallEvent(operationId: null));

            var single = Assert.Single(DiagnosticsLog.ReadAll(path));
            Assert.Null(single.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_TruncatesStackTraceLongerThan2000Characters()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var longTrace = new string('x', 5000);
            var evt = new DiagnosticEvent(
                null, DiagnosticEventKind.Exception, DateTimeOffset.UtcNow,
                null, "System.InvalidOperationException", "boom", longTrace);

            DiagnosticsLog.Append(path, evt);
            var single = Assert.Single(DiagnosticsLog.ReadAll(path));

            Assert.NotNull(single.TruncatedStackTrace);
            Assert.True(single.TruncatedStackTrace!.Length <= 2000);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_DoesNotThrowWhenFileIsLocked()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            File.WriteAllText(path, "");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var exception = Record.Exception(() => DiagnosticsLog.Append(path, SlowCallEvent()));

            Assert.Null(exception);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_BeyondRetentionCap_KeepsOnlyNewestEvents()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            for (var i = 0; i < 2005; i++)
                DiagnosticsLog.Append(path, SlowCallEvent());

            var events = DiagnosticsLog.ReadAll(path);

            Assert.Equal(2000, events.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadAll_ToleratesCorruptLine()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            DiagnosticsLog.Append(path, SlowCallEvent());
            File.AppendAllText(path, "not valid json" + Environment.NewLine);

            var events = DiagnosticsLog.ReadAll(path);

            Assert.Single(events);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DiagnosticsLogTests`
Expected: FAIL — `DiagnosticsLog`, `DiagnosticEvent`, `DiagnosticEventKind` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum DiagnosticEventKind { SlowCall, SlowLiveSnapshot, Exception }

/// <summary> One diagnostic event. OperationId is null for events outside any active operation.
/// Exception* fields are populated only for Kind == Exception. Design doc section 10. </summary>
public sealed record DiagnosticEvent(
    Guid? OperationId,
    DiagnosticEventKind Kind,
    DateTimeOffset RecordedAt,
    long? DurationMilliseconds,
    string? ExceptionTypeName,
    string? ExceptionMessage,
    string? TruncatedStackTrace);

/// <summary>
/// Global (not per-operation) append-only diagnostics.jsonl - the durable source a future
/// diagnostics dump reads from. Append never throws: a diagnostics write failure must not become a
/// new failure mode for the operation that triggered it (design doc section 10). This class has no
/// Dalamud dependency and does not itself log a swallowed failure anywhere - a higher-level sink
/// built in a later plan wraps this and is responsible for the "log to the ordinary Dalamud log as a
/// fallback" behavior the design doc describes.
/// </summary>
public static class DiagnosticsLog
{
    private const int MaxRetainedEvents = 2000;
    private const int MaxStackTraceLength = 2000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Append(string path, DiagnosticEvent evt)
    {
        try
        {
            if (evt.TruncatedStackTrace is { Length: > MaxStackTraceLength } trace)
                evt = evt with { TruncatedStackTrace = trace[..MaxStackTraceLength] };

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(evt, SerializerOptions);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            TrimIfOverCap(path);
        }
        catch (Exception)
        {
            // Diagnostics existing to explain failures must not become a new failure mode itself.
        }
    }

    private static void TrimIfOverCap(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length <= MaxRetainedEvents)
            return;

        var newest = lines[^MaxRetainedEvents..];
        AtomicFile.CreateOrReplace(path, string.Join(Environment.NewLine, newest) + Environment.NewLine);
    }

    public static IReadOnlyList<DiagnosticEvent> ReadAll(string path)
    {
        if (!File.Exists(path))
            return [];

        var events = new List<DiagnosticEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            DiagnosticEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<DiagnosticEvent>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (evt is not null)
                events.Add(evt);
        }

        return events;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DiagnosticsLogTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/DiagnosticsLog.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsLogTests.cs
git commit -m "feat: add durable diagnostics log with retention cap and swallowed write failures"
```

---

### Task 4: OperationBundlePaths

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundlePaths.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundlePathsTests.cs`

**Interfaces:**
- Produces (all pure string functions, no I/O):
  - `OperationBundlePaths.ActiveDirectory(string operationsRoot)` → `string`
  - `OperationBundlePaths.CompletedDirectory(string operationsRoot)` → `string`
  - `OperationBundlePaths.DiagnosticsLogPath(string operationsRoot)` → `string`
  - `OperationBundlePaths.BundleDirectory(string operationsRoot, bool active, Guid operationId)` → `string`
  - `OperationBundlePaths.JournalPath(string bundleDirectory)` → `string`
  - `OperationBundlePaths.PlanPath(string bundleDirectory)` → `string`
  - `OperationBundlePaths.SnapshotPath(string bundleDirectory)` → `string`
  - `OperationBundlePaths.ResultsPath(string bundleDirectory)` → `string`

This is the single source of truth for the directory layout in design §4a — every later task (and later plans) computes paths through this class rather than concatenating strings inline, so the layout only needs to change in one place if it ever does.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationBundlePathsTests
{
    private const string Root = @"C:\config\operations";
    private static readonly Guid OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ActiveDirectory_IsRootSlashActive()
    {
        Assert.Equal(Path.Combine(Root, "active"), OperationBundlePaths.ActiveDirectory(Root));
    }

    [Fact]
    public void CompletedDirectory_IsRootSlashCompleted()
    {
        Assert.Equal(Path.Combine(Root, "completed"), OperationBundlePaths.CompletedDirectory(Root));
    }

    [Fact]
    public void DiagnosticsLogPath_IsRootSlashDiagnosticsJsonl()
    {
        Assert.Equal(Path.Combine(Root, "diagnostics.jsonl"), OperationBundlePaths.DiagnosticsLogPath(Root));
    }

    [Fact]
    public void BundleDirectory_Active_IsUnderActiveDirectoryNamedByOperationId()
    {
        var expected = Path.Combine(Root, "active", OperationId.ToString());
        Assert.Equal(expected, OperationBundlePaths.BundleDirectory(Root, active: true, OperationId));
    }

    [Fact]
    public void BundleDirectory_Completed_IsUnderCompletedDirectoryNamedByOperationId()
    {
        var expected = Path.Combine(Root, "completed", OperationId.ToString());
        Assert.Equal(expected, OperationBundlePaths.BundleDirectory(Root, active: false, OperationId));
    }

    [Fact]
    public void JournalPlanSnapshotResults_AreNamedFilesUnderTheBundleDirectory()
    {
        var bundleDir = OperationBundlePaths.BundleDirectory(Root, active: true, OperationId);

        Assert.Equal(Path.Combine(bundleDir, "journal.json"), OperationBundlePaths.JournalPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "plan.json"), OperationBundlePaths.PlanPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "snapshot.json"), OperationBundlePaths.SnapshotPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "results.jsonl"), OperationBundlePaths.ResultsPath(bundleDir));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationBundlePathsTests`
Expected: FAIL — `OperationBundlePaths` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// The single source of truth for the operation storage directory layout (design doc section 4a).
/// One directory per operation ID makes duplicate-ID collisions structurally impossible - the
/// filesystem itself is the uniqueness constraint, no runtime check needed for that failure mode.
/// </summary>
public static class OperationBundlePaths
{
    public static string ActiveDirectory(string operationsRoot) => Path.Combine(operationsRoot, "active");

    public static string CompletedDirectory(string operationsRoot) => Path.Combine(operationsRoot, "completed");

    public static string DiagnosticsLogPath(string operationsRoot) => Path.Combine(operationsRoot, "diagnostics.jsonl");

    public static string BundleDirectory(string operationsRoot, bool active, Guid operationId) =>
        Path.Combine(active ? ActiveDirectory(operationsRoot) : CompletedDirectory(operationsRoot), operationId.ToString());

    public static string JournalPath(string bundleDirectory) => Path.Combine(bundleDirectory, "journal.json");

    public static string PlanPath(string bundleDirectory) => Path.Combine(bundleDirectory, "plan.json");

    public static string SnapshotPath(string bundleDirectory) => Path.Combine(bundleDirectory, "snapshot.json");

    public static string ResultsPath(string bundleDirectory) => Path.Combine(bundleDirectory, "results.jsonl");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationBundlePathsTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundlePaths.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundlePathsTests.cs
git commit -m "feat: add OperationBundlePaths as the single source of truth for storage layout"
```

---

### Task 5: OperationRecoveryGraph

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationRecoveryGraph.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationRecoveryGraphTests.cs`

**Interfaces:**
- Consumes: `OperationJournal` (`OperationId`, `RecoveryOfOperationId`) — takes a plain in-memory list, no file I/O, no dependency on Task 6.
- Produces:
  - `OperationRecoveryGraphStatus` enum: `SingleAuthoritative, MultipleDisconnectedRoots, CycleDetected`.
  - `OperationRecoveryGraphResult(OperationRecoveryGraphStatus Status, IReadOnlyList<Guid> AuthoritativeOperationIds, IReadOnlyList<Guid> AllOperationIds)` — `AuthoritativeOperationIds` has exactly one entry for `SingleAuthoritative`, one entry per disconnected leaf for `MultipleDisconnectedRoots`, and the set of operation IDs involved in a detected cycle for `CycleDetected`.
  - `OperationRecoveryGraph.Analyze(IReadOnlyList<OperationJournal> journals)` returns `OperationRecoveryGraphResult`.

This is design §4a's discovery algorithm, steps 2–5, as pure logic — the caller (Task 6) is responsible for having already run the startup cleanup pass and loaded only non-terminal journals before calling this. A journal's `RecoveryOfOperationId` only forms a graph edge if the referenced parent is *also* present in the input list — a parent that already terminalized cleanly (the common case) isn't in this list at all, so the child is simply its own single-node, `SingleAuthoritative` component. This is exactly why passing an empty list, or a list where nothing references anything else in it, must produce `SingleAuthoritative`-per-journal or `MultipleDisconnectedRoots`, never a spurious cycle or crash.

**"Leaf"** (authoritative within its component) means: no *other* journal in the input list has `RecoveryOfOperationId` equal to this journal's `OperationId`. A component with several chained journals (grandparent → parent → child) has exactly one leaf (the child); ancestors are included in `AllOperationIds` but never in `AuthoritativeOperationIds`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationRecoveryGraphTests
{
    private static OperationJournal Journal(Guid id, Guid? recoveryOf) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: id,
        Type: OperationType.Apply,
        Stage: OperationStage.Mutating,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 10,
        ProcessedStepCount: 3,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "irrelevant",
        RecoveryOfOperationId: recoveryOf,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Analyze_EmptyList_SingleAuthoritativeIsMeaninglessButMustNotThrow()
    {
        var result = OperationRecoveryGraph.Analyze([]);

        Assert.Empty(result.AuthoritativeOperationIds);
        Assert.Empty(result.AllOperationIds);
    }

    [Fact]
    public void Analyze_SingleUnrelatedJournal_SingleAuthoritative()
    {
        var id = Guid.NewGuid();
        var result = OperationRecoveryGraph.Analyze([Journal(id, recoveryOf: null)]);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([id], result.AuthoritativeOperationIds);
    }

    [Fact]
    public void Analyze_JournalWhoseParentIsNotInTheSet_TreatedAsItsOwnSingleAuthoritativeComponent()
    {
        // The parent already terminalized cleanly and isn't in this "non-terminal journals" list -
        // the common case. The child must not be treated as part of a cycle or an orphaned edge.
        var childId = Guid.NewGuid();
        var result = OperationRecoveryGraph.Analyze([Journal(childId, recoveryOf: Guid.NewGuid())]);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([childId], result.AuthoritativeOperationIds);
    }

    [Fact]
    public void Analyze_TwoJournalChain_ChildIsAuthoritativeParentIsNot()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var journals = new[] { Journal(parentId, recoveryOf: null), Journal(childId, recoveryOf: parentId) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([childId], result.AuthoritativeOperationIds);
        Assert.Equal(2, result.AllOperationIds.Count);
    }

    [Fact]
    public void Analyze_ThreeJournalChain_OnlyTheYoungestGrandchildIsAuthoritative()
    {
        var grandparentId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var journals = new[]
        {
            Journal(grandparentId, recoveryOf: null),
            Journal(parentId, recoveryOf: grandparentId),
            Journal(childId, recoveryOf: parentId),
        };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([childId], result.AuthoritativeOperationIds);
        Assert.Equal(3, result.AllOperationIds.Count);
    }

    [Fact]
    public void Analyze_TwoIndependentJournals_MultipleDisconnectedRoots()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var journals = new[] { Journal(idA, recoveryOf: null), Journal(idB, recoveryOf: null) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, result.Status);
        Assert.Equal(new HashSet<Guid> { idA, idB }, result.AuthoritativeOperationIds.ToHashSet());
    }

    [Fact]
    public void Analyze_DirectCycle_CycleDetected()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var journals = new[] { Journal(idA, recoveryOf: idB), Journal(idB, recoveryOf: idA) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.CycleDetected, result.Status);
        Assert.Equal(new HashSet<Guid> { idA, idB }, result.AuthoritativeOperationIds.ToHashSet());
    }

    [Fact]
    public void Analyze_ThreeWayCycle_CycleDetected()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var journals = new[] { Journal(idA, recoveryOf: idB), Journal(idB, recoveryOf: idC), Journal(idC, recoveryOf: idA) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.CycleDetected, result.Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationRecoveryGraphTests`
Expected: FAIL — `OperationRecoveryGraph`, `OperationRecoveryGraphStatus`, `OperationRecoveryGraphResult` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationRecoveryGraphStatus { SingleAuthoritative, MultipleDisconnectedRoots, CycleDetected }

public sealed record OperationRecoveryGraphResult(
    OperationRecoveryGraphStatus Status,
    IReadOnlyList<Guid> AuthoritativeOperationIds,
    IReadOnlyList<Guid> AllOperationIds);

/// <summary>
/// Design doc section 4a, steps 2-5: given a set of already-loaded, already-confirmed-non-terminal
/// journals, find which one (or which several, if genuinely ambiguous) is operationally
/// authoritative after a crash. A RecoveryOfOperationId only forms a graph edge if the referenced
/// parent is present in this same input set - a parent that already terminalized cleanly (the
/// common case) simply isn't in the "non-terminal journals" list, so its child is its own
/// single-node component, not a dangling edge.
/// </summary>
public static class OperationRecoveryGraph
{
    public static OperationRecoveryGraphResult Analyze(IReadOnlyList<OperationJournal> journals)
    {
        var idSet = journals.Select(j => j.OperationId).ToHashSet();
        var allIds = idSet.ToList();

        // Only edges where the parent is also in this set count for graph structure.
        var childToParent = journals
            .Where(j => j.RecoveryOfOperationId is { } parentId && idSet.Contains(parentId))
            .ToDictionary(j => j.OperationId, j => j.RecoveryOfOperationId!.Value);

        if (TryFindCycle(childToParent, out var cycleMembers))
            return new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.CycleDetected, cycleMembers.ToList(), allIds);

        // A journal is a leaf (authoritative within its component) if no other journal in the set
        // points at it as a parent.
        var referencedAsParent = childToParent.Values.ToHashSet();
        var leaves = allIds.Where(id => !referencedAsParent.Contains(id)).ToList();

        return leaves.Count switch
        {
            1 => new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, leaves, allIds),
            _ => new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, leaves, allIds),
        };
    }

    private static bool TryFindCycle(Dictionary<Guid, Guid> childToParent, out HashSet<Guid> cycleMembers)
    {
        var globallyResolved = new HashSet<Guid>();

        foreach (var start in childToParent.Keys)
        {
            if (globallyResolved.Contains(start))
                continue;

            var pathVisited = new HashSet<Guid>();
            var current = start;
            while (childToParent.TryGetValue(current, out var parent))
            {
                if (!pathVisited.Add(current))
                {
                    cycleMembers = pathVisited;
                    return true;
                }

                current = parent;
            }

            pathVisited.Add(current); // the terminal node this path walked up to (not itself a child of anything in-set)
            globallyResolved.UnionWith(pathVisited);
        }

        cycleMembers = [];
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationRecoveryGraphTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationRecoveryGraph.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationRecoveryGraphTests.cs
git commit -m "feat: add pure recovery-graph discovery (leaves, cycles, disconnected roots)"
```

---

### Task 6: OperationBundleDiscovery

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundleDiscovery.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleDiscoveryTests.cs`

**Interfaces:**
- Consumes: `OperationBundlePaths` (Task 4), `OperationRecoveryGraph` (Task 5), `OperationJournalCodec` (Plan A1).
- Produces:
  - `OperationDiscoveryResult(OperationRecoveryGraphResult Graph, IReadOnlyDictionary<Guid, OperationJournal> Journals)` — `Journals` contains only the successfully-loaded, non-terminal journals that fed the graph analysis.
  - `OperationBundleDiscovery.RunStartupDiscovery(string operationsRoot)` returns `OperationDiscoveryResult` — first relocates any already-terminal bundle sitting under `active/` to `completed/` (self-healing, not a recovery condition), then loads every remaining `active/*/journal.json` (skipping any that fail to load or parse, without throwing), then runs `OperationRecoveryGraph.Analyze` over the successfully-loaded set.

The bundle's *directory name* is the operation ID (`OperationBundlePaths.BundleDirectory`'s `operationId.ToString()`) — this task trusts the directory name is authoritative for enumeration purposes, but always uses the loaded journal's own `OperationId` field (not the folder name) as the dictionary key, so a mismatched/tampered folder name can't corrupt the graph.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationBundleDiscoveryTests
{
    private static OperationJournal Journal(Guid id, OperationStage stage, Guid? recoveryOf = null) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: id,
        Type: OperationType.Apply,
        Stage: stage,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 10,
        ProcessedStepCount: 3,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "irrelevant",
        RecoveryOfOperationId: recoveryOf,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static void SaveActiveBundle(string root, OperationJournal journal)
    {
        var bundleDir = OperationBundlePaths.BundleDirectory(root, active: true, journal.OperationId);
        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), journal);
    }

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

    [Fact]
    public void RunStartupDiscovery_OneNonTerminalActiveBundle_IsLoadedAndAuthoritative()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(id, OperationStage.Mutating));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Single(result.Journals);
            Assert.True(result.Journals.ContainsKey(id));
            Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Graph.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_TerminalBundleUnderActive_IsRelocatedToCompletedAndExcluded()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(id, OperationStage.Completed)); // already terminal

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Empty(result.Journals); // excluded from the non-terminal set
            Assert.False(Directory.Exists(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id)));
            Assert.True(File.Exists(OperationBundlePaths.JournalPath(
                OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id))));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_CorruptJournalFile_IsSkippedWithoutThrowing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var corruptId = Guid.NewGuid();
            var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, corruptId);
            Directory.CreateDirectory(bundleDir);
            File.WriteAllText(OperationBundlePaths.JournalPath(bundleDir), "not valid json");

            var validId = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(validId, OperationStage.Mutating));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Single(result.Journals);
            Assert.True(result.Journals.ContainsKey(validId));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_ParentAndChildBundles_ChildIsAuthoritative()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(parentId, OperationStage.Mutating));
            SaveActiveBundle(dir.FullName, Journal(childId, OperationStage.Preparing, recoveryOf: parentId));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Equal(2, result.Journals.Count);
            Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Graph.Status);
            Assert.Equal([childId], result.Graph.AuthoritativeOperationIds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationBundleDiscoveryTests`
Expected: FAIL — `OperationBundleDiscovery`, `OperationDiscoveryResult` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public sealed record OperationDiscoveryResult(
    OperationRecoveryGraphResult Graph,
    IReadOnlyDictionary<Guid, OperationJournal> Journals);

/// <summary>
/// Design doc section 4a. Startup entry point: relocate any already-terminal bundle sitting under
/// active/ to completed/ (self-healing, not a recovery condition), then load the remaining
/// non-terminal journals and hand them to OperationRecoveryGraph. A journal that fails to load is
/// logged (by the caller, in a later plan - this class has no logging dependency) and excluded, not
/// treated as fatal to startup.
/// </summary>
public static class OperationBundleDiscovery
{
    public static OperationDiscoveryResult RunStartupDiscovery(string operationsRoot)
    {
        RelocateTerminalActiveBundles(operationsRoot);
        var journals = LoadNonTerminalActiveJournals(operationsRoot);
        var graph = OperationRecoveryGraph.Analyze(journals.Values.ToList());
        return new OperationDiscoveryResult(graph, journals);
    }

    private static void RelocateTerminalActiveBundles(string operationsRoot)
    {
        var activeDir = OperationBundlePaths.ActiveDirectory(operationsRoot);
        if (!Directory.Exists(activeDir))
            return;

        foreach (var bundleDir in Directory.GetDirectories(activeDir))
        {
            if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) || journal is null)
                continue; // corrupt/unparseable - leave it for a human, not our concern here
            if (!journal.IsTerminal)
                continue;

            var completedBundleDir = OperationBundlePaths.BundleDirectory(operationsRoot, active: false, journal.OperationId);
            try
            {
                if (Directory.Exists(completedBundleDir))
                    continue; // already relocated by something else - don't clobber it
                Directory.CreateDirectory(OperationBundlePaths.CompletedDirectory(operationsRoot));
                Directory.Move(bundleDir, completedBundleDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unmovable bundle must not block startup or the rest of this pass.
            }
        }
    }

    private static Dictionary<Guid, OperationJournal> LoadNonTerminalActiveJournals(string operationsRoot)
    {
        var result = new Dictionary<Guid, OperationJournal>();
        var activeDir = OperationBundlePaths.ActiveDirectory(operationsRoot);
        if (!Directory.Exists(activeDir))
            return result;

        foreach (var bundleDir in Directory.GetDirectories(activeDir))
        {
            if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) || journal is null)
                continue;
            if (journal.IsTerminal)
                continue; // should have been relocated already; defensively excluded either way

            result[journal.OperationId] = journal;
        }

        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationBundleDiscoveryTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundleDiscovery.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleDiscoveryTests.cs
git commit -m "feat: add startup bundle discovery with terminal-bundle self-healing relocation"
```

---

### Task 7: OperationBundleRetention

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundleRetention.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleRetentionTests.cs`

**Interfaces:**
- Consumes: `OperationBundlePaths` (Task 4), `OperationJournalCodec` (Plan A1).
- Produces: `OperationBundleRetention.RunRetentionPass(string operationsRoot, DateTimeOffset now, int retainNewestCount = 50, TimeSpan? retainAge = null)` (void) — `retainAge` defaults to 30 days. `retainNewestCount`/`retainAge` are exposed as parameters (not hardcoded constants) specifically so tests can exercise the cap/age boundaries without creating dozens of real bundles — the design doc's own 30-day/50-bundle values remain the defaults a real caller uses.

Only operates on `completed/` bundles — `active/` bundles are never touched by retention (design §4a: "active non-terminal journal: retained indefinitely"). A bundle is **deleted** only if it is older than `retainAge` **and** falls outside the newest `retainNewestCount` bundles **and** is not referenced (directly or transitively via `RecoveryOfOperationId`) by any bundle that is itself being retained. A bundle whose journal fails to load is never a deletion candidate — protected by exclusion, not by an explicit check (design §4a: "never delete when reference analysis is inconclusive").

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationBundleRetentionTests
{
    private static OperationJournal Journal(Guid id, DateTimeOffset updatedAt, Guid? recoveryOf = null) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: id,
        Type: OperationType.Apply,
        Stage: OperationStage.Completed,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: updatedAt,
        TotalSteps: 10,
        ProcessedStepCount: 10,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "irrelevant",
        RecoveryOfOperationId: recoveryOf,
        UpdatedAt: updatedAt);

    private static void SaveCompletedBundle(string root, OperationJournal journal)
    {
        var bundleDir = OperationBundlePaths.BundleDirectory(root, active: false, journal.OperationId);
        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), journal);
    }

    private static bool BundleExists(string root, Guid id) =>
        Directory.Exists(OperationBundlePaths.BundleDirectory(root, active: false, id));

    [Fact]
    public void RunRetentionPass_YoungBundle_IsKept()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(id, now.AddDays(-1)));

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(BundleExists(dir.FullName, id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_OldUnreferencedUnrankedBundle_IsDeleted()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(id, now.AddDays(-60)));

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.False(BundleExists(dir.FullName, id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_OldBundleWithinNewestCap_IsKept()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(id, now.AddDays(-60)));

            // Cap of 1 with only one bundle total means it's within the newest 1, regardless of age.
            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 1, retainAge: TimeSpan.FromDays(30));

            Assert.True(BundleExists(dir.FullName, id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_OldBundleReferencedByARetainedChild_IsKept()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(parentId, now.AddDays(-60))); // old, would be deleted alone
            SaveCompletedBundle(dir.FullName, Journal(childId, now.AddDays(-1), recoveryOf: parentId)); // young, retained

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(BundleExists(dir.FullName, childId));
            Assert.True(BundleExists(dir.FullName, parentId)); // kept because the retained child references it
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_ActiveBundlesAreNeverTouched()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var activeBundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDir), Journal(id, now.AddDays(-90)));

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(Directory.Exists(activeBundleDir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_BundleWithCorruptJournal_IsNeverDeleted()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id);
            Directory.CreateDirectory(bundleDir);
            File.WriteAllText(OperationBundlePaths.JournalPath(bundleDir), "not valid json");

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(Directory.Exists(bundleDir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_NoCompletedDirectory_DoesNotThrow()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var exception = Record.Exception(() =>
                OperationBundleRetention.RunRetentionPass(dir.FullName, DateTimeOffset.UtcNow));

            Assert.Null(exception);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationBundleRetentionTests`
Expected: FAIL — `OperationBundleRetention` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Design doc section 4a retention rules. Only touches completed/ - active/ bundles are retained
/// indefinitely and never considered here. A bundle whose journal fails to load is excluded from
/// the loaded set entirely, which means it can never be a deletion candidate - protection by
/// omission, matching "never delete when reference analysis is inconclusive".
/// </summary>
public static class OperationBundleRetention
{
    private const int DefaultRetainNewestCount = 50;
    private static readonly TimeSpan DefaultRetainAge = TimeSpan.FromDays(30);

    public static void RunRetentionPass(
        string operationsRoot, DateTimeOffset now,
        int retainNewestCount = DefaultRetainNewestCount, TimeSpan? retainAge = null)
    {
        var age = retainAge ?? DefaultRetainAge;
        var completedDir = OperationBundlePaths.CompletedDirectory(operationsRoot);
        if (!Directory.Exists(completedDir))
            return;

        var loaded = new Dictionary<Guid, OperationJournal>();
        foreach (var bundleDir in Directory.GetDirectories(completedDir))
        {
            if (OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) && journal is not null)
                loaded[journal.OperationId] = journal;
        }

        var newest = loaded.Values
            .OrderByDescending(j => j.UpdatedAt)
            .Take(retainNewestCount)
            .Select(j => j.OperationId)
            .ToHashSet();

        var retained = new HashSet<Guid>();
        foreach (var (id, journal) in loaded)
        {
            if (now - journal.UpdatedAt <= age || newest.Contains(id))
                retained.Add(id);
        }

        // Transitive closure: a bundle referenced by a retained bundle is retained too, however far up the chain.
        var toProcess = new Queue<Guid>(retained);
        while (toProcess.Count > 0)
        {
            var id = toProcess.Dequeue();
            if (loaded.TryGetValue(id, out var journal)
                && journal.RecoveryOfOperationId is { } parentId
                && loaded.ContainsKey(parentId)
                && retained.Add(parentId))
            {
                toProcess.Enqueue(parentId);
            }
        }

        foreach (var id in loaded.Keys)
        {
            if (retained.Contains(id))
                continue;

            try
            {
                Directory.Delete(OperationBundlePaths.BundleDirectory(operationsRoot, active: false, id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One undeletable bundle must not prevent plugin startup or block cleanup of the rest.
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationBundleRetentionTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationBundleRetention.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationBundleRetentionTests.cs
git commit -m "feat: add fail-safe bundle retention with reference-protected transitive closure"
```

---

### Task 8: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test plus everything added in Tasks 1–7, zero failures.

- [ ] **Step 2: Confirm the working tree is clean and no stray temp dirs leaked**

Run: `git status --short`
Expected: no output (all tests write to `Directory.CreateTempSubdirectory()` outside the repo and delete in `finally`).

---

## What this plan does not cover

Deferred to **Plan B** (design §13) and later plans:

- `IPenumbraOperations` adapter interface and `PenumbraOperationsAdapter` — nothing in Plan A1 or A2 consumes them yet, so they're built where first needed.
- A codec for `snapshot.json` (a `RollbackSnapshot` copy inside an operation bundle) — this plan's discovery/retention logic only ever reads `journal.json` and moves/deletes whole bundle *directories*; it never needs to parse `plan.json`/`snapshot.json` individually. The first thing that needs to *write* a `snapshot.json` is Plan D's Continue/Restore resolution (design §9, step 5), which is where that codec belongs.
- `OperationController`, `PathMutationOperation`, frame-budgeted execution, `Refreshing`, verification settlement — Plan B.
- Recovery classification/assessment (`RecoveryClassifier` v2, `RecoveryAssessment`), the three recovery resolutions, startup wiring into `Plugin.cs` — Plan D.
- Any `MainWindow`/diagnostics-dump UI wiring — Plan E.
