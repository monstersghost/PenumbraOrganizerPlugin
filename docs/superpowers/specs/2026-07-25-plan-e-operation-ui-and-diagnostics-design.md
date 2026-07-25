# Plan E: Operation UI and Diagnostics Presentation

Date: 2026-07-25
Status: Revised after one review round covering both this spec and its implementation plan together
(scope + spec + implementation plan were delivered together per explicit user request, rather than the
iterative-question format used for prior plans in this series, so the first review round covered both
documents at once). Five must-fix findings addressed: multi-root resolution's rediscovery-failure
atomicity, the `Plugin.cs` wrapper's conditional re-scan, retention-pass startup isolation, avoiding
per-frame disk reads for the Recent Operations list, and reporting the interrupted operation's real
timestamp instead of the dump's creation time. Additional design corrections addressed: the progress
bar's fraction now tracks completion (`ProcessedTargets`) rather than success
(`SuccessfulTargets`), the Cancel button's layout reserves width instead of assuming the progress bar
leaves room, recovery-detail messaging distinguishes "not yet checked" from "permanently unavailable"
for every `ArtifactCheckStatus`/classification-null case, multi-root UI copy is precise about
permanently abandoning the selected operation, slow-call diagnostics are grouped by identifier, and
diagnostic-dump sections are failure-isolated from each other. Approved for implementation planning.
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

**Folder Cleanup's button is already disabled by a second, unrelated, pre-existing condition**
(`_selectedOrphans.Count == 0`, `MainWindow.cs:1148`) that today has no tooltip at all — confirmed by
reading the current code, not assumed. Adding `!CanRunFolderCleanup` as a second disabling condition
means the button can now be disabled for two independent reasons, and the new tooltip must distinguish
them rather than always claiming "another operation is in progress or requires recovery" even when the
real reason is simply "nothing is selected." Gate the tooltip on the capability flag specifically (only
show it when a selection exists but `CanRunFolderCleanup` is false), so a no-selection user isn't told
something false about an operation being in progress.

**A stale confirmation popup is a real edge case worth naming explicitly, not just implicitly relying
on the underlying guard.** The Restore-per-snapshot flow captures a preview when the row button is
clicked and opens a popup; if a recovery starts (or another operation begins) in the frames between
that click and the user confirming the still-open popup, the row button would now render disabled, but
the already-open popup and its already-captured preview are untouched by that state change. This is why
`Plugin.PreviewRestore`'s confirm handler re-checking `OperationController` admission at confirm time —
not only at the row-button click — remains load-bearing after this plan's disabled-button wiring lands;
disabling the row button is a UX improvement for the common case, not a substitute for the admission
recheck the confirm handler already performs. §8's manual checklist calls this out as its own scenario
to verify, since it's exactly the kind of interaction-timing edge case automated `OperationController`
tests can't exercise.

## 2. Progress display

Replace the plain-text progress lines in `DrawReviewTab` (Apply tab, `MainWindow.cs:673`) and
`DrawHistoryTab` (Restore, `:817`) with a real `ImGui.ProgressBar`, driven by **`ProcessedTargets`/
`TotalTargets`, not `ProcessedSteps`/`TotalSteps`** — the original design's own reasoning holds and is
worth restating: a cycle-breaking plan has more execution steps than recovery targets (a temporary hop
plus a final move both count as steps for one target), so a step-based fraction misrepresents "how many
mods are done" to a user whose mental model is mods, not steps. `TotalTargets == 0` (an empty plan, per
D2's own zero-step tests) shows the bar at 100% immediately rather than dividing by zero.

**The fraction must track completion, not success.** An earlier draft of this section drove the bar
from `SuccessfulTargets`, reasoning that a full-looking bar shouldn't imply "everything went fine" when
some targets failed. That reasoning is right but the mechanism is wrong: `SuccessfulTargets` is a
subset of `ProcessedTargets` (attempted-and-succeeded, not attempted), so any run with even one failure
makes the bar stop advancing for that target *forever*, including after the operation has finished
processing everything — a 100-target run with 10 failures would settle at a permanently-stuck-looking
90%, not a completed bar, even though there's nothing left running. That's a progress measurement
bug, not the success-vs-progress distinction it was trying to draw. The two concerns are separable:
`ProcessedTargets/TotalTargets` measures how much work the operation has gotten through (completion);
`SuccessfulTargets` vs. failures measures the outcome of that work. Drive the bar's fraction from
`ProcessedTargets` and show the success/failure breakdown as separate text alongside it:

```csharp
var fraction = operationState.TotalTargets > 0
    ? (float)operationState.ProcessedTargets / operationState.TotalTargets
    : 1f;
ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{operationState.ProcessedTargets}/{operationState.TotalTargets} processed");
var failedTargets = operationState.ProcessedTargets - operationState.SuccessfulTargets;
ImGui.TextDisabled(failedTargets > 0
    ? $"{operationState.SuccessfulTargets} succeeded, {failedTargets} failed"
    : $"{operationState.SuccessfulTargets} succeeded");
if (operationState.LastProcessedDisplayName is { } name)
    ImGui.TextDisabled($"Last: {name}");
ImGui.TextDisabled($"{operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage})");
```

Step-level detail stays available as secondary, dimmed text — matching the original design's
"step-level detail available for diagnostics" framing — not removed, just demoted.

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
**no confirmation popup**, unlike every other operation-adjacent button in this panel.

**Layout: the progress bar can't simply take the full width and expect a button to fit beside it.**
§2's `ImGui.ProgressBar` call uses `Vector2(-1, 0)`, which claims all remaining horizontal space: a
naive `ImGui.SameLine()` + `ImGui.Button("Cancel")` placed directly after it has nothing left to lay
out into. Rather than duplicate width-reservation math at both of §2's call sites (Apply tab, Restore
tab), the cleanest fix folds Cancel into `DrawOperationProgress` itself (the one shared helper both
tabs already call): the helper takes an optional cancel callback, and only when both that callback is
non-null and `CanRequestCancellation` is true does it reserve space for the button before drawing the
bar:

```csharp
private static void DrawOperationProgress(OperationStateSnapshot operationState, string verb, Action? onCancel)
{
    var fraction = operationState.TotalTargets > 0
        ? (float)operationState.ProcessedTargets / operationState.TotalTargets
        : 1f;
    var showCancel = onCancel is not null && operationState.CanRequestCancellation;
    var barWidth = -1f;
    var buttonWidth = 0f;
    if (showCancel)
    {
        buttonWidth = ImGui.CalcTextSize("Cancel").X + ImGui.GetStyle().FramePadding.X * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        barWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonWidth - spacing);
    }
    ImGui.ProgressBar(fraction, new Vector2(barWidth, 0), $"{operationState.ProcessedTargets}/{operationState.TotalTargets} processed");
    if (showCancel)
    {
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
            onCancel!();
    }
    // ...success/failure text, LastProcessedDisplayName, step-level detail as in §2
}
```

Each call site passes `onCancel: operationState.CanRequestCancellation ? _plugin.RequestCancellation : null`
(or simply `_plugin.RequestCancellation` unconditionally — the helper re-checks `CanRequestCancellation`
itself, so passing the delegate unconditionally is safe and slightly simpler at the call site). This is a
deliberate asymmetry: cancellation is the one action in this whole UI that's genuinely low-stakes and
reversible in intent (it requests a graceful stop at the next safe boundary — `PathMutationOperation`
already checks `stopRequested` between steps, not mid-step — worst case if clicked by mistake is the
operation finishes a moment sooner than intended, not that anything gets corrupted or lost). Every
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

- **Artifact status** — explains *why* Continue/Restore Previous State might be disabled beyond just
  "the button is greyed out." This needs a new read-only accessor:

  ```csharp
  public (ArtifactCheckStatus Plan, ArtifactCheckStatus Snapshot)? GetPendingRecoveryArtifactStatus() =>
      _pendingRecovery is { } pending ? (pending.PlanCheckStatus, pending.SnapshotCheckStatus) : null;
  ```

  Nullable return, matching `GetRecoveryAssessment()`'s own existing convention for "query accessor with
  no pending recovery" rather than throwing. The underlying `PendingRecoveryContext.PlanCheckStatus`/
  `SnapshotCheckStatus` fields already exist (Task 3, D2) but are private to the class; this is a thin,
  read-only accessor addition, not new computation. `ArtifactCheckStatus` has four members —
  `Unchecked`, `Valid`, `Missing`, `Invalid` — and the UI must not collapse the first three into "not
  Valid ⇒ error": `Unchecked` means the async check simply hasn't run yet, which is a normal transient
  state early in a recovery's lifetime, not a problem. Render per-status, not via an "if not Valid" test:

  ```csharp
  static void DrawArtifactLine(ArtifactCheckStatus status, string artifactName, string unavailableAction)
  {
      switch (status)
      {
          case ArtifactCheckStatus.Unchecked:
              ImGui.TextDisabled($"Checking {artifactName}...");
              break;
          case ArtifactCheckStatus.Missing:
              ImGui.TextColored(PluginTheme.CollisionBad, $"{artifactName} is missing; {unavailableAction} is unavailable.");
              break;
          case ArtifactCheckStatus.Invalid:
              ImGui.TextColored(PluginTheme.CollisionBad, $"{artifactName} is corrupt; {unavailableAction} is unavailable.");
              break;
          case ArtifactCheckStatus.Valid:
              break; // nothing to report - Valid needs no explanatory line
      }
  }
  ```

  called once for the plan (`"Interrupted plan"`, `"Continue"`) and once for the snapshot
  (`"Snapshot"`, `"Restore Previous State"`).
- **Per-mod classification**, from `OperationController.GetRecoveryAssessment()` (already public,
  currently uncalled from the UI): a scrollable child table (`ImGui.Table`, matching `PathTreeView`'s
  existing pattern) listing `Identifier`/`State` for every classification, color-coded — red for
  `AtNeither`/`MissingLive` (blocking), green for `AtIntended`/`AtBoth` (already done), yellow for
  `AtSnapshot`/`AtKnownIntermediate` (queued for Continue). `GetRecoveryAssessment()` returning `null`
  has two distinct causes that need distinct messages, not one blanket "still checking": while
  `operationState.RecoveryClassificationPending` is true, classification genuinely hasn't settled yet
  and "Still checking live mod state…" (matching the panel's existing "Waiting for Penumbra state"
  framing from D1) is correct; but classification can also permanently fail to settle — an invalid plan,
  invalid live-read data, or the live-mod provider becoming terminally unavailable are all non-retryable
  per D2's `ClassificationStatus`/`LiveReadStatus` settling design — in which case
  `RecoveryClassificationPending` is `false` but `GetRecoveryAssessment()` is still `null`, and "Still
  checking" would be permanently, silently wrong. Branch on the flag:

  ```csharp
  if (assessment is null)
  {
      if (operationState.RecoveryClassificationPending)
          ImGui.TextDisabled("Still checking live mod state...");
      else
          ImGui.TextColored(PluginTheme.CollisionBad, "Per-mod classification is unavailable - see the artifact status above.");
  }
  ```

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
   helper, `TryResolveJournalAsKeepCurrent(Guid operationId) -> JournalResolutionOutcome`, so this isn't
   a third copy of that logic).
2. Re-runs `OperationBundleDiscovery.RunStartupDiscovery(_operationsRoot)` over the *remaining* on-disk
   active journals (the just-resolved one dropped out, since it's now terminal and relocated).
3. Feeds the fresh `OperationDiscoveryResult` through the **existing** `RegisterDiscoveredRecovery`
   dispatch (`OperationController.cs:184-204`, unchanged) — `NoRecoveryNeeded` clears the block
   entirely, `SingleAuthoritative` clears the multi-root block and transitions to the ordinary
   single-root recovery panel (§4's full Continue/Restore/Keep-Current UI now applies), and
   `MultipleDisconnectedRoots`/`CycleDetected` stays blocked with the now-smaller graph.

**Failure atomicity matters here and the straightforward implementation gets it wrong.** Clearing
`_blockedMultiRootGraph`/`_blockedMultiRootJournals` *before* calling `RunStartupDiscovery` and only
then registering the result looks natural but isn't safe: if `RunStartupDiscovery` throws (I/O error,
permission failure reading `active/`), the selected journal has already been durably resolved and
relocated on disk, yet the controller has discarded its blocked-graph state with nothing having
replaced it — `State` still reports the stale blocked snapshot while the field backing it is gone, and
a caller catching the exception has no correct state to recover into. The fix: resolve the journal
first (that part is genuinely atomic — either the file write succeeds or it doesn't, and
`TryResolveJournalAsKeepCurrent` already reports which), then attempt rediscovery, and only replace the
blocked-graph fields once rediscovery has actually produced a result. If rediscovery throws, leave the
old blocked-graph fields in place (they're stale — the resolved operation is gone from disk — but
`GetBlockedOperations()` still returning the just-resolved id is a safe staleness: retrying
`ResolveOneMultiRootOperation` on that same id will hit `TryResolveJournalAsKeepCurrent`'s
`AlreadyResolved` outcome and proceed to retry rediscovery, rather than crashing on a re-resolve
attempt). Re-registration itself must also be atomic: a single private `ReplaceDiscoveredRecovery`
helper clears every recovery-related field (`_pendingRecovery`, `_blockedMultiRootGraph`,
`_blockedMultiRootJournals`) and calls `RegisterDiscoveredRecovery` plus `PublishState()` together, so
a multi-root-to-single-root or multi-root-to-none transition can't leave a stale field from the
previous state behind.

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

Only `AuthoritativeOperationIds` (the ones actually independently resolvable — for disconnected roots
these are literal graph leaves, but for a cycle every member is authoritative, since
`OperationRecoveryGraph`'s own semantics name the whole cycle as its authoritative set, not a single
leaf within it) are surfaced, not `AllOperationIds` — a non-authoritative ancestor isn't independently
actionable; it gets folded in automatically once its authoritative descendant resolves and discovery
re-runs.

`AcceptAllAndCloseInterruptedOperations()` stays exactly as-is — the fast bulk option for when the user
doesn't want to inspect individually, unchanged behavior, unchanged tests.

**`Plugin.cs`'s wrapper must not assume resolving one root always reaches `Idle`.** A naive wrapper that
calls `OperationController.ResolveOneMultiRootOperation(id)` and then unconditionally `RunScan()` is
wrong: resolving one root can just as easily leave an ordinary single pending recovery (two roots →
one) or a smaller blocked set (three disconnected roots → two) as it can reach `Idle` (the last root
resolved). In the first two outcomes `CanScan` is still `false`, so an unconditional `RunScan()` either
throws or silently records a misleading error while a recovery is still outstanding. The wrapper must
check `OperationController.State.RequiresRecovery` after the call and only scan when it's `false`:

```csharp
internal void ResolveOneMultiRootOperation(Guid operationId)
{
    OperationController.ResolveOneMultiRootOperation(operationId);
    if (!OperationController.State.RequiresRecovery)
        RunScan();
}
```

## 6. Diagnostics dump v2

`CreateDiagnosticDump()` (`MainWindow.cs:1332-1395`) gains three new sections, reading data the original
design's §10 called for and that already exists but is unread today. Each section is wrapped
independently so one unreadable source (a locked `completed/` directory, a corrupt diagnostics log)
degrades that section's output to an inline failure note rather than aborting the whole dump — the
dump's entire purpose is helping diagnose a problem, so it must stay best-effort:

1. **Interrupted operation section**. Reporting "last updated (approx.) now" — the dump's own creation
   time — would be actively misleading (it's not an approximation of the checkpoint time at all, just
   whenever the user happened to click the button, which could be hours after the interruption). The
   section needs the actual journal timestamp, which means a new accessor:

   ```csharp
   public OperationJournal? GetPendingRecoveryJournal() => _pendingRecovery?.Journal;
   ```

   For the single-root case, report `Stage`, `ProcessedStepCount`/`TotalSteps`, and the real
   `OperationJournal.UpdatedAt` from this accessor. For the multi-root/blocked case (no single
   `_pendingRecovery`, so `GetPendingRecoveryJournal()` returns `null`), fall back to §5's
   `GetBlockedOperations()` and list every blocked journal's `Type`/`Stage`/`UpdatedAt`. If neither
   reports anything, the section reads "(none)".
2. **Recent operations section**: the newest 20 (of whatever retention's already bounded to, §7) entries
   from `completed/*/journal.json`, each showing `Type`/`Stage`/`Resolution`/`UpdatedAt`. Needs a new
   read function, `OperationBundleDiscovery.LoadRecentCompletedJournals(operationsRoot, int take)` —
   lists `completed/`'s subdirectories, loads each journal, sorts by `UpdatedAt` descending, takes the
   requested count. Placed in `OperationBundleDiscovery.cs` alongside its existing `active/`-reading
   logic (same file, same "read journals off disk" responsibility), not a new class. See §7's contract
   for `take`/terminality handling — the same function backs both this section and the History tab's
   live list, so its contract is specified once and applies to both call sites.
3. **Slow-call section**: reads `DiagnosticsLog.ReadAll(DiagnosticsLogPath)`, filters to
   `Kind == SlowCall`, and reports both an aggregate and an individual view — the "worst offenders by
   identifier" framing the original design named means grouping, not just listing the five longest raw
   events (five slow calls to the same identifier would otherwise crowd out four other identifiers that
   are each slow exactly once). Group by `Identifier`, and for each group compute count, worst
   (max) duration, and total duration; show the total event count plus the 5 groups with the highest
   worst-case duration, each line reporting identifier, count, worst, and total:

   ```csharp
   var grouped = slowCalls
       .GroupBy(e => e.Identifier, StringComparer.Ordinal)
       .Select(g => new { Identifier = g.Key, Count = g.Count(), WorstMs = g.Max(e => e.DurationMilliseconds), TotalMs = g.Sum(e => e.DurationMilliseconds) })
       .OrderByDescending(x => x.WorstMs)
       .ThenByDescending(x => x.Count)
       .Take(5)
       .ToList();
   ```

   No new threshold logic — every event in the log already passed `PathMutationOperation`'s existing
   50ms `SlowCallThreshold` (`PathMutationOperation.cs:30`) before being recorded; the dump just reports
   what's already there, grouped for readability.

**Does not add a `RecordException` diagnostic event kind**, even though `DiagnosticEventKind.Exception`
already exists as an enum value with no emitting call site (`IDiagnosticsSink` has only
`RecordSlowCall`/`RecordSlowLiveSnapshot`/`RecordSlowRefresh`). Wiring actual exception recording would
mean deciding *where* in the execution engine to call it — a scope decision touching already-shipped,
already-reviewed B1/C code, not a UI-layer wiring task. Out of scope for this plan; noted in §9.

## 7. Operation history display + retention wiring

**Retention wiring** (the gap found in §0, item 7): `Plugin.cs`'s constructor, immediately after the
existing discovery call (`Plugin.cs:65-66`). Retention is maintenance, not a startup precondition — a
permissions issue, locked directory, or unexpected filesystem failure inside `RunRetentionPass` must
not prevent the plugin from finishing construction, especially since recovery discovery (the thing that
actually matters for correctness) has already completed by this point. `RunRetentionPass` is not
verified to guarantee no exceptions escape it (it does its own filesystem enumeration and deletion), so
the call site needs its own boundary:

```csharp
var discoveredRecovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
OperationController.RegisterDiscoveredRecovery(discoveredRecovery);
try
{
    Organizer.Operations.OperationBundleRetention.RunRetentionPass(OperationsRoot, DateTimeOffset.UtcNow);
}
catch (Exception ex)
{
    Log.Warning(ex, "Operation bundle retention failed; plugin startup will continue.");
}
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

**`LoadRecentCompletedJournals`'s contract**, since it now backs two call sites (here and §6's
diagnostics dump) and needs to be precise about edge cases: `take <= 0` returns `[]` (no negative or
zero-length reads); only journals with `IsTerminal == true` are included, guarding against a
non-terminal journal somehow present under `completed/` (shouldn't happen given how relocation works,
but the read function shouldn't trust the directory it's in over the journal's own state — the same
defensive posture `LoadNonTerminalActiveJournals` already takes toward `active/`); a journal that fails
to parse is skipped, not fatal (matching that same existing pattern).

**The list must not re-read the filesystem every rendered frame.** `ImGui.CollapsingHeader`'s body runs
on every frame the section is expanded, and `LoadRecentCompletedJournals` enumerates `completed/`,
opens and parses every retained journal, sorts, and allocates a new list each call — at 60+ FPS with a
header left open, that's a continuous per-frame disk-read loop, which is exactly the pattern this
codebase has already deliberately avoided elsewhere (restore-preview computation is captured once, on
click, rather than recomputed every frame the confirmation popup is open — see §1's `DrawHistoryTab`
Restore-button note). `MainWindow` caches the result instead of calling the loader from inside the draw
call: a private `_recentOperations`/`_recentOperationsLoaded`/`_recentOperationsError` field triplet,
populated by a `RefreshRecentOperations()` helper that wraps the read in its own try/catch (so one
unreadable `completed/` entry doesn't leave the section blank with no explanation), called once when
the section is first expanded (not on every frame it stays expanded) and again after any action that
changes `completed/`'s contents — a Keep Current resolution (single- or multi-root), an
Accept-All, or a completed Apply/Restore/Continue. The diagnostics dump (§6) is unaffected by this —
it runs once per explicit user click on "Create Diagnostic Dump," not once per frame, so its direct,
uncached call to the loader is fine as-is; only the always-potentially-visible History tab section
needs caching.

## 8. Testing

Pure/xUnit-testable:
- `OperationController.GetPendingRecoveryArtifactStatus()`: returns the correct tuple for
  valid/invalid/missing combinations of plan and snapshot artifact status; returns `null` (not
  throwing) when no pending recovery exists, matching `GetRecoveryAssessment()`'s own convention.
- `OperationController.GetPendingRecoveryJournal()`: returns the pending recovery's journal when one
  exists; returns `null` when it doesn't — the accessor §6's diagnostics dump needs to report the real
  interruption timestamp instead of the dump's own creation time, and also the accessor this section's
  own cycle-resolution test (below) uses to prove *which* operation became authoritative, not merely
  that some journal directory still exists on disk.
- `OperationController.GetBlockedOperations()`: empty when no multi-root block is active; returns
  exactly the authoritative operations (not the full transitive set) with their journals when blocked;
  empty (not throwing) if a requested id's journal somehow isn't in the stored dictionary (defensive,
  matching `RegisterSingleAuthoritative`'s own existing `TryGetValue` defensive pattern).
- `OperationController.ResolveOneMultiRootOperation(Guid operationId)`: resolving the sole remaining member of a two-root
  `MultipleDisconnectedRoots` set transitions the controller to `Idle`, not to a phantom still-blocked
  state; resolving one member of a **3-node cycle** (`A→B→C→A`, `RecoveryOfOperationId` chain)
  transitions to `SingleAuthoritative` **with `GetPendingRecoveryJournal()!.OperationId` asserted equal
  to C specifically** — this is the concrete regression test for §5's hand-traced correctness property,
  and the assertion must name which operation is authoritative, not just that `RequiresRecovery` became
  true or that C's bundle directory exists on disk (directory presence alone doesn't distinguish "C is
  correctly authoritative" from "the controller latched onto the wrong id while C incidentally still
  sits under `active/`"); resolving one of three genuinely disconnected roots leaves the other two still
  blocked with a correctly-shrunk `AuthoritativeOperationIds` list; the resolved journal is correctly
  relocated to `completed/` exactly like `AcceptAllAndCloseInterruptedOperations`'s existing per-journal
  behavior (same collision-safe check, reusing the same extracted helper — proves the extraction didn't
  drop any of the existing safety checks). **The failure-atomicity property itself** (rediscovery
  throwing after the selected journal was already resolved must leave the old blocked-graph fields in
  place rather than clearing them prematurely) is guaranteed by the implementation's ordering — clear
  nothing until a fresh discovery result is in hand — rather than by literally forcing
  `OperationBundleDiscovery.RunStartupDiscovery` to throw in a test: every read failure that function
  can encounter (a missing directory, a corrupt or locked journal file) is already caught and treated
  as "skip this entry," by design, at the layer below it, so there's no portable, non-flaky way to make
  it throw from a plain filesystem-based test. What *is* directly testable, and stands in as the
  regression test for this property, is the retry path the atomicity guarantee exists to make safe:
  resolving an operation whose journal is already durably resolved-and-relocated (simulating "a prior
  call got this far before something interrupted it") must succeed via `TryResolveJournalAsKeepCurrent`'s
  `AlreadyResolved` outcome, not throw or attempt to re-resolve an already-terminal journal.
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
