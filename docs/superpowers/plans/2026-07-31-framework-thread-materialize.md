# Framework-Thread Materialize Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Revised 2026-07-31 after review.

**Goal:** Move every Penumbra IPC read out of the ImGui draw callback and into the framework-update callback, enforce that placement for the coordinator's whole active path with an executable guard, and leave a diagnostic trail in the log.

**Architecture:** `LibraryWorkCoordinator.Start` stops materializing. It takes ownership of the job and closes the admission gates on the click frame; the first `Update()` afterwards captures the epoch, materializes, and launches the background worker. `Update()` asserts it is on the framework thread whenever a run is active, covering publication as well as materialization, via an injected `Func<bool>` so the coordinator stays free of Dalamud references.

**Tech Stack:** C# / .NET 10, Dalamud plugin (API level 15), Penumbra.Api 5.15.1, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-31-framework-thread-materialize-design.md`. Read it before Task 1; it carries the reasoning behind the epoch decision, which is the one part of this change that is easy to get subtly wrong.

## Global Constraints

- **The epoch is captured immediately BEFORE `Materialize()`, never after.** Capturing after would make a change that occurred *during* materialization part of the new baseline, letting a snapshot that spans two Penumbra states publish as valid. That interval is the whole point of this work. This was an explicit review decision, not an accident.
- **The thread guard covers the whole active `Update()` path, not just materialization.** Publication, task settlement and epoch reads all happen in `Update()` and are all part of the framework-thread contract. Guarding only materialization would leave publication protected by convention alone, which is the exact failure mode being fixed.
- **A wrong-thread `Update()` throws.** It does not settle `Failed`. It is a programming error in plugin code, not a runtime condition a user can cause or recover from.
- **Diagnostic logging must never alter coordinator execution.** Every call into an injected log delegate is wrapped so a throwing logger cannot strand a run. A logger that throws inside `Settle` would otherwise leave the coordinator permanently non-idle, gating Scan, Index, Apply, Restore, cleanup and backup with no recovery short of reloading the plugin.
- **Exception types go to the log, never into `State.LastError`.** `LastError` is user-facing text. Do not change what a user reads in order to satisfy a diagnostic test.
- `LibraryWorkCoordinator` must not reference Dalamud or Penumbra types. Enforced by `LibraryWorkPurityTests.PureTypesAndCrossThreadDtos_DoNotReferenceDalamudOrPenumbra`. Inject a `Func<bool>`, never an `IFramework`.
- Comment density and style must match the surrounding file. `LibraryWorkCoordinator.cs` uses full-sentence comments that explain *why*, only where the reason is non-obvious. Do not annotate the obvious.
- No em dashes in user-facing strings, docs, or release notes. Use commas or restructure.
- Baseline before this plan: **886 tests passing**. Expected minimum deltas: Task 1 +11, Task 2 +2, Task 3 +9. Report the real count after each task and treat these as minimums, not targets. Derive the release note's figure from the final actual run only.

---

### Task 1: Defer materialization to the framework-update callback

**Files:**
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs` (`Start`, `Update`, new pending field, new `MaterializePending`)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`

**Interfaces:**
- Consumes: `ILibraryWorkJob<TSeed,TResult>.Materialize()`, `LibraryWorkBatch<TSeed,TResult>`, `LibraryWorkPhase`, `LibraryWorkOutcome`, `LibraryWorkStateSnapshot`.
- Produces: `private void MaterializePending()`, called from `Update()`. Task 2 adds the thread guard to `Update()`; Task 3 adds logging.

**Background the implementer needs:**

`Start` is called from the ImGui draw callback (the button handler in `MainWindow`). `Update()` is called once per frame from `Plugin.OnFrameworkUpdate`. Today `Start` calls `job.Materialize()` synchronously, so all Penumbra IPC happens in the draw callback. This task moves that call into `Update()`.

`Phase` must still become `Materializing` inside `Start`, on the click frame. Every admission gate in the plugin keys off `Phase != Idle`. If the phase change waited for the next `Update()`, a second click in the intervening window would be admitted.

`Plugin.OnFrameworkUpdate` already wraps `ScanWork.Update()` and `IndexWork.Update()` in try/catch with `AbandonRun`, so an exception escaping `Update()` is handled at the call site. Do not add a second layer.

**Cancellation during the pending window, decided deliberately:**

`RequestCancellation()` is unconditional today (`_cts?.Cancel()`), while `CanCancel` on the published snapshot is `phase == Computing`. This task moves `_cts` creation into `Start`, so cancellation becomes *honoured* during the pending window while `CanCancel` stays *false*.

That asymmetry is intentional and must be pinned by a test rather than left ambiguous. `CanCancel` drives whether `MainWindow` draws a Cancel button; the pending window is a single frame, and a button that appears and vanishes within one frame is worse than no button. The invariant is: **a job accepted but not yet started is cancellable by code, but is not offered as a user affordance.** Assert both halves.

- [ ] **Step 1: Write the failing tests**

Add to `LibraryWorkCoordinatorTests.cs`, after the last `[Fact]`:

```csharp
    [Fact]
    public void Start_DoesNotMaterializeOrSchedule_UntilUpdate()
    {
        var job = new FakeJob { Items = ["a", "b"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        Assert.Equal(0, job.MaterializeCalls);
        // Also pins that Start does not schedule a closure that would materialize on a worker.
        Assert.Equal(0, scheduler.ScheduleCalls);

        coordinator.Update();
        Assert.Equal(1, job.MaterializeCalls);
        Assert.Equal(1, scheduler.ScheduleCalls);
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
    public void PendingRun_IsCancellableByCode_ButNotOfferedToTheUser()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);

        // Deliberate asymmetry: the pending window is one frame, so a Cancel button that appears
        // and vanishes within it would be worse than none. Cancellation still works if code asks.
        Assert.False(coordinator.State.CanCancel);

        coordinator.RequestCancellation();
        coordinator.Update();

        Assert.Equal(0, job.MaterializeCalls);
        Assert.Equal(0, scheduler.ScheduleCalls);
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Cancelled, coordinator.State.LastOutcome);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void DisposeBeforeUpdate_DoesNotMaterializeOrPublish()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Dispose();
        coordinator.Update();

        Assert.Equal(0, job.MaterializeCalls);
        Assert.Equal(0, scheduler.ScheduleCalls);
        Assert.Empty(job.Published);

        // Dispose deliberately does NOT normalize the published snapshot. Nothing reads State after
        // teardown, and rewriting it would invent a terminal outcome that never happened. Asserted
        // so the choice is explicit rather than accidental.
        Assert.Equal(LibraryWorkPhase.Materializing, coordinator.State.Phase);
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

        coordinator.Update(); // must not retry the failed job
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
        var (coordinator, scheduler, _) = NewCoordinator(() => epoch);
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };

        coordinator.Start(job);
        epoch = 11L; // the snapshot has not been taken yet, so it will represent epoch 11
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Completed, coordinator.State.LastOutcome);
        Assert.Single(job.Published);
    }

    [Fact]
    public void Epoch_ChangedDuringMaterialize_InvalidatesTheRunImmediately()
    {
        var epoch = 10L;
        var (coordinator, scheduler, _) = NewCoordinator(() => epoch);
        var job = new FakeJob
        {
            Items = ["a"],
            Processor = new FakeProcessor(),
            // Penumbra mutating while the snapshot is taken: it may describe two different states.
            DuringMaterialize = () => epoch = 11L,
        };

        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.StaleModList, coordinator.State.LastOutcome);
        Assert.Empty(job.Published);
        Assert.Equal(0, scheduler.ScheduleCalls); // settled without paying for a doomed worker
    }
```

`NewCoordinator` currently returns the epoch delegate as its third element and takes an optional epoch. Keep that shape; the tests above rely on passing `() => epoch`.

Extend `FakeJob`. Replace its `Materialize` and add two members:

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

Add a counter to `ManualScheduler`, incremented as the first statement of its `Schedule` method:

```csharp
        public int ScheduleCalls { get; private set; }
```

- [ ] **Step 2: Run the new tests and record the actual failures**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "LibraryWorkCoordinatorTests"
```

Expected: the new tests fail. Several **existing** tests will also fail, because they call `Start` and immediately assert on materialized state; that is expected and handled in Step 4. Write down the real output rather than assuming it matches this prediction.

- [ ] **Step 3: Implement the deferral**

In `LibraryWorkCoordinator.cs`, add a field beside the existing ones:

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

        // Created here rather than at materialize time so a run can be cancelled during the pending
        // window. CanCancel stays false for that window on purpose: it is one frame, and a Cancel
        // button that appears and vanishes within a frame is worse than no button.
        _cts = new CancellationTokenSource();

        PublishRunning(LibraryWorkPhase.Materializing);
    }

    /// <summary>
    /// Framework thread. Captures the epoch, takes the Penumbra snapshot, and launches the worker.
    /// Always terminal for this Update: it either settles the run or starts the background task.
    /// </summary>
    private void MaterializePending()
    {
        var job = _pendingJob!;
        _pendingJob = null;

        // Checked before any Penumbra call: a run cancelled while pending must not touch Penumbra.
        if (_cts?.IsCancellationRequested == true)
        {
            Settle(LibraryWorkOutcome.Cancelled, null);
            return;
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
            return;
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

        // Penumbra mutated while the snapshot was being taken, so it may describe two different
        // states. Settling now turns a doomed multi-second scan into an immediate, accurate
        // message; waiting for the worker would reach the same verdict much later.
        if (_readEpoch() != _startEpoch)
        {
            Settle(LibraryWorkOutcome.StaleModList, null);
            return;
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
    }
```

Note the shape: `MaterializePending` returns `void` and its call site always returns afterwards. It is not a `Try...` method, because there is no path on which the same `Update()` should continue into task processing.

Insert the pending branch at the top of `Update()`, immediately after the `_disposed` guard:

```csharp
    public void Update()
    {
        if (_disposed)
            return;

        if (_pendingJob is not null)
        {
            MaterializePending();
            return;
        }

        if (_task is not { IsCompleted: true })
        ...
```

Add `_pendingJob = null;` to `Settle`, `AbandonRun`, and `Dispose`, beside each existing `_job = null;`. Without this a pending job survives a terminal outcome and materializes on a later frame.

- [ ] **Step 4: Update the existing tests that assumed synchronous materialization**

Run the suite and find every failure. For each failing pre-existing test, insert `coordinator.Update();` immediately after `coordinator.Start(job);`. Do not change any assertion and do not delete any test.

Two need more:

`Start_MovesToComputing_WithoutRunningTheProcessor` is now misnamed. Rename and keep both halves:

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

`MaterializeThrowing_FailsBeforeAnyBackgroundWork` must keep meaning what its name says. Add the `Update()` call and assert nothing was scheduled:

```csharp
        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal(0, scheduler.ScheduleCalls);
```

`ModListChangedDuringRun_DiscardsTheResult` (around line 201) is the **third epoch interval**: a change after materialization but before publish. It needs only the `Update()` insertion. Do not rename or weaken it; together with the two new epoch tests it completes the contract:

```
before snapshot -> accepted as the baseline
during snapshot -> settled StaleModList immediately, no worker
after snapshot  -> settled StaleModList at publish
```

- [ ] **Step 5: Verify the critical regression tests by mutation**

Confirm the new tests are tripwires rather than merely passing. Revert one production change at a time, check the expected test goes red, then **restore it**:

1. Remove the `_pendingJob = null;` you added to `Settle`. Expect `MaterializeFailure_ClearsPendingJob_AndStartIsAllowedAgain` to fail.
2. Move the epoch capture in `MaterializePending` from before `job.Materialize()` to after it. Expect `Epoch_ChangedDuringMaterialize_InvalidatesTheRunImmediately` to fail. This is the constraint most likely to be "simplified" later.

Record both results in your report. If either mutation does not produce the expected failure, the test is not pinning what it claims and must be fixed before proceeding.

- [ ] **Step 6: Run the full suite, then commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: all pass, at least 897. Report the real number.

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "fix: materialize library work on the framework thread, not in the draw callback"
```

---

### Task 2: Enforce the framework thread across the whole active update path

**Files:**
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs` (constructor, `Update`)
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:84-87` (both coordinator constructions)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`

**Interfaces:**
- Consumes: `Update` and `MaterializePending` from Task 1.
- Produces: `LibraryWorkCoordinator(Func<long> readEpoch, Func<bool> isFrameworkThread, BackgroundScheduler? scheduler = null, Action<string>? logWarning = null, TimeSpan? disposeWait = null)`.

**Background the implementer needs:**

Task 1 proves materialization is *deferred*. It does not prove it runs on the right *thread*: a future caller could invoke `Update()` from anywhere and every test would still pass. That is a convention, and a convention is exactly what already failed here once.

**The guard goes in `Update()`, not in `MaterializePending()`.** `Update()` also settles the completed task, reads the epoch, transitions state and calls `job.Publish(...)`. Guarding only materialization would leave publication protected by nothing once `_pendingJob` is null, which is the same class of bug in a different place.

The guard fires only when a run is active. An `Update()` call on an idle coordinator does nothing meaningful, and throwing on it would turn a harmless call into a crash.

`Dalamud.Plugin.Services.IFramework.IsInFrameworkUpdateThread` is a `bool` property and exists on the targeted API level (verified against `Dalamud.dll`). The coordinator must not reference `IFramework` directly, because `LibraryWorkPurityTests` forbids Dalamud types in this namespace. Inject a `Func<bool>`, matching the existing `Func<long> readEpoch` style.

`isFrameworkThread` is a **required** positional parameter. An optional one defaulting to permissive would let a future construction site silently opt out, which is the failure mode being fixed.

- [ ] **Step 1: Write the failing tests**

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

    [Fact]
    public void Update_OffTheFrameworkThread_DoesNotPublishCompletedWork()
    {
        // The case a materialize-only guard would miss entirely: by the time the worker finishes,
        // _pendingJob is null, so a guard inside MaterializePending never runs again.
        var onFrameworkThread = true;
        var scheduler = new ManualScheduler();
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => onFrameworkThread, scheduler.Schedule);

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();

        onFrameworkThread = false;

        Assert.Throws<InvalidOperationException>(() => coordinator.Update());
        Assert.Empty(job.Published);
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "Update_OffTheFrameworkThread"
```

Expected: a compile error, because the constructor has no `isFrameworkThread` parameter yet. A compile failure is a legitimate red here; do not add the parameter before writing the tests.

- [ ] **Step 3: Add the parameter and the guard**

Add the field beside `_readEpoch`:

```csharp
    private readonly Func<bool> _isFrameworkThread;
```

Change the constructor signature, inserting `Func<bool> isFrameworkThread` as the second parameter and assigning `_isFrameworkThread = isFrameworkThread;`.

Add the guard to `Update()`, after the `_disposed` check and before the pending branch:

```csharp
    public void Update()
    {
        if (_disposed)
            return;

        // Covers the whole active path, not just materialization: this method also settles the
        // completed task, reads the epoch and calls Publish. Throws rather than settling Failed,
        // because calling it off the framework thread is a programming error in plugin code, not a
        // runtime condition a user can cause or recover from. Idle updates are not guarded: they do
        // nothing, and throwing on them would turn a harmless call into a crash.
        if (State.IsRunning && !_isFrameworkThread())
            throw new InvalidOperationException(
                "Library work updates must run on the framework thread.");

        if (_pendingJob is not null)
        ...
```

Do not add a second check inside `MaterializePending`. One guard at the entry point is the contract; a duplicate invites the two to drift.

- [ ] **Step 4: Update every test construction site**

Update the `NewCoordinator` helper:

```csharp
        var coordinator = new LibraryWorkCoordinator<string, string>(
            readEpoch, isFrameworkThread: () => true, scheduler.Schedule);
```

Then find every remaining direct construction:

```bash
grep -n "new LibraryWorkCoordinator" PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
```

Every site except the two new off-thread tests gets `isFrameworkThread: () => true` as the second argument.

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

`Framework` is the existing static `IFramework` property on `Plugin` (`Plugin.cs:25`), verified.

- [ ] **Step 6: Verify the guard by mutation**

Delete the guard block from `Update()` and re-run. Expect **both** `Update_OffTheFrameworkThread_ThrowsWithoutMaterializing` and `Update_OffTheFrameworkThread_DoesNotPublishCompletedWork` to fail. Then move the guard into `MaterializePending` instead and re-run: expect the publication test to fail while the materialization test passes. That second mutation is the whole point of this task, so record its result explicitly. Restore the correct placement.

- [ ] **Step 7: Run the full suite, then commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

`LibraryWorkPurityTests` must still pass; if it fails, a Dalamud type reached the `LibraryWork` namespace and the `Func<bool>` indirection was bypassed.

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "feat: assert library work updates run on the framework thread"
```

---

### Task 3: Checkpoint logging with run identity

**Files:**
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`

**Interfaces:**
- Consumes: the coordinator's existing `Action<string>? _logWarning`.
- Produces: `Action<string>? logInfo` constructor parameter, after `logWarning`.

**Background the implementer needs:**

A successful scan currently writes nothing to the log, which is why the crash report that motivated this work cannot establish whether a scan ran at all. The trail must let a reader tell, from `dalamud.log` alone, which phase was executing when the log went silent.

Three constraints on how this is built, each of which has a test:

1. **Logging must never alter execution.** The delegate is injected and arbitrary. If it throws inside `Settle`, the run never settles and every gate in the plugin locks permanently. Every call into a log delegate is wrapped, including the existing `_logWarning` calls.
2. **Run identity comes from a captured field, not from `State`.** `State` is a published UI snapshot whose `JobDisplayName` is null once settled, so a terminal checkpoint would lose its label. Capture the label in `Start`.
3. **Exception types go to the log, not to `State.LastError`.** `LastError` is user-facing. Do not change what a user reads to satisfy a diagnostic test.

Scan and Index are separate coordinator instances whose run counters both start at 1, so the label is what disambiguates `[Scan:1]` from `[Index:1]`.

Accepted limitation, already in the spec: a hard native kill may lose the last buffered line or two. A trail means "at least this far", never "exactly this far". Do not write comments implying stronger guarantees.

- [ ] **Step 1: Write the failing tests**

```csharp
    private static List<string> WithoutTimings(IEnumerable<string> lines) =>
        // Elapsed values are non-deterministic, so the ordered comparison below drops them and the
        // materialize line's contents are asserted separately.
        lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @" elapsedMs=\d+", "")).ToList();

    [Fact]
    public void Checkpoints_RecordEveryBoundaryInOrder()
    {
        var lines = new List<string>();
        var (coordinator, scheduler, _) = NewCoordinator(logInfo: lines.Add);
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(
        [
            "[Fake:1] requested",
            "[Fake:1] materialize begin",
            "[Fake:1] materialize complete items=1 epoch=0",
            "[Fake:1] worker started",
            "[Fake:1] worker complete results=1",
            "[Fake:1] publish begin capturedEpoch=0 currentEpoch=0",
            "[Fake:1] publish complete",
            "[Fake:1] settled Completed",
        ], WithoutTimings(lines));
    }

    [Fact]
    public void Checkpoints_RunIdIncrementsPerRun_AndSurvivesSettlement()
    {
        var lines = new List<string>();
        var (coordinator, scheduler, _) = NewCoordinator(logInfo: lines.Add);

        coordinator.Start(new FakeJob { Items = [], Processor = new FakeProcessor() });
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();
        coordinator.Start(new FakeJob { Items = [], Processor = new FakeProcessor() });

        // The terminal line must still carry its label, which it cannot if identity is read from
        // State (JobDisplayName is null once settled).
        Assert.Contains("[Fake:1] settled Completed", lines);
        Assert.Contains("[Fake:2] requested", lines);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("stale")]
    [InlineData("materialize-failure")]
    [InlineData("worker-fault")]
    [InlineData("scheduler-failure")]
    [InlineData("publish-failure")]
    public void Checkpoints_EveryTerminalPath_LogsItsOutcome(string scenario)
    {
        var lines = new List<string>();
        var epoch = 0L;
        var scheduler = new ManualScheduler();
        var throwingScheduler = scenario == "scheduler-failure";
        var job = new FakeJob
        {
            Items = ["a"],
            Processor = new FakeProcessor
            {
                ProcessThrows = scenario == "worker-fault" ? new InvalidOperationException("worker") : null,
            },
            MaterializeThrows = scenario == "materialize-failure" ? new TimeoutException("no response") : null,
            PublishThrows = scenario == "publish-failure" ? new InvalidOperationException("publish") : null,
            DuringMaterialize = scenario == "stale" ? () => epoch = 1L : null,
        };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => epoch,
            isFrameworkThread: () => true,
            throwingScheduler ? (_, _) => throw new InvalidOperationException("scheduler") : scheduler.Schedule,
            logInfo: lines.Add);

        coordinator.Start(job);
        if (scenario == "cancelled")
            coordinator.RequestCancellation();
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Contains(lines, l => l.Contains("settled"));
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
    }

    [Fact]
    public void Checkpoints_FailureLogsExceptionType_ButLastErrorStaysUserFacing()
    {
        var lines = new List<string>();
        var (coordinator, _, _) = NewCoordinator(logInfo: lines.Add);
        var job = new FakeJob
        {
            Items = [],
            Processor = new FakeProcessor(),
            MaterializeThrows = new TimeoutException("Penumbra did not respond."),
        };

        coordinator.Start(job);
        coordinator.Update();

        Assert.Contains(lines, l => l.Contains("TimeoutException"));
        // The user sees the message, not the type name.
        Assert.Equal("Penumbra did not respond.", coordinator.State.LastError);
    }

    [Fact]
    public void ThrowingLogger_DoesNotStrandTheRun()
    {
        var scheduler = new ManualScheduler();
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L,
            isFrameworkThread: () => true,
            scheduler.Schedule,
            logWarning: _ => throw new InvalidOperationException("logger is broken"),
            logInfo: _ => throw new InvalidOperationException("logger is broken"));

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        // A diagnostic delegate must never be able to gate the plugin.
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Completed, coordinator.State.LastOutcome);
        Assert.Single(job.Published);
    }
```

Extend `NewCoordinator` with an optional `Action<string>? logInfo = null` parameter, passed through to the constructor.

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "Checkpoints_|ThrowingLogger_"
```

Expected: compile error, no `logInfo` parameter exists.

- [ ] **Step 3: Implement**

Add fields:

```csharp
    private readonly Action<string>? _logInfo;
    private int _runId;
    private string? _runLabel;
```

Constructor gains `Action<string>? logInfo = null` after `logWarning`.

Add the two safe emitters. Every log call in the class goes through one of them:

```csharp
    // Run identity is captured rather than read from State: State.JobDisplayName is null once a run
    // settles, so a terminal checkpoint would lose its label. Scan and Index are separate instances
    // whose counters both start at 1, so the label is what tells [Scan:1] from [Index:1].
    private void Checkpoint(string message) => SafeLog(_logInfo, $"[{_runLabel}:{_runId}] {message}");

    private void Warn(string message) => SafeLog(_logWarning, message);

    private static void SafeLog(Action<string>? sink, string message)
    {
        try
        {
            sink?.Invoke(message);
        }
        catch
        {
            // Diagnostic logging must never alter coordinator execution. A delegate throwing inside
            // Settle would otherwise leave the run non-terminal, permanently gating Scan, Index,
            // Apply, Restore, cleanup and backup with no recovery short of reloading the plugin.
        }
    }
```

Replace the two existing `_logWarning?.Invoke(...)` call sites (the materialize duration warning and the teardown warning in `Dispose`) with `Warn(...)`.

In `Start`, assign identity **before** publishing state, so no snapshot or callback can observe a running operation whose diagnostic identity is not yet initialised:

```csharp
        _runId++;
        _runLabel = job.DisplayName;

        PublishRunning(LibraryWorkPhase.Materializing);
        Checkpoint("requested");
```

In `MaterializePending`, add `Checkpoint("materialize begin");` immediately before the `try`, and after the duration warning:

```csharp
        Checkpoint($"materialize complete items={batch.Items.Count} elapsedMs={materializeElapsed.TotalMilliseconds:F0} epoch={_startEpoch}");
```

After the scheduler assignment succeeds, `Checkpoint("worker started");`.

In `Update()`, place `Checkpoint($"worker complete results={task.Result.Count}");` **after** the `IsCanceled` and `IsFaulted` early returns, since `task.Result` throws otherwise.

Read the epoch **once** and use the same value for both the log line and the decision, so the log always describes the value that was acted on:

```csharp
        var currentEpoch = _readEpoch();
        if (currentEpoch != _startEpoch)
        {
            Settle(LibraryWorkOutcome.StaleModList, null);
            return;
        }

        Checkpoint($"publish begin capturedEpoch={_startEpoch} currentEpoch={currentEpoch}");
        PublishRunning(LibraryWorkPhase.Publishing);
        try
        {
            _job!.Publish(task.Result);
        }
        catch (Exception ex)
        {
            SettleFailure(ex);
            return;
        }

        Checkpoint("publish complete");
        Settle(LibraryWorkOutcome.Completed, null);
```

Add one failure helper and route **every** exception path through it, so the type reaches the log without reaching the user:

```csharp
    private void SettleFailure(Exception ex)
    {
        Checkpoint($"failed exception={ex.GetType().Name} message={ex.Message}");
        Settle(LibraryWorkOutcome.Failed, ex.Message);
    }
```

Replace every `Settle(LibraryWorkOutcome.Failed, ex.Message)` and `Settle(LibraryWorkOutcome.Failed, task.Exception!.GetBaseException().Message)` call with `SettleFailure(...)`. `State.LastError` keeps exactly the text it has today.

Log the outcome from `Settle` itself, so no terminal path can be added later without a checkpoint:

```csharp
    private void Settle(LibraryWorkOutcome outcome, string? error)
    {
        Checkpoint($"settled {outcome}");
        ...
```

Clear `_runLabel` nowhere. It is overwritten by the next `Start` and must survive settlement so the terminal line keeps its label.

- [ ] **Step 4: Wire production**

In `Plugin.cs`, add `logInfo: message => Log.Information(message)` to both coordinator constructions. Immediately before the existing `Log.Information("Penumbra Organizer (MVP) plugin loaded.")`, add:

```csharp
        Log.Information($"Penumbra Organizer {typeof(Plugin).Assembly.GetName().Version?.ToString(4) ?? "unknown"} starting.");
```

- [ ] **Step 5: Verify by mutation, run the suite, commit**

Remove the `try`/`catch` from `SafeLog` and re-run: expect `ThrowingLogger_DoesNotStrandTheRun` to fail. Restore it. Then change `Checkpoint` to read `State.JobDisplayName` instead of `_runLabel` and re-run: expect `Checkpoints_RunIdIncrementsPerRun_AndSurvivesSettlement` to fail on the settled line. Restore it. Record both.

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

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

**Background the implementer needs:**

Do **not** touch `repo.json`, and do **not** tag or publish. The maintainer publishes releases explicitly after reviewing the notes. Preparing them is in scope; shipping them is not.

The published 0.5.2.0 notes contain a Known Issue describing an `ImGui` format-string hazard with mod names. That entry is **wrong**: IL inspection showed `ImGui.TextColored` and `ImGui.SetTooltip` both route through `ImGui.Text(ImU8String)`, which calls `ImGuiNative.TextUnformatted`. There is no printf path. State the correction explicitly rather than dropping the entry.

The notes must not assert how Penumbra schedules its own work. What was established is the plugin's own call path: reads were happening in the draw callback rather than the framework-update callback its design specifies. Everything beyond that is hypothesis, and the crash was never reproduced.

- [ ] **Step 1: Bump the version**

Change `<Version>0.5.2.0</Version>` to `<Version>0.5.3.0</Version>`.

- [ ] **Step 2: Write the release notes**

Create `docs/RELEASE_NOTES_0.5.3.0.md`:

```markdown
# Penumbra Organizer Plugin v0.5.3.0

## Changes since v0.5.2.0

### Fixed: the plugin was asking Penumbra for your mod list from the wrong place

When you pressed Refresh mod list, the plugin requested Penumbra's live mod data from the game's
drawing work rather than from the update work its own design specifies. Those reads now happen where
they were always meant to, alongside the rest of the plugin's Penumbra work.

This may address reports of the game closing instantly, with no error, on pressing Refresh mod list.
It is only "may". That crash could not be reproduced here and no crash dump was available for it, so
its cause remains unconfirmed. The behaviour was wrong regardless, and is now correct.

### Added: the plugin leaves a trail in the log

A scan used to write nothing to the Dalamud log, so a crash report could not show whether a scan had
even started. Each scan and index build now records what it is doing as it goes.

If the plugin is involved in a future crash, `dalamud.log` will show how far it got. If you hit
something like this, that is the file worth sending.

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
`Update` (the framework-update callback). An injected predicate asserts the thread for the whole
active update path, publication included, so the placement cannot silently regress. The staleness
epoch is captured immediately before the snapshot rather than after, so a mutation during
materialization invalidates the run instead of becoming its baseline.

Materialization is still unbounded and now holds the framework thread instead of the render thread.
That is a different stall in a different place, not the absence of one. Making it incremental is a
separate concern.

<N> tests pass on this release.
```

Replace `<N>` with the actual count from the final test run.

- [ ] **Step 3: Update the user guide**

In `docs/USER_GUIDE.md`, in the Scan section, after the paragraph beginning "If you add, remove, or move a mod in Penumbra while a scan is running", add:

```markdown
If the mod list changes while the plugin is taking its initial snapshot, the scan stops straight away
and asks you to run it again, rather than spending time on a snapshot that may already be out of
date.
```

- [ ] **Step 4: Verify and commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj docs/RELEASE_NOTES_0.5.3.0.md docs/USER_GUIDE.md
git commit -m "docs: prepare 0.5.3.0 notes and bump the version"
```

---

## Self-review notes

- **Spec coverage:** deferral (T1), epoch capture before materialize plus the early-out (T1), pending ownership including cancellation and disposal (T1), whole-path thread guard (T2), checkpoint logging with run identity, non-throwing delegates and exception types (T3), release wording and the 0.5.2.0 correction (T4).
- **The three epoch intervals each have a test:** before the snapshot, `Epoch_ChangedBetweenStartAndUpdate_DoesNotInvalidateTheRun` (new); during, `Epoch_ChangedDuringMaterialize_InvalidatesTheRunImmediately` (new); after, the pre-existing `ModListChangedDuringRun_DiscardsTheResult`, which T1 Step 4 preserves rather than rewrites.
- **Mutation checks cover the three constraints most likely to be "simplified" later:** epoch capture position (T1), guard placement in `Update` vs `MaterializePending` (T2), and the `SafeLog` try/catch plus captured run label (T3).
- **Two deliberate deviations from the review, asserted rather than left implicit:** `CanCancel` stays false during the pending window, because the window is one frame and a flickering button is worse than none; and `Dispose` does not normalize the published snapshot, because nothing reads `State` after teardown and rewriting it would invent a terminal outcome that never occurred.
- **Known deviation from strict TDD:** T2 and T3 Step 2 expect a compile error rather than an assertion failure, because both exercise a constructor parameter that does not exist yet. Called out rather than papered over.
- **Test counts are minimums, not targets.** Task deltas are stated as "at least"; the release note figure comes from the final actual run.
