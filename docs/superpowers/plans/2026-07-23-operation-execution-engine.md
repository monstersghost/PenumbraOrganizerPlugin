# Operation Execution Engine (Plan B1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the frame-budgeted mutation execution engine — the adapter interface, refresh/verification settlement, group-cascade mutation logic, and the operation controller skeleton — entirely against a fake Penumbra adapter, so the whole engine is proven correct in xUnit before it ever touches real Dalamud/Penumbra IPC.

**Architecture:** This is Plan B split into B1 (this plan — fully unit-testable) and B2 (a later, separate plan: the real `PenumbraOperationsAdapter` wrapping actual Penumbra IPC, `Framework.Update` subscription, and replacing `Plugin.cs`'s `ApplyChanges`/`ExecuteOrderedMoves`). This repo has no Dalamud test-double infrastructure — `PluginInterface`/`Config`/`_operationInProgress` in `Plugin.cs` are populated by Dalamud's own DI at runtime and cannot be constructed in xUnit — so B1 is scoped to depend on nothing but `IPenumbraOperations` (an interface, faked in tests) and `IElapsedTimeSource` (already built in Plan A1, faked in tests). Everything in this plan is real production code, not scaffolding — B2 wires it to Dalamud, it does not rewrite it.

**Tech Stack:** .NET (project SDK per `PenumbraOrganizer.Plugin.csproj`), `System.Text.Json` (via already-merged codecs), xUnit 2.5.3.

## Global Constraints

Copied from `docs/superpowers/specs/2026-07-22-operation-controller-design.md` sections 5, 5a, 5b, 6, 7, 7a, 7b; every task's requirements implicitly include these:

- **`Advance` always attempts at least one eligible step per call**, then stops before starting the *next* step if the frame budget is exhausted or a stop was requested — one `SetModPath`/adapter call is never split across two `Advance` calls.
- **Group-cascade on failure**: when a step's IPC call itself fails, every remaining not-yet-processed step in that step's `GroupId` is recorded `SkippedAfterEarlierFailure` and the journal's `ProcessedStepCount` advances past the whole contiguous group range in one move — never partially, never past an unprocessed step from a different group (this is safe *because* Plan A1 guarantees every group is a contiguous `StepIndex` range).
- **The durable step result is appended (`StepResultLog.Append`) before `ProcessedStepCount` advances past that step** — never the reverse.
- **IPC failure continuation policy** (item-level failures cascade their group and continue; operation-integrity conditions stop everything) — see Task 4's exact table, which also covers `NothingChanged` (confirmed as a real `Penumbra.Api.Enums.PenumbraApiEc` member not in the original design table — treated as a no-op success).
- **`Refreshing` always runs once** after `Mutating` concludes, regardless of how it concluded (success, item-failure exhaustion, integrity stop, or cancellation) — never skipped, never run more than once per operation.
- **Verification/refresh settlement never blocks synchronously** — one attempt per `Update()` tick, gated by a retry interval, bounded by a maximum attempt count.
- **`Update()` always attempts progress on the active operation and never throws** — an outer exception boundary around the whole `Advance()` call fails the operation safely rather than escaping to the (future) framework update callback.
- **Non-reentrancy**: one active operation at a time; starting a second is rejected, not queued.
- **`sealed record` for data types, `static class` for pure stateless logic, `sealed class` for stateful engines** — follow `AtomicFile.cs`/`OperationPlan.cs`/`VerificationSettlement` (design doc §6, transcribed in Task 2).
- **Timestamps persisted to a journal are always `DateTimeOffset.UtcNow`** — `IElapsedTimeSource` values are process-relative and never written to disk.

Run the full suite with `dotnet test` from the repo root. Commit with `git add` on specific files only (never `git add -A`).

---

### Task 1: IPenumbraOperations interface and adapter result types

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/IPenumbraOperations.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperations.cs` (test double, lives in the test project — this is the seam every later task's tests drive)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperationsTests.cs`

**Interfaces:**
- Consumes: `LiveModSnapshot` (Plan A1), `Penumbra.Api.Enums.PenumbraApiEc` (existing NuGet dependency, already used in `Plugin.cs`).
- Produces:
  - `LiveModReadStatus` enum: `Success, TemporarilyUnavailable, ProviderUnavailable, InvalidData`.
  - `LiveModReadResult(LiveModReadStatus Status, LiveModSnapshot? Snapshot)`.
  - `RefreshStatus` enum: `Success, TemporarilyUnavailable, ProviderUnavailable, InvalidState`.
  - `RefreshResult(RefreshStatus Status)`.
  - `IPenumbraOperations` interface:
    - `LiveModReadResult GetLiveMods()`
    - `Penumbra.Api.Enums.PenumbraApiEc SetModPath(string identifier, string targetPath)`
    - `RefreshResult RequestPostMutationRefresh()`
  - `FakePenumbraOperations` (test project only): implements `IPenumbraOperations` with fully controllable, queued responses — `EnqueueLiveModRead(LiveModReadResult)`, `EnqueueSetModPathResult(Penumbra.Api.Enums.PenumbraApiEc)`, `EnqueueRefreshResult(RefreshResult)`, plus recorded call lists (`SetModPathCalls: IReadOnlyList<(string Identifier, string TargetPath)>`) so tests can assert exactly what was called, not just what was returned. Throws `InvalidOperationException` if a method is called with no queued response — this is deliberate: a test that doesn't set up an expected call should fail loudly, not silently return a default.

This interface is the seam that makes every other task in this plan unit-testable without Dalamud. `SetModPath` returns the real Penumbra enum directly (not a wrapping type) — it's a lightweight existing dependency, and duplicating it would just be another thing to keep in sync.

- [ ] **Step 1: Write the failing tests**

```csharp
using Penumbra.Api.Enums;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class FakePenumbraOperationsTests
{
    [Fact]
    public void GetLiveMods_ReturnsQueuedResultInOrder()
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
        fake.EnqueueSetModPathResult(PenumbraApiEc.Success);

        var result = fake.SetModPath("mod-a", "Weapons/A");

        Assert.Equal(PenumbraApiEc.Success, result);
        var call = Assert.Single(fake.SetModPathCalls);
        Assert.Equal("mod-a", call.Identifier);
        Assert.Equal("Weapons/A", call.TargetPath);
    }

    [Fact]
    public void SetModPath_NoQueuedResult_Throws()
    {
        var fake = new FakePenumbraOperations();

        Assert.Throws<InvalidOperationException>(() => fake.SetModPath("mod-a", "Weapons/A"));
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FakePenumbraOperationsTests`
Expected: FAIL — none of the types exist yet.

- [ ] **Step 3: Write the interface and result types**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum LiveModReadStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidData }

/// <summary> Design doc section 6. Snapshot is null for any non-Success status. </summary>
public sealed record LiveModReadResult(LiveModReadStatus Status, LiveModSnapshot? Snapshot);

public enum RefreshStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidState }

/// <summary> Design doc section 5b. </summary>
public sealed record RefreshResult(RefreshStatus Status);

/// <summary>
/// Narrow Penumbra IPC boundary the execution engine depends on instead of the concrete Plugin
/// class - design doc section 2. SetModPath returns the real Penumbra.Api.Enums.PenumbraApiEc
/// directly rather than a wrapping type, since it's already a lightweight existing dependency.
/// A real implementation (PenumbraOperationsAdapter, wrapping the actual Penumbra IPC subscribers)
/// is built in a later plan; this interface is what makes everything in Plan B1 unit-testable
/// without Dalamud.
/// </summary>
public interface IPenumbraOperations
{
    LiveModReadResult GetLiveMods();
    Penumbra.Api.Enums.PenumbraApiEc SetModPath(string identifier, string targetPath);
    RefreshResult RequestPostMutationRefresh();
}
```

- [ ] **Step 4: Write the fake**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperations.cs`:

```csharp
using Penumbra.Api.Enums;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

/// <summary>
/// Test double for IPenumbraOperations. Every call must have a queued response, or it throws -
/// a test that forgets to set up an expected call fails loudly rather than silently returning a
/// default value that could mask a real bug.
/// </summary>
public sealed class FakePenumbraOperations : IPenumbraOperations
{
    private readonly Queue<LiveModReadResult> _liveModReads = new();
    private readonly Queue<PenumbraApiEc> _setModPathResults = new();
    private readonly Queue<RefreshResult> _refreshResults = new();
    private readonly List<(string Identifier, string TargetPath)> _setModPathCalls = [];

    public IReadOnlyList<(string Identifier, string TargetPath)> SetModPathCalls => _setModPathCalls;

    public void EnqueueLiveModRead(LiveModReadResult result) => _liveModReads.Enqueue(result);

    public void EnqueueSetModPathResult(PenumbraApiEc result) => _setModPathResults.Enqueue(result);

    public void EnqueueRefreshResult(RefreshResult result) => _refreshResults.Enqueue(result);

    public LiveModReadResult GetLiveMods() =>
        _liveModReads.Count > 0
            ? _liveModReads.Dequeue()
            : throw new InvalidOperationException("FakePenumbraOperations.GetLiveMods called with no queued result.");

    public PenumbraApiEc SetModPath(string identifier, string targetPath)
    {
        _setModPathCalls.Add((identifier, targetPath));
        return _setModPathResults.Count > 0
            ? _setModPathResults.Dequeue()
            : throw new InvalidOperationException("FakePenumbraOperations.SetModPath called with no queued result.");
    }

    public RefreshResult RequestPostMutationRefresh() =>
        _refreshResults.Count > 0
            ? _refreshResults.Dequeue()
            : throw new InvalidOperationException("FakePenumbraOperations.RequestPostMutationRefresh called with no queued result.");
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~FakePenumbraOperationsTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/IPenumbraOperations.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperations.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/FakePenumbraOperationsTests.cs
git commit -m "feat: add IPenumbraOperations adapter interface and its test fake"
```

---

### Task 2: IDiagnosticsSink

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/IDiagnosticsSink.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsSinkTests.cs`

**Interfaces:**
- Consumes: `DiagnosticEvent`/`DiagnosticEventKind`/`DiagnosticsLog` (Plan A2, already merged).
- Produces:
  - `IDiagnosticsSink` interface: `void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration)`, `void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration)`.
  - `FileDiagnosticsSink` — the production implementation, writing through `DiagnosticsLog.Append` at a fixed path.

This is the small abstraction design §5 calls for so `PathMutationOperation`/`VerificationSettlement` don't depend on a concrete file path directly. `FileDiagnosticsSink.Append` can never throw (it calls `DiagnosticsLog.Append`, which already swallows its own failures per Plan A2) — no new try/catch needed here, the guarantee is inherited.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class FileDiagnosticsSinkTests
{
    [Fact]
    public void RecordSlowCall_AppendsASlowCallEventToTheLog()
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
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RecordSlowLiveSnapshot_AppendsASlowLiveSnapshotEventToTheLog()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var sink = new FileDiagnosticsSink(path);

            sink.RecordSlowLiveSnapshot(null, TimeSpan.FromMilliseconds(120));

            var events = DiagnosticsLog.ReadAll(path);
            var single = Assert.Single(events);
            Assert.Equal(DiagnosticEventKind.SlowLiveSnapshot, single.Kind);
            Assert.Null(single.OperationId);
            Assert.Equal(120, single.DurationMilliseconds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FileDiagnosticsSinkTests`
Expected: FAIL — `IDiagnosticsSink`/`FileDiagnosticsSink` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Design doc section 5: PathMutationOperation/VerificationSettlement depend on this
/// abstraction, not on a file path directly. </summary>
public interface IDiagnosticsSink
{
    void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration);
    void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration);
}

/// <summary> Writes through DiagnosticsLog, which already swallows its own write failures (Plan
/// A2) - no additional exception handling needed here. </summary>
public sealed class FileDiagnosticsSink(string diagnosticsLogPath) : IDiagnosticsSink
{
    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowCall, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null));

    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowLiveSnapshot, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~FileDiagnosticsSinkTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/IDiagnosticsSink.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/DiagnosticsSinkTests.cs
git commit -m "feat: add IDiagnosticsSink and its DiagnosticsLog-backed implementation"
```

---

### Task 3: VerificationSettlement and RefreshSettlement

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/VerificationSettlement.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/RefreshSettlement.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/VerificationSettlementTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RefreshSettlementTests.cs`

**Interfaces:**
- Consumes: `IPenumbraOperations`/`IElapsedTimeSource`/`IDiagnosticsSink` (Tasks 1–2, Plan A1), `OperationRecoveryTarget` (Plan A1), `TargetMutationStatus` (Task 4 — see note below).
- Produces:
  - `VerificationStatus` enum: `Waiting, Settled, TimedOut, RecoveryRequired`.
  - `RecoveryRequiredReason` enum: `DuplicateIdentifiers, ProviderUnavailable, InvalidData, TransientReadExhausted`.
  - `VerificationResult(VerificationStatus Status, IReadOnlyList<string> UnsettledIdentifiers, RecoveryRequiredReason? Reason)`.
  - `VerificationSettlement` sealed class: `VerificationResult Advance(IPenumbraOperations adapter, IElapsedTimeSource clock, IReadOnlyList<OperationRecoveryTarget> targets, IReadOnlyDictionary<string, TargetMutationStatus> mutationStatuses, IDiagnosticsSink diagnostics, Guid operationId)`.
  - `RefreshSettlementStatus` enum: `Waiting, Settled, RecoveryRequired`.
  - `RefreshSettlementResult(RefreshSettlementStatus Status)`.
  - `RefreshSettlement` sealed class: `RefreshSettlementResult Advance(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, Guid operationId)`.

**`TargetMutationStatus` is defined in this task**, not Task 4, even though design §5 introduces it in the mutation-execution section — `VerificationSettlement` consumes it as a parameter type, and this task is being implemented before Task 4. Define it here; Task 4 reuses it (same file location doesn't matter, same type does — see Task 4's Interfaces block, which consumes this exact type).

`RefreshSettlement` isn't given complete code in the design doc (only a policy table), but design §5b explicitly says it "reuses the same attempt-count/interval shape as verification settlement" — so its implementation mirrors `VerificationSettlement`'s bounded-retry shape exactly, calling `adapter.RequestPostMutationRefresh()` instead of `adapter.GetLiveMods()`, with no `TimedOut` status (§5b's table has no timeout-but-otherwise-fine outcome — `TemporarilyUnavailable` either resolves within the bound or becomes `RecoveryRequired`, there's no analog to verification's per-identifier `TimedOut`).

- [ ] **Step 1: Write the failing tests**

```csharp
using Penumbra.Api.Enums;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class VerificationSettlementTests
{
    private static OperationRecoveryTarget Target(string id, string finalPath) => new(id, "Gear/" + id, finalPath, id);

    private static LiveModSnapshot Snapshot(params (string Id, string Path)[] mods) =>
        LiveModSnapshotBuilder.Build(mods.Select(m => new LiveMod(m.Id, m.Id, m.Path, HeliosphereManaged: false)));

    private class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static readonly Dictionary<string, TargetMutationStatus> NoOp = new();

    [Fact]
    public void Advance_AllTargetsSettled_ReturnsSettled()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot(("mod-a", "Weapons/A"))));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var settlement = new VerificationSettlement();
        var result = settlement.Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_TargetNotYetSettled_KeepsWaitingUntilMaxAttemptsThenTimesOut()
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

        var settlement = new VerificationSettlement();
        var result = settlement.Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

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

        var settlement = new VerificationSettlement();
        var result = settlement.Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.DuplicateIdentifiers, result.Reason);
    }

    [Fact]
    public void Advance_ProviderUnavailable_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));
        var clock = new FakeClock();

        var settlement = new VerificationSettlement();
        var result = settlement.Advance(adapter, clock, [], NoOp, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.ProviderUnavailable, result.Reason);
    }

    [Fact]
    public void Advance_SecondCallWithinRetryInterval_ReturnsWaitingWithoutReadingAgain()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot()));
        var clock = new FakeClock();

        var settlement = new VerificationSettlement();
        settlement.Advance(adapter, clock, [], NoOp, new NoOpDiagnosticsSink(), Guid.NewGuid()); // consumes the one queued read
        var second = settlement.Advance(adapter, clock, [], NoOp, new NoOpDiagnosticsSink(), Guid.NewGuid()); // no time advanced

        Assert.Equal(VerificationStatus.Waiting, second.Status);
        // No exception from an empty queue proves GetLiveMods was not called a second time.
    }
}

/// <summary> No-op sink for tests that don't care about diagnostics events. </summary>
internal sealed class NoOpDiagnosticsSink : IDiagnosticsSink
{
    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) { }
    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) { }
}
```

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RefreshSettlementTests
{
    private class FakeClock : IElapsedTimeSource
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
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        var result = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_ProviderUnavailable_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.ProviderUnavailable));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        var result = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.RecoveryRequired, result.Status);
    }

    [Fact]
    public void Advance_InvalidState_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.InvalidState));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        var result = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());

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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~VerificationSettlementTests|FullyQualifiedName~RefreshSettlementTests`
Expected: FAIL — none of the types exist yet.

- [ ] **Step 3: Write TargetMutationStatus and VerificationSettlement**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Per-identifier disposition tracked in-memory during Mutating. Design doc section 5.
/// Task 4 (PathMutationOperation) is this type's primary producer; VerificationSettlement (this
/// file) is its primary consumer. </summary>
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
/// already recorded failed during Mutating is not waited on.
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
        if (read.Snapshot!.DuplicateIdentifiers.Count > 0)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.DuplicateIdentifiers);

        var expected = targets.Where(t => mutationStatuses.TryGetValue(t.Identifier, out var status)
            && status is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);
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

- [ ] **Step 4: Write RefreshSettlement**

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
        if (duration >= SlowCallThreshold) diagnostics.RecordSlowCall(operationId, "RequestPostMutationRefresh", duration);

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
Expected: PASS (12 tests: 6 verification + 6 refresh).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/VerificationSettlement.cs PenumbraOrganizer.Plugin/Organizer/Operations/RefreshSettlement.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/VerificationSettlementTests.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RefreshSettlementTests.cs
git commit -m "feat: add verification and refresh settlement (bounded, non-blocking retry)"
```

---

### Task 4: PathMutationOperation — frame-budgeted execution with group-cascade

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/PathMutationOperation.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/PathMutationOperationTests.cs`

**Interfaces:**
- Consumes: `IPenumbraOperations`/`IElapsedTimeSource`/`IDiagnosticsSink` (Tasks 1–2), `TargetMutationStatus` (Task 3), `OperationPlan`/`OperationExecutionStep`/`OperationStepKind` (Plan A1), `OperationJournal`/`OperationStage`/`CheckpointPolicy` (Plan A1), `OperationStepResult`/`OperationStepDisposition`/`StepResultLog` (Plan A2).
- Produces:
  - `PathMutationOperation` sealed class, constructed with `(OperationPlan plan, IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, string bundleDirectory)`.
  - `IReadOnlyDictionary<string, TargetMutationStatus> MutationStatusByIdentifier { get; }` property — read by `VerificationSettlement`'s caller (Task 5/later plans) once mutation concludes.
  - `OperationJournal Advance(OperationJournal journal, TimeSpan budget, bool stopRequested)` — the frame-budgeted step loop. Returns an updated `OperationJournal` (new `ProcessedStepCount`/`LastCompletedIdentifier`/`Stage`/`UpdatedAt` as appropriate); the caller (Task 5) is responsible for persisting it via `OperationJournalCodec.Save` when the returned journal differs from the checkpoint already on disk — this method does not persist the journal itself, only the per-step result log (§5a's ordering requirement: result appended before the cursor advances, and that only matters for `StepResultLog`, which this method does own).

`PathMutationOperation` is transitional — a `Mutating`-stage-only engine. Task 5's `OperationController` owns the full stage sequence (`Preparing → Prepared → Mutating → Refreshing → Verifying → terminal`); this class's `Advance` is called only while the controller's copy of the stage is `Mutating`, and it signals "I'm done" by the returned journal's `Stage` no longer being `Mutating` (moved to whatever the caller should transition to next). For this task, `Advance` only ever returns `Stage = Mutating` (still working) or `Stage = Refreshing` (all steps processed or an operation-integrity stop occurred) — it never itself decides `FailedBeforeMutation`/`Cancelled`/etc.; those terminal decisions belong to `OperationController` once `Refreshing`/`Verifying` conclude (Task 5 and later plans).

- [ ] **Step 1: Write the failing tests**

```csharp
using Penumbra.Api.Enums;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class PathMutationOperationTests
{
    private class FakeClock : IElapsedTimeSource
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

    [Fact]
    public void Advance_TwoIndependentSuccessfulSteps_ProcessesBothInOneCallGivenAmpleBudget()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0), Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success);
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir.FullName);
            var journal = op.Advance(NewJournal(plan), TimeSpan.FromMilliseconds(4), stopRequested: false);

            Assert.Equal(2, journal.ProcessedStepCount);
            Assert.Equal(OperationStage.Refreshing, journal.Stage); // all steps done -> moves on
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-a"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-b"]);

            var results = StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir.FullName));
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(OperationStepDisposition.Succeeded, r.Disposition));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_AlwaysProcessesAtLeastOneStepEvenWithZeroBudget()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir.FullName);
            var journal = op.Advance(NewJournal(plan), TimeSpan.Zero, stopRequested: false);

            Assert.Equal(1, journal.ProcessedStepCount);
        }
        finally
        {
            dir.Delete(recursive: true);
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
        adapter.EnqueueSetModPathResult(PenumbraApiEc.PathRenameFailed); // X's temp hop fails
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success); // mod-c, group 1, unaffected

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir.FullName);
            var journal = op.Advance(NewJournal(plan), TimeSpan.FromMilliseconds(10), stopRequested: false);

            Assert.Equal(4, journal.ProcessedStepCount); // cascaded past the whole group 0 range, then processed group 1
            Assert.Equal(TargetMutationStatus.FinalStepFailed, op.MutationStatusByIdentifier["X"]);
            Assert.Equal(TargetMutationStatus.SkippedAfterEarlierFailure, op.MutationStatusByIdentifier["Y"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-c"]);

            var results = StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir.FullName));
            Assert.Equal(4, results.Count);
            Assert.Equal(OperationStepDisposition.Failed, results.Single(r => r.StepIndex == 0).Disposition);
            Assert.Equal(OperationStepDisposition.SkippedAfterEarlierFailure, results.Single(r => r.StepIndex == 1).Disposition);
            Assert.Equal(OperationStepDisposition.SkippedAfterEarlierFailure, results.Single(r => r.StepIndex == 2).Disposition);
            Assert.Equal(OperationStepDisposition.Succeeded, results.Single(r => r.StepIndex == 3).Disposition);

            // Only ONE SetModPath call was ever made for the cascaded group - steps 1 and 2 were
            // never attempted, proving the cascade skips rather than tries-then-discards.
            Assert.Equal(2, adapter.SetModPathCalls.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_NothingChanged_TreatedAsSuccessNotFailure()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PenumbraApiEc.NothingChanged);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir.FullName);
            op.Advance(NewJournal(plan), TimeSpan.FromMilliseconds(10), stopRequested: false);

            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-a"]);
            var result = Assert.Single(StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir.FullName)));
            Assert.Equal(OperationStepDisposition.Succeeded, result.Disposition);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_StopRequested_HaltsBeforeTheNextStepButFinishesTheCurrentOne()
    {
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success); // only one call expected

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir.FullName);
            var journal = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(10), stopRequested: true);

            Assert.Equal(1, journal.ProcessedStepCount); // step 0 finished; step 1 never started
            Assert.Single(adapter.SetModPathCalls);
        }
        finally
        {
            dir.Delete(recursive: true);
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
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success);
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir.FullName);
            var afterFirst = op.Advance(NewJournal(plan), TimeSpan.Zero, stopRequested: false); // budget forces exactly one step
            var afterSecond = op.Advance(afterFirst, TimeSpan.Zero, stopRequested: false);

            Assert.Equal(1, afterFirst.ProcessedStepCount);
            Assert.Equal(2, afterSecond.ProcessedStepCount);
            Assert.Equal(OperationStage.Refreshing, afterSecond.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
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

/// <summary>
/// Design doc section 5. Frame-budgeted, group-cascading mutation execution. Only ever drives one
/// stage: Mutating. The caller (OperationController, a later task) owns the surrounding stage
/// sequence and reads MutationStatusByIdentifier once this signals done (returned Stage is no
/// longer Mutating).
/// </summary>
public sealed class PathMutationOperation
{
    private readonly OperationPlan _plan;
    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly string _bundleDirectory;
    private readonly Dictionary<string, TargetMutationStatus> _mutationStatusByIdentifier;
    private readonly Dictionary<int, string> _stepIndexToGroupRangeEnd = new();

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
        _mutationStatusByIdentifier = plan.RecoveryTargets.ToDictionary(t => t.Identifier, _ => TargetMutationStatus.NotAttempted);
    }

    public IReadOnlyDictionary<string, TargetMutationStatus> MutationStatusByIdentifier => _mutationStatusByIdentifier;

    public OperationJournal Advance(OperationJournal journal, TimeSpan budget, bool stopRequested)
    {
        var start = _clock.GetTimestamp();
        var index = journal.ProcessedStepCount;
        var lastIdentifier = journal.LastCompletedIdentifier;

        while (index < _plan.ExecutionSteps.Count)
        {
            var isFirstIterationThisCall = index == journal.ProcessedStepCount;
            if (!isFirstIterationThisCall && (stopRequested || _clock.GetElapsedTime(start) >= budget))
                break;

            var step = _plan.ExecutionSteps[index];
            var callStart = _clock.GetTimestamp();
            Penumbra.Api.Enums.PenumbraApiEc ipcResult;
            try
            {
                ipcResult = _adapter.SetModPath(step.Identifier, step.TargetRawPath);
            }
            catch (Exception)
            {
                // Unexpected exception, IPC boundary still usable (design doc section 5's table) -
                // treated as an item failure for this step, same as an explicit non-Success result.
                ipcResult = Penumbra.Api.Enums.PenumbraApiEc.UnknownError;
            }

            var callDuration = _clock.GetElapsedTime(callStart);
            if (callDuration >= SlowCallThreshold)
                _diagnostics.RecordSlowCall(journal.OperationId, step.Identifier, callDuration);

            var succeeded = ipcResult is Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged;

            if (succeeded)
            {
                AppendResult(step, OperationStepDisposition.Succeeded, ipcResult.ToString(), null, callDuration);
                if (step.Kind == OperationStepKind.FinalMove)
                    _mutationStatusByIdentifier[step.Identifier] = TargetMutationStatus.FinalStepSucceeded;
                lastIdentifier = step.Identifier;
                index++;
            }
            else
            {
                AppendResult(step, OperationStepDisposition.Failed, ipcResult.ToString(), ipcResult.ToString(), callDuration);
                _mutationStatusByIdentifier[step.Identifier] = TargetMutationStatus.FinalStepFailed;

                // Group-cascade: skip every remaining step in this contiguous GroupId range.
                var groupId = step.GroupId;
                var cascadeIndex = index + 1;
                while (cascadeIndex < _plan.ExecutionSteps.Count && _plan.ExecutionSteps[cascadeIndex].GroupId == groupId)
                {
                    var cascadeStep = _plan.ExecutionSteps[cascadeIndex];
                    AppendResult(cascadeStep, OperationStepDisposition.SkippedAfterEarlierFailure, null, null, null);
                    if (cascadeStep.Identifier != step.Identifier)
                        _mutationStatusByIdentifier[cascadeStep.Identifier] = TargetMutationStatus.SkippedAfterEarlierFailure;
                    lastIdentifier = cascadeStep.Identifier;
                    cascadeIndex++;
                }

                index = cascadeIndex;
            }

            journal = journal with { ProcessedStepCount = index, LastCompletedIdentifier = lastIdentifier, UpdatedAt = DateTimeOffset.UtcNow };
        }

        if (index >= _plan.ExecutionSteps.Count)
            journal = journal with { Stage = OperationStage.Refreshing, UpdatedAt = DateTimeOffset.UtcNow };

        return journal;
    }

    private void AppendResult(
        OperationExecutionStep step, OperationStepDisposition disposition,
        string? ipcResultName, string? failureDetail, TimeSpan? duration)
    {
        StepResultLog.Append(OperationBundlePaths.ResultsPath(_bundleDirectory), new OperationStepResult(
            step.StepIndex, step.Identifier, disposition, ipcResultName, failureDetail,
            DateTimeOffset.UtcNow, duration is null ? null : (long)duration.Value.TotalMilliseconds));
    }
}
```

Note: this method does not track or persist checkpoint cadence itself — it only appends per-step results via `StepResultLog.Append` (the §5a ordering requirement) and returns the updated in-memory journal. Checkpoint-cadence integration (`CheckpointPolicy.IsDue`, deciding *when* to call `OperationJournalCodec.Save`) is Task 5's responsibility, once `OperationController` owns persistence timing across multiple `Advance` calls.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PathMutationOperationTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/PathMutationOperation.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/PathMutationOperationTests.cs
git commit -m "feat: add PathMutationOperation with frame-budgeted group-cascade execution"
```

---

### Task 5: OperationController skeleton

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs`

**Interfaces:**
- Consumes: `PathMutationOperation`/`RefreshSettlement`/`VerificationSettlement` (Tasks 3–4), `IPenumbraOperations`/`IElapsedTimeSource`/`IDiagnosticsSink` (Tasks 1–2), `OperationPlan`/`OperationJournal`/`OperationJournalCodec`/`CheckpointPolicy` (Plan A1), `OperationBundlePaths` (Plan A2).
- Produces:
  - `OperationStateSnapshot` record (design §7b, transcribed in full — see Step 3).
  - `OperationController` sealed class: constructed with `(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, TimeSpan frameBudget)`.
    - `OperationStateSnapshot State { get; }` — published atomically, replaced (not mutated) after every meaningful transition.
    - `void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory)` — throws `InvalidOperationException` if an operation is already active (non-reentrancy). Persists the initial journal (`Stage = Prepared`, then immediately `Mutating` per design §3's ordering — this task treats "Preparing"/plan-and-snapshot construction as the caller's job, since that requires `Plugin.cs`/`OrganizerState` data this plan doesn't have; `StartApply` receives an already-validated, already-persisted `OperationPlan` and only owns the journal from `Prepared` onward).
    - `void RequestCancellation()` — sets an internal stop flag consumed by the next `Update()`.
    - `void Update()` — advances the active operation by one frame-budget's worth of work through `Mutating → Refreshing → Verifying → terminal`, persisting the journal via `OperationJournalCodec.Save` whenever `CheckpointPolicy.IsDue` says so (or on every stage transition, which always checkpoints regardless of the policy — matches design §4's "forced write immediately on entering each stage"). Never throws — wraps the whole per-call body in a boundary that fails the operation safely (`Stage = FailedPartiallyApplied` or `FailedBeforeMutation` depending on whether `ProcessedStepCount > 0`) rather than letting an exception escape.

This task deliberately stops short of `RecoveryRequired` transitions reaching an actual resolution (Continue/Restore/Keep Current don't exist yet — Plan D) and stops short of `Cancelled` needing verification-trust precedence (§5a's rule — also needs `RecoveryRequired` plumbing that isn't wired end-to-end until Plan D). For this task: if `VerificationSettlement`/`RefreshSettlement` ever return `RecoveryRequired`, the controller sets `Stage` to whatever non-terminal stage it was in and stops calling `Advance` on it (the operation becomes inert — a later plan's recovery detection is what would notice this on next startup); it does not fabricate a `RecoveryRequired` `OperationStage` value, since design's enum has no such member (`RecoveryRequired` is a controller-level concept per design §2/§4, tracked by the *absence* of further `Update()` progress plus a non-terminal `Stage`, not a persisted enum value).

- [ ] **Step 1: Write the failing tests**

```csharp
using Penumbra.Api.Enums;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationControllerTests
{
    private class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static OperationPlan SinglePlan(string id = "mod-a") =>
        OperationPlan.Create(
            OperationType.Apply,
            [new(0, id, "Weapons/A", OperationStepKind.FinalMove, 0)],
            [new(id, "Gear/A", "Weapons/A", id)]);

    [Fact]
    public void State_Initially_CanStartApplyIsTrue()
    {
        var controller = new OperationController(new FakePenumbraOperations(), new FakeClock(), new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4));

        Assert.True(controller.State.CanStartApply);
        Assert.Null(controller.State.Stage);
    }

    [Fact]
    public void StartApply_TwiceInARow_ThrowsOnTheSecondCall()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = new OperationController(new FakePenumbraOperations(), new FakeClock(), new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4));
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            Assert.Throws<InvalidOperationException>(() => controller.StartApply(SinglePlan("mod-b"), Guid.NewGuid(), dir.FullName));
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
            var controller = new OperationController(new FakePenumbraOperations(), new FakeClock(), new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4));
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
    public void Update_DrivesMutationThroughRefreshingToVerifyingAndSettles()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PenumbraApiEc.Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(
            LiveModReadStatus.Success,
            LiveModSnapshotBuilder.Build([new PenumbraOrganizer.Plugin.Organizer.LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
        var clock = new FakeClock();

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = new OperationController(adapter, clock, new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4));
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Completed

            Assert.Equal(OperationStage.Completed, controller.State.Stage);
            Assert.True(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_WithNoActiveOperation_DoesNothingAndDoesNotThrow()
    {
        var controller = new OperationController(new FakePenumbraOperations(), new FakeClock(), new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4));

        var exception = Record.Exception(controller.Update);

        Assert.Null(exception);
    }

    [Fact]
    public void Update_UnexpectedExceptionFromAdapter_FailsTheOperationSafelyRatherThanThrowing()
    {
        var adapter = new FakePenumbraOperations(); // SetModPath called with no queued result -> throws internally
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = new OperationController(adapter, new FakeClock(), new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4));
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            var exception = Record.Exception(controller.Update);

            Assert.Null(exception);
            Assert.True(controller.State.CanStartApply); // operation concluded (failed), slot freed for a new one
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

/// <summary> Design doc section 7b. The only thing MainWindow (a later plan) is allowed to read.
/// Published as a whole new instance after every meaningful transition, never mutated in place. </summary>
public sealed record OperationStateSnapshot(
    OperationStage? Stage,
    OperationType? Kind,
    int ProcessedSteps,
    int TotalSteps,
    int CompletedTargets,
    int TotalTargets,
    string? CurrentIdentifier,
    string? CurrentDisplayName,
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
        Stage: null, Kind: null, ProcessedSteps: 0, TotalSteps: 0, CompletedTargets: 0, TotalTargets: 0,
        CurrentIdentifier: null, CurrentDisplayName: null, LastError: null,
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
/// Design doc sections 2, 7, 7a. Owns the operation state machine from Prepared onward (Preparing
/// / plan construction is the caller's job - it needs OrganizerState data this layer doesn't have).
/// Recovery-classification servicing (design section 7's _pendingRecoveryClassification) is added
/// in a later plan once RecoveryAssessment exists; this class only drives a single active
/// Apply/Restore operation.
/// </summary>
public sealed class OperationController
{
    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly TimeSpan _frameBudget;

    private PathMutationOperation? _mutation;
    private RefreshSettlement? _refresh;
    private VerificationSettlement? _verification;
    private OperationJournal? _journal;
    private OperationPlan? _plan;
    private string? _bundleDirectory;
    private bool _stopRequested;
    private int _itemsSinceLastCheckpoint;
    private long _lastCheckpointTimestamp;

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
        if (_journal is not null)
            throw new InvalidOperationException("Another organizer operation is already in progress.");

        var journal = new OperationJournal(
            SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: plan.OperationId, Type: plan.Type,
            Stage: OperationStage.Mutating, Resolution: OperationResolution.None, SuccessorOperationId: null,
            CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: plan.ExecutionSteps.Count,
            ProcessedStepCount: 0, LastCompletedIdentifier: null, SnapshotId: snapshotId, PlanId: plan.OperationId,
            TargetHash: plan.IntegrityHash, RecoveryOfOperationId: null, UpdatedAt: DateTimeOffset.UtcNow);

        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirectory), journal);

        _plan = plan;
        _journal = journal;
        _bundleDirectory = bundleDirectory;
        _mutation = new PathMutationOperation(plan, _adapter, _clock, _diagnostics, bundleDirectory);
        _refresh = null;
        _verification = null;
        _stopRequested = false;
        _itemsSinceLastCheckpoint = 0;
        _lastCheckpointTimestamp = _clock.GetTimestamp();

        PublishState();
    }

    public void RequestCancellation() => _stopRequested = true;

    public void Update()
    {
        if (_journal is null) return;

        try
        {
            AdvanceActiveOperation();
        }
        catch (Exception)
        {
            var failedJournal = _journal with
            {
                Stage = _journal.ProcessedStepCount > 0 ? OperationStage.FailedPartiallyApplied : OperationStage.FailedBeforeMutation,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            Checkpoint(failedJournal, force: true);
            ClearActiveOperation();
        }

        PublishState();
    }

    private void AdvanceActiveOperation()
    {
        var journal = _journal!;

        if (journal.Stage == OperationStage.Mutating)
        {
            var before = journal.ProcessedStepCount;
            journal = _mutation!.Advance(journal, _frameBudget, _stopRequested);
            _itemsSinceLastCheckpoint += journal.ProcessedStepCount - before;
            Checkpoint(journal, force: journal.Stage != OperationStage.Mutating);
            _journal = journal;
            return;
        }

        if (journal.Stage == OperationStage.Refreshing)
        {
            _refresh ??= new RefreshSettlement();
            var result = _refresh.Advance(_adapter, _clock, _diagnostics, journal.OperationId);
            if (result.Status == RefreshSettlementStatus.Settled)
            {
                journal = journal with { Stage = OperationStage.Verifying, UpdatedAt = DateTimeOffset.UtcNow };
                Checkpoint(journal, force: true);
            }
            else if (result.Status == RefreshSettlementStatus.RecoveryRequired)
            {
                // Left non-terminal (still Refreshing) - a later plan's startup recovery detection
                // is what notices this. The active-operation slot still clears so a stuck refresh
                // doesn't wedge the whole controller for the rest of the session.
                ClearActiveOperation();
            }
            _journal = journal;
            return;
        }

        if (journal.Stage == OperationStage.Verifying)
        {
            _verification ??= new VerificationSettlement();
            var result = _verification.Advance(
                _adapter, _clock, _plan!.RecoveryTargets, _mutation!.MutationStatusByIdentifier, _diagnostics, journal.OperationId);

            if (result.Status == VerificationStatus.Settled)
            {
                var hasFailures = _mutation.MutationStatusByIdentifier.Values.Any(s =>
                    s is TargetMutationStatus.FinalStepFailed or TargetMutationStatus.SkippedAfterEarlierFailure);
                journal = journal with
                {
                    Stage = hasFailures ? OperationStage.CompletedWithItemFailures : OperationStage.Completed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                Checkpoint(journal, force: true);
                _journal = journal;
                ClearActiveOperation();
            }
            else if (result.Status == VerificationStatus.TimedOut)
            {
                journal = journal with { Stage = OperationStage.CompletedWithItemFailures, UpdatedAt = DateTimeOffset.UtcNow };
                Checkpoint(journal, force: true);
                _journal = journal;
                ClearActiveOperation();
            }
            else if (result.Status == VerificationStatus.RecoveryRequired)
            {
                _journal = journal; // left non-terminal (still Verifying)
                ClearActiveOperation();
            }
            else
            {
                _journal = journal; // Waiting - try again next Update()
            }
        }
    }

    private void Checkpoint(OperationJournal journal, bool force)
    {
        var elapsed = _clock.GetElapsedTime(_lastCheckpointTimestamp);
        if (!force && !CheckpointPolicy.IsDue(_itemsSinceLastCheckpoint, elapsed))
            return;

        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(_bundleDirectory!), journal);
        _itemsSinceLastCheckpoint = 0;
        _lastCheckpointTimestamp = _clock.GetTimestamp();
    }

    private void ClearActiveOperation()
    {
        _journal = null;
        _plan = null;
        _mutation = null;
        _refresh = null;
        _verification = null;
        _bundleDirectory = null;
        _stopRequested = false;
    }

    private void PublishState()
    {
        if (_journal is null)
        {
            State = OperationStateSnapshot.Idle;
            return;
        }

        var totalTargets = _plan!.RecoveryTargets.Count;
        var completedTargets = _mutation is null
            ? 0
            : _mutation.MutationStatusByIdentifier.Values.Count(s => s is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);

        State = new OperationStateSnapshot(
            Stage: _journal.Stage, Kind: _journal.Type,
            ProcessedSteps: _journal.ProcessedStepCount, TotalSteps: _journal.TotalSteps,
            CompletedTargets: completedTargets, TotalTargets: totalTargets,
            CurrentIdentifier: _journal.LastCompletedIdentifier, CurrentDisplayName: _journal.LastCompletedIdentifier,
            LastError: null,
            RequiresRecovery: false, RecoveryClassificationPending: false,
            CanStartApply: false, CanStartRestore: false, CanScan: false, CanIndex: false,
            CanRunFolderCleanup: false, CanRunFolderCleanupRollback: false, CanCreateBackup: false,
            CanResolveRecovery: false, CanRequestCancellation: _journal.Stage == OperationStage.Mutating);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationControllerTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: add OperationController driving Mutating through Verifying for Apply"
```

---

### Task 6: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test plus everything added in Tasks 1–5, zero failures.

- [ ] **Step 2: Confirm the working tree is clean and no stray temp dirs leaked**

Run: `git status --short`
Expected: no output (all tests write to `Directory.CreateTempSubdirectory()` outside the repo and delete in `finally`).

---

## What this plan does not cover

Deferred to **Plan B2** (Dalamud wiring, verified by code review + manual in-game testing, not xUnit — this repo has no Dalamud test-double infrastructure):

- `PenumbraOperationsAdapter` — the real `IPenumbraOperations` implementation wrapping `GetModListAdapterIpc`/`SetModPathIpc`/refresh IPC.
- Subscribing `OperationController.Update` to `Framework.Update`, constructed once in `Plugin.cs`.
- Replacing `Plugin.cs`'s `ApplyChanges`/`ExecuteOrderedMoves` to build an `OperationPlan` from `OrganizerState` and call `StartApply` instead of looping `SetModPathIpc.Invoke` directly.
- Cancellation UI wiring (`RequestCancellation` has no caller yet).

Deferred to **Plan C** (design §13): the same execution engine configured for Restore, validating whether `PathMutationOperation` genuinely needs no Restore-specific branching.

Deferred to **Plan D** (design §13): `RecoveryAssessment`, startup deferred classification (`_pendingRecoveryClassification`), the three recovery resolutions, multi-journal discovery wired into controller startup, `RecoveryDialogSnapshot` population.

Deferred to **Plan E** (design §13): `MainWindow` UI wiring, the recovery dialog, diagnostics dump changes.

Also out of scope for this plan specifically:
- `Preparing`/`Prepared` stages and the plan/snapshot construction pipeline that precedes `StartApply` (design §3's steps 1–6) — this plan's `StartApply` receives an already-built, already-persisted `OperationPlan`; building that plan from `OrganizerState.Mods` is `Plugin.cs`-specific work for Plan B2.
- The `Cancelled`/`AtBoth` no-op-move interaction and the cancellation-vs-verification-trust precedence rule (design §5a) in full — `RequestCancellation` exists and `PathMutationOperation.Advance` honors `stopRequested`, but the controller's cancellation-specific terminal `Stage = Cancelled` transition (as opposed to just falling through to whatever `Verifying` naturally concludes) is not separately implemented in this plan; today a mid-Mutating stop simply lets the remaining steps go unprocessed and proceeds through `Refreshing`/`Verifying` normally, landing on `Completed`/`CompletedWithItemFailures` rather than a dedicated `Cancelled` outcome. This is a real gap versus the design doc, flagged here rather than silently shipped — closing it needs the `CancellationRequested` field (already on `OperationJournal`, unused by this plan) to actually influence `AdvanceActiveOperation`'s terminal-stage decision, which is straightforward but was left out of this task list's scope to keep Task 5 reviewable; add it as the first task of Plan B2 or a small follow-up.
