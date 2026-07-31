# Framework-Thread Materialize Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move every Penumbra IPC read out of the ImGui draw callback and into the framework-update callback, enforce that placement with an executable guard, and leave a diagnostic trail in the log.

**Architecture:** `LibraryWorkCoordinator.Start` stops materializing. It takes ownership of the job and closes the admission gates on the click frame; the first `Update()` afterwards captures the epoch, materializes, and launches the background worker. `Update()` asserts it is on the framework thread before materializing, via an injected `Func<bool>` so the coordinator stays free of Dalamud references.

**Tech Stack:** C# / .NET 10, Dalamud plugin (API level 15), Penumbra.Api 5.15.1, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-31-framework-thread-materialize-design.md`. Read it before Task 1; it carries the reasoning behind the epoch decision, which is the one part of this change that is easy to get subtly wrong.

## Global Constraints

- **The epoch is captured immediately BEFORE `Materialize()`, never after.** Capturing after would make a change that occurred *during* materialization part of the new baseline, letting a snapshot that spans two Penumbra states publish as valid. That interval is the whole point of this work. This was an explicit review decision, not an accident.
- **A wrong-thread `Update()` throws.** It does not settle `Failed`. It is a programming error in plugin code, not a runtime condition a user can cause or recover from.
- `LibraryWorkCoordinator` must not reference Dalamud or Penumbra types. This is enforced by `LibraryWorkPurityTests.PureTypesAndCrossThreadDtos_DoNotReferenceDalamudOrPenumbra`. Inject a `Func<bool>`, never an `IFramework`.
- Comment density and style must match the surrounding file. `LibraryWorkCoordinator.cs` uses full-sentence comments that explain *why*, only where the reason is non-obvious. Do not annotate the obvious.
- No em dashes in user-facing strings, docs, or release notes. Use commas or restructure.
- Baseline before this plan: **886 tests passing**. Report the count after each task.

---

### Task 1: Defer materialization to the framework-update callback

**Files:**
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs` (`Start`, `Update`, new pending fields, class doc)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`

**Interfaces:**
- Consumes: `ILibraryWorkJob<TSeed,TResult>.Materialize()`, `LibraryWorkBatch<TSeed,TResult>`, `LibraryWorkPhase`, `LibraryWorkOutcome`, `LibraryWorkStateSnapshot`.
- Produces: `Start` no longer materializes. Task 2 adds a guard inside the new materialize block; Task 3 adds logging around it.

**Background the implementer needs:**

`Start` is called from the ImGui draw callback (the button handler in `MainWindow`). `Update()` is called once per frame from `Plugin.OnFrameworkUpdate`. Today `Start` calls `job.Materialize()` synchronously, so all Penumbra IPC happens in the draw callback. This task moves that call into `Update()`.

`Phase` must still become `Materializing` inside `Start`, on the click frame. Every admission gate in the plugin keys off `Phase != Idle`. If the phase change waited for the next `Update()`, a second click in the intervening window would be admitted and would throw or start a second run.

`Plugin.OnFrameworkUpdate` already wraps `ScanWork.Update()` and `IndexWork.Update()` in try/catch with `AbandonRun`, so an exception escaping `Update()` is already handled at the call site. Do not add a second layer.

- [ ] **Step 1: Write the failing tests**

Add to `LibraryWorkCoordinatorTests.cs`, after the last `[Fact]`:

```csharp
    [Fact]
    public void Start_DoesNotMaterialize_UntilUpdate()
    {
        var processor = new FakeProcessor();
        var job = new FakeJob { Items = ["a", "b"], Processor = processor };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);
        Assert.Equal(0, job.MaterializeCalls);

        coordinator.Update();
        Assert.Equal(1, job.MaterializeCalls);
    }

    [Fact]
    public void Start_ClosesTheGateImmediately_BeforeAnyUpdate()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);

        Assert.Equal(LibraryWorkPhase.Materializing, coordinator.State.Phase);
        Assert.True(coordinator.State.IsRunning);
        Assert.Throws<InvalidOperationException>(() => coordinator.Start(job));
    }

    [Fact]
    public void Update_Twice_MaterializesOnlyOnce()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        coordinator.Update();

        Assert.Equal(1, job.MaterializeCalls);
    }

    [Fact]
    public void CancelBeforeUpdate_DoesNotMaterialize_AndSettlesCancelled()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.RequestCancellation();
        coordinator.Update();

        Assert.Equal(0, job.MaterializeCalls);
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Cancelled, coordinator.State.LastOutcome);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void DisposeBeforeUpdate_DoesNotMaterialize()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Dispose();
        coordinator.Update();

        Assert.Equal(0, job.MaterializeCalls);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void MaterializeFailure_ClearsPendingJob_AndStartIsAllowedAgain()
    {
        var boom = new InvalidOperationException("Penumbra is unavailable.");
        var failing = new FakeJob { Items = [], Processor = new FakeProcessor(), MaterializeThrows = boom };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(failing);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("Penumbra is unavailable.", coordinator.State.LastError);

        // A second Update must not retry the failed job.
        coordinator.Update();
        Assert.Equal(1, failing.MaterializeCalls);

        var good = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        coordinator.Start(good);
        coordinator.Update();
        Assert.Equal(1, good.MaterializeCalls);
    }

    [Fact]
    public void Epoch_ChangedBetweenStartAndUpdate_DoesNotInvalidateTheRun()
    {
        var epoch = 10L;
        var scheduler = new ManualScheduler();
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(() => epoch, scheduler.Schedule);

        coordinator.Start(job);
        epoch = 11L; // the snapshot has not been taken yet, so it will represent epoch 11
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Completed, coordinator.State.LastOutcome);
        Assert.Single(job.Published);
    }

    [Fact]
    public void Epoch_ChangedDuringMaterialize_InvalidatesTheRun()
    {
        var epoch = 10L;
        var scheduler = new ManualScheduler();
        var job = new FakeJob
        {
            Items = ["a"],
            Processor = new FakeProcessor(),
            // Penumbra mutating while the snapshot is being taken: the snapshot spans two states.
            DuringMaterialize = () => epoch = 11L,
        };
        var coordinator = new LibraryWorkCoordinator<string, string>(() => epoch, scheduler.Schedule);

        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.StaleModList, coordinator.State.LastOutcome);
        Assert.Empty(job.Published);
        Assert.Equal(0, scheduler.ScheduleCalls);
    }
```

Extend `FakeJob` to record calls and to allow mutation during materialize. Replace the existing `Materialize` method and add the two members:

```csharp
        public Action? DuringMaterialize { get; init; }
        public int MaterializeCalls { get; private set; }

        public LibraryWorkBatch<string, string> Materialize()
        {
            MaterializeCalls++;
            DuringMaterialize?.Invoke();
            if (MaterializeThrows is not null)
                throw MaterializeThrows;
            return new LibraryWorkBatch<string, string>(Items, Processor);
        }
```

`ManualScheduler` needs a `ScheduleCalls` counter for the last test. Add to that class:

```csharp
        public int ScheduleCalls { get; private set; }
```

and increment it as the first statement of its `Schedule` method.

- [ ] **Step 2: Run the new tests and record the actual failures**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "LibraryWorkCoordinatorTests"
```

Expected: the new tests fail. `Start_DoesNotMaterialize_UntilUpdate` fails asserting `0 == 1` because `Start` still materializes. Write down the real output; do not assume it matches this prediction.

Several **existing** tests will also fail now, because they call `Start` and immediately assert on materialized state. That is expected and is handled in Step 4.

- [ ] **Step 3: Implement the deferral**

In `LibraryWorkCoordinator.cs`, add a pending field beside the existing ones:

```csharp
    private ILibraryWorkJob<TSeed, TResult>? _pendingJob;
```

Replace the body of `Start` (currently lines 49-105) with:

```csharp
    /// <summary>
    /// Called from the UI thread. Takes ownership of the job and closes every admission gate that
    /// keys off Phase, but does no Penumbra work: materialization is deferred to the next Update()
    /// so that all IPC reads happen on the framework thread.
    /// </summary>
    public void Start(ILibraryWorkJob<TSeed, TResult> job)
    {
        // Without this, anything calling RunScan during teardown schedules fresh background work
        // into a plugin that is going away.
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State.IsRunning)
            throw new InvalidOperationException($"{State.JobDisplayName} is already running.");

        _job = job;
        _pendingJob = job;
        _processed = 0;
        _total = 0;
        _cts = new CancellationTokenSource();

        // Materializing is published here, on the click frame, rather than when materialization
        // actually happens. The gates key off Phase, so deferring this would leave a window in
        // which a second click is admitted.
        PublishRunning(LibraryWorkPhase.Materializing);
    }

    /// <summary>
    /// Framework thread. Captures the epoch, takes the Penumbra snapshot, and launches the worker.
    /// Returns true if the caller should stop processing this Update (the run settled or started).
    /// </summary>
    private bool TryMaterializePending()
    {
        var job = _pendingJob!;
        _pendingJob = null;

        // Cancellation and disposal are both checked before any Penumbra call: a run cancelled
        // while pending must not touch Penumbra at all.
        if (_cts?.IsCancellationRequested == true)
        {
            Settle(LibraryWorkOutcome.Cancelled, null);
            return true;
        }

        // Captured BEFORE the snapshot, never after. Capturing after would fold a change that
        // happened during materialization into the new baseline, letting a snapshot that spans two
        // Penumbra states publish as valid. That interval is exactly the one this design exists to
        // catch, so it is the last one to stop watching.
        _startEpoch = _readEpoch();

        LibraryWorkBatch<TSeed, TResult> batch;
        var materializeStarted = Stopwatch.GetTimestamp();
        try
        {
            batch = job.Materialize();
        }
        catch (Exception ex)
        {
            Settle(LibraryWorkOutcome.Failed, ex.Message);
            return true;
        }

        // Materialization holds the framework thread for its whole duration, so it is measured
        // rather than assumed. 100ms is roughly six frames at 60fps: long enough not to fire on a
        // healthy library, short enough to catch a hitch a user would notice. A starting value to
        // revise once real numbers exist, not a claim about what is achievable.
        var materializeElapsed = Stopwatch.GetElapsedTime(materializeStarted);
        if (materializeElapsed > MaterializeWarningThreshold)
            _logWarning?.Invoke(
                $"{job.DisplayName}: materializing {batch.Items.Count} mods held the framework "
                + $"thread for {materializeElapsed.TotalMilliseconds:F0}ms.");

        // Penumbra mutated while the snapshot was being taken, so the snapshot may describe two
        // different states. Settling now turns a doomed multi-second scan into an immediate,
        // accurate message; waiting for the worker would reach the same verdict much later.
        if (_readEpoch() != _startEpoch)
        {
            Settle(LibraryWorkOutcome.StaleModList, null);
            return true;
        }

        _total = batch.Items.Count;
        var ct = _cts!.Token;
        PublishRunning(LibraryWorkPhase.Computing);

        // A scheduler that throws synchronously (or hands back null) would otherwise leave
        // Phase == Computing with _task == null - a state Update() can never settle, permanently
        // gating Scan, Index, Apply, Restore, cleanup and backup with no recovery short of
        // reloading the plugin. Unreachable with Task.Run; the scheduler is an injectable boundary.
        try
        {
            _task = _scheduler(() => RunBatch(batch, ct), ct)
                ?? throw new InvalidOperationException("The background scheduler returned no task.");
        }
        catch (Exception ex)
        {
            Settle(LibraryWorkOutcome.Failed, ex.Message);
        }

        return true;
    }
```

Then insert the pending check at the top of `Update()`, immediately after the `_disposed` guard:

```csharp
    public void Update()
    {
        if (_disposed)
            return;

        if (_pendingJob is not null && TryMaterializePending())
            return;

        if (_task is not { IsCompleted: true })
        ...
```

Finally, add `_pendingJob = null;` to both `Settle` and `AbandonRun` (beside the existing `_job = null;`), and to `Dispose` (beside `_job = null;`). Without this, a pending job survives a terminal outcome and materializes on a later frame.

Update the class doc's first line, which already claims the correct behaviour and will now be true:

```csharp
/// Runs a library job in three phases: Materialize on the framework thread, the whole of Process on
```

Leave that line as-is; it needs no edit. Remove nothing else from the doc.

- [ ] **Step 4: Update the existing tests that assumed synchronous materialization**

Run the suite and find every failure:

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "LibraryWorkCoordinatorTests"
```

For each failing pre-existing test, insert a `coordinator.Update();` immediately after the `coordinator.Start(job);` line. Do not change any assertion, and do not delete any test.

Two need more than that:

`Start_MovesToComputing_WithoutRunningTheProcessor` is now misnamed: `Start` alone moves to `Materializing`. Rename it and add the intermediate assertion, so it pins both halves rather than losing the first:

```csharp
    [Fact]
    public void StartThenUpdate_MovesToComputing_WithoutRunningTheProcessor()
    {
        var processor = new FakeProcessor();
        var job = new FakeJob { Items = ["a", "b"], Processor = processor };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);
        Assert.Equal(LibraryWorkPhase.Materializing, coordinator.State.Phase);

        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
        Assert.Equal(2, coordinator.State.TotalItems);
        Assert.Equal(0, coordinator.State.ProcessedItems);
        Assert.True(coordinator.State.CanCancel);
        Assert.Equal(0, processor.PrepareCalls);
        Assert.Empty(job.Published);
    }
```

`MaterializeThrowing_FailsBeforeAnyBackgroundWork` must keep meaning what its name says. Add the `Update()` call and additionally assert no work was scheduled:

```csharp
        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal(0, scheduler.ScheduleCalls);
```

- [ ] **Step 5: Verify the updated tests still catch what they were written for**

For each pre-existing test you modified in Step 4, confirm it is still a tripwire rather than merely passing. Temporarily revert one production change at a time and check the expected test goes red:

1. Comment out the `_pendingJob = null;` line you added to `Settle`. Expect `MaterializeFailure_ClearsPendingJob_AndStartIsAllowedAgain` to fail. Restore it.
2. Change the epoch capture in `TryMaterializePending` from before `job.Materialize()` to after it. Expect `Epoch_ChangedDuringMaterialize_InvalidatesTheRun` to fail. **Restore it.** This is the constraint most likely to be "simplified" later; confirm the test defends it.

Record both results in your report. If either mutation does not produce the expected failure, the test is not pinning what it claims and must be fixed before proceeding.

- [ ] **Step 6: Run the full suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: all pass. Baseline was 886; this task adds 8 and renames 1, so expect 894.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "fix: materialize library work on the framework thread, not in the draw callback"
```

---

### Task 2: Enforce the framework thread with an executable guard

**Files:**
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs` (constructor, `TryMaterializePending`)
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:84-87` (both coordinator constructions)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`

**Interfaces:**
- Consumes: `LibraryWorkCoordinator.TryMaterializePending` from Task 1.
- Produces: constructor signature `LibraryWorkCoordinator(Func<long> readEpoch, Func<bool> isFrameworkThread, BackgroundScheduler? scheduler = null, Action<string>? logWarning = null, TimeSpan? disposeWait = null)`.

**Background the implementer needs:**

Task 1 proves materialization is *deferred*. It does not prove it runs on the right *thread*: a future caller could invoke `Update()` from anywhere and every test would still pass. The code would be relying on a convention that has already failed once, which is how this bug happened.

`Dalamud.Plugin.Services.IFramework.IsInFrameworkUpdateThread` is a `bool` property and exists on the API level this plugin targets (verified against `Dalamud.dll`). The coordinator must not reference `IFramework` directly, because `LibraryWorkPurityTests` forbids Dalamud types in this namespace. Inject a `Func<bool>` instead, matching the existing `Func<long> readEpoch` style.

`Func<bool> isFrameworkThread` is a **required** positional parameter, not optional. An optional one defaulting to "always true" would let a future construction site silently opt out of the guard, which is exactly the failure mode being fixed.

- [ ] **Step 1: Write the failing test**

Add to `LibraryWorkCoordinatorTests.cs`:

```csharp
    [Fact]
    public void Update_OffTheFrameworkThread_ThrowsWithoutMaterializing()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => false, scheduler.Schedule);

        coordinator.Start(job);

        var ex = Assert.Throws<InvalidOperationException>(() => coordinator.Update());
        Assert.Contains("framework thread", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, job.MaterializeCalls);
    }
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "Update_OffTheFrameworkThread_ThrowsWithoutMaterializing"
```

Expected: a compile error, because the constructor has no `isFrameworkThread` parameter yet. A compile failure is a legitimate red for this test; do not work around it by adding the parameter before writing the test.

- [ ] **Step 3: Add the constructor parameter and the guard**

In `LibraryWorkCoordinator.cs`, add the field beside `_readEpoch`:

```csharp
    private readonly Func<bool> _isFrameworkThread;
```

Change the constructor signature and add the assignment:

```csharp
    public LibraryWorkCoordinator(
        Func<long> readEpoch,
        Func<bool> isFrameworkThread,
        BackgroundScheduler? scheduler = null,
        Action<string>? logWarning = null,
        TimeSpan? disposeWait = null)
    {
        _readEpoch = readEpoch;
        _isFrameworkThread = isFrameworkThread;
        _scheduler = scheduler ?? ((work, ct) => Task.Run(work, ct));
        _logWarning = logWarning;
        _disposeWait = disposeWait ?? TimeSpan.FromSeconds(2);
    }
```

Add the assertion as the first statement of `TryMaterializePending`, before `_pendingJob` is cleared, so a wrong-thread call leaves the run pending rather than silently dropping it:

```csharp
    private bool TryMaterializePending()
    {
        // Throws rather than settling Failed: calling this off the framework thread is a
        // programming error in plugin code, not a runtime condition a user can cause or recover
        // from. The whole point of this class is that Penumbra is read from one specific thread,
        // and a convention alone already failed to hold once.
        if (!_isFrameworkThread())
            throw new InvalidOperationException(
                "Library job materialization must run on the framework thread.");

        var job = _pendingJob!;
        _pendingJob = null;
        ...
```

- [ ] **Step 4: Update every test construction site**

In `LibraryWorkCoordinatorTests.cs`, update the `NewCoordinator` helper:

```csharp
        var coordinator = new LibraryWorkCoordinator<string, string>(
            readEpoch, isFrameworkThread: () => true, scheduler.Schedule);
```

Then find every remaining direct construction and add `isFrameworkThread: () => true` as the second argument:

```bash
grep -n "new LibraryWorkCoordinator" PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
```

Every site except `Update_OffTheFrameworkThread_ThrowsWithoutMaterializing` gets `() => true`. Also update the two tests you added in Task 1 that construct directly (`Epoch_ChangedBetweenStartAndUpdate_DoesNotInvalidateTheRun` and `Epoch_ChangedDuringMaterialize_InvalidatesTheRun`).

- [ ] **Step 5: Wire production to Dalamud**

In `PenumbraOrganizer.Plugin/Plugin.cs`, change both coordinator constructions (currently lines 84-87):

```csharp
        ScanWork = new LibraryWorkCoordinator<LibraryWork.Pure.ScanSeed, Organizer.OrganizerModRow>(
            () => ModEvents.Current,
            () => Framework.IsInFrameworkUpdateThread,
            logWarning: message => Log.Warning(message));
        IndexWork = new LibraryWorkCoordinator<LibraryWork.Pure.IndexSeed, LibrarySearch.IndexedMod>(
            () => ModEvents.Current,
            () => Framework.IsInFrameworkUpdateThread,
            logWarning: message => Log.Warning(message));
```

`Framework` is the existing `IFramework` service field on `Plugin`. Confirm its exact name before editing:

```bash
grep -n "IFramework" PenumbraOrganizer.Plugin/Plugin.cs
```

If the field is named differently, use the real name and note the deviation in your report.

- [ ] **Step 6: Run the full suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: all pass, 895. `LibraryWorkPurityTests` must still pass; if it fails, a Dalamud type reached the `LibraryWork` namespace and the `Func<bool>` indirection was bypassed.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "feat: assert library materialization runs on the framework thread"
```

---

### Task 3: Checkpoint logging with run identity

**Files:**
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (pass a log delegate to both coordinators; log the version at startup)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`

**Interfaces:**
- Consumes: the coordinator's existing `Action<string>? _logWarning`.
- Produces: a second optional delegate `Action<string>? logInfo`.

**Background the implementer needs:**

A successful scan currently writes nothing to the log. That is why the crash report that motivated this work cannot establish whether a scan ran at all. The trail must let a reader tell, from `dalamud.log` alone, which phase was executing when the log went silent.

Runs need identity: a user may run several scans and index builds in a session, and both coordinators share one logger. A bare "Scan requested" is unsearchable once interleaved.

Accepted limitation, already recorded in the spec: a hard native kill may lose the last buffered line or two. A trail means "at least this far", never "exactly this far". Do not write code or comments implying stronger guarantees.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Checkpoints_TagEveryLineWithTheJobAndRunId()
    {
        var lines = new List<string>();
        var scheduler = new ManualScheduler();
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 7L, isFrameworkThread: () => true, scheduler.Schedule, logInfo: lines.Add);

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.All(lines, line => Assert.StartsWith("[Fake:1] ", line));
        Assert.Contains(lines, l => l.Contains("requested"));
        Assert.Contains(lines, l => l.Contains("materialize complete") && l.Contains("items=1") && l.Contains("epoch=7"));
        Assert.Contains(lines, l => l.Contains("worker complete") && l.Contains("results=1"));
        Assert.Contains(lines, l => l.Contains("publish complete"));
    }

    [Fact]
    public void Checkpoints_RunIdIncrementsPerRun()
    {
        var lines = new List<string>();
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true, scheduler.Schedule, logInfo: lines.Add);

        coordinator.Start(new FakeJob { Items = [], Processor = new FakeProcessor() });
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();
        coordinator.Start(new FakeJob { Items = [], Processor = new FakeProcessor() });

        Assert.Contains(lines, l => l.StartsWith("[Fake:1] "));
        Assert.Contains(lines, l => l.StartsWith("[Fake:2] "));
    }

    [Fact]
    public void Checkpoints_TerminalFailure_RecordsExceptionTypeAndMessage()
    {
        var lines = new List<string>();
        var job = new FakeJob
        {
            Items = [],
            Processor = new FakeProcessor(),
            MaterializeThrows = new TimeoutException("Penumbra did not respond."),
        };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true, new ManualScheduler().Schedule, logInfo: lines.Add);

        coordinator.Start(job);
        coordinator.Update();

        Assert.Contains(lines, l => l.Contains("TimeoutException") && l.Contains("Penumbra did not respond."));
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "Checkpoints_"
```

Expected: compile error, no `logInfo` parameter exists.

- [ ] **Step 3: Implement**

Add the field, a run counter, and the parameter:

```csharp
    private readonly Action<string>? _logInfo;
    private int _runId;
```

Constructor gains `Action<string>? logInfo = null` after `logWarning`, assigned to `_logInfo`.

Add the helper:

```csharp
    // Runs are tagged because a session interleaves Scan and Index runs through one logger, and a
    // bare phase name is unsearchable once they mix. The id restarts at 1 per plugin load, which is
    // enough to correlate one session's lines.
    private void Checkpoint(string message) =>
        _logInfo?.Invoke($"[{State.JobDisplayName ?? _job?.DisplayName}:{_runId}] {message}");
```

In `Start`, after `PublishRunning(LibraryWorkPhase.Materializing);`:

```csharp
        _runId++;
        Checkpoint("requested");
```

In `TryMaterializePending`, add `Checkpoint("materialize begin");` immediately before the `try` around `job.Materialize()`, and after the elapsed calculation:

```csharp
        Checkpoint($"materialize complete items={batch.Items.Count} elapsedMs={materializeElapsed.TotalMilliseconds:F0} epoch={_startEpoch}");
```

After the scheduler launch succeeds, add `Checkpoint("worker started");`.

In `Update()`, immediately after the completed-task branch determines the task ran to completion and before the cancellation check, add:

```csharp
        Checkpoint($"worker complete results={task.Result.Count}");
```

Guard this so it is only reached when `task` is neither cancelled nor faulted; `task.Result` throws otherwise. Place it after the `IsCanceled` and `IsFaulted` early returns.

Around the publish call:

```csharp
        Checkpoint($"publish begin capturedEpoch={_startEpoch} currentEpoch={_readEpoch()}");
        PublishRunning(LibraryWorkPhase.Publishing);
        try
        {
            _job!.Publish(task.Result);
        }
        ...
        Checkpoint("publish complete");
```

Change `Settle` to log its own outcome, so every terminal path is covered without adding a call at each site:

```csharp
    private void Settle(LibraryWorkOutcome outcome, string? error)
    {
        Checkpoint($"settled {outcome}{(error is null ? "" : $": {error}")}");
        ...
```

For failure paths, pass the exception type as well as the message. Change the three `Settle(LibraryWorkOutcome.Failed, ex.Message)` call sites to:

```csharp
            Settle(LibraryWorkOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
```

Note the consequence and accept it: `State.LastError` now carries the type prefix, so it appears in the UI's failure message too. That is a small readability cost for a real diagnostic gain. Check whether any existing test asserts on an exact `LastError` string and update it if so.

- [ ] **Step 4: Wire production**

In `Plugin.cs`, add `logInfo: message => Log.Information(message)` to both coordinator constructions.

Log the version once at startup. Immediately before the existing `Log.Information("Penumbra Organizer (MVP) plugin loaded.")` line, add:

```csharp
        Log.Information($"Penumbra Organizer {typeof(Plugin).Assembly.GetName().Version?.ToString(4) ?? "unknown"} starting.");
```

- [ ] **Step 5: Run the full suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: all pass, 898.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "feat: log run-tagged checkpoints through every library work phase"
```

---

### Task 4: Release preparation for 0.5.3.0

**Files:**
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj` (`<Version>`)
- Create: `docs/RELEASE_NOTES_0.5.3.0.md`
- Modify: `docs/USER_GUIDE.md`

**Interfaces:** none. Documentation and version only.

**Background the implementer needs:**

Do **not** touch `repo.json` and do **not** tag or publish anything. The maintainer publishes releases explicitly, after reviewing the notes. Preparing them is in scope; shipping them is not.

The published 0.5.2.0 notes contain a Known Issue describing an `ImGui` format-string hazard with mod names. That entry is **wrong**: IL inspection showed `ImGui.TextColored` and `ImGui.SetTooltip` both route through `ImGui.Text(ImU8String)`, which calls `ImGuiNative.TextUnformatted`. There is no printf path. The correction is stated explicitly rather than the entry being quietly dropped.

The crash this work targets was never reproduced and no minidump was available. The notes must not claim it is fixed.

- [ ] **Step 1: Bump the version**

In `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`, change `<Version>0.5.2.0</Version>` to `<Version>0.5.3.0</Version>`.

- [ ] **Step 2: Write the release notes**

Create `docs/RELEASE_NOTES_0.5.3.0.md`:

```markdown
# Penumbra Organizer Plugin v0.5.3.0

## Changes since v0.5.2.0

### Fixed: the plugin was reading Penumbra from the wrong place

When you pressed Refresh mod list, the plugin read your mod list from Penumbra during the game's
drawing work rather than during its update work. Penumbra changes that same data during update work.
Reading it from the other side meant the plugin could be looking at your mod list at the exact
moment Penumbra was rewriting it.

The reads now happen where they were always supposed to, alongside Penumbra's own work rather than
across from it.

This is a suspected cause of reports where the game closes instantly, with no error, on pressing
Refresh mod list. It is only suspected. That crash could not be reproduced here and no crash dump
was available for it, so the actual cause remains unconfirmed. The behaviour was wrong regardless
and is now correct.

### Added: the plugin leaves a trail in the log

A scan used to write nothing to the Dalamud log, so a crash report could not show whether a scan had
even started. Each scan and index build now records what it is doing as it goes.

If the plugin is involved in a future crash, `dalamud.log` will show how far it got. If you hit
something like this, that file is the one worth sending.

### Correction to the 0.5.2.0 notes

Version 0.5.2.0 listed a known issue claiming that a mod with an unusual name could cause problems
when drawn. That was investigated and is wrong: the text drawing involved does not interpret mod
names in the way that entry assumed. There is no such hazard, and there never was. Apologies for the
noise.

## Known issues

- Applying a large plan moves every mod one at a time, and Penumbra announces each move on its own
  schedule. The automatic rescan that follows an Apply can therefore see those announcements
  arriving late and decide its own results are stale. If that happens you will be told the mod list
  changed immediately after an operation you just watched succeed. Nothing is wrong with the result,
  and running the scan again will work, but the message is misleading.

## For developers

Materialization moved from `Start` (called in the ImGui draw callback) to the first coordinator
`Update` (the framework-update callback), with an injected predicate asserting the thread so the
placement cannot silently regress. The staleness epoch is captured immediately before the snapshot
rather than after, so a mutation occurring during materialization invalidates the run instead of
becoming its baseline.

Materialization is still unbounded and now holds the framework thread instead of the render thread.
That is a different stall in a different place, not the absence of one. Making it incremental is a
separate concern.

898 tests pass on this release.
```

Replace `898` with the real number from Task 3 if it differs.

- [ ] **Step 3: Update the user guide**

In `docs/USER_GUIDE.md`, in the Scan section, after the paragraph beginning "If you add, remove, or move a mod in Penumbra while a scan is running", add:

```markdown
If a scan is interrupted by something changing your mod list at the moment it starts reading, it
stops immediately and tells you, rather than spending time on a result it would have to throw away.
```

- [ ] **Step 4: Verify and commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: unchanged from Task 3.

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj docs/RELEASE_NOTES_0.5.3.0.md docs/USER_GUIDE.md
git commit -m "docs: prepare 0.5.3.0 notes and bump the version"
```

---

## Self-review notes

- **Spec coverage:** deferral (T1), epoch capture before materialize with the early-out (T1), pending ownership table (T1 tests cover cancel, dispose, double-update, materialize failure), framework-thread guard (T2), checkpoint logging with run ids and exception types (T3), release wording and the 0.5.2.0 correction (T4). The spec's "what is not changed" section requires no task.
- **The epoch constraint is defended twice:** by `Epoch_ChangedDuringMaterialize_InvalidatesTheRun` and by the explicit mutation check in T1 Step 5, because it is the single most likely thing for a later change to "simplify" incorrectly.
- **Type consistency:** `TryMaterializePending` is introduced in T1 and modified in T2 and T3; the constructor signature changes in T2 and again in T3, and both changes are shown in full rather than described.
- **Known deviation from strict TDD:** T2 and T3 Step 2 expect a compile error rather than an assertion failure, because both tests exercise a constructor parameter that does not exist yet. This is called out rather than papered over.
