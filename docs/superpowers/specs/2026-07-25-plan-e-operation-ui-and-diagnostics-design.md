# Plan E: Operation UI and Diagnostics Presentation

Date: 2026-07-25
Status: Design draft, written for a single consolidated review pass (scope + spec + implementation
plan all delivered together per explicit user request, rather than the iterative-question format
used for prior plans in this series).
Builds on: `docs/superpowers/specs/2026-07-22-operation-controller-design.md` §13 (Plan E's original,
speculative scope — "capability lockout, progress display, Stop control, recovery dialog including
multiple-root selection, diagnostics dump, operation history display"), and everything already shipped
on `main` through Plan D2 (`635b5e1`).

## 0. Why this spec doesn't just implement the original §13 sketch

The original design doc's Plan E sketch (§7b's `RecoveryDialogSnapshot`, §8's `ItemRecoveryState` with
`MissingSnapshot`/`MissingPlan`, §10's diagnostics dump shape) was written before Plans B1 through D2
existed and diverged from it substantially during their own review cycles — `MissingSnapshot`/
`MissingPlan` were dropped as redundant with artifact-level `ArtifactCheckStatus` (D1's own finding),
`RecoveryDialogSnapshot` was never built (the crude panel reads `OperationController.State` directly),
and `OperationStateSnapshot`'s real field names differ from the sketch's. Grounded this plan entirely
against the **real, current shipped code** instead (verified file:line citations throughout), not the
three-week-old sketch, which is cited here only for the six-area scope list it still correctly names.

**Grounding findings, verified directly against `main` @ `635b5e1` before writing a word of design:**

1. **Capability lockout**: `OperationStateSnapshot` (`OperationController.cs:10-44`) already computes
   11 `Can*` booleans. `MainWindow.cs` consults `CanStartApply`/`CanStartRestore`/`CanContinueRecovery`/
   `CanRestorePreviousState` already. It never consults `CanScan`, `CanIndex`, `CanRunFolderCleanup`,
   `CanRunFolderCleanupRollback`, `CanCreateBackup`, `CanResolveRecovery`, or `CanRequestCancellation`
   — those buttons are unconditionally clickable today, relying entirely on the underlying
   `InvalidOperationException`/`_lastError` path to explain a rejected click after the fact.
2. **Progress display**: `MainWindow.cs:673,677` (Apply tab) and `:817-823` (Restore/History tab) show
   plain text — `"Applying... {ProcessedSteps}/{TotalSteps} steps ({Stage})."` — no `ImGui.ProgressBar`
   anywhere in the file. Only step counts are shown; `ProcessedTargets`/`TotalTargets`/
   `LastProcessedDisplayName` are computed by `PublishState` (`OperationController.cs:835-845`) but
   never read by `MainWindow` except `SuccessfulTargets`/`TotalTargets` in the terminal-state line.
3. **Stop control**: `OperationController.RequestCancellation()` (`OperationController.cs:502-519`) and
   `CanRequestCancellation` are fully implemented with **zero UI wiring** — no button, no `Plugin.cs`
   wrapper, confirmed via a whole-repo grep.
4. **Recovery dialog**: `DrawRecoveryPanelIfNeeded()` (`MainWindow.cs:124-224`, read in full) shows
   three buttons (Keep Current/Continue/Restore Previous State) with **zero per-mod detail** — no
   listing of which mods are `AtSnapshot`/`AtKnownIntermediate`/blocking, even though
   `OperationController.GetRecoveryAssessment()` (`OperationController.cs:221`) already computes exactly
   that data and has **zero call sites outside the controller and its own tests**. The multi-root branch
   (`MultipleDisconnectedRoots`/`CycleDetected`, both routed identically per
   `RegisterDiscoveredRecovery`, `OperationController.cs:199-203`) shows only static text and one "Accept
   All" button — the panel's own copy admits "picking which one to recover isn't supported yet."
5. **Diagnostics dump**: `CreateDiagnosticDump()` (`MainWindow.cs:1332-1395`) exports config/session
   summaries (`Config.LastApply` etc., `DiagnosticSummaryFormatter`) but **never touches
   `DiagnosticsLog`, `IDiagnosticsSink`, or any operation journal** — `DiagnosticsLog.ReadAll`
   (`DiagnosticsLog.cs:78-104`) is fully implemented and has **zero production call sites** (grep
   confirmed, only test files call it).
6. **Operation history display**: `DrawHistoryTab()` is built entirely on `RollbackSnapshot`
   (pre/post file-state backups) via `RollbackHistory.Load` — there is **no UI anywhere** that lists
   past *operations* (Apply/Restore/Continue journals: what ran, what stage it reached, what its
   resolution was). `completed/*/journal.json` is written by every operation but never read back for
   display.
7. **A seventh, unscoped-but-real gap found during this same grounding pass**: `OperationBundleRetention.
   RunRetentionPass` (`OperationBundleRetention.cs:14`) — which prunes `completed/` to the newest 50
   bundles or 30 days, whichever is more, with transitive `RecoveryOfOperationId` chain retention — is
   **never called from anywhere in the production code** (confirmed via whole-repo grep: the only match
   for `RunRetentionPass` in the entire `PenumbraOrganizer.Plugin` project is its own definition).
   `completed/` has grown unboundedly since Plan B2 first started writing to it. Folding this into Plan
   E since item 6 (operation history display) is the first feature that gives this gap user-visible
   consequence — the display's data source should be the same bounded window retention already exists
   to enforce, not literally everything ever written.

Given this, Plan E's real job is: wire seven areas of already-computed-but-undisplayed data and
already-built-but-unwired backend capability into `MainWindow.cs`/`Plugin.cs`, plus one genuinely new
piece of `OperationController` logic (multi-root incremental resolution, §6 below) and one genuinely
new diagnostics-reading feature (§7). Nothing here requires new execution-engine behavior — the same
"no execution-engine changes needed" finding that shaped D1/D2 holds for this plan too, confirmed by
this grounding pass rather than assumed by analogy.

## 1. Capability lockout

Wire the six currently-unconsulted `Can*` fields into their corresponding `MainWindow.cs` buttons,
matching the existing `ImGui.BeginDisabled(!operationState.CanX)` pattern already used for
`CanStartApply`/`CanContinueRecovery`/`CanRestorePreviousState`:

| Button | Field | Current state |
|---|---|---|
| Scan / Refresh mod list (`DrawScanTab`) | `CanScan` | unconditional |
| Create Backup (`DrawHistoryTab`) | `CanCreateBackup` | unconditional |
| Restore (per-snapshot button, `DrawHistoryTab`) | `CanStartRestore` | unconditional |
| Clean Up Selected Folders | `CanRunFolderCleanup` | unconditional |
| Rollback Folder Cleanup | `CanRunFolderCleanupRollback` | unconditional |
| Keep Current State (recovery panel) | `CanResolveRecovery` | unconditional |

`CanIndex` has no corresponding button in the current UI (no Index tab exists yet in this codebase —
confirmed absent) — left unconsulted, not a gap, nothing to wire it to.

This is UX polish, not a correctness fix: every one of these buttons is already backstopped by the
underlying `OperationController` admission guard throwing `InvalidOperationException`, caught by each
`Plugin.cs`/`MainWindow.cs` wrapper's existing try/catch into `_lastError` (confirmed for every one of
these six call paths). Proactively disabling the button just means the user doesn't have to click and
read an error to learn something's blocked — the safety net stays exactly as it is.

**Also add a disabled-state tooltip** (`ImGui.SetItemTooltip` when `ImGui.IsItemHovered()` and the
button is disabled) explaining *why* — "Another operation is in progress or requires recovery" — since
a disabled button with no explanation is only a partial improvement over today's after-the-fact error.

## 2. Progress display

Replace the plain-text progress lines in `DrawReviewTab` (Apply tab, `MainWindow.cs:673`) and
`DrawHistoryTab` (Restore, `:817`) with a real `ImGui.ProgressBar`, driven by **`ProcessedTargets`/
`TotalTargets`, not `ProcessedSteps`/`TotalSteps`** — the original design's own reasoning holds and is
worth restating: a cycle-breaking plan has more execution steps than recovery targets (a temporary hop
plus a final move both count as steps for one target), so a step-based fraction misrepresents "how many
mods are done" to a user whose mental model is mods, not steps. `TotalTargets == 0` (an empty plan, per
D2's own zero-step tests) shows the bar at 100% immediately rather than dividing by zero.

```csharp
var fraction = operationState.TotalTargets > 0
    ? (float)operationState.SuccessfulTargets / operationState.TotalTargets
    : 1f;
ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{operationState.SuccessfulTargets}/{operationState.TotalTargets} mods");
if (operationState.LastProcessedDisplayName is { } name)
    ImGui.TextDisabled($"Last: {name}");
ImGui.TextDisabled($"{operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage})");
```

Note `SuccessfulTargets` (not `ProcessedTargets`) drives the bar fraction — `ProcessedTargets` includes
targets that were *attempted and failed*, which would make the bar appear to complete even on a run with
real failures. `ProcessedTargets` is still shown for the step-level detail line's context but doesn't
drive the fraction. Step-level detail stays available as secondary, dimmed text — matching the original
design's "step-level detail available for diagnostics" framing — not removed, just demoted.

## 3. Stop control

`Plugin.cs` gains a thin wrapper (matching every other operation-adjacent method's shape):

```csharp
internal void RequestCancellation() => OperationController.RequestCancellation();
```

No `_operationInProgress` guard needed here — `RequestCancellation()` is itself a no-op guarded
internally (`OperationController.cs:502`: only acts while `Stage == Mutating`), and it doesn't start a
new operation or need reentrancy protection the way `StartApplyOperation`/`ResolveContinue` do.

`MainWindow.cs` adds a "Cancel" button next to the progress bar (§2), gated on
`operationState.CanRequestCancellation`, calling `_plugin.RequestCancellation()` directly —
**no confirmation popup**, unlike every other operation-adjacent button in this panel. This is a
deliberate asymmetry: cancellation is the one action in this whole UI that's genuinely low-stakes and
reversible in intent (it requests a graceful stop at the next safe boundary — `PathMutationOperation`
already checks `stopRequested` between steps, not mid-step — worst case if clicked by mistake is the
operation finishes a moment sooner than intended, not that anything gets corrupted or lost). Every
*other* confirm popup in this codebase gates a genuinely consequential action (starting a multi-mod
move, discarding recovery state, resolving an interrupted operation) — Cancel doesn't belong in that
category.

## 4. Recovery dialog: per-mod classification detail

`DrawRecoveryPanelIfNeeded()`'s single-root branch (`MainWindow.cs:165-223`) gains a collapsible
"Details" section (`ImGui.CollapsingHeader`, matching the codebase's existing pattern for optional
detail — see `DrawOrphanedFoldersSection`) showing:

- **Artifact status**, if either is not `Valid` — explains *why* Continue/Restore Previous State might
  be disabled beyond just "the button is greyed out": "Interrupted plan is missing/corrupt — Continue is
  unavailable" / "Snapshot is missing/corrupt — Restore Previous State is unavailable." This needs a new
  read-only accessor:

  ```csharp
  public (ArtifactCheckStatus Plan, ArtifactCheckStatus Snapshot)? GetPendingRecoveryArtifactStatus() =>
      _pendingRecovery is { } pending ? (pending.PlanCheckStatus, pending.SnapshotCheckStatus) : null;
  ```

  Nullable return, matching `GetRecoveryAssessment()`'s own existing convention for "query accessor with
  no pending recovery" rather than throwing. The underlying `PendingRecoveryContext.PlanCheckStatus`/
  `SnapshotCheckStatus` fields already exist (Task 3, D2) but are private to the class; this is a thin,
  read-only accessor addition, not new computation.
- **Per-mod classification**, from `OperationController.GetRecoveryAssessment()` (already public,
  currently uncalled from the UI): a scrollable child table (`ImGui.Table`, matching `PathTreeView`'s
  existing pattern) listing `Identifier`/`State` for every classification, color-coded — red for
  `AtNeither`/`MissingLive` (blocking), green for `AtIntended`/`AtBoth` (already done), yellow for
  `AtSnapshot`/`AtKnownIntermediate` (queued for Continue). If `GetRecoveryAssessment()` returns `null`
  (classification hasn't settled yet — `RecoveryClassificationPending`), show "Still checking live mod
  state…" instead of an empty table, matching the panel's existing "Waiting for Penumbra state" framing
  from D1.

This section is read-only presentation over already-computed data — no new `OperationController`
resolution logic, only the one small accessor addition named above.

## 5. Recovery dialog: multi-root incremental resolution

**The hardest design decision in this plan.** The original doc's "multiple-root selection" is
underspecified — it names the need but never says what "selecting a root" actually *does*. Reasoned
through several options before settling on one; documenting the reasoning since it's the one place in
this plan making a real architectural call rather than just wiring existing data.

**Rejected: full per-root Continue/Restore/Keep-Current, all roots resolvable independently and
simultaneously.** Would require `OperationController` to track N independent `PendingRecoveryContext`s
at once instead of one, each with its own classification/artifact-check/availability state — a genuine
architectural expansion disproportionate to how rare this state is (it requires either two genuinely
unrelated interrupted operation chains, or a `RecoveryOfOperationId` cycle — neither has ever been
observed outside a hand-constructed test in this whole project's history). Not worth building.

**Adopted: resolve one root via Keep Current, then re-run discovery over what's left.** Every
`AuthoritativeOperationId` in the blocked graph gets its own row (journal type/stage/timestamp) and a
"Keep Current State" button — no Continue/Restore option per-root (those stay reserved for the
single-root case, §4). Clicking one:

1. Resolves *that one* journal via the exact same commit-then-relocate logic
   `AcceptAllAndCloseInterruptedOperations` already uses per-journal (extracted into a shared private
   helper, `ResolveJournalAsKeepCurrent(Guid operationId, string activeBundleDirectory, OperationJournal
   journal)`, so this isn't a third copy of that logic).
2. Re-runs `OperationBundleDiscovery.RunStartupDiscovery(_operationsRoot)` over the *remaining* on-disk
   active journals (the just-resolved one dropped out, since it's now terminal and relocated).
3. Feeds the fresh `OperationDiscoveryResult` through the **existing** `RegisterDiscoveredRecovery`
   dispatch (`OperationController.cs:184-204`, unchanged) — `NoRecoveryNeeded` clears the block
   entirely, `SingleAuthoritative` clears the multi-root block and transitions to the ordinary
   single-root recovery panel (§4's full Continue/Restore/Keep-Current UI now applies), and
   `MultipleDisconnectedRoots`/`CycleDetected` stays blocked with the now-smaller graph.

**A genuine correctness property, verified by hand-tracing the existing graph algorithm rather than
assumed, since this design depends on it:** does resolving one arbitrary member of a `CycleDetected`
set actually break the cycle correctly, or could it leave a still-cyclic remainder?
`OperationRecoveryGraph.Analyze` (`OperationRecoveryGraph.cs:20-46`) builds `childToParent` edges only
between journals *both present in the current non-terminal input set* (`idSet.Contains(parentId)` guard,
line 30). Once one cycle member resolves to terminal, it's excluded from the next `LoadNonTerminalActiveJournals`
pass entirely — any edge into or out of it vanishes with it. Traced a concrete 3-cycle (A→B→C→A,
resolving B): the next pass sees only {A, C}; C's edge to A survives (both still in-set); A's edge to B
and B's edge to C are both gone (B absent). Result: a simple 2-chain, `leaves = {C}` (nothing points at
C), which correctly resolves to `SingleAuthoritative`. **Resolving any single member of a cycle always
strictly shrinks it** — the remaining graph can never still contain the same cycle, since the resolved
node's presence was required for every edge that touched it. This means the exact same "resolve one,
re-discover" flow that's *needed* for `MultipleDisconnectedRoots` also *correctly* handles
`CycleDetected`, with no special-casing between the two — matching how `RegisterDiscoveredRecovery`
already treats them identically. §8 adds a test proving this concretely, not just asserting it from this
proof.

`OperationController` needs one more thing this design surfaces as a real gap: **`_blockedMultiRootGraph`
is set without ever storing the corresponding journals.** `RegisterDiscoveredRecovery`'s
`MultipleDisconnectedRoots`/`CycleDetected` branch (`OperationController.cs:196-198`) discards
`discovery.Journals` entirely — meaning today, once blocked, there is no way to look up *any* detail
about the blocked operations (not even their `OperationType`/`Stage`), which is exactly the data this
section's per-root list needs to display. Fix: add a new field, `_blockedMultiRootJournals` (`Dictionary<
Guid, OperationJournal>`), populated alongside `_blockedMultiRootGraph` in the same branch, and a new
public accessor:

```csharp
public IReadOnlyList<(Guid OperationId, OperationJournal Journal)> GetBlockedOperations() =>
    _blockedMultiRootGraph is not { } graph
        ? []
        : graph.AuthoritativeOperationIds
            .Where(id => _blockedMultiRootJournals!.ContainsKey(id))
            .Select(id => (id, _blockedMultiRootJournals![id]))
            .ToList();
```

Only `AuthoritativeOperationIds` (the leaves — the ones actually independently resolvable) are surfaced,
not `AllOperationIds` — a non-leaf ancestor isn't independently actionable; it gets folded in
automatically once its authoritative descendant resolves and discovery re-runs.

`AcceptAllAndCloseInterruptedOperations()` stays exactly as-is — the fast bulk option for when the user
doesn't want to inspect individually, unchanged behavior, unchanged tests.

## 6. Diagnostics dump v2

`CreateDiagnosticDump()` (`MainWindow.cs:1332-1395`) gains three new sections, reading data the original
design's §10 called for and that already exists but is unread today:

1. **Interrupted operation section** (only if `operationState.RequiresRecovery`): `Stage`, `N/M steps`,
   last-updated timestamp (`OperationJournal.UpdatedAt` — the closest available proxy for "last
   checkpoint time," since checkpoints don't record their own timestamp separately from the journal
   they wrote). This is the exact case the original design's §10 named as the reason the old
   `Config.LastApply`-only dump couldn't explain a stuck operation — the file this session already knows
   was the original bug report's own motivation.
2. **Recent operations section**: the newest 20 (of whatever retention's already bounded to, §7) entries
   from `completed/*/journal.json`, each showing `Type`/`Stage`/`Resolution`/`UpdatedAt`. Needs a new
   read function, `OperationBundleDiscovery.LoadRecentCompletedJournals(operationsRoot, int take)` —
   lists `completed/`'s subdirectories, loads each journal, sorts by `UpdatedAt` descending, takes the
   requested count. Placed in `OperationBundleDiscovery.cs` alongside its existing `active/`-reading
   logic (same file, same "read journals off disk" responsibility), not a new class.
3. **Slow-call section**: reads `DiagnosticsLog.ReadAll(DiagnosticsLogPath)`, filters to
   `Kind == SlowCall`, groups by `Identifier`, shows count and the 5 worst (longest `DurationMilliseconds`)
   — the "worst offenders by identifier" framing the original design named. No new threshold logic —
   every event in the log already passed `PathMutationOperation`'s existing 50ms `SlowCallThreshold`
   (`PathMutationOperation.cs:30`) before being recorded; the dump just reports what's already there.

**Does not add a `RecordException` diagnostic event kind**, even though `DiagnosticEventKind.Exception`
already exists as an enum value with no emitting call site (`IDiagnosticsSink` has only
`RecordSlowCall`/`RecordSlowLiveSnapshot`/`RecordSlowRefresh`). Wiring actual exception recording would
mean deciding *where* in the execution engine to call it — a scope decision touching already-shipped,
already-reviewed B1/C code, not a UI-layer wiring task. Out of scope for this plan; noted in §9.

## 7. Operation history display + retention wiring

**Retention wiring** (the gap found in §0, item 7): `Plugin.cs`'s constructor, immediately after the
existing discovery call (`Plugin.cs:65-66`):

```csharp
var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
Organizer.Operations.OperationBundleRetention.RunRetentionPass(OperationsRoot, DateTimeOffset.UtcNow);
```

Ordering doesn't matter for correctness (retention only ever touches `completed/`; discovery only ever
reads `active/` — no shared state between the two calls), placed after discovery only to keep the
constructor's existing "handle what's outstanding, then do routine maintenance" read order.

**Operation history display**: `DrawHistoryTab()` gains a new collapsible section, "Recent Operations"
(`ImGui.CollapsingHeader`, default collapsed — this is diagnostic/audit information, not the primary
Restore workflow that section already serves), listing the same
`OperationBundleDiscovery.LoadRecentCompletedJournals` data §6 introduced (shared, not duplicated —
one function, two call sites: the diagnostic dump text export and this live UI list). Each row shows
`Type`/`Stage`/`Resolution`/`UpdatedAt`, read-only — no Restore/Continue/Delete actions on a completed
journal row (that's what the existing `RollbackSnapshot`-based list above it is already for; this
section answers "what actually happened," not "what can I revert to"). Deliberately kept visually
distinct from and below the existing snapshot list, not merged into one combined table — a
`RollbackSnapshot` and an `OperationJournal` are different concepts (a point-in-time file-state backup
vs. a record of what an operation did) and conflating them into one row shape would blur that
distinction rather than clarify it.

## 8. Testing

Pure/xUnit-testable:
- `OperationController.GetPendingRecoveryArtifactStatus()`: returns the correct tuple for
  valid/invalid/missing combinations of plan and snapshot artifact status; returns `null` (not
  throwing) when no pending recovery exists, matching `GetRecoveryAssessment()`'s own convention.
- `OperationController.GetBlockedOperations()`: empty when no multi-root block is active; returns
  exactly the authoritative operations (not the full transitive set) with their journals when blocked;
  empty (not throwing) if a requested id's journal somehow isn't in the stored dictionary (defensive,
  matching `RegisterSingleAuthoritative`'s own existing `TryGetValue` defensive pattern).
- `OperationController.ResolveOneMultiRootOperation(Guid operationId)`: resolving the sole remaining member of a two-root
  `MultipleDisconnectedRoots` set transitions the controller to `Idle`, not to a phantom still-blocked
  state; resolving one member of a **3-node cycle** (`A→B→C→A`, `RecoveryOfOperationId` chain)
  transitions to `SingleAuthoritative` with the correct remaining operation as authoritative — this is
  the concrete regression test for §5's hand-traced correctness property, not just a restatement of it;
  resolving one of three genuinely disconnected roots leaves the other two still blocked with a
  correctly-shrunk `AuthoritativeOperationIds` list; the resolved journal is correctly relocated to
  `completed/` exactly like `AcceptAllAndCloseInterruptedOperations`'s existing per-journal behavior
  (same collision-safe check, reusing the same extracted helper — proves the extraction didn't drop
  any of the existing safety checks).
- `OperationBundleRetention.RunRetentionPass` wiring: no new tests needed for the retention logic itself
  (already fully tested, `OperationBundleRetentionTests.cs` predates this plan) — only that `Plugin.cs`
  calls it, which is Dalamud-coupled and therefore not automatable, same documented limitation as every
  prior plan's `Plugin.cs` changes.
- `OperationBundleDiscovery.LoadRecentCompletedJournals`: returns journals newest-first, respects the
  `take` count, skips a bundle whose journal fails to load (matching `LoadNonTerminalActiveJournals`'s
  own established "corrupt journal excluded, not fatal" pattern), returns `[]` when `completed/` doesn't
  exist yet (fresh install, matching `RunRetentionPass`'s own early-return for the same case).

Not automatable: every `MainWindow.cs` change (§1 disabled-state wiring, §2 progress bar, §3 Cancel
button, §4/§5 recovery panel additions, §6/§7's dump/history UI) — same documented Dalamud/ImGui-coupled
limitation as every prior plan. Manual checklist in the implementation plan's final task.

## 9. What this plan does not cover

- `DiagnosticEventKind.Exception` actually being emitted anywhere (§6) — the enum value and record
  shape exist; wiring a real emitter is a distinct scope decision about *where* in the execution engine
  exceptions should be captured, not a UI-layer task.
- Full independent per-root Continue/Restore for the multi-root case (§5) — Keep-Current-only,
  reasoned through and rejected as disproportionate to how rare this state is.
- Any change to `RollbackSnapshot`'s own shape or the existing snapshot-based History workflow (§7) —
  additive only, a new section alongside it.
- Scan/Index/Folder Cleanup's own incremental (frame-budgeted) treatment — still explicitly deferred
  per the original design's §7 framing, unchanged by this plan; this plan only adds *lockout* around
  those still-synchronous operations, not incremental execution for them.
- A dedicated "Index" tab/button to give `CanIndex` something to gate — no such feature exists in this
  codebase yet; out of scope to invent one just to exercise the field.

## 10. Global constraints for the implementation plan

- `dotnet build` must introduce no new warnings/errors beyond the accepted baseline at worktree setup
  (re-verify fresh, per established precedent).
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC.
- `PenumbraPathSemantics.AreEquivalent`/`Normalize` for any new path comparison — this plan's new code
  doesn't introduce any (it's presentation and discovery/retention wiring, not new path logic), but the
  constraint carries forward unchanged.
- `RecoveryClassifier`, `ContinuationPlanner`, `RollbackHistory`, `ApplyPlanner`, `OperationBundleRetention`'s
  existing retention algorithm are out of scope for behavior changes — this plan consumes their existing
  output/reads their existing data unchanged.
- Every `OperationController` addition must preserve the class's existing invariants: no method may let
  an exception escape `Update()`, `PublishState()` remains the sole place `State` is assigned, and the
  new multi-root resolution path must not reopen either of D2's own hard-won failure-atomicity or
  admission-guard-scoping lessons (a resolved-then-relocated journal must not be resurrectable, and the
  new resolution path must not bypass any lockout it isn't explicitly designed to bypass).
