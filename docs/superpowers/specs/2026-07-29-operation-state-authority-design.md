# Operation State Authority

Date: 2026-07-29
Status: Draft, not yet reviewed.
Scope: Items 3, 4, 7, and 8 of the code cleanliness remediation brief, combined into one change
because they share a root cause. See `2026-07-29-cleanup-brief-verification.md` for why the other
ten items are not in scope (six already implemented, two overstated, one half-done, and item 1
deliberately sequenced last).
Verified against: `main` @ `06ab30d`.

**This is a behaviour-preserving refactor.** No user-visible behaviour, recovery semantics,
diagnostics output, or persisted format changes.

---

## 1. The root cause

Three independent things currently believe they know whether work is in flight:

| Authority | Location | Cleared by |
|---|---|---|
| `_operationInProgress` | `Plugin.cs:39`, 29 call sites | `catch` on failure; an inference latch (`:129-139`) on success |
| `OperationStateSnapshot.Can*` | `OperationController.cs:906-918` | Journal terminality |
| `_applyOperationActive` / `_restoreOperationActive` | `MainWindow.cs:36-37` | `Kind == X && CanStartX` inference (`:850`, `:946`) |

Item 4 (repeated admission boilerplate) is what the first row costs at every entry point. Item 8
(fragmented completion) is what the third row costs. Item 7 (stale history) is downstream of item 8:
every `_historyCache = null` lives *inside* a completion-latch block, so a missed latch silently
takes cache invalidation with it.

### Why the flag cannot simply be deleted

`_operationInProgress` covers a window the controller genuinely cannot see. `StartApplyOperation`
(`Plugin.cs:445-489`) validates, builds a plan, reads live mods over IPC, captures a rollback
snapshot, and appends it to history — all **before** `OperationController.StartApply` is called.
During that window the controller is legitimately `Idle` while real, failure-prone work is in
flight. The comment at `Plugin.cs:494` records the duplication as deliberate: "Defense-in-depth
alongside `_operationInProgress`, not a replacement for it."

Any design that consolidates on the controller must therefore extend the controller's authority
*backwards* to cover preparation, not just execution.

---

## 2. Admission

### Approaches considered

**A — Reservation object.** `TryReserve(type)` returns a token; the caller builds the plan, then
either promotes it via `StartApply(token, plan, …)` or calls `Release()`. Explicit, but leakable: a
caller that forgets `Release` on an unexpected exception path wedges the controller permanently.
That is precisely the failure the brief says the API must make impossible, so a design whose
correctness depends on caller discipline does not qualify.

**B — Inverted preparation (chosen).** The caller hands the controller a factory; the controller
owns the whole admitted window.

```csharp
public sealed record PreparedOperation(OperationPlan Plan, Guid SnapshotId, string BundleDirectory);

public sealed record OperationStartResult(bool Started, string? RejectionReason)
{
    public static OperationStartResult Ok { get; } = new(true, null);
    public static OperationStartResult Rejected(string reason) => new(false, reason);
}

public OperationStartResult TryStart(OperationType type, Func<PreparedOperation> prepare);
```

The controller checks admission, marks itself starting, invokes `prepare`, and starts the operation.
If `prepare` throws, the starting mark is released in a `finally` and the exception propagates to
the caller with the controller returned to its previous state. Failure-atomicity is structural
rather than a caller obligation, and there is no token to leak.

**C — Move preparation into the controller.** The controller would need `OrganizerState`, `Config`,
history paths, and the folder-collision check. That inverts the brief's own dependency direction and
turns a 920-line class into a much larger one. Rejected.

### The starting mark

One private field, `_starting`, set inside `TryStart` for the duration of `prepare` plus the
handoff, and folded into `_active` on success. It is not a second state machine: it has no
transitions, is never observed by the UI directly, and exists only so `CanStartNext` accounts for
the preparation window. `PublishState` reports it through the existing `Can*` booleans, so
`OperationStateSnapshot`'s shape is unchanged for this half of the design.

### Non-operation activity

Scan and the Search index build are not operations — they have no journal, no plan, and no recovery.
They must still participate in admission, or the same duplication reappears one layer over.

The controller takes an injected external gate at construction:

```csharp
// Returns null when nothing external is blocking, otherwise the reason.
public OperationController(…, Func<string?>? externalActivityGate = null)
```

`Plugin` wires it to the library-work coordinators. The controller never learns what a scan is, and
there is exactly one admission call path for every long-running activity. This replaces Task 8 of
`2026-07-29-non-blocking-library-work.md`, which would otherwise have introduced a third mechanism.

Symmetrically, library work consults `OperationController.CanStart` before starting, so the gate is
bidirectional through one predicate rather than two copies of a rule.

---

## 3. Completion

### Generation counter

`OperationStateSnapshot` gains two fields:

```csharp
Guid? OperationId,             // the active or most recently finished operation
long CompletionGeneration      // incremented exactly once per operation reaching terminal
```

`CompletionGeneration` starts at 0 and is incremented by `PublishState` at the single point where an
operation first becomes terminal. Novelty is then a numeric comparison rather than an inference from
`Kind` and `Stage`, which is what makes re-rendering the same terminal snapshot free of side
effects.

`OperationId` is carried for diagnostics and log correlation; it is not what the UI compares. A Guid
cannot express "newer than", and the UI needs ordering, not identity.

### One consumer

`MainWindow` holds a single `long _lastConsumedCompletion` and one method, called once at the top of
`Draw()` before any tab renders:

```csharp
private void ConsumeCompletionIfNew()
{
    var state = _plugin.OperationController.State;
    if (state.CompletionGeneration <= _lastConsumedCompletion)
        return;

    _lastConsumedCompletion = state.CompletionGeneration;

    // Item 7 collapses to this one line. Every operation that reaches terminal has either
    // appended a pre-operation snapshot or could have; invalidating unconditionally is correct
    // and cheap, and removes the need to reason about which kinds mutate history.
    _historyCache = null;

    switch (state.Kind)
    {
        case OperationType.Apply:   OnApplyCompleted(state);   break;
        case OperationType.Restore: OnRestoreCompleted(state); break;
    }
}
```

Recovery successors reach terminal through the same path and are consumed by the same code, which is
what the brief asks for and what the current per-kind latches cannot do.

### Deletions

- `MainWindow._applyOperationActive`, `_restoreOperationActive` (`:36-37`) and the two inference
  blocks (`:850-853`, `:946-949`).
- `Plugin._operationInProgress` (`:39`) and its 29 call sites, including the success-clear latch in
  `OnFrameworkUpdate` (`:129-139`).
- The now-redundant `try/catch { flag = false; throw; }` wrappers at eight entry points.

Six of the 29 flag sites disappear for free when the dead `ApplyChanges`/`Restore` methods are
removed, so that removal is a prerequisite step rather than a parallel one.

---

## 4. What does not change

- `OperationJournal`, bundle layout, and every persisted format.
- Recovery classification, the recovery graph, and all three resolution paths. Item 9 of the brief
  is already satisfied; `StartRecoverySuccessorOrThrow` keeps its existing `bypassPendingRecoveryLockout`
  behaviour and simply routes through `TryStart`'s admission check for the non-bypassed case.
- `IPenumbraOperations`, `IDiagnosticsSink`, `LiveModSnapshot`, `ContinuationPlanResult`.
- Every user-visible string, dialog, and progress display.

---

## 5. Testing

Characterization tests first, per the brief's refactoring rules — these must pass before and after:

- A second operation cannot start while one is active.
- A normal operation cannot start while recovery is required.
- An approved recovery successor can start despite the recovery lockout.
- Terminal state is reached for each of Apply, Restore, and both recovery successors.

New tests:

- **Failure atomicity:** a `prepare` factory that throws leaves `CanStart` true, publishes no
  operation, and propagates the original exception unchanged.
- **Admission during preparation:** a second `TryStart` invoked from *inside* a `prepare` factory is
  rejected. This is the window `_operationInProgress` existed to cover, and the test pins that the
  replacement actually covers it.
- **External gate:** a gate returning a reason rejects `TryStart` with that reason; a null-returning
  gate admits.
- **Completion generation:** increments exactly once per operation; re-publishing the same terminal
  snapshot does not increment; consuming twice from the same generation dispatches once.
- **History invalidation:** the cache is invalidated exactly once per completion, and a failed
  operation does not fabricate a history entry.
- **Recovery successors** raise the generation through the same path as ordinary operations.

---

## 6. Sequencing

1. Remove the dead `ApplyChanges`/`Restore` methods (already Task 9 of the library-work plan).
2. Add characterization tests.
3. `TryStart` + `_starting` + external gate; migrate the eight entry points; delete
   `_operationInProgress`.
4. `CompletionGeneration` + `OperationId` on the snapshot; single consumer; delete the UI latches
   and fold history invalidation in.
5. Rewrite the library-work plan's Task 8 to wire the external gate instead of adding
   `ActivityAdmission`.

Step 5 is why this spec should land before the library-work plan executes: the two designs overlap
at exactly one type, and building both means deleting one of them a week later.
