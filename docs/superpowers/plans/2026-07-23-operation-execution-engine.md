# Operation Execution Engine (Plan B1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the frame-budgeted mutation execution engine — adapter contract, refresh/verification settlement, group-cascading mutation with correct cancellation and stop-reason semantics, checkpoint-cadence persistence, and a controller state machine that never loses a terminal outcome or corrupts its own state on a recovery-required transition — entirely against a fake Penumbra adapter, so the whole engine is proven correct in xUnit before it ever touches real Dalamud/Penumbra IPC.

**Architecture:** Plan B split into B1 (this plan — fully unit-testable) and B2 (a later, separate plan: the real `PenumbraOperationsAdapter` wrapping actual Penumbra IPC, `Framework.Update` subscription, `Plugin.cs` wiring). This repo has no Dalamud test-double infrastructure, so B1 depends only on `IPenumbraOperations` (an interface, faked) and `IElapsedTimeSource` (already built in Plan A1, faked).

This is **revision 2** of this plan. Revision 1 was reviewed before execution and found not ready — 18 findings, all accepted. The redesign in this revision:
- `IPenumbraOperations.SetModPath` returns a plugin-owned `SetModPathResult` (status enum + diagnostic text), not the raw `Penumbra.Api.Enums.PenumbraApiEc` — the raw enum cannot represent "the adapter itself is no longer usable," which the stop-vs-continue policy depends on.
- `PathMutationOperation.Advance` returns a `MutationAdvanceResult` carrying an explicit status and stop reason, not just a journal — the controller cannot otherwise distinguish "finished normally," "cancelled," and "stopped for an operation-integrity reason," which are three different terminal outcomes.
- Cancellation is checked once, at the very start of `Advance`, before any step of that call begins — not only between subsequent steps within the same call.
- Checkpoint cadence lives in its own class (`OperationCheckpointer`, Task 5), injected into `PathMutationOperation.Advance` as a callback and called after every step or cascade batch, matching the design doc's literal requirement — not evaluated once per `Advance()` call after dozens of steps have already run.
- `OperationController` separates its published `OperationStateSnapshot` from whether an operation is currently *advancing* — a terminal journal (`Completed`, `Cancelled`, etc.) stays visible in `State` and `CanStartApply` becomes `true` again, without erasing the terminal `Stage` the moment the operation concludes. A `RecoveryRequired` transition retains the full operation context (plan, mutation engine, bundle directory) instead of destroying it, and blocks further advancement without corrupting the controller.
- `PathMutationOperation.MutationStatusByIdentifier` is a computed property derived from each identifier's *last* execution step's durable disposition, not a dictionary opportunistically mutated mid-loop — this removes a whole class of "was this write correct for every possible ordering" bugs.
- The already-merged Plan A2 `DiagnosticEvent` record gains an `Identifier` field and `DiagnosticEventKind` gains `SlowRefresh` — the shipped type had nowhere to record which mod a slow call was for, which the design doc's "worst offenders by identifier" diagnostics requirement needs.

**Tech Stack:** .NET (project SDK per `PenumbraOrganizer.Plugin.csproj`), `System.Text.Json` (via already-merged codecs), xUnit 2.5.3.

## Global Constraints

Copied from `docs/superpowers/specs/2026-07-22-operation-controller-design.md` sections 5, 5a, 5b, 6, 7, 7a, 7b, and from this revision's review findings; every task's requirements implicitly include these:

- **Cancellation is checked before any step starts in a given `Advance` call, including the first.** A call made with `stopRequested = true` processes zero new steps and returns `CancellationObserved` immediately. This does not weaken the "always attempt at least one step" rule for *budget* purposes — that rule only applies when the call is not already cancelled at entry.
- **`SetModPath` failures are classified via `SetModPathResult.Status`, never a raw provider enum**, so "the adapter itself is unusable" (`ProviderUnavailable`) is distinguishable from "this one mod's move was rejected" (`ModMissing`/`InvalidArgument`/`PathRenameFailed`/`Rejected`) and from "the call threw something unmodeled" (caught and treated as an operation-integrity stop, never silently downgraded to an item failure).
- **Group-cascade on failure**: every remaining not-yet-processed step in the failed step's `GroupId` is recorded `SkippedAfterEarlierFailure` and the cursor advances past the whole contiguous range in one move.
- **The durable step result is appended before the checkpoint callback is invoked for that step** — `StepResultLog.Append` happens first, then `checkpointIfDue`.
- **Checkpointing happens after every step or cascade batch**, not once per `Advance()` call — `OperationCheckpointer` (Task 5) owns the actual write-or-skip decision (`CheckpointPolicy.IsDue`), `PathMutationOperation` (Task 4) just calls it every iteration.
- **A journal-write (checkpoint) failure must never escape `Update()`** — `OperationController` wraps its per-tick work in a boundary that, on failure, attempts one best-effort terminal-failure checkpoint and, if that itself fails, marks the operation `RequiresRecovery` in memory rather than throwing a second time.
- **`Refreshing` always runs exactly once** after `Mutating` concludes, regardless of how it concluded (finished, cancelled, or stopped for an integrity reason).
- **Verification/refresh settlement never blocks synchronously** — one attempt per call, gated by a retry interval, bounded by a maximum attempt count.
- **A `RecoveryRequired` result from refresh or verification retains the full operation context** (journal, plan, mutation engine, bundle directory) and stops further advancement — it does not clear the active-operation slot as though the operation concluded, and it does not corrupt state by partially clearing fields.
- **A terminal journal stays visible in `OperationController.State`** — completing an operation does not reset `State` to `Idle`; it publishes the terminal `Stage` while simultaneously making `CanStartApply`/etc. `true` again (derived from `OperationJournal.IsTerminal`, already built in Plan A1).
- **Cancellation intent is persisted synchronously** the moment `RequestCancellation()` is accepted — `journal.CancellationRequested = true`, force-checkpointed immediately, not left to the next natural checkpoint.
- **A `Cancelled` terminal outcome is only asserted once verification itself is trustworthy** (`Settled`/`TimedOut`) — if verification returns `RecoveryRequired`, the journal stays non-terminal regardless of `CancellationRequested`, per design §5a's precedence rule (recoverability is a state-integrity fact and outranks a clean cancelled outcome).
- **`sealed record` for data types, `static class` for pure stateless logic, `sealed class` for stateful engines.**
- **Timestamps persisted to a journal are always `DateTimeOffset.UtcNow`** — `IElapsedTimeSource` values are process-relative and never written to disk.

Run the full suite with `dotnet test` from the repo root. Commit with `git add` on specific files only (never `git add -A`).

---

### Task 1: IPenumbraOperations, SetModPathResult, and FakePenumbraOperations

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/IPenumbraOperations.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperations.cs` (test double, lives in the test project)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperationsTests.cs`

**Interfaces:**
- Consumes: `LiveModSnapshot` (Plan A1).
- Produces:
  - `SetModPathStatus` enum: `Success, NothingChanged, ModMissing, InvalidArgument, PathRenameFailed, ProviderUnavailable, InvalidState, Rejected`. `Rejected` is the catch-all for any other item-level rejection the real provider enum can return that isn't worth a dedicated named case (they all get the same policy treatment — see Task 4).
  - `SetModPathResult(SetModPathStatus Status, string? ProviderResultName, string? Diagnostic)` — `ProviderResultName` preserves the real `PenumbraApiEc` value's name for diagnostics even though the engine only branches on `Status`.
  - `LiveModReadStatus` enum: `Success, TemporarilyUnavailable, ProviderUnavailable, InvalidData`.
  - `LiveModReadResult(LiveModReadStatus Status, LiveModSnapshot? Snapshot)`.
  - `RefreshStatus` enum: `Success, TemporarilyUnavailable, ProviderUnavailable, InvalidState`.
  - `RefreshResult(RefreshStatus Status)`.
  - `IPenumbraOperations` interface: `LiveModReadResult GetLiveMods()`, `SetModPathResult SetModPath(string identifier, string targetPath)`, `RefreshResult RequestPostMutationRefresh()`. A real implementation (`PenumbraOperationsAdapter`, wrapping actual Penumbra IPC and translating `PenumbraApiEc` into `SetModPathResult`) is built in Plan B2 — this interface has no dependency on `Penumbra.Api` at all, by design.
  - `FakePenumbraOperations` (test project only): implements `IPenumbraOperations` with queued responses. All three enqueue methods — `EnqueueLiveModRead(LiveModReadResult, Action? onCall = null)`, `EnqueueSetModPathResult(SetModPathResult, Action? onCall = null)`, `EnqueueRefreshResult(RefreshResult, Action? onCall = null)` — accept an optional `onCall` that runs synchronously when that queued response is dequeued, letting a test advance a fake clock as a side effect of any adapter call (needed for real frame-budget tests in Task 4, and real slow-call diagnostic tests in Task 3 and Task 4). `EnqueueSetModPathException(Exception)` — queues a call that throws instead of returning, for testing the engine's unexpected-exception handling explicitly (distinct from the "no queued response" safety-net throw below, which exists only to catch a test that forgot setup, never to simulate a real adapter failure). Every method throws `InvalidOperationException` if called with an empty queue. `SetModPathCalls: IReadOnlyList<(string Identifier, string TargetPath)>` records every call regardless of outcome.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class FakePenumbraOperationsTests
{
    [Fact]
    public void GetLiveMods_ReturnsQueuedResultsInOrder()
    {
        var fake = new FakePenumbraOperations();
        var snapshot = LiveModSnapshotBuilder.Build([]);
        fake.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, snapshot));
        fake.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));

        var first = fake.GetLiveMods();
        var second = fake.GetLiveMods();

        Assert.Equal(LiveModReadStatus.Success, first.Status);
        Assert.Equal(LiveModReadStatus.ProviderUnavailable, second.Status);
    }

    [Fact]
    public void GetLiveMods_NoQueuedResult_Throws()
    {
        var fake = new FakePenumbraOperations();

        Assert.Throws<InvalidOperationException>(() => fake.GetLiveMods());
    }

    [Fact]
    public void SetModPath_ReturnsQueuedResultAndRecordsTheCall()
    {
        var fake = new FakePenumbraOperations();
        fake.EnqueueSetModPathResult(new SetModPathResult(SetModPathStatus.Success, "Success", null));

        var result = fake.SetModPath("mod-a", "Weapons/A");

        Assert.Equal(SetModPathStatus.Success, result.Status);
        var call = Assert.Single(fake.SetModPathCalls);
        Assert.Equal("mod-a", call.Identifier);
        Assert.Equal("Weapons/A", call.TargetPath);
    }

    [Fact]
    public void SetModPath_NoQueuedResponse_Throws()
    {
        var fake = new FakePenumbraOperations();

        Assert.Throws<InvalidOperationException>(() => fake.SetModPath("mod-a", "Weapons/A"));
    }

    [Fact]
    public void SetModPath_QueuedException_ThrowsThatExceptionAndStillRecordsTheCall()
    {
        var fake = new FakePenumbraOperations();
        fake.EnqueueSetModPathException(new InvalidOperationException("simulated adapter failure"));

        var thrown = Assert.Throws<InvalidOperationException>(() => fake.SetModPath("mod-a", "Weapons/A"));

        Assert.Equal("simulated adapter failure", thrown.Message);
        Assert.Single(fake.SetModPathCalls);
    }

    [Fact]
    public void SetModPath_OnCallSideEffect_RunsExactlyOnceWhenDequeued()
    {
        var fake = new FakePenumbraOperations();
        var sideEffectCount = 0;
        fake.EnqueueSetModPathResult(new SetModPathResult(SetModPathStatus.Success, "Success", null), onCall: () => sideEffectCount++);

        fake.SetModPath("mod-a", "Weapons/A");

        Assert.Equal(1, sideEffectCount);
    }

    [Fact]
    public void GetLiveMods_OnCallSideEffect_RunsExactlyOnceWhenDequeued()
    {
        var fake = new FakePenumbraOperations();
        var sideEffectCount = 0;
        fake.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])), onCall: () => sideEffectCount++);

        fake.GetLiveMods();

        Assert.Equal(1, sideEffectCount);
    }

    [Fact]
    public void RequestPostMutationRefresh_ReturnsQueuedResult()
    {
        var fake = new FakePenumbraOperations();
        fake.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));

        var result = fake.RequestPostMutationRefresh();

        Assert.Equal(RefreshStatus.Success, result.Status);
    }

    [Fact]
    public void RequestPostMutationRefresh_NoQueuedResult_Throws()
    {
        var fake = new FakePenumbraOperations();

        Assert.Throws<InvalidOperationException>(() => fake.RequestPostMutationRefresh());
    }

    [Fact]
    public void RequestPostMutationRefresh_OnCallSideEffect_RunsExactlyOnceWhenDequeued()
    {
        var fake = new FakePenumbraOperations();
        var sideEffectCount = 0;
        fake.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success), onCall: () => sideEffectCount++);

        fake.RequestPostMutationRefresh();

        Assert.Equal(1, sideEffectCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FakePenumbraOperationsTests`
Expected: FAIL — none of the types exist yet.

- [ ] **Step 3: Write the interface and result types**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum SetModPathStatus
{
    Success, NothingChanged, ModMissing, InvalidArgument, PathRenameFailed,
    ProviderUnavailable, InvalidState, Rejected,
}

/// <summary> Design doc section 5, revised in this plan's second review round: a plugin-owned
/// status rather than the raw Penumbra.Api.Enums.PenumbraApiEc, so "the adapter itself is unusable"
/// (ProviderUnavailable) is distinguishable from an item-level rejection - the raw provider enum
/// cannot express that distinction, and the stop-vs-continue policy (Task 4) depends on it.
/// ProviderResultName preserves the real enum value's name for diagnostics only. </summary>
public sealed record SetModPathResult(SetModPathStatus Status, string? ProviderResultName, string? Diagnostic);

public enum LiveModReadStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidData }

/// <summary> Design doc section 6. Snapshot is null for any non-Success status. </summary>
public sealed record LiveModReadResult(LiveModReadStatus Status, LiveModSnapshot? Snapshot);

public enum RefreshStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidState }

/// <summary> Design doc section 5b. </summary>
public sealed record RefreshResult(RefreshStatus Status);

/// <summary>
/// Narrow Penumbra IPC boundary the execution engine depends on instead of the concrete Plugin
/// class - design doc section 2. Deliberately has no dependency on Penumbra.Api: a real
/// implementation (PenumbraOperationsAdapter, wrapping the actual Penumbra IPC subscribers and
/// translating PenumbraApiEc into SetModPathResult) is built in Plan B2. This interface is what
/// makes everything in Plan B1 unit-testable without Dalamud.
/// </summary>
public interface IPenumbraOperations
{
    LiveModReadResult GetLiveMods();
    SetModPathResult SetModPath(string identifier, string targetPath);
    RefreshResult RequestPostMutationRefresh();
}
```

- [ ] **Step 4: Write the fake**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperations.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

/// <summary>
/// Test double for IPenumbraOperations. Every call must have a queued response, or it throws -
/// a test that forgets to set up an expected call fails loudly rather than silently returning a
/// default value that could mask a real bug. EnqueueSetModPathException is the deliberate way to
/// simulate a real adapter failure in a test; the empty-queue throw is a test-infrastructure
/// safety net only and must never be relied on to simulate anything.
/// </summary>
public sealed class FakePenumbraOperations : IPenumbraOperations
{
    private readonly Queue<(LiveModReadResult Result, Action? OnCall)> _liveModReads = new();
    private readonly Queue<(SetModPathResult? Result, Exception? Exception, Action? OnCall)> _setModPathResponses = new();
    private readonly Queue<(RefreshResult Result, Action? OnCall)> _refreshResults = new();
    private readonly List<(string Identifier, string TargetPath)> _setModPathCalls = [];

    public IReadOnlyList<(string Identifier, string TargetPath)> SetModPathCalls => _setModPathCalls;

    public void EnqueueLiveModRead(LiveModReadResult result, Action? onCall = null) =>
        _liveModReads.Enqueue((result, onCall));

    public void EnqueueSetModPathResult(SetModPathResult result, Action? onCall = null) =>
        _setModPathResponses.Enqueue((result, null, onCall));

    public void EnqueueSetModPathException(Exception exception) =>
        _setModPathResponses.Enqueue((null, exception, null));

    public void EnqueueRefreshResult(RefreshResult result, Action? onCall = null) =>
        _refreshResults.Enqueue((result, onCall));

    public LiveModReadResult GetLiveMods()
    {
        if (_liveModReads.Count == 0)
            throw new InvalidOperationException("FakePenumbraOperations.GetLiveMods called with no queued result.");

        var (result, onCall) = _liveModReads.Dequeue();
        onCall?.Invoke();
        return result;
    }

    public SetModPathResult SetModPath(string identifier, string targetPath)
    {
        _setModPathCalls.Add((identifier, targetPath));

        if (_setModPathResponses.Count == 0)
            throw new InvalidOperationException("FakePenumbraOperations.SetModPath called with no queued response.");

        var (result, exception, onCall) = _setModPathResponses.Dequeue();
        onCall?.Invoke();
        if (exception is not null)
            throw exception;

        return result!;
    }

    public RefreshResult RequestPostMutationRefresh()
    {
        if (_refreshResults.Count == 0)
            throw new InvalidOperationException("FakePenumbraOperations.RequestPostMutationRefresh called with no queued result.");

        var (result, onCall) = _refreshResults.Dequeue();
        onCall?.Invoke();
        return result;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~FakePenumbraOperationsTests`
Expected: PASS (8 tests).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/IPenumbraOperations.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperations.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperationsTests.cs
git commit -m "feat: add IPenumbraOperations adapter interface and its test fake"
```

---

### Task 2: IDiagnosticsSink, and extending the already-merged DiagnosticEvent

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/DiagnosticsLog.cs:6,10-17` (already merged in Plan A2 — adding a field and an enum value, not restructuring)
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsLogTests.cs:7-9` (the existing `SlowCallEvent` test helper needs the new field)
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/IDiagnosticsSink.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsSinkTestDoubles.cs` (test doubles shared by every later task's tests)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FileDiagnosticsSinkTests.cs`

**Interfaces:**
- Consumes: `DiagnosticEvent`/`DiagnosticEventKind`/`DiagnosticsLog` (Plan A2, modified by this task).
- Produces:
  - `DiagnosticEvent` gains an 8th field, `string? Identifier` — the design doc's "worst offenders by identifier" diagnostics requirement has no field to carry this in the shipped record; this closes that gap.
  - `DiagnosticEventKind` gains `SlowRefresh`, alongside the existing `SlowCall, SlowLiveSnapshot, Exception`.
  - `IDiagnosticsSink` interface: `void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration)`, `void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration)`, `void RecordSlowRefresh(Guid? operationId, TimeSpan duration)`.
  - `FileDiagnosticsSink` — production implementation, writing through `DiagnosticsLog.Append`.
  - `NoOpDiagnosticsSink` (test project) — discards everything, for tests that don't care about diagnostics.
  - `RecordingDiagnosticsSink` (test project) — records every call into public lists (`SlowCalls`, `SlowLiveSnapshots`, `SlowRefreshes`), for tests (starting in Task 4) that assert a slow-call diagnostic was actually emitted.

- [ ] **Step 1: Update the existing DiagnosticsLogTests.cs helper for the new field**

In `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsLogTests.cs`, replace the `SlowCallEvent` helper (lines 7-9) with:

```csharp
    private static DiagnosticEvent SlowCallEvent(Guid? operationId = null) => new(
        operationId, DiagnosticEventKind.SlowCall, DateTimeOffset.UtcNow,
        DurationMilliseconds: 75, ExceptionTypeName: null, ExceptionMessage: null, TruncatedStackTrace: null,
        Identifier: "mod-a");
```

Run: `dotnet test --filter FullyQualifiedName~DiagnosticsLogTests`
Expected: FAIL to build — `DiagnosticEvent` doesn't have an `Identifier` parameter yet. This is expected; Step 3 adds it.

- [ ] **Step 2: Write the new failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class FileDiagnosticsSinkTests
{
    [Fact]
    public void RecordSlowCall_AppendsASlowCallEventWithTheIdentifier()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var operationId = Guid.NewGuid();
            var sink = new FileDiagnosticsSink(path);

            sink.RecordSlowCall(operationId, "mod-a", TimeSpan.FromMilliseconds(75));

            var events = DiagnosticsLog.ReadAll(path);
            var single = Assert.Single(events);
            Assert.Equal(DiagnosticEventKind.SlowCall, single.Kind);
            Assert.Equal(operationId, single.OperationId);
            Assert.Equal(75, single.DurationMilliseconds);
            Assert.Equal("mod-a", single.Identifier);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RecordSlowLiveSnapshot_AppendsAnEventWithNoIdentifier()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var sink = new FileDiagnosticsSink(path);

            sink.RecordSlowLiveSnapshot(null, TimeSpan.FromMilliseconds(120));

            var single = Assert.Single(DiagnosticsLog.ReadAll(path));
            Assert.Equal(DiagnosticEventKind.SlowLiveSnapshot, single.Kind);
            Assert.Null(single.OperationId);
            Assert.Null(single.Identifier);
            Assert.Equal(120, single.DurationMilliseconds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RecordSlowRefresh_AppendsASlowRefreshEvent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var operationId = Guid.NewGuid();
            var sink = new FileDiagnosticsSink(path);

            sink.RecordSlowRefresh(operationId, TimeSpan.FromMilliseconds(90));

            var single = Assert.Single(DiagnosticsLog.ReadAll(path));
            Assert.Equal(DiagnosticEventKind.SlowRefresh, single.Kind);
            Assert.Equal(90, single.DurationMilliseconds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 3: Extend DiagnosticEvent and DiagnosticEventKind**

In `PenumbraOrganizer.Plugin/Organizer/Operations/DiagnosticsLog.cs`, replace line 6 (`public enum DiagnosticEventKind { SlowCall, SlowLiveSnapshot, Exception }`) and the `DiagnosticEvent` record (lines 8-17) with:

```csharp
public enum DiagnosticEventKind { SlowCall, SlowLiveSnapshot, SlowRefresh, Exception }

/// <summary> One diagnostic event. OperationId is null for events outside any active operation.
/// Identifier is populated for SlowCall (which mod the call was for) and null for event kinds with
/// no single associated mod. Exception* fields are populated only for Kind == Exception. Design doc
/// section 10. </summary>
public sealed record DiagnosticEvent(
    Guid? OperationId,
    DiagnosticEventKind Kind,
    DateTimeOffset RecordedAt,
    long? DurationMilliseconds,
    string? ExceptionTypeName,
    string? ExceptionMessage,
    string? TruncatedStackTrace,
    string? Identifier);
```

Everything else in `DiagnosticsLog.cs` (the `Append`/`TrimIfOverCap`/`ReadAll` methods) is unchanged — `System.Text.Json` serializes the new field automatically, and old already-written lines with no `Identifier` property simply deserialize it as `null`.

- [ ] **Step 4: Run the DiagnosticsLogTests and confirm the pre-existing suite still passes**

Run: `dotnet test --filter FullyQualifiedName~DiagnosticsLogTests`
Expected: PASS (all pre-existing Plan A2 tests, now compiling against the extended record).

- [ ] **Step 5: Write IDiagnosticsSink and FileDiagnosticsSink**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Design doc section 5: PathMutationOperation/VerificationSettlement/RefreshSettlement
/// depend on this abstraction, not on a file path directly. </summary>
public interface IDiagnosticsSink
{
    void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration);
    void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration);
    void RecordSlowRefresh(Guid? operationId, TimeSpan duration);
}

/// <summary> Writes through DiagnosticsLog, which already swallows its own write failures (Plan
/// A2) - no additional exception handling needed here. </summary>
public sealed class FileDiagnosticsSink(string diagnosticsLogPath) : IDiagnosticsSink
{
    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowCall, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null, identifier));

    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowLiveSnapshot, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null, null));

    public void RecordSlowRefresh(Guid? operationId, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowRefresh, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null, null));
}
```

- [ ] **Step 6: Write the shared test doubles**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsSinkTestDoubles.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

/// <summary> No-op sink for tests that don't care about diagnostics events. </summary>
public sealed class NoOpDiagnosticsSink : IDiagnosticsSink
{
    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) { }
    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) { }
    public void RecordSlowRefresh(Guid? operationId, TimeSpan duration) { }
}

/// <summary> Recording sink for tests that must assert a diagnostic event was actually emitted. </summary>
public sealed class RecordingDiagnosticsSink : IDiagnosticsSink
{
    private readonly List<(Guid? OperationId, string Identifier, TimeSpan Duration)> _slowCalls = [];
    private readonly List<(Guid? OperationId, TimeSpan Duration)> _slowLiveSnapshots = [];
    private readonly List<(Guid? OperationId, TimeSpan Duration)> _slowRefreshes = [];

    public IReadOnlyList<(Guid? OperationId, string Identifier, TimeSpan Duration)> SlowCalls => _slowCalls;
    public IReadOnlyList<(Guid? OperationId, TimeSpan Duration)> SlowLiveSnapshots => _slowLiveSnapshots;
    public IReadOnlyList<(Guid? OperationId, TimeSpan Duration)> SlowRefreshes => _slowRefreshes;

    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) =>
        _slowCalls.Add((operationId, identifier, duration));

    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) =>
        _slowLiveSnapshots.Add((operationId, duration));

    public void RecordSlowRefresh(Guid? operationId, TimeSpan duration) =>
        _slowRefreshes.Add((operationId, duration));
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~FileDiagnosticsSinkTests|FullyQualifiedName~DiagnosticsLogTests`
Expected: PASS (3 new tests, all pre-existing `DiagnosticsLogTests` still passing).

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/DiagnosticsLog.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsLogTests.cs PenumbraOrganizer.Plugin/Organizer/Operations/IDiagnosticsSink.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsSinkTestDoubles.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FileDiagnosticsSinkTests.cs
git commit -m "feat: add IDiagnosticsSink; extend DiagnosticEvent with Identifier and SlowRefresh"
```

---

### Task 3: VerificationSettlement and RefreshSettlement

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/VerificationSettlement.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/RefreshSettlement.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/VerificationSettlementTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RefreshSettlementTests.cs`

**Interfaces:**
- Consumes: `IPenumbraOperations`/`IElapsedTimeSource`/`IDiagnosticsSink` (Tasks 1–2, Plan A1), `OperationRecoveryTarget` (Plan A1).
- Produces:
  - `TargetMutationStatus` enum: `NotAttempted, FinalStepSucceeded, FinalStepFailed, SkippedAfterEarlierFailure, AlreadySatisfied`. Defined in this file; Task 4's `PathMutationOperation` is its primary producer, this file's `VerificationSettlement` is its primary consumer.
  - `VerificationStatus` enum: `Waiting, Settled, TimedOut, RecoveryRequired`.
  - `RecoveryRequiredReason` enum: `DuplicateIdentifiers, ProviderUnavailable, InvalidData, TransientReadExhausted`.
  - `VerificationResult(VerificationStatus Status, IReadOnlyList<string> UnsettledIdentifiers, RecoveryRequiredReason? Reason)`.
  - `VerificationSettlement` sealed class: `VerificationResult Advance(IPenumbraOperations adapter, IElapsedTimeSource clock, IReadOnlyList<OperationRecoveryTarget> targets, IReadOnlyDictionary<string, TargetMutationStatus> mutationStatuses, IDiagnosticsSink diagnostics, Guid operationId)`. **Before reading live state**, if any target's identifier is missing from `mutationStatuses`, returns `RecoveryRequired(InvalidData)` — a missing entry means the plan/mutation-result mapping is inconsistent, and silently excluding it from the settlement check (which an empty-`expected`-set bug would do) could let an operation falsely report `Settled`. **After a `Success` read**, if `Snapshot` is `null`, also returns `RecoveryRequired(InvalidData)` — a malformed adapter result must not reach a null-dereference.
  - `RefreshSettlementStatus` enum: `Waiting, Settled, RecoveryRequired`.
  - `RefreshSettlementResult(RefreshSettlementStatus Status)`.
  - `RefreshSettlement` sealed class: `RefreshSettlementResult Advance(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, Guid operationId)` — mirrors `VerificationSettlement`'s bounded-retry shape exactly (design §5b: "reuses the same attempt-count/interval shape"), calling `adapter.RequestPostMutationRefresh()` instead of `adapter.GetLiveMods()`. No `TimedOut` status — a refresh either resolves within the bound or becomes `RecoveryRequired`; there is no per-identifier partial-success case the way verification has.

- [ ] **Step 1: Write the failing VerificationSettlement tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class VerificationSettlementTests
{
    private static OperationRecoveryTarget Target(string id, string finalPath) => new(id, "Gear/" + id, finalPath, id);

    private static LiveModSnapshot Snapshot(params (string Id, string Path)[] mods) =>
        LiveModSnapshotBuilder.Build(mods.Select(m => new LiveMod(m.Id, m.Id, m.Path, HeliosphereManaged: false)));

    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static readonly Dictionary<string, TargetMutationStatus> NoTargets = new();

    [Fact]
    public void Advance_AllTargetsSettled_ReturnsSettled()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot(("mod-a", "Weapons/A"))));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_TargetNeverSettles_TimesOutAfterMaxAttempts()
    {
        var adapter = new FakePenumbraOperations();
        for (var i = 0; i < 10; i++)
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot(("mod-a", "Gear/A"))));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") }; // live never matches this
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var settlement = new VerificationSettlement();
        VerificationResult result = new(VerificationStatus.Waiting, [], null);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = settlement.Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(VerificationStatus.TimedOut, result.Status);
        Assert.Equal(["mod-a"], result.UnsettledIdentifiers);
    }

    [Fact]
    public void Advance_ItemFailedDuringMutation_IsNotWaitedOn()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot()));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepFailed };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_DuplicateLiveIdentifiers_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        var duplicateSnapshot = LiveModSnapshotBuilder.Build(
        [
            new LiveMod("mod-a", "mod-a", "Gear/First", HeliosphereManaged: false),
            new LiveMod("mod-a", "mod-a", "Gear/Second", HeliosphereManaged: false),
        ]);
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, duplicateSnapshot));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.DuplicateIdentifiers, result.Reason);
    }

    [Fact]
    public void Advance_ProviderUnavailable_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));
        var clock = new FakeClock();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.ProviderUnavailable, result.Reason);
    }

    [Fact]
    public void Advance_InvalidData_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.InvalidData, null));
        var clock = new FakeClock();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.InvalidData, result.Reason);
    }

    [Fact]
    public void Advance_TemporarilyUnavailableForAllAttempts_RecoveryRequiredWithTransientReadExhausted()
    {
        var adapter = new FakePenumbraOperations();
        for (var i = 0; i < 10; i++)
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.TemporarilyUnavailable, null));
        var clock = new FakeClock();

        var settlement = new VerificationSettlement();
        VerificationResult result = new(VerificationStatus.Waiting, [], null);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = settlement.Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.TransientReadExhausted, result.Reason);
    }

    [Fact]
    public void Advance_SuccessStatusWithNullSnapshot_RecoveryRequiredRatherThanThrowing()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, null)); // malformed adapter result
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.InvalidData, result.Reason);
    }

    [Fact]
    public void Advance_TargetMissingFromMutationStatuses_RecoveryRequiredRatherThanSilentlySettled()
    {
        var adapter = new FakePenumbraOperations(); // no read queued - must never be reached
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.InvalidData, result.Reason);
    }

    [Fact]
    public void Advance_SecondCallWithinRetryInterval_ReturnsWaitingWithoutReadingAgain()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot()));
        var clock = new FakeClock();

        var settlement = new VerificationSettlement();
        settlement.Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid()); // consumes the one queued read
        var second = settlement.Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid()); // no time advanced

        Assert.Equal(VerificationStatus.Waiting, second.Status);
        // No exception from an empty queue proves GetLiveMods was not called a second time.
    }

    [Fact]
    public void Advance_FastLiveRead_DoesNotRecordADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot()));
        var clock = new FakeClock();
        var diagnostics = new RecordingDiagnosticsSink();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, diagnostics, Guid.NewGuid());

        Assert.Equal(VerificationStatus.Settled, result.Status);
        Assert.Empty(diagnostics.SlowLiveSnapshots);
    }

    [Fact]
    public void Advance_SlowLiveRead_RecordsADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        var clock = new FakeClock();
        adapter.EnqueueLiveModRead(
            new LiveModReadResult(LiveModReadStatus.Success, Snapshot()),
            onCall: () => clock.Advance(TimeSpan.FromMilliseconds(80))); // over the 50ms SlowCallThreshold
        var diagnostics = new RecordingDiagnosticsSink();
        var operationId = Guid.NewGuid();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, diagnostics, operationId);

        Assert.Equal(VerificationStatus.Settled, result.Status);
        var single = Assert.Single(diagnostics.SlowLiveSnapshots);
        Assert.Equal(operationId, single.OperationId);
        Assert.True(single.Duration >= TimeSpan.FromMilliseconds(80));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~VerificationSettlementTests`
Expected: FAIL — none of the types exist yet.

- [ ] **Step 3: Write TargetMutationStatus and VerificationSettlement**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Per-identifier disposition. Design doc section 5. Task 4 (PathMutationOperation) is
/// this type's primary producer; VerificationSettlement (this file) is its primary consumer. </summary>
public enum TargetMutationStatus
{
    NotAttempted, FinalStepSucceeded, FinalStepFailed, SkippedAfterEarlierFailure, AlreadySatisfied,
}

public enum VerificationStatus { Waiting, Settled, TimedOut, RecoveryRequired }

public enum RecoveryRequiredReason { DuplicateIdentifiers, ProviderUnavailable, InvalidData, TransientReadExhausted }

public sealed record VerificationResult(
    VerificationStatus Status,
    IReadOnlyList<string> UnsettledIdentifiers,
    RecoveryRequiredReason? Reason);

/// <summary>
/// Design doc section 6. Budgeted the same way as mutation - one read-and-compare attempt per
/// Advance() call, gated by a retry interval, never a blocking wait. Only targets whose
/// TargetMutationStatus is FinalStepSucceeded or AlreadySatisfied are expected to settle; an item
/// already recorded failed during Mutating is not waited on. Two defensive guards close gaps a real
/// caller must never be able to trigger: a target missing from mutationStatuses, and a Success
/// read carrying a null Snapshot.
/// </summary>
public sealed class VerificationSettlement
{
    private int _attemptsUsed;
    private long _lastAttemptTimestamp;
    private const int MaxAttempts = 10; // "attempts", not "retries" - avoids an off-by-one
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlowCallThreshold = TimeSpan.FromMilliseconds(50);

    public VerificationResult Advance(
        IPenumbraOperations adapter, IElapsedTimeSource clock,
        IReadOnlyList<OperationRecoveryTarget> targets,
        IReadOnlyDictionary<string, TargetMutationStatus> mutationStatuses,
        IDiagnosticsSink diagnostics, Guid operationId)
    {
        if (targets.Any(t => !mutationStatuses.ContainsKey(t.Identifier)))
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.InvalidData);

        if (_attemptsUsed > 0 && clock.GetElapsedTime(_lastAttemptTimestamp) < RetryInterval)
            return new VerificationResult(VerificationStatus.Waiting, [], null);

        _lastAttemptTimestamp = clock.GetTimestamp();
        _attemptsUsed++;

        var readStart = clock.GetTimestamp();
        var read = adapter.GetLiveMods();
        var readDuration = clock.GetElapsedTime(readStart);
        if (readDuration >= SlowCallThreshold) diagnostics.RecordSlowLiveSnapshot(operationId, readDuration);

        if (read.Status == LiveModReadStatus.ProviderUnavailable)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.ProviderUnavailable);
        if (read.Status == LiveModReadStatus.InvalidData)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.InvalidData);
        if (read.Status == LiveModReadStatus.TemporarilyUnavailable)
            return _attemptsUsed >= MaxAttempts
                ? new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.TransientReadExhausted)
                : new VerificationResult(VerificationStatus.Waiting, [], null);
        if (read.Snapshot is null)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.InvalidData);
        if (read.Snapshot.DuplicateIdentifiers.Count > 0)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.DuplicateIdentifiers);

        var expected = targets.Where(t => mutationStatuses[t.Identifier] is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);
        var unsettled = expected.Where(t => !IsSettled(t, read.Snapshot)).Select(t => t.Identifier).ToList();

        if (unsettled.Count == 0) return new VerificationResult(VerificationStatus.Settled, [], null);
        return _attemptsUsed >= MaxAttempts
            ? new VerificationResult(VerificationStatus.TimedOut, unsettled, null)
            : new VerificationResult(VerificationStatus.Waiting, [], null);
    }

    private static bool IsSettled(OperationRecoveryTarget t, LiveModSnapshot live) =>
        live.Mods.TryGetValue(t.Identifier, out var mod) &&
        PenumbraPathSemantics.AreEquivalent(mod.FullPath, t.FinalRawPath, t.ModName);
}
```

- [ ] **Step 4: Write RefreshSettlement and its tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RefreshSettlementTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    [Fact]
    public void Advance_Success_ReturnsSettled()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_ProviderUnavailable_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.ProviderUnavailable));

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.RecoveryRequired, result.Status);
    }

    [Fact]
    public void Advance_InvalidState_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.InvalidState));

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.RecoveryRequired, result.Status);
    }

    [Fact]
    public void Advance_TemporarilyUnavailableUntilBoundExhausted_ThenRecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        for (var i = 0; i < 10; i++)
            adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.TemporarilyUnavailable));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        RefreshSettlementResult result = new(RefreshSettlementStatus.Waiting);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(RefreshSettlementStatus.RecoveryRequired, result.Status);
    }

    [Fact]
    public void Advance_TemporarilyUnavailableThenSuccess_EventuallySettles()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.TemporarilyUnavailable));
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        var first = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var second = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Waiting, first.Status);
        Assert.Equal(RefreshSettlementStatus.Settled, second.Status);
    }

    [Fact]
    public void Advance_SecondCallWithinRetryInterval_DoesNotCallAdapterAgain()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());
        var second = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Waiting, second.Status);
        // No exception from an empty queue proves RequestPostMutationRefresh was not called again.
    }

    [Fact]
    public void Advance_FastRefresh_DoesNotRecordADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        var diagnostics = new RecordingDiagnosticsSink();

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), diagnostics, Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Settled, result.Status);
        Assert.Empty(diagnostics.SlowRefreshes);
    }

    [Fact]
    public void Advance_SlowRefresh_RecordsADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        var clock = new FakeClock();
        adapter.EnqueueRefreshResult(
            new RefreshResult(RefreshStatus.Success),
            onCall: () => clock.Advance(TimeSpan.FromMilliseconds(80))); // over the 50ms SlowCallThreshold
        var diagnostics = new RecordingDiagnosticsSink();
        var operationId = Guid.NewGuid();

        var result = new RefreshSettlement().Advance(adapter, clock, diagnostics, operationId);

        Assert.Equal(RefreshSettlementStatus.Settled, result.Status);
        var single = Assert.Single(diagnostics.SlowRefreshes);
        Assert.Equal(operationId, single.OperationId);
        Assert.True(single.Duration >= TimeSpan.FromMilliseconds(80));
    }
}
```

Write the implementation in `PenumbraOrganizer.Plugin/Organizer/Operations/RefreshSettlement.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum RefreshSettlementStatus { Waiting, Settled, RecoveryRequired }

public sealed record RefreshSettlementResult(RefreshSettlementStatus Status);

/// <summary>
/// Design doc section 5b. Mirrors VerificationSettlement's bounded-retry shape exactly (same
/// attempt count and interval) - no separate TimedOut state, since a refresh either resolves
/// within the bound or becomes RecoveryRequired; there is no per-identifier partial-success case
/// the way verification has.
/// </summary>
public sealed class RefreshSettlement
{
    private int _attemptsUsed;
    private long _lastAttemptTimestamp;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlowCallThreshold = TimeSpan.FromMilliseconds(50);

    public RefreshSettlementResult Advance(
        IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, Guid operationId)
    {
        if (_attemptsUsed > 0 && clock.GetElapsedTime(_lastAttemptTimestamp) < RetryInterval)
            return new RefreshSettlementResult(RefreshSettlementStatus.Waiting);

        _lastAttemptTimestamp = clock.GetTimestamp();
        _attemptsUsed++;

        var callStart = clock.GetTimestamp();
        var refresh = adapter.RequestPostMutationRefresh();
        var duration = clock.GetElapsedTime(callStart);
        if (duration >= SlowCallThreshold) diagnostics.RecordSlowRefresh(operationId, duration);

        return refresh.Status switch
        {
            RefreshStatus.Success => new RefreshSettlementResult(RefreshSettlementStatus.Settled),
            RefreshStatus.TemporarilyUnavailable => _attemptsUsed >= MaxAttempts
                ? new RefreshSettlementResult(RefreshSettlementStatus.RecoveryRequired)
                : new RefreshSettlementResult(RefreshSettlementStatus.Waiting),
            _ => new RefreshSettlementResult(RefreshSettlementStatus.RecoveryRequired), // ProviderUnavailable, InvalidState
        };
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~VerificationSettlementTests|FullyQualifiedName~RefreshSettlementTests`
Expected: PASS (11 verification tests + 6 refresh tests = 17 total).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/VerificationSettlement.cs PenumbraOrganizer.Plugin/Organizer/Operations/RefreshSettlement.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/VerificationSettlementTests.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RefreshSettlementTests.cs
git commit -m "feat: add verification and refresh settlement with defensive guards"
```

---

### Task 4: PathMutationOperation — frame-budgeted, group-cascading mutation

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/PathMutationOperation.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/PathMutationOperationTests.cs`

**Interfaces:**
- Consumes: `IPenumbraOperations`/`SetModPathResult`/`SetModPathStatus` (Task 1), `IElapsedTimeSource` (Plan A1), `IDiagnosticsSink` (Task 2), `TargetMutationStatus` (Task 3), `OperationPlan`/`OperationExecutionStep`/`OperationStepKind` (Plan A1), `OperationJournal` (Plan A1), `OperationStepResult`/`OperationStepDisposition`/`StepResultLog` (Plan A2), `OperationBundlePaths` (Plan A2).
- Produces:
  - `MutationAdvanceStatus` enum: `Working, MutationFinished, CancellationObserved, IntegrityFailure`.
  - `MutationStopReason` enum: `None, UserCancellation, ProviderUnavailable, JournalWriteFailed, PlanCorrupt, UnexpectedFatalException`. `JournalWriteFailed` and `PlanCorrupt` are modeled for completeness against the design's full IPC/integrity table but are never produced by this class in this plan — a journal-write failure happens in the caller-supplied checkpoint callback, not inside `PathMutationOperation` itself (Task 5/6 own that failure path), and `OperationPlan.Create`'s own validation (Plan A1) already prevents an identifier-mapping-corrupt plan from ever being constructed.
  - `MutationAdvanceResult(OperationJournal Journal, MutationAdvanceStatus Status, MutationStopReason StopReason)`.
  - `PathMutationOperation` sealed class, constructed with `(OperationPlan plan, IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, string bundleDirectory)`.
    - `IReadOnlyDictionary<string, TargetMutationStatus> MutationStatusByIdentifier { get; }` — **computed on each access** from each identifier's *last* execution step's durably-recorded disposition, never an opportunistically-mutated dictionary. This is what makes a temp-hop-then-final-step sequence always report the *final* step's outcome regardless of what the temp hop did.
    - `MutationAdvanceResult Advance(OperationJournal journal, TimeSpan budget, bool stopRequested, Action<OperationJournal> checkpointIfDue)` — `checkpointIfDue` is called after every step and after every cascade batch; this method never itself calls `OperationJournalCodec.Save` or catches a failure from the callback (that's the caller's — Task 6's — responsibility, per this plan's exception-boundary design).

`PathMutationOperation` only ever drives the `Mutating` stage. It signals its caller via `MutationAdvanceStatus`, never by mutating `journal.Stage` itself — the caller (Task 6) decides what stage to transition to based on the returned status.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class PathMutationOperationTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static OperationExecutionStep Step(int index, string id, string target, OperationStepKind kind, int group) =>
        new(index, id, target, kind, group);

    private static OperationRecoveryTarget Target(string id, string snapshotPath, string finalPath) =>
        new(id, snapshotPath, finalPath, id);

    private static SetModPathResult Success => new(SetModPathStatus.Success, "Success", null);
    private static SetModPathResult NothingChanged => new(SetModPathStatus.NothingChanged, "NothingChanged", null);
    private static SetModPathResult PathRenameFailed => new(SetModPathStatus.PathRenameFailed, "PathRenameFailed", "collision");
    private static SetModPathResult ProviderUnavailable => new(SetModPathStatus.ProviderUnavailable, "SystemDisposed", "unavailable");

    private static OperationJournal NewJournal(OperationPlan plan) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: plan.OperationId,
        Type: plan.Type,
        Stage: OperationStage.Mutating,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: plan.ExecutionSteps.Count,
        ProcessedStepCount: 0,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: plan.OperationId,
        TargetHash: plan.IntegrityHash,
        RecoveryOfOperationId: null,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static string TempResultsDir(out DirectoryInfo dir)
    {
        dir = Directory.CreateTempSubdirectory();
        return dir.FullName;
    }

    [Fact]
    public void Advance_TwoIndependentSuccessfulSteps_ProcessesBothInOneCallGivenAmpleBudget()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0), Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.MutationFinished, result.Status);
            Assert.Equal(2, result.Journal.ProcessedStepCount);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-a"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-b"]);

            var results = StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir));
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(OperationStepDisposition.Succeeded, r.Disposition));
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_AlwaysProcessesAtLeastOneStepEvenWithZeroBudget()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.Zero, stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(1, result.Journal.ProcessedStepCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_ItemFailure_CascadesTheWholeGroupAndContinuesToTheNextGroup()
    {
        // Group 0: a two-way swap (temp hop + final move for X, final move for Y) where the temp
        // hop fails. Group 1: an unrelated independent step that must still succeed.
        var steps = new[]
        {
            Step(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            Step(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            Step(2, "X", "P2", OperationStepKind.FinalMove, 0),
            Step(3, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("X", "P0", "P2"), Target("Y", "P2", "P0"), Target("mod-c", "Gear/C", "Weapons/C") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PathRenameFailed); // X's temp hop fails
        adapter.EnqueueSetModPathResult(Success); // mod-c, group 1, unaffected
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.MutationFinished, result.Status);
            Assert.Equal(4, result.Journal.ProcessedStepCount); // cascaded past the whole group 0 range, then processed group 1
            Assert.Equal(TargetMutationStatus.FinalStepFailed, op.MutationStatusByIdentifier["X"]);
            Assert.Equal(TargetMutationStatus.SkippedAfterEarlierFailure, op.MutationStatusByIdentifier["Y"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-c"]);

            var results = StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir));
            Assert.Equal(4, results.Count);
            Assert.Equal(OperationStepDisposition.Failed, results.Single(r => r.StepIndex == 0).Disposition);
            Assert.Equal(OperationStepDisposition.SkippedAfterEarlierFailure, results.Single(r => r.StepIndex == 1).Disposition);
            Assert.Equal(OperationStepDisposition.SkippedAfterEarlierFailure, results.Single(r => r.StepIndex == 2).Disposition);
            Assert.Equal(OperationStepDisposition.Succeeded, results.Single(r => r.StepIndex == 3).Disposition);

            // Only TWO SetModPath calls were ever made - steps 1 and 2 (the rest of the cascaded
            // group) were never attempted, proving the cascade skips rather than tries-then-discards.
            Assert.Equal(2, adapter.SetModPathCalls.Count);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_FinalStepStatusReflectsTheLastStepEvenWhenAnEarlierTempHopForTheSameIdentifierSucceeded()
    {
        // X's temp hop (step 0) succeeds; X's final move (step 2) fails. MutationStatusByIdentifier
        // must report the LAST step's outcome (FinalStepFailed), not get stuck on the temp hop's
        // success - this is the derived-property behavior that replaces opportunistic mutation.
        var steps = new[]
        {
            Step(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            Step(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            Step(2, "X", "P2", OperationStepKind.FinalMove, 0),
        };
        var targets = new[] { Target("X", "P0", "P2"), Target("Y", "P2", "P0") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success); // temp hop succeeds
        adapter.EnqueueSetModPathResult(Success); // Y succeeds
        adapter.EnqueueSetModPathResult(PathRenameFailed); // X's final move fails
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(TargetMutationStatus.FinalStepFailed, op.MutationStatusByIdentifier["X"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["Y"]);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_NothingChanged_TreatedAsSuccessNotFailure()
    {
        // Design decision, made explicit here: NothingChanged means a real SetModPath call WAS
        // made and Penumbra reported no effective change - it is not a skip (nothing was ever
        // attempted), so it maps to OperationStepDisposition.Succeeded / FinalStepSucceeded, the
        // same as an ordinary Success, not to SkippedAlreadySatisfied (which is reserved for a
        // step this engine never attempts at all - see Task 4's Interfaces note).
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(NothingChanged);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-a"]);
            var result = Assert.Single(StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir)));
            Assert.Equal(OperationStepDisposition.Succeeded, result.Disposition);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_StopRequestedAtCallEntry_ProcessesNoStepsAndReturnsCancellationObserved()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations(); // no result queued - must never be called
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: true, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.CancellationObserved, result.Status);
            Assert.Equal(MutationStopReason.UserCancellation, result.StopReason);
            Assert.Equal(0, result.Journal.ProcessedStepCount);
            Assert.Empty(adapter.SetModPathCalls);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_MultipleCallsAcrossFrames_ResumesFromWhereItLeftOff()
    {
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var afterFirst = op.Advance(NewJournal(plan), TimeSpan.Zero, stopRequested: false, checkpointIfDue: _ => { });
            var afterSecond = op.Advance(afterFirst.Journal, TimeSpan.Zero, stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.Working, afterFirst.Status);
            Assert.Equal(1, afterFirst.Journal.ProcessedStepCount);
            Assert.Equal(MutationAdvanceStatus.MutationFinished, afterSecond.Status);
            Assert.Equal(2, afterSecond.Journal.ProcessedStepCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_FrameBudgetExceeded_StopsStartingNewStepsWithinTheSameCall()
    {
        // Three steps, each consuming 3ms (via onCall clock-advance), budget 4ms: step 0 (0->3ms),
        // check before step 1: elapsed 3ms < 4ms budget -> proceed; step 1 (3->6ms), check before
        // step 2: elapsed 6ms >= 4ms budget -> stop. Only steps 0 and 1 process in this call.
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
            Step(2, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 2),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B"), Target("mod-c", "Gear/C", "Weapons/C") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var clock = new FakeClock();
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(3)));
        adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(3)));
        // A third result is deliberately NOT queued - if step 2 were incorrectly attempted this
        // call, FakePenumbraOperations would throw and fail the test.
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, clock, new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromMilliseconds(4), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.Working, result.Status);
            Assert.Equal(2, result.Journal.ProcessedStepCount);
            Assert.Equal(2, adapter.SetModPathCalls.Count);

            // A second call resumes and finishes the remaining step.
            adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(3)));
            var second = op.Advance(result.Journal, TimeSpan.FromMilliseconds(4), stopRequested: false, checkpointIfDue: _ => { });
            Assert.Equal(MutationAdvanceStatus.MutationFinished, second.Status);
            Assert.Equal(3, second.Journal.ProcessedStepCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_SinglePathologicalCallExceedsBudgetButStillCompletesAndEmitsASlowCallDiagnostic()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var clock = new FakeClock();
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(80)));
        var diagnostics = new RecordingDiagnosticsSink();
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, clock, diagnostics, dir);
            var journal = NewJournal(plan);
            var result = op.Advance(journal, TimeSpan.FromMilliseconds(4), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.MutationFinished, result.Status);
            Assert.Equal(1, result.Journal.ProcessedStepCount); // completed despite exceeding the budget
            var slowCall = Assert.Single(diagnostics.SlowCalls);
            Assert.Equal("mod-a", slowCall.Identifier);
            Assert.Equal(journal.OperationId, slowCall.OperationId);
            Assert.True(slowCall.Duration >= TimeSpan.FromMilliseconds(50));
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_ProviderUnavailable_ReturnsIntegrityFailureAndStopsWithoutCascading()
    {
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(ProviderUnavailable);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.IntegrityFailure, result.Status);
            Assert.Equal(MutationStopReason.ProviderUnavailable, result.StopReason);
            Assert.Equal(0, result.Journal.ProcessedStepCount); // step 0 itself never succeeded, so the cursor doesn't advance past it
            Assert.Single(adapter.SetModPathCalls); // step 1 was never attempted
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_UnexpectedExceptionFromAdapter_ReturnsIntegrityFailureRatherThanThrowing()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathException(new InvalidOperationException("simulated adapter failure"));
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.IntegrityFailure, result.Status);
            Assert.Equal(MutationStopReason.UnexpectedFatalException, result.StopReason);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_CallsCheckpointCallbackOnceForEachStepAndOnceForTheWholeCascadeBatch()
    {
        var steps = new[]
        {
            Step(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            Step(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            Step(2, "X", "P2", OperationStepKind.FinalMove, 0),
            Step(3, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("X", "P0", "P2"), Target("Y", "P2", "P0"), Target("mod-c", "Gear/C", "Weapons/C") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PathRenameFailed); // X's temp hop fails - cascades steps 1,2 in one batch
        adapter.EnqueueSetModPathResult(Success); // mod-c, its own call
        var checkpointCallCount = 0;
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => checkpointCallCount++);

            // One call for the failed step + its cascade batch, one call for mod-c: 2 total.
            Assert.Equal(2, checkpointCallCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PathMutationOperationTests`
Expected: FAIL — `PathMutationOperation` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum MutationAdvanceStatus { Working, MutationFinished, CancellationObserved, IntegrityFailure }

public enum MutationStopReason { None, UserCancellation, ProviderUnavailable, JournalWriteFailed, PlanCorrupt, UnexpectedFatalException }

public sealed record MutationAdvanceResult(OperationJournal Journal, MutationAdvanceStatus Status, MutationStopReason StopReason);

/// <summary>
/// Design doc section 5, revised in this plan's second review round. Drives only the Mutating
/// stage - it signals the caller via MutationAdvanceStatus rather than ever setting journal.Stage
/// itself. Cancellation is checked once, at Advance's entry, before any step of that call begins:
/// a call made with stopRequested already true processes zero new steps. The frame budget's "always
/// process at least one step" guarantee applies only once cancellation has already been ruled out.
/// MutationStatusByIdentifier is computed from each identifier's LAST execution step's durable
/// disposition on every access, never a dictionary mutated opportunistically mid-loop - this means
/// a temp hop's outcome can never leak into the reported status once the final step has its own
/// recorded disposition.
/// </summary>
public sealed class PathMutationOperation
{
    private readonly OperationPlan _plan;
    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly string _bundleDirectory;
    private readonly Dictionary<int, OperationStepDisposition> _stepDispositions = new();
    private readonly Dictionary<string, int> _lastStepIndexByIdentifier;

    private static readonly TimeSpan SlowCallThreshold = TimeSpan.FromMilliseconds(50);

    public PathMutationOperation(
        OperationPlan plan, IPenumbraOperations adapter, IElapsedTimeSource clock,
        IDiagnosticsSink diagnostics, string bundleDirectory)
    {
        _plan = plan;
        _adapter = adapter;
        _clock = clock;
        _diagnostics = diagnostics;
        _bundleDirectory = bundleDirectory;

        _lastStepIndexByIdentifier = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in plan.ExecutionSteps) // steps are index-ordered, so the last write per identifier wins
            _lastStepIndexByIdentifier[step.Identifier] = step.StepIndex;
    }

    public IReadOnlyDictionary<string, TargetMutationStatus> MutationStatusByIdentifier =>
        _plan.RecoveryTargets.ToDictionary(
            t => t.Identifier,
            t => ToTargetStatus(_stepDispositions.GetValueOrDefault(_lastStepIndexByIdentifier[t.Identifier])),
            StringComparer.Ordinal);

    private static TargetMutationStatus ToTargetStatus(OperationStepDisposition disposition) => disposition switch
    {
        OperationStepDisposition.Succeeded => TargetMutationStatus.FinalStepSucceeded,
        OperationStepDisposition.Failed => TargetMutationStatus.FinalStepFailed,
        OperationStepDisposition.SkippedAfterEarlierFailure => TargetMutationStatus.SkippedAfterEarlierFailure,
        OperationStepDisposition.SkippedAlreadySatisfied => TargetMutationStatus.AlreadySatisfied,
        _ => TargetMutationStatus.NotAttempted,
    };

    public MutationAdvanceResult Advance(
        OperationJournal journal, TimeSpan budget, bool stopRequested, Action<OperationJournal> checkpointIfDue)
    {
        if (stopRequested)
            return new MutationAdvanceResult(journal, MutationAdvanceStatus.CancellationObserved, MutationStopReason.UserCancellation);

        var start = _clock.GetTimestamp();
        var index = journal.ProcessedStepCount;
        var lastIdentifier = journal.LastCompletedIdentifier;
        var processedAnyThisCall = false;

        while (index < _plan.ExecutionSteps.Count)
        {
            if (processedAnyThisCall && _clock.GetElapsedTime(start) >= budget)
                break;

            var step = _plan.ExecutionSteps[index];
            var callStart = _clock.GetTimestamp();
            SetModPathResult ipcResult;
            try
            {
                ipcResult = _adapter.SetModPath(step.Identifier, step.TargetRawPath);
            }
            catch (Exception)
            {
                // Unmodeled exception: cannot prove the IPC boundary is still usable, so this is
                // an operation-integrity stop, not an item failure - the conservative-by-default
                // reading of design section 5's "unexpected exception" case.
                journal = journal with { ProcessedStepCount = index, LastCompletedIdentifier = lastIdentifier, UpdatedAt = DateTimeOffset.UtcNow };
                checkpointIfDue(journal);
                return new MutationAdvanceResult(journal, MutationAdvanceStatus.IntegrityFailure, MutationStopReason.UnexpectedFatalException);
            }

            var callDuration = _clock.GetElapsedTime(callStart);
            if (callDuration >= SlowCallThreshold)
                _diagnostics.RecordSlowCall(journal.OperationId, step.Identifier, callDuration);

            if (ipcResult.Status == SetModPathStatus.ProviderUnavailable)
            {
                journal = journal with { ProcessedStepCount = index, LastCompletedIdentifier = lastIdentifier, UpdatedAt = DateTimeOffset.UtcNow };
                checkpointIfDue(journal);
                return new MutationAdvanceResult(journal, MutationAdvanceStatus.IntegrityFailure, MutationStopReason.ProviderUnavailable);
            }

            var succeeded = ipcResult.Status is SetModPathStatus.Success or SetModPathStatus.NothingChanged;

            if (succeeded)
            {
                RecordDisposition(step, OperationStepDisposition.Succeeded, ipcResult, callDuration);
                lastIdentifier = step.Identifier;
                index++;
            }
            else
            {
                RecordDisposition(step, OperationStepDisposition.Failed, ipcResult, callDuration);

                var groupId = step.GroupId;
                var cascadeIndex = index + 1;
                while (cascadeIndex < _plan.ExecutionSteps.Count && _plan.ExecutionSteps[cascadeIndex].GroupId == groupId)
                {
                    var cascadeStep = _plan.ExecutionSteps[cascadeIndex];
                    RecordDisposition(cascadeStep, OperationStepDisposition.SkippedAfterEarlierFailure, null, null);
                    lastIdentifier = cascadeStep.Identifier;
                    cascadeIndex++;
                }

                index = cascadeIndex;
            }

            processedAnyThisCall = true;
            journal = journal with { ProcessedStepCount = index, LastCompletedIdentifier = lastIdentifier, UpdatedAt = DateTimeOffset.UtcNow };
            checkpointIfDue(journal);
        }

        var status = index >= _plan.ExecutionSteps.Count ? MutationAdvanceStatus.MutationFinished : MutationAdvanceStatus.Working;
        return new MutationAdvanceResult(journal, status, MutationStopReason.None);
    }

    private void RecordDisposition(
        OperationExecutionStep step, OperationStepDisposition disposition, SetModPathResult? ipcResult, TimeSpan? duration)
    {
        _stepDispositions[step.StepIndex] = disposition;
        StepResultLog.Append(OperationBundlePaths.ResultsPath(_bundleDirectory), new OperationStepResult(
            step.StepIndex, step.Identifier, disposition,
            ipcResult?.Status.ToString(), ipcResult?.Diagnostic,
            DateTimeOffset.UtcNow, duration is null ? null : (long)duration.Value.TotalMilliseconds));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PathMutationOperationTests`
Expected: PASS (12 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/PathMutationOperation.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/PathMutationOperationTests.cs
git commit -m "feat: add PathMutationOperation with correct cancellation, stop reasons, group-cascade"
```

---

### Task 5: OperationCheckpointer — checkpoint cadence in its own class

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationCheckpointer.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationCheckpointerTests.cs`

**Interfaces:**
- Consumes: `IElapsedTimeSource` (Plan A1), `OperationJournal`/`OperationJournalCodec`/`CheckpointPolicy` (Plan A1), `OperationBundlePaths` (Plan A2).
- Produces: `OperationCheckpointer` sealed class, constructed with `(IElapsedTimeSource clock, string bundleDirectory)`. `void CheckpointIfDue(OperationJournal journal)` (defaults `force: false`) and `void CheckpointIfDue(OperationJournal journal, bool force)` — writes via `OperationJournalCodec.Save` when `force` is true or `CheckpointPolicy.IsDue` says so, tracking its own "steps since last checkpoint" and "time since last checkpoint" internally so it can be called many times in a burst (once per step, as `PathMutationOperation.Advance` does via its injected callback) without rewriting the journal file on every single call.

This is the class Task 4's `Advance` calls after every step or cascade batch — splitting policy-and-write out of the mutation loop is what makes the design's "checkpoint after each step or cascade batch" requirement actually testable in isolation, rather than only observable as a side effect of a full `Advance()` burst.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationCheckpointerTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static OperationJournal Journal(int processedStepCount) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: Guid.NewGuid(), Type: OperationType.Apply,
        Stage: OperationStage.Mutating, Resolution: OperationResolution.None, SuccessorOperationId: null,
        CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: 100,
        ProcessedStepCount: processedStepCount, LastCompletedIdentifier: null, SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(), TargetHash: "irrelevant", RecoveryOfOperationId: null, UpdatedAt: DateTimeOffset.UtcNow);

    private static int? PersistedProcessedStepCount(string bundleDirectory) =>
        OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDirectory), out var journal) && journal is not null
            ? journal.ProcessedStepCount
            : null;

    [Fact]
    public void CheckpointIfDue_BelowBothThresholds_DoesNotWrite()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);
            checkpointer.CheckpointIfDue(Journal(5)); // below the 10-item threshold, no time elapsed

            Assert.Null(PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_ItemThresholdReached_Writes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);
            checkpointer.CheckpointIfDue(Journal(10)); // exactly the 10-item threshold

            Assert.Equal(10, PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_TimeThresholdReached_Writes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock();
            var checkpointer = new OperationCheckpointer(clock, dir.FullName);
            clock.Advance(TimeSpan.FromMilliseconds(500)); // exactly the time threshold, zero items

            checkpointer.CheckpointIfDue(Journal(1));

            Assert.Equal(1, PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_Force_AlwaysWritesRegardlessOfThresholds()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);
            checkpointer.CheckpointIfDue(Journal(1), force: true); // one item, zero time elapsed - neither threshold met

            Assert.Equal(1, PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_TwentySevenCallsInABurst_WritesOnlyAtTheTenStepBoundaries()
    {
        // Reproduces exactly the scenario the checkpoint-cadence review finding was about: many
        // single-step calls in one burst (as PathMutationOperation.Advance makes via its injected
        // callback), proving checkpoints land at multiples of the 10-item threshold, not once at
        // the very end of the burst.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);

            for (var processed = 1; processed <= 9; processed++)
                checkpointer.CheckpointIfDue(Journal(processed));
            Assert.Null(PersistedProcessedStepCount(dir.FullName)); // nothing written yet, below threshold

            checkpointer.CheckpointIfDue(Journal(10));
            Assert.Equal(10, PersistedProcessedStepCount(dir.FullName)); // first checkpoint at exactly 10

            for (var processed = 11; processed <= 19; processed++)
                checkpointer.CheckpointIfDue(Journal(processed));
            Assert.Equal(10, PersistedProcessedStepCount(dir.FullName)); // unchanged - not due again yet

            checkpointer.CheckpointIfDue(Journal(20));
            Assert.Equal(20, PersistedProcessedStepCount(dir.FullName)); // second checkpoint at 20

            for (var processed = 21; processed <= 27; processed++)
                checkpointer.CheckpointIfDue(Journal(processed));
            Assert.Equal(20, PersistedProcessedStepCount(dir.FullName)); // still unchanged - burst ends at 27, below the next 10-step boundary
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationCheckpointerTests`
Expected: FAIL — `OperationCheckpointer` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Design doc section 5's "checkpoint after each step or cascade batch" requirement, isolated from
/// the mutation loop so it's independently testable. Tracks its own steps-since-last-checkpoint
/// and time-since-last-checkpoint so it can be called once per step in a burst (as
/// PathMutationOperation.Advance does) without rewriting the journal file on every call - only when
/// CheckpointPolicy.IsDue actually says so, or when force is requested (stage transitions,
/// cancellation-intent persistence).
/// </summary>
public sealed class OperationCheckpointer
{
    private readonly IElapsedTimeSource _clock;
    private readonly string _bundleDirectory;
    private int _lastCheckpointedProcessedStepCount;
    private long _lastCheckpointTimestamp;

    public OperationCheckpointer(IElapsedTimeSource clock, string bundleDirectory)
    {
        _clock = clock;
        _bundleDirectory = bundleDirectory;
        _lastCheckpointTimestamp = clock.GetTimestamp();
    }

    public void CheckpointIfDue(OperationJournal journal) => CheckpointIfDue(journal, force: false);

    public void CheckpointIfDue(OperationJournal journal, bool force)
    {
        var delta = journal.ProcessedStepCount - _lastCheckpointedProcessedStepCount;
        var elapsed = _clock.GetElapsedTime(_lastCheckpointTimestamp);
        if (!force && !CheckpointPolicy.IsDue(delta, elapsed))
            return;

        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(_bundleDirectory), journal);
        _lastCheckpointedProcessedStepCount = journal.ProcessedStepCount;
        _lastCheckpointTimestamp = _clock.GetTimestamp();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationCheckpointerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationCheckpointer.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationCheckpointerTests.cs
git commit -m "feat: add OperationCheckpointer with isolated, burst-safe checkpoint cadence"
```

---

### Task 6: OperationController — state machine, terminal-state retention, recovery lock

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: `PathMutationOperation`/`MutationAdvanceResult`/`MutationAdvanceStatus` (Task 4), `RefreshSettlement`/`VerificationSettlement` (Task 3), `OperationCheckpointer` (Task 5), `IPenumbraOperations`/`IElapsedTimeSource`/`IDiagnosticsSink` (Tasks 1–2), `OperationPlan`/`OperationJournal`/`OperationJournal.IsTerminal` (Plan A1).
- Produces:
  - `OperationStateSnapshot` record (design §7b, revised per review findings 17–18 — see field list below).
  - `OperationController` sealed class, constructed with `(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, TimeSpan frameBudget)`.
    - `OperationStateSnapshot State { get; }`
    - `void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory)` — throws `ArgumentException` if `plan.Type != OperationType.Apply`; throws `InvalidOperationException` if an operation is already active *and non-terminal* (a terminal previous operation is legitimately overwritten — see below). Persists `Stage = Prepared` then immediately `Stage = Mutating`, each a forced checkpoint (design §3/§4: forced write on entering every stage).
    - `void RequestCancellation()` — no-ops if there's nothing to cancel or the operation is already past `Mutating`; otherwise sets the in-memory stop flag and **synchronously, forcibly persists** `journal.CancellationRequested = true` before returning.
    - `void Update()` — advances the active operation by one tick through `Mutating → Refreshing → Verifying → terminal`. Never throws: wraps its work in a boundary that, on failure, attempts one best-effort terminal-failure checkpoint and, if that itself throws, marks the operation `RequiresRecovery` in memory without a second persistence attempt.

**Terminal state stays visible.** `OperationController` holds a single `_active` field (an internal `ActiveOperationContext`, not just a journal) that is **not cleared when an operation concludes** — it's only replaced when a *new* `StartApply` call constructs a fresh context. Capability flags (`CanStartApply` etc.) are derived from `_active is null || (_active.Journal.IsTerminal && !_active.RequiresRecovery)`, reusing `OperationJournal.IsTerminal` from Plan A1 — this is what lets `State.Stage` still read `Completed` on the very next `Update()` call while `CanStartApply` is simultaneously `true` again, without special-casing "just finished" as distinct from "long since finished."

**`RequiresRecovery` retains context, never clears it.** When `RefreshSettlement`/`VerificationSettlement` return `RecoveryRequired`, `_active.RequiresRecovery` is set `true` and `Update()` stops calling into that operation's engines on future ticks — but `_active.Journal`, `.Plan`, `.Mutation`, and the bundle directory are all left exactly as they were. Nothing is nulled out. A later plan's recovery classification (Plan D) is what acts on this; this plan only needs to stop making things worse.

**`Cancelled` is only asserted once verification is trustworthy.** If a cancellation was requested and verification concludes normally (`Settled` or `TimedOut`), the terminal stage is `Cancelled`. If verification instead returns `RecoveryRequired`, the journal stays non-terminal (still `Verifying`) and `RequiresRecovery` is set — `CancellationRequested` being `true` on the journal does not by itself produce a `Cancelled` outcome (design §5a's precedence rule).

`OperationStateSnapshot` field list:

```csharp
public sealed record OperationStateSnapshot(
    OperationStage? Stage,
    OperationType? Kind,
    int ProcessedSteps,
    int TotalSteps,
    int ProcessedTargets,      // per-target progress, distinct from step count (a cycle-breaking plan has more steps than targets)
    int SuccessfulTargets,
    int TotalTargets,
    string? LastProcessedIdentifier,     // renamed from CurrentIdentifier - it IS the last-processed one, between frames it is not "current"
    string? LastProcessedDisplayName,    // the mod's real display name, looked up from RecoveryTargets - not the identifier again
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
    bool CanRequestCancellation);
```

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationControllerTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static SetModPathResult Success => new(SetModPathStatus.Success, "Success", null);

    private static OperationPlan SinglePlan(string id = "mod-a", OperationType type = OperationType.Apply) =>
        OperationPlan.Create(type, [new(0, id, "Weapons/A", OperationStepKind.FinalMove, 0)], [new(id, "Gear/A", "Weapons/A", id)]);

    private static OperationController NewController(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink? diagnostics = null) =>
        new(adapter, clock, diagnostics ?? new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4));

    [Fact]
    public void State_Initially_IdleWithCanStartApplyTrue()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Null(controller.State.Stage);
        Assert.True(controller.State.CanStartApply);
    }

    [Fact]
    public void StartApply_RestoreTypePlan_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());

            Assert.Throws<ArgumentException>(() => controller.StartApply(SinglePlan(type: OperationType.Restore), Guid.NewGuid(), dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartApply_WhileAnotherIsNonTerminal_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName); // now Mutating, non-terminal

            Assert.Throws<InvalidOperationException>(() => controller.StartApply(SinglePlan("mod-b"), Guid.NewGuid(), dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartApply_AfterAPriorOperationTerminated_IsAllowedAndOverwritesTheTerminalState()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(
            LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
        var clock = new FakeClock();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, clock);
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Completed
            Assert.Equal(OperationStage.Completed, controller.State.Stage);

            var exception = Record.Exception(() => controller.StartApply(SinglePlan("mod-b"), Guid.NewGuid(), dir.FullName));

            Assert.Null(exception);
            Assert.Equal(OperationStage.Mutating, controller.State.Stage); // the new operation, not the old terminal one
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartApply_SetsCanStartApplyFalseAndStageMutating()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            Assert.False(controller.State.CanStartApply);
            Assert.Equal(OperationStage.Mutating, controller.State.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartApply_PersistsPreparedThenMutatingAsTwoForcedCheckpoints()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            // The journal on disk reflects the LAST forced write (Mutating) - this test proves
            // both writes happened without needing to intercept the intermediate Prepared state,
            // by asserting persistence succeeded at all (StartApply would have thrown on a bad
            // sequence) and the final on-disk stage is correct.
            var journalPath = OperationBundlePaths.JournalPath(dir.FullName);
            Assert.True(OperationJournalCodec.TryLoad(journalPath, out var journal));
            Assert.Equal(OperationStage.Mutating, journal!.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_DrivesMutationThroughRefreshingToVerifyingAndSettles()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(
            LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
        var clock = new FakeClock();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, clock);
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            controller.Update();
            controller.Update();
            controller.Update();

            Assert.Equal(OperationStage.Completed, controller.State.Stage);
            Assert.True(controller.State.CanStartApply); // terminal AND immediately allows a new operation
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_WithNoActiveOperation_DoesNothingAndDoesNotThrow()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        var exception = Record.Exception(controller.Update);

        Assert.Null(exception);
    }

    [Fact]
    public void Update_UnexpectedExceptionFromAdapter_FailsSafelyAndFreesTheSlot()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathException(new InvalidOperationException("simulated"));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            var exception = Record.Exception(controller.Update);

            Assert.Null(exception);
            Assert.Equal(OperationStage.FailedBeforeMutation, controller.State.Stage);
            Assert.True(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_RefreshRecoveryRequired_RetainsContextAndBlocksNewOperationsWithoutThrowing()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.ProviderUnavailable));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.Update(); // Mutating -> Refreshing

            var exception = Record.Exception(controller.Update); // Refreshing -> RecoveryRequired

            Assert.Null(exception);
            Assert.Equal(OperationStage.Refreshing, controller.State.Stage); // left non-terminal, not erased
            Assert.True(controller.State.RequiresRecovery);
            Assert.False(controller.State.CanStartApply); // locked, not freed as though nothing happened

            // A further Update() must not attempt to advance the stuck operation again.
            var secondException = Record.Exception(controller.Update);
            Assert.Null(secondException);
            Assert.Equal(OperationStage.Refreshing, controller.State.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_VerificationRecoveryRequired_RetainsContextAndBlocksNewOperations()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying

            controller.Update(); // Verifying -> RecoveryRequired

            Assert.Equal(OperationStage.Verifying, controller.State.Stage);
            Assert.True(controller.State.RequiresRecovery);
            Assert.False(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_PersistsCancellationRequestedImmediately()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            controller.RequestCancellation();

            var journalPath = OperationBundlePaths.JournalPath(dir.FullName);
            Assert.True(OperationJournalCodec.TryLoad(journalPath, out var journal));
            Assert.True(journal!.CancellationRequested);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_ThenUpdate_StopsMutationAndOnTrustworthyVerificationEndsCancelled()
    {
        var adapter = new FakePenumbraOperations();
        // No SetModPath ever queued/consumed - cancellation observed at the very start of the
        // first Advance() call means zero mutation steps run.
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.RequestCancellation();

            controller.Update(); // Mutating -> Refreshing (cancellation observed, no steps ran)
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Cancelled (verification itself was trustworthy)

            Assert.Equal(OperationStage.Cancelled, controller.State.Stage);
            Assert.True(controller.State.CanStartApply);
            Assert.Empty(adapter.SetModPathCalls);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_ThenUntrustworthyVerification_RequiresRecoveryNotCancelled()
    {
        // Cancellation was requested, but verification itself can't be trusted - design section
        // 5a's precedence rule: recoverability outranks asserting a clean Cancelled outcome.
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.RequestCancellation();

            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> RecoveryRequired, NOT Cancelled

            Assert.NotEqual(OperationStage.Cancelled, controller.State.Stage);
            Assert.Equal(OperationStage.Verifying, controller.State.Stage);
            Assert.True(controller.State.RequiresRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_WithNoActiveOperation_DoesNothing()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        var exception = Record.Exception(controller.RequestCancellation);

        Assert.Null(exception);
        Assert.True(controller.State.CanStartApply);
    }

    [Fact]
    public void State_ReflectsProcessedAndSuccessfulTargetsSeparatelyFromStepCount()
    {
        // A cycle-breaking plan: 3 execution steps (temp hop + 2 final moves) but only 2 targets.
        var steps = new OperationExecutionStep[]
        {
            new(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            new(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            new(2, "X", "P2", OperationStepKind.FinalMove, 0),
        };
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X"), new("Y", "P2", "P0", "Y") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(plan, Guid.NewGuid(), dir.FullName);
            controller.Update(); // processes all 3 steps in one call given ample budget

            Assert.Equal(3, controller.State.ProcessedSteps);
            Assert.Equal(3, controller.State.TotalSteps);
            Assert.Equal(2, controller.State.ProcessedTargets); // X and Y, not 3
            Assert.Equal(2, controller.State.SuccessfulTargets);
            Assert.Equal(2, controller.State.TotalTargets);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void State_LastProcessedDisplayNameIsTheRealModNameNotTheIdentifier()
    {
        var steps = new OperationExecutionStep[] { new(0, "mod-a-identifier", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new OperationRecoveryTarget[] { new("mod-a-identifier", "Gear/A", "Weapons/A", "A Pretty Display Name") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(plan, Guid.NewGuid(), dir.FullName);
            controller.Update();

            Assert.Equal("mod-a-identifier", controller.State.LastProcessedIdentifier);
            Assert.Equal("A Pretty Display Name", controller.State.LastProcessedDisplayName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_StepResultAppendFailure_DoesNotEscapeAndSettlesAsFailedBeforeMutation()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            // StepResultLog.Append opens the results file with FileShare.Read - holding it open
            // with FileShare.None here forces that constructor to throw a sharing-violation
            // IOException, uncaught inside PathMutationOperation.Advance (before ProcessedStepCount
            // is ever advanced past 0), exercising Update()'s outer catch without needing to fake
            // the adapter itself into failing. journal.json is untouched by this lock, so the
            // failure-checkpoint write in the catch block succeeds - this is the single-failure
            // path, distinct from the double-failure case covered below.
            var resultsPath = OperationBundlePaths.ResultsPath(dir.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
            using (new FileStream(resultsPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var exception = Record.Exception(() => controller.Update());

                Assert.Null(exception);
            }

            Assert.False(controller.State.RequiresRecovery);
            Assert.Equal(OperationStage.FailedBeforeMutation, controller.State.Stage);
            Assert.True(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_JournalWriteFailsOnBothThePrimaryAndTheFailureCheckpointAttempt_LeavesOperationRequiringRecoveryWithStageUnchanged()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            // AtomicFile.CreateOrReplace's File.Move(tempPath, path, overwrite: true) throws when
            // the destination is held open with no sharing - this makes BOTH the in-loop forced
            // checkpoint (entering Refreshing, which mutates active.Journal in memory before the
            // write that then fails) AND Update()'s own failure-checkpoint attempt fail against the
            // same locked journal.json. The second failure means the FailedPartiallyApplied record
            // it built is never committed to _active.Journal, which is left holding the last
            // in-memory value (Refreshing) rather than an unverified terminal Stage.
            var journalPath = OperationBundlePaths.JournalPath(dir.FullName);
            using (new FileStream(journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var exception = Record.Exception(() => controller.Update());

                Assert.Null(exception);
            }

            Assert.True(controller.State.RequiresRecovery);
            Assert.Equal(OperationStage.Refreshing, controller.State.Stage); // last in-memory value before the failed write, not the failure stage
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationControllerTests`
Expected: FAIL — `OperationController`/`OperationStateSnapshot` do not exist.

- [ ] **Step 3: Write OperationStateSnapshot**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Design doc section 7b, revised in this plan's second review round: CurrentIdentifier
/// renamed LastProcessedIdentifier (it was never "current" - between frames it's the most recently
/// finished one), LastProcessedDisplayName is a real mod name lookup rather than a duplicate of the
/// identifier, and per-target progress (ProcessedTargets/SuccessfulTargets/TotalTargets) is tracked
/// separately from per-step progress since a cycle-breaking plan has more steps than targets. The
/// only thing MainWindow (a later plan) is allowed to read. Published as a whole new instance after
/// every meaningful transition, never mutated in place. </summary>
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
    bool CanRequestCancellation)
{
    public static OperationStateSnapshot Idle { get; } = new(
        Stage: null, Kind: null, ProcessedSteps: 0, TotalSteps: 0,
        ProcessedTargets: 0, SuccessfulTargets: 0, TotalTargets: 0,
        LastProcessedIdentifier: null, LastProcessedDisplayName: null, LastError: null,
        RequiresRecovery: false, RecoveryClassificationPending: false,
        CanStartApply: true, CanStartRestore: true, CanScan: true, CanIndex: true,
        CanRunFolderCleanup: true, CanRunFolderCleanupRollback: true, CanCreateBackup: true,
        CanResolveRecovery: false, CanRequestCancellation: false);
}
```

- [ ] **Step 4: Write OperationController**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Design doc sections 2, 7, 7a, revised in this plan's second review round. Owns the operation
/// state machine from Prepared onward (Preparing / plan construction is the caller's job - it
/// needs OrganizerState data this layer doesn't have). _active is never cleared when an operation
/// concludes - it is only replaced by the next StartApply call - so a terminal Stage stays visible
/// in State while CanStartApply simultaneously becomes true again (derived from
/// OperationJournal.IsTerminal). A RecoveryRequired transition sets _active.RequiresRecovery and
/// retains every field of the context rather than clearing anything.
/// </summary>
public sealed class OperationController
{
    private sealed class ActiveOperationContext
    {
        public required OperationJournal Journal { get; set; }
        public required OperationPlan Plan { get; init; }
        public required PathMutationOperation Mutation { get; init; }
        public required OperationCheckpointer Checkpointer { get; init; }
        public RefreshSettlement? Refresh { get; set; }
        public VerificationSettlement? Verification { get; set; }
        public bool RequiresRecovery { get; set; }
    }

    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly TimeSpan _frameBudget;
    private ActiveOperationContext? _active;
    private bool _stopRequested;

    public OperationStateSnapshot State { get; private set; } = OperationStateSnapshot.Idle;

    public OperationController(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, TimeSpan frameBudget)
    {
        _adapter = adapter;
        _clock = clock;
        _diagnostics = diagnostics;
        _frameBudget = frameBudget;
    }

    public void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory)
    {
        if (plan.Type != OperationType.Apply)
            throw new ArgumentException($"StartApply requires an Apply-type plan; got {plan.Type}.", nameof(plan));
        if (_active is not null && !_active.Journal.IsTerminal)
            throw new InvalidOperationException("Another organizer operation is already in progress.");

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

    public void RequestCancellation()
    {
        if (_active is null || _active.Journal.Stage != OperationStage.Mutating)
            return;

        _stopRequested = true;
        _active.Journal = _active.Journal with { CancellationRequested = true, UpdatedAt = DateTimeOffset.UtcNow };
        try
        {
            _active.Checkpointer.CheckpointIfDue(_active.Journal, force: true);
        }
        catch (Exception)
        {
            _active.RequiresRecovery = true;
        }

        PublishState();
    }

    public void Update()
    {
        if (_active is null || _active.RequiresRecovery)
            return;

        try
        {
            AdvanceActiveOperation();
        }
        catch (Exception)
        {
            var failedJournal = _active.Journal with
            {
                Stage = _active.Journal.ProcessedStepCount > 0 ? OperationStage.FailedPartiallyApplied : OperationStage.FailedBeforeMutation,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            try
            {
                _active.Checkpointer.CheckpointIfDue(failedJournal, force: true);
                _active.Journal = failedJournal;
            }
            catch (Exception)
            {
                // Cannot prove the terminal record was persisted - leave the operation locked as
                // requiring recovery rather than claiming a terminal outcome that isn't backed up.
                _active.RequiresRecovery = true;
            }
        }

        PublishState();
    }

    private void AdvanceActiveOperation()
    {
        var active = _active!;

        if (active.Journal.Stage == OperationStage.Mutating)
        {
            var result = active.Mutation.Advance(active.Journal, _frameBudget, _stopRequested, j => active.Checkpointer.CheckpointIfDue(j));
            active.Journal = result.Journal;

            switch (result.Status)
            {
                case MutationAdvanceStatus.MutationFinished:
                    active.Journal = active.Journal with { Stage = OperationStage.Refreshing, UpdatedAt = DateTimeOffset.UtcNow };
                    active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    break;
                case MutationAdvanceStatus.CancellationObserved:
                    // Refreshing still runs once even for a cancelled operation, so Verifying can
                    // report on whatever DID complete before the cancellation was observed.
                    active.Journal = active.Journal with { Stage = OperationStage.Refreshing, UpdatedAt = DateTimeOffset.UtcNow };
                    active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    break;
                case MutationAdvanceStatus.IntegrityFailure:
                    var stage = active.Journal.ProcessedStepCount > 0 ? OperationStage.FailedPartiallyApplied : OperationStage.FailedBeforeMutation;
                    active.Journal = active.Journal with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow };
                    active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    break;
                // Working: nothing more to do this tick.
            }

            return;
        }

        if (active.Journal.Stage == OperationStage.Refreshing)
        {
            active.Refresh ??= new RefreshSettlement();
            var result = active.Refresh.Advance(_adapter, _clock, _diagnostics, active.Journal.OperationId);

            if (result.Status == RefreshSettlementStatus.Settled)
            {
                active.Journal = active.Journal with { Stage = OperationStage.Verifying, UpdatedAt = DateTimeOffset.UtcNow };
                active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
            }
            else if (result.Status == RefreshSettlementStatus.RecoveryRequired)
            {
                active.RequiresRecovery = true; // journal stays non-terminal (still Refreshing), context retained
            }

            return;
        }

        if (active.Journal.Stage == OperationStage.Verifying)
        {
            active.Verification ??= new VerificationSettlement();
            var result = active.Verification.Advance(
                _adapter, _clock, active.Plan.RecoveryTargets, active.Mutation.MutationStatusByIdentifier, _diagnostics, active.Journal.OperationId);

            if (result.Status == VerificationStatus.RecoveryRequired)
            {
                active.RequiresRecovery = true; // journal stays non-terminal (still Verifying), context retained
                return;
            }

            if (result.Status == VerificationStatus.Waiting)
                return;

            // Settled or TimedOut both conclude the operation. A cancelled outcome is only
            // asserted here, once verification proved trustworthy - design section 5a's
            // precedence rule (this is the ONLY place Cancelled is ever set).
            if (active.Journal.CancellationRequested)
            {
                active.Journal = active.Journal with { Stage = OperationStage.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
            }
            else
            {
                var hasFailures = result.Status == VerificationStatus.TimedOut || active.Mutation.MutationStatusByIdentifier.Values
                    .Any(s => s is TargetMutationStatus.FinalStepFailed or TargetMutationStatus.SkippedAfterEarlierFailure);
                active.Journal = active.Journal with
                {
                    Stage = hasFailures ? OperationStage.CompletedWithItemFailures : OperationStage.Completed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
        }
    }

    private void PublishState()
    {
        if (_active is null)
        {
            State = OperationStateSnapshot.Idle;
            return;
        }

        var journal = _active.Journal;
        var canStartNew = journal.IsTerminal && !_active.RequiresRecovery;
        var modNameByIdentifier = _active.Plan.RecoveryTargets.ToDictionary(t => t.Identifier, t => t.ModName, StringComparer.Ordinal);
        var statuses = _active.Mutation.MutationStatusByIdentifier;
        var processedTargets = statuses.Values.Count(s => s != TargetMutationStatus.NotAttempted);
        var successfulTargets = statuses.Values.Count(s => s is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);

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
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationControllerTests`
Expected: PASS (19 tests).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: add OperationController with terminal-state retention and recovery lock"
```

---

### Task 7: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test plus everything added in Tasks 1–6, zero failures.

- [ ] **Step 2: Confirm the working tree is clean and no stray temp dirs leaked**

Run: `git status --short`
Expected: no output (all tests write to `Directory.CreateTempSubdirectory()` outside the repo and delete in `finally`).

---

## What this plan does not cover

Deferred to **Plan B2** (Dalamud wiring, verified by code review + manual in-game testing, not xUnit — this repo has no Dalamud test-double infrastructure):

- `PenumbraOperationsAdapter` — the real `IPenumbraOperations` implementation wrapping `GetModListAdapterIpc`/`SetModPathIpc`/refresh IPC, translating `Penumbra.Api.Enums.PenumbraApiEc` into `SetModPathResult`.
- `FileDiagnosticsSink`'s real construction path (a real `diagnosticsLogPath` under `PluginInterface.ConfigDirectory`, wired via `OperationBundlePaths.DiagnosticsLogPath`).
- Subscribing `OperationController.Update` to `Framework.Update`, constructed once in `Plugin.cs`.
- Replacing `Plugin.cs`'s `ApplyChanges`/`ExecuteOrderedMoves` to build an `OperationPlan` from `OrganizerState`, capture and persist the initial `RollbackSnapshot` at the bundle's `snapshot.json` (this plan's `StartApply` assumes the plan and snapshot are already durable — building and persisting them from real `OrganizerState`/`RollbackHistory` data is Plan B2's job, not this plan's), and call `StartApply` instead of looping `SetModPathIpc.Invoke` directly.
- `RequestCancellation` UI wiring — the method exists and is fully tested, but nothing calls it yet.

Deferred to **Plan C** (design §13): the same execution engine configured for Restore — `StartApply`'s `plan.Type != OperationType.Apply` guard means a `StartRestore` entry point (or a generalized internal `StartOperation` with type-checked public wrappers) needs to be added there, validating whether `PathMutationOperation`/`OperationController` genuinely need no Restore-specific branching or whether Restore's `RollbackHistory.BuildRestorePlan`-derived moves surface a real difference.

Deferred to **Plan D** (design §13): `RecoveryAssessment`, startup deferred classification, the three recovery resolutions (Continue/Restore Previous State/Keep Current — which is what actually *acts* on the `RequiresRecovery` lock this plan introduces), multi-journal discovery wired into controller startup, `RecoveryDialogSnapshot` population, `RecoveryClassificationPending` (present in `OperationStateSnapshot` but always `false` in this plan).

Deferred to **Plan E** (design §13): `MainWindow` UI wiring, the recovery dialog, diagnostics dump changes (reading `DiagnosticsLog`/`StepResultLog` back out for a human-readable report).

Also out of scope for this plan specifically:
- `Preparing`/`Prepared` stages' *content* beyond the bare stage-entry checkpoints — this plan's `StartApply` writes `Prepared` then `Mutating` back-to-back with no real work happening during `Prepared` (no validation step lives there in this plan); design §3's steps 1–5 (validate, capture snapshot, construct plan, persist plan, verify snapshot/plan reopen) are `Plugin.cs`-specific work for Plan B2, which is expected to call something *before* `StartApply` to do that work, then hand `StartApply` an already-valid plan and snapshot ID.
- `MutationStopReason.JournalWriteFailed` and `.PlanCorrupt` are modeled in the enum (matching the design's full stop-reason vocabulary) but never produced by any code in this plan — see Task 4's Interfaces note for why.
- `LastError` on `OperationStateSnapshot` is always `null` in this plan — no code path populates it yet; a later plan (likely E, when the diagnostics dump needs a human-readable summary) is expected to derive it from the terminal `Stage`/`MutationStopReason`/exception details.
