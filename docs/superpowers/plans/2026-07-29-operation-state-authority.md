# Operation State Authority Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `OperationController` the single authority for whether work is in flight, and make operation completion something the UI consumes exactly once.

**Architecture:** Admission moves into the controller via `TryStart(type, prepare)`, which inverts preparation so a failed plan build cannot leave the controller locked. A `_starting` flag extends the controller's authority backwards over the preparation window that `_operationInProgress` existed to cover. An injected `Func<string?>` gate lets non-operation activity participate without the controller knowing what it is. A `CompletionGeneration` counter on the published snapshot replaces inferring completion from `Kind` and `Stage`.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), Dalamud.NET.Sdk 15.0.0, xunit 2.5.3.

**Spec:** `docs/superpowers/specs/2026-07-29-operation-state-authority-design.md`
**Context:** `docs/superpowers/specs/2026-07-29-cleanup-brief-verification.md` explains why brief items 5, 9, 10, 11, 12, and 14 are not in this plan.

## Global Constraints

- **This is a behaviour-preserving refactor, with three named exceptions.** No user-visible behaviour, recovery semantics, diagnostics output, or persisted format changes, and every exception message a caller can observe today remains observable with the same type and text — except: (1) completion consequences fire on the frame completion is observed rather than waiting for a tab visit (Task 5, documented there — the old deferral was itself a latent staleness bug), (2) `CleanUpFolders`/`RollbackFolderCleanup` gain a domain-level admission guard they never had (Task 3 Step 4 — not user-visible, since the UI gate already prevents the click), and (3) the Delete History Snapshot button in the History tab is now disabled during a pending recovery, where it previously remained clickable and failed with an error (final review fix — brought in line with the Create Backup and Restore buttons in the same tab, which were already gated this way).
- Target framework `net10.0-windows7.0`; `ImplicitUsings` and `Nullable` enabled in both projects. Test project has `<Using Include="Xunit" />`, so `[Fact]` and `Assert` need no `using`.
- Test namespaces mirror folder structure (`PenumbraOrganizer.Plugin.Tests.<Folder>`).
- Build/test command: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`. Baseline before this plan: **800 passed, 0 failed**, one pre-existing `xUnit2017` analyzer warning in `ApplyPlannerTests.cs:306` that is not this plan's to fix.
- Prerequisite already merged: `chore/remove-dead-sync-paths` (commits `62fcbc0`, `f6079dc`) removed `Plugin.ApplyChanges`, `Plugin.Restore(Guid)`, `ExecuteOrderedMoves`, `ReadCurrentModPaths`, and both dead result fields. Line numbers below are against that state — `Plugin.cs` is 651 lines.
- Work on branch `feat/operation-state-authority`, off `main`.

---

## Task 1: Characterization tests

The brief's own refactoring rules require pinning current behaviour before extracting anything. These tests must pass **unchanged** before and after every later task — they are the proof the refactor preserved behaviour.

**Files:**
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationAdmissionCharacterizationTests.cs`

**Interfaces:**
- Consumes: existing `OperationController`, `FakePenumbraOperations`, and the `FakeClock`/`SinglePlan`/`NewController` helpers in `OperationControllerTests.cs` — read that file first and mirror its helper shapes rather than inventing new ones.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Read the existing test helpers**

Read `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs` in full, in particular `FakeClock`, `SinglePlan`, `InterruptedJournal`, `NewController`, and `FakePenumbraOperations`. Copy those helper shapes; do not invent parallel ones.

- [ ] **Step 2: Write the characterization tests**

Create the file with these facts. Each asserts *current* behaviour:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

/// <summary>
/// Pins the admission and completion behaviour that exists BEFORE the state-authority refactor.
/// Every one of these must still pass, unchanged, after it. A test here that needs editing during
/// the refactor is a behaviour change and must be raised rather than edited.
/// </summary>
public class OperationAdmissionCharacterizationTests
{
    // Mirror the helpers from OperationControllerTests here (FakeClock, FakePenumbraOperations,
    // SinglePlan, NewController) exactly as that file defines them.

    [Fact]
    public void SecondOperation_CannotStartWhileOneIsActive()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        controller.StartApply(SinglePlan(), Guid.NewGuid(), NewBundleDirectory());

        Assert.False(controller.State.CanStartApply);
        Assert.False(controller.State.CanStartRestore);
    }

    [Fact]
    public void NormalOperation_CannotStartWhileRecoveryIsRequired()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        controller.RegisterDiscoveredRecovery(/* the discovered-recovery shape OperationControllerTests already uses */);

        Assert.True(controller.State.RequiresRecovery);
        Assert.False(controller.State.CanStartApply);
        Assert.False(controller.State.CanScan);
    }

    [Fact]
    public void CanStartNext_RequiresBothTerminalAndNoRecovery()
    {
        // OperationStage's terminal success member is Completed - there is no "Succeeded" stage
        // (the enum: Preparing/Prepared/Mutating/Refreshing/Verifying/Completed/
        // CompletedWithItemFailures/FailedBeforeMutation/FailedPartiallyApplied/Cancelled).
        var terminal = InterruptedJournal(Guid.NewGuid()) with { Stage = OperationStage.Completed };

        Assert.True(OperationController.CanStartNext(terminal, requiresRecovery: false));
        Assert.False(OperationController.CanStartNext(terminal, requiresRecovery: true));
    }

    [Fact]
    public void TerminalState_IsRetainedAndCanStartBecomesTrueAgain()
    {
        // _active is never cleared when an operation concludes - a terminal Stage stays visible in
        // State while CanStartApply simultaneously becomes true again. Pin both halves.
        var controller = RunSingleOperationToTerminal();

        Assert.NotNull(controller.State.Stage);
        Assert.True(controller.State.CanStartApply);
    }

    [Fact]
    public void ReRunningUpdateOnATerminalOperation_DoesNotChangeTheSnapshot()
    {
        var controller = RunSingleOperationToTerminal();
        var before = controller.State;

        controller.Update();
        controller.Update();

        Assert.Equal(before, controller.State); // OperationStateSnapshot is a record; value equality
    }
}
```

Fill `NewBundleDirectory()`, `RegisterDiscoveredRecovery`'s argument, and `RunSingleOperationToTerminal()` from what `OperationControllerTests.cs` already does — it drives an operation to terminal via `FakePenumbraOperations` and repeated `Update()` calls. Read it rather than guessing the shape.

- [ ] **Step 3: Run them and confirm they pass against unmodified code**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OperationAdmissionCharacterizationTests" --nologo`

Expected: PASS. A characterization test that fails now is describing behaviour that does not exist — fix the test, not the production code.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationAdmissionCharacterizationTests.cs
git commit -m "test: pin current operation admission and completion behaviour"
```

---

## Task 2: TryStart, the starting window, and the external gate

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs` — constructor (`:107-114`), add `TryStart`, add `_starting`, extend the admission guard
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs` (append)

**Interfaces:**
- Consumes: existing `OperationPlan`, `OperationType`, `CanStartNext`.
- Produces: `PreparedOperation(OperationPlan Plan, Guid SnapshotId, string BundleDirectory)`; `OperationStartResult(bool Started, string? RejectionReason)` with `Ok` and `Rejected(reason)`; `OperationController.TryStart(OperationType type, Func<PreparedOperation> prepare)`; a new optional constructor parameter `Func<string?>? externalActivityGate`.

- [ ] **Step 1: Write the failing tests**

Append to `OperationControllerTests.cs`:

```csharp
    [Fact]
    public void TryStart_WhenIdle_StartsTheOperation()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        var result = controller.TryStart(OperationType.Apply,
            () => new PreparedOperation(SinglePlan(), Guid.NewGuid(), NewBundleDirectory()));

        Assert.True(result.Started);
        Assert.Null(result.RejectionReason);
        Assert.False(controller.State.CanStartApply);
    }

    [Fact]
    public void TryStart_WhileActive_IsRejectedWithoutInvokingPrepare()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        controller.TryStart(OperationType.Apply,
            () => new PreparedOperation(SinglePlan(), Guid.NewGuid(), NewBundleDirectory()));

        var prepareCalls = 0;
        var result = controller.TryStart(OperationType.Apply, () =>
        {
            prepareCalls++;
            return new PreparedOperation(SinglePlan(), Guid.NewGuid(), NewBundleDirectory());
        });

        Assert.False(result.Started);
        Assert.NotNull(result.RejectionReason);
        Assert.Equal(0, prepareCalls);
    }

    [Fact]
    public void TryStart_WhenPrepareThrows_PropagatesAndLeavesTheControllerStartable()
    {
        // This is the whole point of inverting preparation: a failed plan build must not lock the
        // controller. _operationInProgress could, which is why it needed a catch at every call site.
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            controller.TryStart(OperationType.Apply,
                () => throw new InvalidOperationException("plan build failed")));

        Assert.Equal("plan build failed", thrown.Message);
        Assert.True(controller.State.CanStartApply);

        // And a real start still works afterwards.
        var result = controller.TryStart(OperationType.Apply,
            () => new PreparedOperation(SinglePlan(), Guid.NewGuid(), NewBundleDirectory()));
        Assert.True(result.Started);
    }

    [Fact]
    public void TryStart_FromInsidePrepare_IsRejected()
    {
        // The preparation window is exactly what _operationInProgress existed to cover: the
        // controller is not yet "active", but real, failure-prone work is in flight.
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        OperationStartResult? reentrant = null;

        controller.TryStart(OperationType.Apply, () =>
        {
            reentrant = controller.TryStart(OperationType.Restore,
                () => new PreparedOperation(SinglePlan(type: OperationType.Restore), Guid.NewGuid(), NewBundleDirectory()));
            return new PreparedOperation(SinglePlan(), Guid.NewGuid(), NewBundleDirectory());
        });

        Assert.NotNull(reentrant);
        Assert.False(reentrant!.Started);
    }

    [Fact]
    public void TryStart_WhenTheExternalGateBlocks_IsRejectedWithTheGatesReason()
    {
        var controller = new OperationController(
            new FakePenumbraOperations(), new FakeClock(), new NoOpDiagnosticsSink(),
            TimeSpan.FromMilliseconds(4), NewBundleDirectory(),
            externalActivityGate: () => "A scan is already running.");

        var result = controller.TryStart(OperationType.Apply,
            () => new PreparedOperation(SinglePlan(), Guid.NewGuid(), NewBundleDirectory()));

        Assert.False(result.Started);
        Assert.Equal("A scan is already running.", result.RejectionReason);
    }

    [Fact]
    public void TryStart_WhenTheExternalGateAllows_Starts()
    {
        var controller = new OperationController(
            new FakePenumbraOperations(), new FakeClock(), new NoOpDiagnosticsSink(),
            TimeSpan.FromMilliseconds(4), NewBundleDirectory(),
            externalActivityGate: () => null);

        var result = controller.TryStart(OperationType.Apply,
            () => new PreparedOperation(SinglePlan(), Guid.NewGuid(), NewBundleDirectory()));

        Assert.True(result.Started);
    }
```

`SinglePlan` currently takes `(string id, OperationType type)`; use its existing signature rather than the `type:` shorthand above if it differs.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OperationControllerTests" --nologo`

Expected: FAIL to compile, `CS1061: 'OperationController' does not contain a definition for 'TryStart'`.

- [ ] **Step 3: Add the result types**

Create `PenumbraOrganizer.Plugin/Organizer/Operations/OperationStartResult.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Everything the controller needs to begin an operation, produced by the caller's
/// preparation step inside TryStart. </summary>
public sealed record PreparedOperation(OperationPlan Plan, Guid SnapshotId, string BundleDirectory);

/// <summary>
/// Structured admission outcome. Rejection is a value, not an exception, so a caller can decide
/// whether being turned away is exceptional for it - the recovery paths, for instance, treat a
/// rejected refresh scan as ordinary.
/// </summary>
public sealed record OperationStartResult(bool Started, string? RejectionReason)
{
    public static OperationStartResult Ok { get; } = new(true, null);

    public static OperationStartResult Rejected(string reason) => new(false, reason);
}
```

- [ ] **Step 4: Add the gate, the starting flag, and TryStart**

In `OperationController.cs`, add a field beside `_stopRequested` (`:103`):

```csharp
    private readonly Func<string?>? _externalActivityGate;

    // Covers the window between "a caller has been admitted" and "an operation exists". The
    // preparation step reads live mods over IPC, captures a rollback snapshot, and appends it to
    // history - real, failure-prone work during which _active is legitimately null. This is the
    // window Plugin._operationInProgress existed to cover; owning it here is what lets that flag go.
    // Not a second state machine: no transitions, never published directly, only consulted by
    // AdmissionRejectionReason.
    private bool _starting;
```

Extend the constructor (`:107-114`) with an optional trailing parameter, so every existing call site keeps compiling:

```csharp
    public OperationController(
        IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics,
        TimeSpan frameBudget, string operationsRoot, Func<string?>? externalActivityGate = null)
    {
        _adapter = adapter;
        _clock = clock;
        _diagnostics = diagnostics;
        _frameBudget = frameBudget;
        _operationsRoot = operationsRoot;
        _externalActivityGate = externalActivityGate;
    }
```

Add the admission predicate and `TryStart` next to `StartApply`/`StartRestore` (`:116-120`):

```csharp
    /// <summary> Null when a new operation may begin; otherwise the reason it may not. </summary>
    public string? AdmissionRejectionReason()
    {
        if (_starting)
            return "Another organizer operation is already starting.";
        if (_active is { } active && !CanStartNext(active.Journal, active.RequiresRecovery))
            return "Another organizer operation is already in progress.";
        if (_pendingRecovery is not null || _blockedMultiRootGraph is not null)
            return "An interrupted operation requires recovery before anything else can run.";

        return _externalActivityGate?.Invoke();
    }

    /// <summary>
    /// Admission plus failure-atomic startup. The caller's preparation runs INSIDE the admitted
    /// window, so a second caller cannot slip in while a plan is being built, and a preparation
    /// that throws releases the window on the way out rather than leaving the controller locked.
    /// Exceptions from <paramref name="prepare"/> propagate unchanged - callers that already
    /// translate them into user-facing errors keep working untouched.
    /// </summary>
    public OperationStartResult TryStart(OperationType type, Func<PreparedOperation> prepare)
    {
        if (AdmissionRejectionReason() is { } reason)
            return OperationStartResult.Rejected(reason);

        _starting = true;
        try
        {
            var prepared = prepare();
            StartOperation(prepared.Plan, prepared.SnapshotId, prepared.BundleDirectory, type);
            return OperationStartResult.Ok;
        }
        finally
        {
            _starting = false;
        }
    }
```

`StartOperation` is the existing private method behind `StartApply`/`StartRestore`; confirm its exact parameter order before wiring (`:116-120` shows the public wrappers). Leave `StartApply`/`StartRestore` in place — Task 3 migrates their callers and they are still used by tests.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS, 800 + 6 new = 806, including every characterization test from Task 1 unchanged.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationStartResult.cs PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: failure-atomic operation admission in the controller"
```

---

## Task 3: Migrate Plugin's entry points and delete `_operationInProgress`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` — `_operationInProgress` field, `OnFrameworkUpdate`, `CreateBackup`, `DeleteHistorySnapshot`, `StartApplyOperation`, `StartRestoreOperation`, `ResolveContinue`, `ResolveRestorePreviousState`, `CleanUpFolders`, `RollbackFolderCleanup`

**Interfaces:**
- Consumes: `TryStart`, `AdmissionRejectionReason`, `PreparedOperation`, `OperationStartResult` from Task 2.
- Produces: `Plugin.EnsureAdmitted()`; `_operationInProgress` no longer exists.

- [ ] **Step 1: Read every `_operationInProgress` site**

Run: `grep -n "_operationInProgress" PenumbraOrganizer.Plugin/Plugin.cs`

There are 23 after the dead-code removal. Read each enclosing method in full before editing it. Three shapes exist: the guard-and-set pair, the `catch { flag = false; throw; }` wrapper, and the success-clear latch in `OnFrameworkUpdate`.

- [ ] **Step 2: Add the shared admission helper**

Add to `Plugin.cs` next to `RequestCancellation` (`:407` after the dead-code removal — verify the line):

```csharp
    // Throws rather than returning the result, deliberately: MainWindow's handlers already catch
    // InvalidOperationException and surface the message via _lastError, so preserving the exception
    // keeps this a behaviour-preserving refactor. The structured OperationStartResult is available
    // for the UI to adopt later, when changing what the user sees is actually in scope.
    internal void EnsureAdmitted()
    {
        if (OperationController.AdmissionRejectionReason() is { } reason)
            throw new InvalidOperationException(reason);
    }
```

- [ ] **Step 3: Migrate the two operation entry points**

`StartApplyOperation` becomes a `TryStart` call wrapping its existing body verbatim. Do not change any of the preparation logic — move it, unedited, inside the lambda:

```csharp
    internal void StartApplyOperation()
    {
        var result = OperationController.TryStart(OperationType.Apply, () =>
        {
            // Everything that is in this method today, from the Validate() check through the
            // codec saves, unchanged - ending with the bundle directory and snapshot id it
            // already computes.
            //
            // The old "if (_operationInProgress) throw" guard at the top is DELETED: TryStart
            // performed that check before invoking this lambda. The old
            // "_operationInProgress = true" and its catch/finally clearing are DELETED too.
            return new PreparedOperation(plan, snapshot.Id, bundleDirectory);
        });

        if (!result.Started)
            throw new InvalidOperationException(result.RejectionReason!);
    }
```

Apply the identical transformation to `StartRestoreOperation`, with `OperationType.Restore`.

Preserve exactly: the `Validate()` throw, the folder-collision throw and its full message, the snapshot capture, the `AppendSnapshot` call, and every codec save. Their order must not change — the spec's atomicity discussion depends on the history append staying where it is.

- [ ] **Step 4: Migrate the non-operation entry points**

Two different situations here — do not treat them as one.

**`CreateBackup` and `DeleteHistorySnapshot`** have the flag shape today. In each, replace:

```csharp
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            // body
        }
        finally
        {
            _operationInProgress = false;
        }
```

with:

```csharp
        EnsureAdmitted();
        // body, no try/finally
```

The `try/finally` existed only to clear the flag. Any `catch` that does something *other* than clear the flag — logging, wrapping, config writes — must be kept.

**`CleanUpFolders` and `RollbackFolderCleanup` have NO admission guard today** — verified by grep; the 23 flag sites live entirely in the six methods above and `OnFrameworkUpdate`. They have relied solely on the UI's `CanRunFolderCleanup`/`CanRunFolderCleanupRollback` gating. Add `EnsureAdmitted();` as their first statement. This is a guard **addition**, not a replacement: it hardens the direct-call path the spec's admission-everywhere rule requires, and is not user-visible because the UI gate already prevents the click. Do not go looking for a flag shape to strip from these two — there isn't one.

`ResolveContinue` and `ResolveRestorePreviousState` also guard on the flag today. They start recovery successors through the controller's own bypass path, so they use `EnsureAdmitted()` too, but note the bypass: their successor start is *supposed* to proceed despite `RequiresRecovery`. Check `AdmissionRejectionReason` does not reject them — if it does (it will, via the `_pendingRecovery` clause), give them a dedicated guard that checks only `_starting` and `_active`:

```csharp
    // Recovery resolution is admitted despite _pendingRecovery - that is the state it exists to
    // clear. It must still be excluded by an in-flight operation or another starting caller.
    internal void EnsureAdmittedForRecoveryResolution()
    {
        if (OperationController.RecoveryResolutionRejectionReason() is { } reason)
            throw new InvalidOperationException(reason);
    }
```

and add the matching controller method beside `AdmissionRejectionReason`:

```csharp
    /// <summary> Admission for recovery resolution, which is allowed despite pending recovery
    /// because clearing that state is its purpose. Every other exclusion still applies. </summary>
    public string? RecoveryResolutionRejectionReason()
    {
        if (_starting)
            return "Another organizer operation is already starting.";
        if (_active is { } active && !CanStartNext(active.Journal, active.RequiresRecovery))
            return "Another organizer operation is already in progress.";

        return _externalActivityGate?.Invoke();
    }
```

- [ ] **Step 5: Delete the field and the success-clear latch**

Delete the `_operationInProgress` field declaration, and delete the entire success-clear block from `OnFrameworkUpdate` — the `if (_operationInProgress && (...CanStartApply || ...RequiresRecovery))` statement and its long trailing comment. `OnFrameworkUpdate` should be left containing only `OperationController.Update();`.

- [ ] **Step 6: Verify no residue and run the full suite**

Run: `grep -n "_operationInProgress" PenumbraOrganizer.Plugin/Plugin.cs`

Expected: no output.

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS, 806, **including every Task 1 characterization test unchanged**. If a characterization test fails, behaviour changed — stop and raise it rather than editing the test.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs
git commit -m "refactor: retire _operationInProgress for controller-owned admission"
```

---

## Task 4: Completion generation on the snapshot

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs` — `OperationStateSnapshot` (`:10-44`), `PublishState` (`:862-919`), add `_lastTerminalOperationId` and `_completionGeneration`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs` (append)

**Interfaces:**
- Consumes: existing `OperationJournal.IsTerminal`.
- Produces: `OperationStateSnapshot.OperationId` (`Guid?`) and `OperationStateSnapshot.CompletionGeneration` (`long`).

- [ ] **Step 1: Write the failing tests**

Append to `OperationControllerTests.cs`:

```csharp
    [Fact]
    public void CompletionGeneration_IsZeroBeforeAnythingCompletes()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Equal(0, controller.State.CompletionGeneration);
    }

    [Fact]
    public void CompletionGeneration_IncrementsOnceWhenAnOperationReachesTerminal()
    {
        var controller = RunSingleOperationToTerminal();

        Assert.Equal(1, controller.State.CompletionGeneration);
    }

    [Fact]
    public void CompletionGeneration_DoesNotIncrementOnFurtherUpdates()
    {
        // Terminal state is retained, so PublishState keeps being called with the same terminal
        // journal. Inferring novelty from Kind and Stage is what made this fragile before.
        var controller = RunSingleOperationToTerminal();

        controller.Update();
        controller.Update();
        controller.Update();

        Assert.Equal(1, controller.State.CompletionGeneration);
    }

    [Fact]
    public void CompletionGeneration_IncrementsAgainForASecondOperation()
    {
        var controller = RunSingleOperationToTerminal();
        RunAnotherOperationToTerminal(controller);

        Assert.Equal(2, controller.State.CompletionGeneration);
    }

    [Fact]
    public void OperationId_IsPublishedForTheActiveOperation()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var plan = SinglePlan();
        controller.StartApply(plan, Guid.NewGuid(), NewBundleDirectory());

        Assert.Equal(plan.OperationId, controller.State.OperationId);
    }
```

Write `RunAnotherOperationToTerminal(controller)` mirroring `RunSingleOperationToTerminal` but starting a second plan on the same controller.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OperationControllerTests" --nologo`

Expected: FAIL to compile, `CS1061: 'OperationStateSnapshot' does not contain a definition for 'CompletionGeneration'`.

- [ ] **Step 3: Extend the snapshot**

In `OperationController.cs`, add two members to `OperationStateSnapshot`'s parameter list, after `Kind`:

```csharp
    Guid? OperationId,              // the active or most recently concluded operation, for log correlation
    long CompletionGeneration,      // incremented exactly once per operation reaching terminal
```

Update `OperationStateSnapshot.Idle` with `OperationId: null, CompletionGeneration: 0`.

Every `OperationStateSnapshot.Idle with { … }` expression keeps compiling unchanged, because `with` only names the members it changes. The one full `new OperationStateSnapshot(...)` call in `PublishState` needs both new arguments.

- [ ] **Step 4: Increment in exactly one place**

Add fields beside `_starting`:

```csharp
    // Novelty is a comparison, not an inference. _lastTerminalOperationId is what makes the
    // increment fire once per operation even though _active (and therefore the terminal journal)
    // is deliberately retained after completion.
    private Guid? _lastTerminalOperationId;
    private long _completionGeneration;
```

**Corrected during execution — the first draft of this step was wrong.** Placing the guard only inside `PublishState`'s `_active` branch silently misses every Resolution-driven conclusion: `IsTerminal` is `Resolution != OperationResolution.None || TerminalStages.Contains(Stage)`, and each Resolution site clears `_active`/`_pendingRecovery`/`_blockedMultiRootGraph` **before** calling `PublishState`, which then lands in an early-return branch that never touches the counter. Restructuring `PublishState` to inspect "whichever journal is reported" does not fix it either — Keep Current reports nothing at all.

Extract the guard into one private helper:

```csharp
    // Records a journal's first arrival at a terminal state. Must be called wherever a journal
    // becomes terminal - including Resolution-driven conclusions, which clear their in-memory
    // context before PublishState runs and would otherwise never be observed. Keyed on
    // OperationId, so a second call for the same journal is a no-op.
    private void NoteTerminalIfNew(OperationJournal journal)
    {
        if (journal.IsTerminal && _lastTerminalOperationId != journal.OperationId)
        {
            _lastTerminalOperationId = journal.OperationId;
            _completionGeneration++;
        }
    }
```

Call it from **four** places — in each Resolution case, before that site clears its context:

1. `PublishState`'s `_active` branch, immediately before the final `State = new OperationStateSnapshot(...)` assignment.
2. `ResolveKeepCurrent`, pending-recovery branch.
3. `ResolveKeepCurrent`, live-`_active` branch.
4. `TryResolveJournalAsKeepCurrent` — the shared extraction point behind both `AcceptAllAndCloseInterruptedOperations`'s loop and per-root resolution.

`StartRecoverySuccessorOrThrow` resolves the parent journal but must **not** call `NoteTerminalIfNew` on it: that resolution hands off to a successor operation that is already durably running, rather than ending the work. The governing distinction is hand-off versus ending: count a resolution when it ends the work; do not count it when it hands off to a successor. The successor supplies the single increment when it itself reaches a terminal state and runs through `PublishState`.

Pass `OperationId: journal.OperationId` and `CompletionGeneration: _completionGeneration` into the constructor call.

For the three early-return branches (Idle, blocked-multi-root, pending-recovery), pass the counter through so it is never lost:

```csharp
            State = OperationStateSnapshot.Idle with { CompletionGeneration = _completionGeneration, /* existing overrides */ };
```

and for the plain-Idle branch:

```csharp
            State = OperationStateSnapshot.Idle with { CompletionGeneration = _completionGeneration };
```

A generation that went backwards when the controller returned to Idle would make the UI re-consume an old completion.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS, 811. Note `ReRunningUpdateOnATerminalOperation_DoesNotChangeTheSnapshot` from Task 1 still passing is meaningful here: it proves the increment does not fire on repeat updates.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationController.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationControllerTests.cs
git commit -m "feat: publish a completion generation on the operation snapshot"
```

---

## Task 5: One completion consumer in MainWindow

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` — `_applyOperationActive`/`_restoreOperationActive` fields, `Draw` (`:105`), the Apply completion block (`~:850`), the Restore completion block (`~:946`), start handlers, `_historyCache` sites

**Interfaces:**
- Consumes: `OperationStateSnapshot.CompletionGeneration` from Task 4.
- Produces: nothing. Final task.

- [ ] **Step 1: Read the two completion blocks in full**

Read `MainWindow.cs` around the Apply completion block and the Restore completion block. Note exactly what each does beyond clearing its latch — the Apply one calls `RunScan()` and opens the Rediscover Mods reminder popup; the Restore one nulls `_historyCache`. Every one of those consequences must survive, moved into the new consumer.

- [ ] **Step 2: Add the consumer**

Add near the top of `MainWindow`:

```csharp
    private long _lastConsumedCompletion;
```

Add a second field beside it:

```csharp
    // Set by the completion consumer, consumed inside the Review Changes tab's own draw. The
    // OpenPopup call CANNOT move into the consumer: BeginTabBar pushes an ID override, so the
    // matching BeginPopupModal inside the tab resolves the popup name against a different ID stack
    // than Draw()'s root - a root-level OpenPopup would mark a popup open that the tab's
    // BeginPopupModal never sees. The flag carries the decision across that scope boundary.
    private bool _pendingApplyReminder;
```

Add the method, and call it as the **first** statement of `Draw()`, before `DrawRecoveryPanelIfNeeded()`:

```csharp
    // The single place an operation completion turns into UI consequences. Guarded by a generation
    // comparison rather than a per-kind latch, so a terminal snapshot that stays published for many
    // frames is consumed exactly once, and recovery successors are consumed by the same code as
    // ordinary operations rather than needing their own polling.
    //
    // DELIBERATE BEHAVIOUR CHANGE, the one exception to this plan's behaviour-preserving rule: the
    // old latches lived inside tab draw methods that early-return when their tab is not selected,
    // so completion consequences waited until the user visited the right tab. This consumer fires
    // on the frame completion is first observed, whatever tab is visible. Deferred consumption was
    // itself a latent staleness bug (a completed Apply's RunScan would not happen until a tab
    // visit), so immediate consumption is adopted knowingly rather than reproduced.
    private void ConsumeCompletionIfNew()
    {
        var state = _plugin.OperationController.State;
        if (state.CompletionGeneration <= _lastConsumedCompletion)
            return;

        _lastConsumedCompletion = state.CompletionGeneration;

        // Every operation appended a pre-operation snapshot before it started, so any completion
        // means history moved. Invalidating unconditionally is correct and removes the need to
        // reason about which kinds mutate it.
        _historyCache = null;

        switch (state.Kind)
        {
            case Organizer.Operations.OperationType.Apply:
                // Penumbra's own tree is now stale relative to what was just written, and
                // OrganizerState's cached CurrentPath values are stale too - RunScan re-reads both.
                RunScan();
                if (state.SuccessfulTargets > 0)
                    _pendingApplyReminder = true;
                break;

            case Organizer.Operations.OperationType.Restore:
                RunScan(); // matches today's Restore completion block: cache null + RunScan, no popup
                break;
        }
    }
```

Inside the Review Changes tab's draw, where the old completion block sat, leave only:

```csharp
        if (_pendingApplyReminder)
        {
            _pendingApplyReminder = false;
            // In-scope with this tab's BeginPopupModal - see the field's comment for why the
            // consumer cannot call this itself.
            ImGui.OpenPopup("Apply complete - Rediscover Mods reminder");
        }
```

This preserves today's popup semantics exactly: the modal appears when the Review Changes tab is visible, which is where the user who clicked Apply already is.

- [ ] **Step 3: Delete the old latches**

Remove the `_applyOperationActive` and `_restoreOperationActive` fields, both completion blocks that read them, and every assignment (`= true` at the start handlers, `= false` in the completion blocks). Where a start handler used `_applyOperationActive = true` purely to arm completion detection, delete the line — the generation counter needs no arming.

Where either flag gated *rendering* rather than completion — for example `if (_restoreOperationActive)` wrapping the progress display — replace the condition with a direct read of the snapshot, since that is what it was approximating:

```csharp
        if (operationState.Kind == Organizer.Operations.OperationType.Restore && !operationState.CanStartRestore)
```

- [ ] **Step 4: Invalidate history at operation start too**

In `MainWindow`'s Apply and Restore start handlers, after the successful `_plugin.StartApplyOperation()` / `_plugin.StartRestoreOperation(...)` call, add:

```csharp
            _historyCache = null; // the pre-operation snapshot was just appended
```

This closes a pre-existing gap: an operation that ends in `RequiresRecovery` and is resolved via Keep Current clears `_active` without ever publishing a terminal state, so its already-appended snapshot would otherwise sit behind a stale cache.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS, 811, no new warnings. Confirm `grep -n "_applyOperationActive\|_restoreOperationActive" PenumbraOrganizer.Plugin/Windows/MainWindow.cs` returns nothing.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "refactor: consume operation completion exactly once

Replaces two per-kind latches and their Kind+Stage inference with a single
generation-guarded consumer, and folds history invalidation into it."
```

---

## Manual verification (in-game, cannot be automated)

The suite has no game process and `PenumbraOperationsAdapter` has no automated coverage, so these must be checked by hand.

- [ ] Apply a real change. It completes, the mod list refreshes, and the Rediscover Mods reminder appears exactly once.
- [ ] Restore a snapshot. It completes and the History tab shows the new pre-restore entry **without** needing a tab switch.
- [ ] Start an Apply, then while it is running try Restore, Create Backup, Folder Cleanup, and a second Apply. Each is refused with a clear message, and none leaves the plugin stuck afterwards.
- [ ] Force a preparation failure — stage a proposed path that collides with an orphaned folder entry, which throws inside the folder-collision check. Confirm the error appears **and** that Apply is immediately usable again afterwards. This is the failure-atomicity case that `_operationInProgress` handled with a catch at every call site.
- [ ] Force a `RequiresRecovery` state, resolve it with Keep Current, and confirm the History tab reflects the pre-operation snapshot without a manual refresh. This is the pre-existing gap Task 5 Step 4 closes.
- [ ] Leave the plugin window open on the History tab for a minute after an operation completes. Nothing re-fires: no repeated scan, no reopening popup, no flicker.
- [ ] Start an Apply from Review Changes, switch to the Protect tab, and wait for completion there. The mod list refreshes without visiting Review Changes (the deliberate timing change), and on returning to Review Changes the Rediscover Mods reminder appears exactly once.
