# Operation Controller, Frame-Budgeted Execution, and Recovery UI

Date: 2026-07-22
Status: Design approved, implementation-ready (revision 3), not yet implemented
Builds on: `docs/superpowers/specs/2026-07-21-incremental-operations-design.md` (original design)
and the merged persistence foundations (`docs/superpowers/plans/2026-07-21-operation-persistence-foundations.md`)

## 1. Scope and relationship to prior work

The persistence foundations plan built a pure, Dalamud-free data layer: `AtomicFile`,
`OperationPlan`/`OperationPlanCodec`, `OperationJournal`/`OperationJournalCodec`,
`RecoveryClassifier`. This design covers everything deferred from that plan: the operation
controller that actually drives Apply/Restore incrementally, frame-budgeted execution against
Dalamud's framework thread, verification settlement, startup crash recovery, recovery resolutions,
and the `MainWindow`/diagnostics wiring that makes all of it visible and operable.

**This design supersedes parts of the original 2026-07-21 design doc and the persistence
foundations' shipped schema.** Where they conflict, this document is authoritative —
`OperationPlan`'s shape (§3), `OperationJournal`'s shape (§4), and `RecoveryClassifier`'s state set
(§8) all change from what's on `main`, because building this controller surfaced real gaps in the
v1 model.

## 2. File structure and dependency direction

```
MainWindow (UI)
    │  StartApply() / StartRestore() / ResolveRecovery(...) / RequestCancel()
    │  reads: OperationStateSnapshot (compact, frequent) + RecoveryDialogSnapshot (large, rare)
    ▼
OperationController                    ◄── Plugin.Framework.Update calls controller.Update()
    │  owns: state machine, exclusivity, journal/checkpoint transitions,
    │        recovery-resolution orchestration, publishing both snapshots,
    │        servicing deferred startup recovery classification
    │  delegates bounded work per Update() to:
    ▼
PathMutationOperation (one type, configured by OperationType — see §14)
    │  owns: current execution step, phase, frame-budgeted stepping,
    │        settlement/retry state, durable per-step results
    ▼
IPenumbraOperations (narrow adapter interface)
    ▼
PenumbraOperationsAdapter (implements IPenumbraOperations using existing IPC subscribers)
    ▼
Penumbra IPC
```

`OperationController` depends only on `IPenumbraOperations`, `IElapsedTimeSource`, and the
persistence codecs. It never depends on `MainWindow` or the concrete `Plugin` class.
`Plugin.cs` is the composition root: constructs the adapter, the clock, the controller; wires
`Framework.Update += controller.Update`; unsubscribes on `Dispose()`. It does not implement
`IPenumbraOperations` itself — that stays a dedicated adapter class so unrelated plugin
responsibilities can't leak into the operations layer.

New files under `Organizer/Operations/`:

- `OperationController.cs`
- `PathMutationOperation.cs` (single type — see §14 for why this replaced separate Apply/Restore types)
- `IPenumbraOperations.cs`, `PenumbraOperationsAdapter.cs`
- `IElapsedTimeSource.cs`, `StopwatchElapsedTimeSource.cs`
- `VerificationSettlement.cs`
- `RecoveryAssessment.cs`
- `StepResultLog.cs` (append-only durable per-step results — §5a)
- `DiagnosticsLog.cs` (append-only durable diagnostic events — §10)
- `OperationStorage.cs` (multi-operation directory layout, discovery, retention — §4a)

Modified: `OperationPlan.cs`, `OperationJournal.cs`, `RecoveryClassifier.cs`, `ApplyPlanner.cs`
(schema v2 — see §3, §4, §8).

## 3. OperationPlan v2: ExecutionSteps and RecoveryTargets

**The v1 shape (single `Items` list of `OperationPlanItem`) is insufficient** and is being replaced,
not extended. `ApplyPlanner.OrderMovesForApply` can legitimately emit two steps for the same
identifier — a cycle-breaking temporary hop, then the real target — to resolve a swap/rotation
between mods without deadlocking on Penumbra's shared path-uniqueness namespace. A single
per-identifier item can't represent that, and collapsing to "final target only" makes a crash that
lands a mod at its temporary path misclassify as `AtNeither` during recovery — exactly the case
cycle-breaking exists for.

**Every step also belongs to a dependency group**, not only cycle steps. `ApplyPlanner`'s own doc
comment explains why: chains are resolved by processing in reverse specifically so each target is
vacated before something moves into it — which means a chain member's failure blocks every
subsequent chain member the same way a cycle's temp-hop failure blocks the rest of its cycle. So
`GroupId` is not cycle-specific; every step, chain or cycle, belongs to exactly one group (a
single-step chain gets its own trivial group), and `OrderMovesForApply` already computes this
partition internally — it just needs to expose it.

```csharp
internal enum OperationStepKind { FinalMove, CycleBreakingTemporaryMove }

internal sealed record OperationExecutionStep(
    int StepIndex, string Identifier, string TargetRawPath, OperationStepKind Kind, int GroupId);

internal sealed record OperationRecoveryTarget(
    string Identifier, string SnapshotRawPath, string FinalRawPath, string ModName);

internal sealed record OperationPlan(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OperationExecutionStep> ExecutionSteps,
    IReadOnlyList<OperationRecoveryTarget> RecoveryTargets,
    string IntegrityHash);
```

`ExecutionSteps` is exactly what `Advance()` iterates, duplicates (per identifier) allowed.
`RecoveryTargets` is one entry per identifier — the desired semantic outcome, carrying both the
original snapshot path and the final target so recovery never has to infer the snapshot side from
execution steps.

**Validated invariants**, checked in `OperationPlan.Create(...)` before the integrity hash is
computed (construction throws if any fail — a plan must never be persisted in a state it would
reject on reload, same principle as v1):

- Every execution-step identifier has exactly one recovery target.
- Every recovery target has at least one execution step.
- The final execution step for each identifier targets its `FinalRawPath`.
- A `CycleBreakingTemporaryMove` step is never the last step for its identifier.
- Recovery target identifiers are unique.
- Execution-step indices are contiguous, starting at 0, strictly ordered.
- Every `GroupId` is non-negative and assigned deterministically (0-based, in the order
  `OrderMovesForApply` first emits each group) — not arbitrary, so the same input plan always
  produces the same `GroupId` assignment.
- **All steps sharing a `GroupId` occupy one contiguous `StepIndex` range.** This is required, not
  incidental — §5's group-cascade behavior depends on being able to skip "the rest of this group" as
  a single contiguous slice of `ExecutionSteps` without scanning the whole remaining list, and
  without risking a skip that jumps over an unprocessed step belonging to a different group (see §5's
  cursor-safety rule).
- A single identifier never appears in more than one `GroupId`.
- Every recovery target maps to exactly one `GroupId` (derivable from its identifier's steps, but
  validated explicitly rather than assumed).
- A `CycleBreakingTemporaryMove` step and its identifier's corresponding `FinalMove` step always
  share the same `GroupId`.

Chain, swap, and rotation test cases (already implied by `ApplyPlanner`'s existing test coverage)
must each assert group membership explicitly, not just step order — a passing order-only test could
still hide a `GroupId` that isn't actually contiguous.

**Integrity hash — fully canonical, length-prefixed, not delimiter-based.** A `\0` delimiter is only
safe if every string field is guaranteed never to contain `\0`; that's an invariant the hash would
be silently trusting rather than enforcing. Length-prefixing removes the dependency on that
assumption entirely and, more importantly, the v1 hash (even with a delimiter) only covered
`Identifier` and normalized target path — it never bound `OperationStepKind`, meaning a step could
flip from `CycleBreakingTemporaryMove` to `FinalMove` with identical identifier and path and the
hash would not change, even though recovery behavior for that step changes materially.

Canonical representation, one `<byte-length>:<utf8-bytes>` field per line, in this exact order:

```text
SchemaVersion
OperationType
ExecutionSteps.Count
  for each step, in StepIndex order:
    StepIndex (as decimal string)
    Identifier
    TargetRawPath
    Kind (enum name, e.g. "FinalMove" — stable text, not the numeric ordinal)
    GroupId (as decimal string)
RecoveryTargets.Count
  for each target, ordered by Identifier (ordinal):
    Identifier
    SnapshotRawPath
    FinalRawPath
    ModName
```

`OperationId` and `CreatedAt` are **deliberately excluded** — the hash binds executable content and
intended semantics, not the plan's own identity or creation time. Stating this explicitly rather
than leaving it implicit, since a reader could otherwise reasonably expect a plan's identity to be
self-verifying too; it isn't, by design — `OperationId` is generated once and never needs to prove
itself against its own content.

`TargetRawPath`/`SnapshotRawPath`/`FinalRawPath` are hashed via `PenumbraPathSemantics.Normalize`
(unchanged from v1's reasoning) before length-prefixing, so a Penumbra reload reshuffling a
duplicate-marker suffix still produces an identical hash.

**Construction** (shared by Apply and Restore — both already produce a `IReadOnlyList<ModMove>`
before hitting `ApplyPlanner.OrderMovesForApply`, confirmed identical for both call sites):

```csharp
var orderedSteps = ApplyPlanner.OrderMovesForApply(moves); // ApplyStep needs IsTemporary + GroupId added
var executionSteps = orderedSteps.Select((step, index) => new OperationExecutionStep(
    index, step.Identifier, step.TargetPath,
    step.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
    step.GroupId)).ToList();
var recoveryTargets = moves.Select(m => new OperationRecoveryTarget(
    m.Identifier, m.CurrentPath, m.TargetPath, namesByIdentifier[m.Identifier])).ToList();
var plan = OperationPlan.Create(operationType, executionSteps, recoveryTargets);
```

`ApplyPlanner.ApplyStep`/`OrderMovesForApply` need both `IsTemporary` and `GroupId` added at the
source — do not infer either later from path-naming conventions or step adjacency.

## 4. OperationJournal v2

```csharp
internal enum OperationStage
{
    Preparing, Prepared, Mutating, Refreshing, Verifying,
    Completed, CompletedWithItemFailures,
    FailedBeforeMutation, FailedPartiallyApplied,
    Cancelled,
}

internal enum OperationResolution { None, AcceptedCurrentState, ContinuedByNewOperation, RestoredByNewOperation }

internal sealed record OperationJournal(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    OperationStage Stage,
    OperationResolution Resolution,
    Guid? SuccessorOperationId,       // set only when Resolution is Continued/RestoredByNewOperation
    bool CancellationRequested,       // user intent, independent of whether Stage could honor it — see §5a precedence rule
    DateTimeOffset StartedAt,
    int TotalSteps,
    int ProcessedStepCount,           // renamed from CompletedStepCount — see rationale below
    string? LastCompletedIdentifier,  // diagnostic/UI only, never drives resume logic
    Guid SnapshotId,
    Guid PlanId,
    string TargetHash,
    Guid? RecoveryOfOperationId,
    DateTimeOffset UpdatedAt)
{
    public bool IsTerminal =>
        Resolution != OperationResolution.None ||
        Stage is OperationStage.Completed or OperationStage.CompletedWithItemFailures
            or OperationStage.FailedBeforeMutation or OperationStage.FailedPartiallyApplied
            or OperationStage.Cancelled;
}
```

**`CompletedStepCount` is renamed `ProcessedStepCount` because "completed" was never accurate.** A
step can be skipped without any IPC call ever happening — if step 10 (a temp hop) fails, step 12
(the same identifier's later final move, or any other step in the same `GroupId`) is never attempted
at all, yet the cursor must still advance past it or execution can never reach step 13 onward.
`ProcessedStepCount` means *the number of execution steps whose disposition has been durably
decided* — succeeded, failed, or skipped are all "processed." The durable disposition record (§5a)
must be written **before** `ProcessedStepCount` advances past that step's index, never after —
otherwise a crash between the two leaves a step that looks processed with no record explaining why.

**`Stage` and `Resolution` are deliberately separate axes.** `Stage` records what execution actually
did — it can stay frozen at a non-terminal value like `Mutating` forever if the process died there,
and that's the truth, not a bug. `Resolution` records a later human/system decision applied on top
of that frozen record. `IsTerminal` is `Resolution != None` OR `Stage` reached a terminal execution
outcome — either is sufficient, independently. This lets a superseded journal keep an honest,
unmodified historical `Stage` while still being correctly excluded from future recovery prompts via
`Resolution`.

**`CancellationRequested` is separate from both `Stage` and `Resolution`.** It records user intent at
the moment it was expressed, independent of whether the operation could actually honor it as a clean
`Cancelled` outcome — see §5a's precedence rule for why this split matters.

**Schema v1 → v2 policy: no migration.** `OperationPlan`/`OperationJournal` v1 merged to `main` in
the immediately prior session and has never shipped to an end user — this plugin has no public
release yet. `TryLoad` therefore rejects a v1 (or any non-current) `SchemaVersion` exactly like any
other invalid-schema case, with no decoder and no silent best-effort recovery. This is safe only
*because* v1 never left development; a future schema bump, once the plugin has real users with
real on-disk journals, would need an actual migration policy decided at that time, not this one
copied forward by default.

**Persisted enums are strings** (`JsonStringEnumConverter`, per the fix already applied to v1) —
carried forward unchanged, this was already the right call and stays.

## 4a. Storage layout and multi-operation recovery discovery

A single `activeJournalPath` cannot represent the state that Continue/Restore resolutions can
legitimately produce: a crash between persisting a child journal and terminalizing its parent
(§9) leaves **two** non-terminal journals simultaneously. The layout must support discovering and
relating an arbitrary small number of them, not just reading one fixed path.

```text
ConfigDirectory/operations/
  active/
    <operationId>/
      journal.json
      plan.json
      snapshot.json
      results.jsonl        (§5a)
  completed/
    <operationId>/
      journal.json
      plan.json
      snapshot.json
      results.jsonl
  diagnostics.jsonl         (§10 — global, not per-operation)
```

One directory per operation ID makes duplicate-ID collisions structurally impossible (the
filesystem itself is the uniqueness constraint) — that specific failure mode doesn't need runtime
detection.

**Startup cleanup pass** (before recovery discovery runs): any `active/<id>/journal.json` that loads
and is already `IsTerminal` is relocated to `completed/<id>/` — this is normal self-healing
(a terminalization that landed but wasn't yet moved by whatever last touched it), not a recovery
condition.

**Recovery discovery algorithm**, run once at startup after cleanup:

1. Enumerate all `active/*/journal.json`. `TryLoad` each independently; a journal that fails to load
   or fails its integrity/schema check is logged and excluded from the graph, not treated as fatal
   to plugin startup — it becomes an orphaned artifact for a human to investigate via diagnostics,
   not a crash.
2. Build the directed graph among the remaining (all confirmed non-terminal, post-cleanup)
   journals: edge child → parent via `RecoveryOfOperationId`.
3. **Cycle detection**: by construction this graph should be acyclic (a journal can only reference an
   *earlier* operation as its parent), but validate defensively — a cycle found here means the data
   is structurally inconsistent. Mark every journal in the cycle as requiring manual resolution
   (View Details only; Continue is blocked for all of them) rather than guessing which is
   authoritative.
4. For each connected component (chain of supersession), the leaf (no non-terminal child points at
   it) is operationally authoritative. Ancestors are retained for history/diagnostics but are not
   surfaced as separate recovery prompts — this is the "newer child supersedes even if the parent's
   own terminalization was interrupted" rule from §9, now given a concrete discovery mechanism.
5. **Multiple disconnected leaves** (more than one connected component with no relationship to each
   other) should not occur under normal operation, since starting a new operation is already blocked
   while `RequiresRecovery` is true — but if it's found anyway (manual file tampering, a future bug),
   it's a real ambiguous condition: surface **all** of them as a list in the recovery UI, and require
   resolving one before any component's dialog is shown as the "current" one.

**Retention — fail-safe rules**, replacing the earlier "30 days or 50-operation cap" one-liner with
concrete, defensive behavior:

- Never delete an artifact when reference analysis is inconclusive (a journal that failed to load
  during discovery means its snapshot/plan references can't be verified — leave them alone).
- The 50-operation cap applies per **complete operation bundle** (journal + plan + snapshot +
  results, identified by `operationId`), not independently per file type — a plan orphaned from its
  journal by an inconsistent per-file cap is worse than an extra retained bundle.
- A terminal operation's bundle is deleted only when it is **older than 30 days** and **not the
  newest 50 terminal bundles**, whichever is more permissive — restated precisely:
  *delete a terminal bundle when it is older than 30 days, unless it's within the newest 50 terminal
  bundles or is referenced (directly or transitively via `RecoveryOfOperationId`) by a retained
  bundle.*
- The parent of a retained (non-terminal, or referenced-by-a-retained-child) bundle is retained
  regardless of the parent's own age or the 50-cap — a chain's history must not be severed in the
  middle.
- Recovery discovery (above) always runs before any deletion.
- Deletion failures (`IOException`, `UnauthorizedAccessException`) are caught per-bundle and logged;
  one undeletable bundle must not prevent plugin startup or block cleanup of the rest.

## 5. Frame-budgeted execution (`Advance`)

**`IDiagnosticsSink`**: a small logging abstraction (wrapping `IPluginLog`/the existing
`PluginLogAdapter`), backed durably by `DiagnosticsLog` (§10) — introduced in this plan. Conceptually
needs `RecordSlowCall(identifier, duration)` and `RecordSlowLiveSnapshot(duration)` at minimum;
writing to the sink must never itself be able to fail the operation (§10).

**`SlowCallThreshold`**: provisional constant, `TimeSpan.FromMilliseconds(50)` — picked the same way
`CheckpointPolicy`'s 10-item/500ms and the verification settlement's 10-attempt/100ms were picked:
a defensible starting value with no profiling data yet, cheap to retune once real telemetry exists.

```csharp
internal enum TargetMutationStatus
{
    NotAttempted, FinalStepSucceeded, FinalStepFailed, SkippedAfterEarlierFailure, AlreadySatisfied,
}
```

`PathMutationOperation.Advance(TimeSpan budget, bool stopRequested)`:

1. Restart the elapsed-time measurement at entry.
2. Loop: always attempt at least one eligible step. Before starting each *subsequent* step, check
   whether the budget is exhausted **or** `stopRequested` is true — if so, stop; the currently
   in-flight step (if any) always finishes, one `SetModPath` call is never split across two
   `Advance` calls.
3. Record each step's IPC result and its call duration; a duration past the slow-call threshold
   emits a diagnostic event.
4. Determine disposition and **append a durable `OperationStepResult` (§5a) before advancing
   `ProcessedStepCount`** past that step's index.
5. **Group-cascade on failure — cursor safety.** `ProcessedStepCount` is a contiguous-prefix cursor:
   it can never validly skip past a later step without every step before it already being processed,
   including steps belonging to other groups. This is exactly why §3 requires every group's steps to
   occupy one contiguous `StepIndex` range — that requirement is what makes cascading safe. When a
   step's IPC call itself fails (any non-`Success` result, not a pre-emptive skip): append a
   `SkippedAfterEarlierFailure` result for every remaining step in that failed step's contiguous
   `GroupId` range, then advance `ProcessedStepCount` to the index immediately following that range
   — never further, and never in a way that could leave a gap. Because the range is contiguous by
   construction, "skip to the end of this group" and "advance the cursor past everything just
   skipped" are the same operation; there is no interleaving case to handle. This is what prevents a
   single failed temp hop from producing a series of misleading, seemingly-unrelated
   `PathRenameFailed` reports for every other identifier whose move depended on that hop's target
   being vacated.
6. Checkpoint the journal (`CheckpointPolicy.IsDue`, unchanged from v1) after each step or cascade
   batch, using `ProcessedStepCount`, not identifier.

**IPC failure continuation policy** — item-level failures never stop the batch (beyond their own
`GroupId` cascade); only operation-integrity conditions stop everything:

| Result | Action |
|---|---|
| `Success` | record, continue |
| `ModMissing` | record item failure, cascade group, continue |
| `InvalidArgument` | record item failure, cascade group, continue |
| `PathRenameFailed` | record item failure, cascade group, continue |
| Unexpected exception, IPC boundary still usable | record item failure, cascade group, continue |
| IPC unavailable/disposed | stop — operation-integrity failure |
| Journal/checkpoint write failure | stop — operation-integrity failure |
| Duplicate live identifiers detected | stop — operation-integrity failure |
| Plan/identifier mapping corrupt | stop — operation-integrity failure |

**Exception boundaries**: an outer boundary around the entire `Advance()` call (in
`OperationController.Update()`) catches anything escaping and fails the operation safely rather than
letting it escape the framework update callback; an inner boundary around each individual step
decides, via `CanSafelyContinue(exception)`, whether to record an item failure (and cascade its
group) or stop the operation entirely.

**Honest responsiveness claim** (for the design doc and any user-facing documentation — do not
overstate this): *this eliminates the known whole-library blocking loop and keeps the game
responsive under normal per-item costs; it cannot bound a single pathological IPC call, since the
budget is checked between calls, not during one.* Per-frame runtime is approximately the budget plus
the duration of whatever call was already in flight when the budget was exceeded.

## 5a. Durable per-step results and cancellation precedence

**`ProcessedStepCount` alone cannot explain a crash.** "173 of 401 steps processed" says where
execution stopped, not what happened within those 173 — which succeeded, which failed, which were
cascade-skipped. Without a durable per-step record, neither an accurate terminal report nor
verification's "which targets are expected to settle" (§6) can be reconstructed after a restart.

```csharp
internal enum OperationStepDisposition { Succeeded, Failed, SkippedAfterEarlierFailure, SkippedAlreadySatisfied }

internal sealed record OperationStepResult(
    int StepIndex,
    string Identifier,
    OperationStepDisposition Disposition,
    string? IpcResultName,           // e.g. "PathRenameFailed" — null for skipped dispositions
    string? FailureDetail,
    DateTimeOffset RecordedAt,
    long? DurationMilliseconds);     // null for skipped dispositions
```

**`StepResultLog`**: append-only, one JSON object per line (`results.jsonl`), not a
`AtomicFile.CreateOrReplace`-style whole-file rewrite — rewriting a growing file on every checkpoint
gets more expensive as the operation progresses, which is exactly the cost this whole design exists
to avoid. Each append is a single durable write (open in append mode, write one line, flush). A
truncated final line (the write in progress when a crash happened) is tolerated on read: parse
line-by-line, discard an unparseable trailing line, log a diagnostic — never fail the whole read
over one incomplete record. Ordering guarantee: the result line for a step is appended *before* the
journal checkpoint advances `ProcessedStepCount` past it (§4's requirement), so a crash between the
two only ever leaves the journal slightly behind a result that's already durably recorded — never
the reverse.

**Reconciliation rule at startup — the journal, not the result log, is the authority on committed
progress.** The append-before-checkpoint ordering above means `results.jsonl` can legitimately be
*ahead* of `journal.ProcessedStepCount` after a crash (a result was appended, then the process died
before the journal checkpoint that would have advanced past it). Startup reconciliation must resolve
this deterministically:

- Parse every valid line in `results.jsonl`.
- Require **exactly one** valid result for every `StepIndex < ProcessedStepCount`. A gap (a missing
  result below the journal's cursor) means the journal claims progress the result log can't
  substantiate — this is not recoverable by inference; it makes the operation `Indeterminate` and
  routes to the normal recovery flow (§9), it does not get silently patched over.
- A duplicate result for the same `StepIndex` is rejected the same way — evidence of a corrupted or
  double-written log, not something to pick one of arbitrarily.
- A result whose `Identifier` doesn't match the plan's step at that `StepIndex` is rejected the same
  way — evidence the result log and plan have diverged.
- Results with `StepIndex >= ProcessedStepCount` are **expected and normal** (the ahead-of-journal
  case described above) — they're preserved for diagnostics but never used to advance
  `ProcessedStepCount` on their own. **The journal's `ProcessedStepCount` is never auto-advanced from
  extra result lines.** Promoting an appended-but-uncheckpointed write into committed progress would
  mean a result that was written but never confirmed by the journal's own checkpoint gets treated as
  confirmed anyway — exactly the kind of silent authority-inversion this whole reconciliation rule
  exists to prevent. If execution resumes, it resumes from `ProcessedStepCount` as the journal last
  recorded it, re-processing (or re-cascading) anything at or after that index regardless of what the
  result log shows past that point.

**Cancellation vs. verification trust — precedence rule.** The original draft said `Stage = Cancelled`
regardless of what verification finds; that's wrong when verification itself returns
`RecoveryRequired` (duplicate identifiers, provider unavailable, unreadable live state). Marking the
journal terminal as `Cancelled` in that situation would assert the interrupted state is understood
when it demonstrably isn't. The corrected rule:

- Cancellation requested, and verification reaches `Settled` or `TimedOut` (both are trustworthy —
  the live state was actually readable) → terminal `Stage = Cancelled`.
- Cancellation requested, but verification reaches `RecoveryRequired` (live state could not be
  trusted) → `Stage` stays at its last non-terminal value (`Verifying`), journal remains
  non-terminal, `CancellationRequested = true` is already persisted from when the user pressed Stop,
  and the operation enters the normal startup-style recovery flow (§9) — with the recovery dialog
  additionally showing that the operation was being cancelled when interrupted, as context for the
  user's decision.

**Cancellation is a user intent; recoverability is a state-integrity fact — the latter always takes
precedence over asserting the former as a clean terminal outcome.**

## 5b. Refreshing

Left fully unspecified in the prior draft — a material gap, since an unbounded synchronous refresh
would simply relocate the freeze from mutation to this stage instead of eliminating it.

```csharp
internal enum RefreshStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidState }
internal sealed record RefreshResult(RefreshStatus Status);

// IPenumbraOperations:
RefreshResult RequestPostMutationRefresh();
```

Called **once** after `Mutating` concludes (whether by completion, item-failure exhaustion, an
operation-integrity stop, or user cancellation — `Refreshing` always runs if any mutation was
attempted, so the subsequent `Verifying` stage has a chance at accurate live state). Measured with
the same slow-call instrumentation as every other adapter call (§5's `SlowCallThreshold`).

| Result | Action |
|---|---|
| `Success` | proceed to `Verifying` |
| `TemporarilyUnavailable` | retry, bounded — reuses the same attempt-count/interval shape as verification settlement (§6), not a separate unbounded loop |
| `ProviderUnavailable` | `RecoveryRequired` — cannot trust anything `Verifying` would read afterward |
| `InvalidState` | `RecoveryRequired` |
| Unexpected exception | `RecoveryRequired` if the adapter reports itself unusable afterward, otherwise a bounded retry same as `TemporarilyUnavailable` |

If refresh never succeeds within its bound, the operation enters `RecoveryRequired` exactly like an
untrustworthy verification read does (§5a) — refresh failure and verification-read failure are the
same *kind* of problem (can't trust what Penumbra reports right now) and get the same treatment.

## 6. Verification settlement

Budgeted the same way as mutation — one read-and-compare attempt per `Update()` tick, gated by a
retry interval, never a blocking synchronous wait. The result type is a record, not a bare enum, so
the caller (and the persisted diagnostic trail) can see *which* identifiers didn't settle and *why*
a `RecoveryRequired` verdict was reached, rather than a single opaque value:

```csharp
internal enum LiveModReadStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidData }
internal sealed record LiveModReadResult(LiveModReadStatus Status, LiveModSnapshot? Snapshot);

internal enum VerificationStatus { Waiting, Settled, TimedOut, RecoveryRequired }
internal enum RecoveryRequiredReason { DuplicateIdentifiers, ProviderUnavailable, InvalidData, TransientReadExhausted }

internal sealed record VerificationResult(
    VerificationStatus Status,
    IReadOnlyList<string> UnsettledIdentifiers,   // populated only for TimedOut
    RecoveryRequiredReason? Reason);               // populated only for RecoveryRequired

internal sealed class VerificationSettlement
{
    private int _attemptsUsed;
    private long _lastAttemptTimestamp;
    private const int MaxAttempts = 10; // "attempts", not "retries" — avoids an off-by-one
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    public VerificationResult Advance(
        IPenumbraOperations adapter, IElapsedTimeSource clock,
        IReadOnlyList<OperationRecoveryTarget> targets,
        IReadOnlyDictionary<string, TargetMutationStatus> mutationStatuses,
        IDiagnosticsSink diagnostics)
    {
        if (_attemptsUsed > 0 && clock.GetElapsedTime(_lastAttemptTimestamp) < RetryInterval)
            return new VerificationResult(VerificationStatus.Waiting, [], null);

        _lastAttemptTimestamp = clock.GetTimestamp();
        _attemptsUsed++;

        var readStart = clock.GetTimestamp();
        var read = adapter.GetLiveMods();
        var readDuration = clock.GetElapsedTime(readStart);
        if (readDuration >= SlowCallThreshold) diagnostics.RecordSlowLiveSnapshot(readDuration);

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

        var expected = targets.Where(t => mutationStatuses[t.Identifier]
            is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);
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

Only targets whose `TargetMutationStatus` is `FinalStepSucceeded` or `AlreadySatisfied` are expected
to settle — an item already recorded as failed during Mutating isn't waited on. `IsSettled` checks
the live path directly against a real read; there is no shortcut that infers settlement from
snapshot/target equivalence without inspecting live state (that would false-positive for a mod that's
missing entirely).

**Outcome mapping**:

| Condition | Outcome |
|---|---|
| All required targets settle, no item failures | `Completed` |
| All required targets settle, some item failures during Mutating | `CompletedWithItemFailures` |
| Some uniquely-identifiable targets time out | `CompletedWithItemFailures` (reason: `VerificationTimeout`, carried per-identifier from `UnsettledIdentifiers`) |
| `RecoveryRequired` (any `RecoveryRequiredReason`) | journal stays non-terminal, enters recovery flow — see §5a for the cancellation interaction |
| Journal/plan durability fails during verification | `FailedPartiallyApplied` or `FailedBeforeMutation`, per whether mutation occurred |

A verification timeout is item-level (the operation still concludes with a clear report); an
inability to trust the live-state read at all is operation-level (the journal is left recoverable
rather than asserting a terminal outcome it can't actually support).

**Persistence discipline**: only `DateTimeOffset.UtcNow` wall-clock values go in the journal.
`Stopwatch`/`GetTimestamp()` values are process-relative and meaningless after a restart — they stay
in-process only, used solely for in-process interval decisions. Checkpoint on entering `Verifying`,
on each actual attempt (not on every `Waiting` tick that didn't yet reach the retry interval), and on
the terminal result.

## 7. Non-reentrancy and conflicting-control lockout

`OperationController` holds a single `_activeOperation` field; `StartApply`/`StartRestore` reject if
it's already set. **Scan, Index, Folder Cleanup, and Folder Cleanup Rollback stay outside the
controller in this plan** — their own incremental treatment is explicitly deferred (original design
§12/§16, pending profiling) — but each gains a guard so they can't run concurrently with an active
Apply/Restore: `if (!snapshot.CanScan) return;` (non-throwing — see §7a).

**"Rollback" and "Restore" are confirmed to be two unrelated features** (`RollbackFolderCleanup`
undoes `organization.json` folder structure from its own backup file via `FolderCleanupExecutor`;
`Restore` moves mod paths via `SetModPath`/`RollbackHistory`) — no naming collision to resolve, no
shared controller ownership needed; Folder Cleanup Rollback gets its own `CanRunFolderCleanupRollback`
guard, not `StartRestore`.

**`Update()` has work even when no operation is active.** The controller tracks two independent
things — `_activeOperation` (an in-progress Apply/Restore) and `_pendingRecoveryClassification` (the
startup deferred-classification window from §9) — and services both from `Update()`. An early return
on `_activeOperation is null` would silently stall recovery classification forever on a session
where no new operation is ever started:

```csharp
public void Update()
{
    if (_pendingRecoveryClassification is not null)
        AdvancePendingRecoveryClassification(); // attempts RecoveryAssessment once IPC looks ready

    if (_activeOperation is null) return;
    _activeOperation.Advance(_frameBudget, _stopRequested);
    if (_activeOperation.IsDone) TransitionFromCompletedOperation(_activeOperation.Result);
}
```

### 7a. Non-throwing UI guards

Every `MainWindow` entry point checks a capability flag and returns early — it does not throw.
Throwing for an expected user action (a stale button click, or a future code path) risks an
unnecessary Dalamud error boundary:

```csharp
private void OnApplyClicked()
{
    if (!_plugin.OperationController.State.CanStartApply) return; // button should already be disabled
    _plugin.OperationController.StartApply(...);
}
```

## 7b. OperationStateSnapshot and RecoveryDialogSnapshot

Two immutable records, published atomically (replace the reference, never mutate in place) after
every meaningful transition — split so the frequently-read frame-to-frame snapshot stays compact,
and the recovery dialog's much larger, much rarer data doesn't ride along with it on every `Draw()`:

```csharp
internal sealed record OperationStateSnapshot(
    OperationStage? Stage,                    // null when Idle
    OperationType? Kind,
    int ProcessedSteps,                       // renamed from "CompletedItems" — the plan is step-based,
    int TotalSteps,                           // and a cycle-breaking plan has more steps than mods
    int CompletedTargets,                     // separate target-level progress for a more meaningful "N of M mods" display
    int TotalTargets,
    string? CurrentIdentifier,
    string? CurrentDisplayName,               // identifier alone can't provide "Applying: <ModName>"
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

internal sealed record RecoveryDialogSnapshot(
    OperationType InterruptedOperationType,   // for "Continue Apply" vs "Continue Restore" labeling — see §9
    bool CancellationWasRequested,            // §5a context: was this interrupted operation already being cancelled?
    ArtifactStatus PlanStatus,
    ArtifactStatus SnapshotStatus,
    IReadOnlyList<ItemRecoveryClassification> Classifications,
    RecoveryOutcome Outcome,
    bool ContinueEnabled, bool RestoreEnabled, bool KeepCurrentEnabled,
    IReadOnlyList<string> BlockingIdentifiers); // AtNeither/Missing* — surfaced for "View Details"
```

`TotalTargets` (recovery targets, one per mod) vs. `TotalSteps` (execution steps, cycle hops
included) are genuinely different numbers on a plan with cycles — presenting "173 of 401 items"
using the step count when the user's mental model is "mods" would be misleading; `CompletedTargets`/
`TotalTargets` is what the progress bar should actually show, with step-level detail available for
diagnostics.

## 8. RecoveryClassifier v2

```csharp
internal enum ItemRecoveryState
{
    AtSnapshot, AtIntended, AtBoth, AtKnownIntermediate, AtNeither,
    MissingLive, MissingSnapshot, MissingPlan,
}
```

`AtBoth`: snapshot and final target are equivalent (a no-op move) — checked and matched against
live state directly (not inferred), same fix as `IsSettled` in §6.
`AtKnownIntermediate`: live path matches a `CycleBreakingTemporaryMove` step's target for this
identifier. This state alone does **not** mean safe-to-continue — it's cross-checked in §8a.

Artifact-level degradation is tracked separately from per-item state, since a missing snapshot file
is not a property of any individual mod:

```csharp
internal enum ArtifactStatus { Valid, PlanMissing, PlanInvalid, SnapshotMissing, SnapshotInvalid, JournalInvalid }
```

### 8a. RecoveryAssessment — one atomic read feeding both classification and replanning

```csharp
internal sealed record RecoveryAssessment(
    LiveModSnapshot LiveSnapshot, IReadOnlyList<ItemRecoveryClassification> Classifications, string SnapshotGenerationHash);
```

Built from a single `GetLiveMods()` call. Both the recovery dialog's classification and Continue's
residual-plan construction read from the *same* `RecoveryAssessment` — never two independent live
reads, which could disagree if the library changed between them.

**`AtKnownIntermediate` validity is proven by attempting to replan, not by hand-enumerated rules.**
The residual move set for Continue is built from *current live paths* to *original final targets*
(`new ModMove(id, live.Mods[id].FullPath, target.FinalRawPath)` — never from the original snapshot
path, which for an `AtKnownIntermediate` target is stale), then run through
`ApplyPlanner.OrderMovesForApply` again. If that succeeds, the intermediate state was consistent; if
it fails, Continue is blocked for those identifiers. This is a stronger check than trying to
enumerate consistency conditions by hand, because it's the exact operation Continue would actually
perform — replanning against reality is the real test of whether reality is resumable.

## 9. Recovery resolutions and startup wiring

**Startup** (`Plugin.cs` constructor): run the cleanup pass and recovery discovery (§4a) — both are
pure file I/O, safe at construction time. If discovery finds an authoritative non-terminal journal
(the leaf of its chain), enter `RecoveryRequiredPendingClassification`. **Do not call Penumbra IPC
for live classification in the constructor** — the provider may not be ready yet; `MainWindow` shows
*"Interrupted operation detected. Waiting for Penumbra state to become available…"* until
`OperationController.Update()` (§7) successfully builds a `RecoveryAssessment`, at which point it
publishes the real `RecoveryDialogSnapshot`. This avoids treating normal startup ordering as a false
indeterminate.

**Continue** (operation-type neutral — dispatches on the interrupted operation's `OperationType`
rather than being Apply-specific; internally `ContinueOperation(...)`, with `MainWindow` choosing the
dialog label "Continue Apply" or "Continue Restore" from `RecoveryDialogSnapshot.InterruptedOperationType`).
Durable ordering, crash-safe in both directions:

1. Use the already-built `RecoveryAssessment` (§8a) — do not re-read live state.
2. Capture a **new** `RollbackSnapshot` from that same live-state generation.
3. Build and validate the continuation `OperationPlan` (new `OperationId`, fresh `ExecutionSteps`
   from replanning residual moves, fresh `RecoveryTargets`).
4. Persist the new plan.
5. Persist the new snapshot.
6. Persist the new non-terminal journal, `RecoveryOfOperationId` pointing at the interrupted operation.
7. Only now, mark the original journal `Resolution = ContinuedByNewOperation`,
   `SuccessorOperationId = <new operation's id>`.
8. Activate the new operation.

A crash between steps 6 and 7 leaves both journals non-terminal simultaneously — expected and
tolerated by §4a's discovery algorithm (the child's `RecoveryOfOperationId` makes it authoritative
regardless of whether step 7 landed). Restore Previous State follows the identical ordering with
`Resolution = RestoredByNewOperation`.

Per-target residual rule for Continue:

| Classification | Residual action |
|---|---|
| `AtIntended`, `AtBoth` | skip |
| `AtSnapshot` | queue (from current live path — see §8a) |
| `AtKnownIntermediate` | queue if replanning succeeds; otherwise treated as blocking |
| `AtNeither`, `MissingLive`, `MissingSnapshot` | block Continue; dialog offers View Details instead |

**Keep Current State**: `Resolution = AcceptedCurrentState` on the interrupted journal, archive its
plan, preserve its snapshot for the retention window, trigger a fresh `RunScan()`.

### 9a. Recovery dialog option availability

Not all three choices are always enabled — availability depends on artifact/classification state:

| Condition | Continue | Restore | Keep Current |
|---|---|---|---|
| Plan and classification valid, no blockers | Enabled | Enabled if snapshot valid | Enabled |
| Plan invalid | Disabled | Enabled if snapshot usable | Enabled |
| Snapshot missing | Enabled if plan valid | Disabled | Enabled |
| Duplicate live identifiers | Disabled | Disabled | Enabled, with a warning |
| IPC unavailable | Pending (not yet decidable) | Pending | Pending — can't scan current state to accept it |
| Any `AtNeither` present | Disabled | Enabled if snapshot valid | Enabled |
| Multiple disconnected recovery roots found (§4a) | Blocked until one root is chosen | Blocked until one root is chosen | Blocked until one root is chosen |

Keep Current remains available in most degraded cases, but the dialog makes explicit that it accepts
an incompletely-understood live state when classification itself was degraded.

## 10. Diagnostics

**`DiagnosticsLog`**: a global (not per-operation) append-only `diagnostics.jsonl` under
`ConfigDirectory/operations/`, using the same append-only-line/tolerate-truncated-trailing-line
approach as `StepResultLog` (§5a) — this is the durable source the diagnostics dump actually reads
from; an in-memory-only or ordinary-log-output `IDiagnosticsSink` could not survive a crash to
explain one, defeating the entire point.

Policy:

- **A diagnostics write failure must never stop or fail an operation.** The sink swallows its own
  I/O exceptions internally (logs to the ordinary Dalamud log as a fallback, does not propagate).
  Diagnostics existing to explain failures must not become a new failure mode itself.
- Each event carries the `operationId` it correlates to (or `null` for events outside any active
  operation), an event kind (`SlowCall`, `SlowLiveSnapshot`, ...), the measured duration, and for
  exception-carrying events: the exception's type name and message, plus a truncated stack trace
  (first ~2000 characters) — full stack traces are not persisted, both to bound line size and
  because a very deep or recursive exception's trace has diminishing diagnostic value past a point.
- Retained events are capped (e.g. most recent 2000 lines) — trimmed opportunistically on write
  rather than requiring a separate scheduled pass, consistent with "no background timer
  infrastructure" already established for retention (§4a).

**Diagnostics dump** stops relying solely on `Config.LastApply`/`LastRestore`:

- A non-terminal journal at dump time produces an explicit section regardless of the config summary:
  last `Stage`, N/M steps, last checkpoint time, count of recorded slow calls from `DiagnosticsLog`
  filtered to that `operationId` — this is the exact case the original bug report needed and the old
  dump couldn't show.
- Recent terminal journals (from `completed/`, not just the single rolling summary) are listed with
  `Stage`/`Resolution`/timing.
- A slow-call section reads directly from `DiagnosticsLog` — count past threshold, worst offenders by
  identifier — the concrete evidence needed to distinguish "this machine's storage is slow" from
  "something hung."

## 11. Small deferred items folded into this plan

- `AtomicFile.TryReadValidated` gains an `IOException` catch (locked file), returning `false` —
  matches the `Try` contract callers already assume.
- `OperationPlanItem.OriginalRawPath`'s "document its future purpose" gap is moot — that record no
  longer exists; `RecoveryTargets.SnapshotRawPath`/`FinalRawPath` are explicit, load-bearing fields
  from the start.
- The missing `AtBoth`-with-live-at-neither-location test case is added as part of rewriting the
  classifier for the v2 state set, not as a separate follow-up.
- The `\`-vs-`/` test literal fix (persistence foundations' Task 2 and Task 5 tests) ships as its
  own small, independent task — unrelated to everything else in this plan, no reason to entangle it.
- **Recommended, not required — decide during the journal task, not here.** `Stage = Cancelled` plus
  `CancellationRequested`/`ProcessedStepCount` can explain *that* a run was cancelled and roughly how
  far it got, but not *why it stopped where it did* without cross-referencing the result log and
  resolution fields — diagnostics would be clearer with an explicit `OperationCompletionReason`/
  `OperationFailureReason` pair (`Normal`/`UserCancelled`/`CompletedWithItemFailures`/
  `VerificationTimedOut` and `IpcUnavailable`/`JournalWriteFailed`/`PlanInvalid`/`RefreshUnavailable`
  or similar) alongside `Stage`/`Resolution`, rather than always reconstructing the reason from
  several other fields together. This doesn't change any architectural decision in this document —
  it's an additive diagnostics field — so it's left for whoever implements `OperationJournal` v2 to
  size concretely against the actual failure paths that exist by then, rather than speculatively
  enumerated here.

## 12. What this design does not cover

- Scan/Index's own incremental split — still gated on profiling, per the original design.
- Off-thread IPC — still pending verification of Penumbra's actual thread-affinity guarantees.
- Penumbra 1.6 vs 1.7 compatibility comparison — still requires a crash log from a session that
  actually crashed.

## 13. Implementation sequencing (for the follow-on plan(s))

Five sequenced implementation plans, mirroring how the persistence foundations work was split from
this — kept as a five-way split, but the first two boundaries are drawn slightly differently than an
earlier pass through this section had them, to keep "pure, heavily unit-tested, no Dalamud" work
together as one coherent plan rather than splitting it across the schema work and the controller work:

**Plan A — Schema and storage foundations** (pure, mostly Dalamud-free, heavily unit-tested):
`OperationPlan` v2 (canonical hash, `GroupId`, contiguity invariants), `OperationJournal` v2
(`ProcessedStepCount`, `Resolution`, `CancellationRequested`), `ApplyPlanner` step metadata and group
invariants (`IsTemporary`, `GroupId`, contiguous-range guarantee), `StepResultLog` plus its
reconciliation rule (§5a), `OperationStorage` (multi-operation layout, recovery graph discovery,
retention), `DiagnosticsLog`, adapter result types, `IElapsedTimeSource`, duplicate-identifier guard,
`AtomicFile` `IOException` catch, `\`-vs-`/` test literal fix.

**Plan B — Apply execution engine**: `PathMutationOperation`, `OperationController`, frame budgeting,
group-cascade behavior (cursor-safe per §3/§5's contiguity requirement), journal/result commit
ordering, cancellation, `Refreshing`, verification settlement — wired to Apply only.

**Plan C — Restore integration**: `PathMutationOperation` configured for Restore, reusing Plan B's
machinery unchanged. This is the abstraction test for §14 — if Restore needs no branching beyond plan
construction and display metadata, the single-type decision is confirmed; if it needs real branching,
that's the signal to split before it's harder to undo.

**Plan D — Recovery**: `RecoveryAssessment`, startup deferred classification, multiple-root handling,
Continue/Restore Previous State/Keep Current, successor/parent terminalization ordering.

**Plan E — UI and diagnostics presentation**: capability lockout, progress display, Stop control,
recovery dialog (including multiple-root selection), diagnostics dump, operation history display.

## 14. Architectural note: composition over inheritance

The prior draft named separate `ApplyOperation`/`RestoreOperation` types sharing "a common base for
the parts that don't differ." Not committing to that inheritance hierarchy here — Apply and Restore
share nearly all execution mechanics (frame-budgeted stepping, verification settlement, the entire
`ExecutionSteps`/`RecoveryTargets` model), and a single `PathMutationOperation` configured by
`OperationType` is very likely cleaner than a base class with two thin subclasses. But this is a
judgment call best validated once the implementation plan (specifically task 3 above) has a second
real caller to check the abstraction against — composition is the safer default until then, not a
final decision the spec should lock in prematurely.
