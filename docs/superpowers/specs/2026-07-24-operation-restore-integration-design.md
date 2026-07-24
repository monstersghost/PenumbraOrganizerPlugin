# Operation Restore Integration (Plan C)

Date: 2026-07-24
Status: Design approved, implementation-ready, not yet planned
Builds on: `docs/superpowers/specs/2026-07-22-operation-controller-design.md` (§13/§14 scoped this
as "Plan C — Restore integration"), the merged Plan B1
(`docs/superpowers/plans/2026-07-23-operation-execution-engine.md`) and Plan B2
(`docs/superpowers/plans/2026-07-23-operation-execution-engine-wiring.md`)

## 1. Scope and relationship to prior work

Plan B2 wired Apply onto the frame-budgeted `OperationController` engine against real Penumbra IPC.
`Plugin.Restore(Guid)` still runs the old synchronous path (`ExecuteOrderedMoves` looping
`SetModPathIpc.Invoke` directly) — this was a deliberate B2 scope boundary, not an oversight. This
plan replaces Restore's entry point with the same async engine Apply already uses, following the
design doc's own §14 "abstraction test": *if Restore needs no branching beyond plan construction and
display metadata, the single-type decision is confirmed.*

That test is now confirmed against real code, not just design intent: `OperationController.StartApply`
has exactly one Apply-specific line (a type guard); the journal/checkpointer/`PathMutationOperation`
construction beneath it is already fully generic over `OperationType`, and
`OperationStateSnapshot.CanStartRestore` already exists from Plan B1 (currently always mirroring
`CanStartApply`, since nothing sets it independently yet). Restore needs no engine changes — only
plan construction (`OperationPlanBuilder`) and orchestration (`Plugin.cs`/`MainWindow.cs`) are new.

**Scope boundary, matching B2's own precedent exactly:** this plan does not update `Config.LastRestore`
or produce a displayed `RestoreResult` classification list from the new async path — those stay
Plan E's job, same as `_lastApplyResults` staying frozen under B2. What this plan *does* guarantee is
that the facts Plan E will need to do that interpretation later are durably persisted now, not
discarded — see §4.

## 2. Architecture overview

```
MainWindow.DrawHistoryTab()
    │  StartRestoreOperation(snapshotId) — fire-and-return
    ▼
Plugin.StartRestoreOperation(Guid snapshotId)
    │  loads target snapshot, reads currentMods, builds RestorePlan (existing RollbackHistory
    │  logic, untouched), builds NamedModMove list, builds OperationPlan + RestoreResultSeed,
    │  persists both into a fresh operation bundle, appends the pre-restore snapshot to
    │  organizer-history.json last, then hands off to the controller
    ▼
OperationController.StartRestore(plan, snapshotId, bundleDirectory)
    │  identical machinery to StartApply - journal, checkpointer, PathMutationOperation - no
    │  Restore-specific branching anywhere in this layer
    ▼
PathMutationOperation / IPenumbraOperations
    (unchanged from Plan B1/B2 - Restore's moves are ordinary SetModPath calls)
```

## 3. `OperationController`: generalize the entry point, and fix a pre-existing recovery-admission gap

`StartApply`'s body has no Apply-specific logic besides its guard. Extract a private `StartOperation`
and make both public methods thin wrappers, avoiding duplication of the journal/checkpointer/mutation
construction:

```csharp
public void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
    StartOperation(plan, snapshotId, bundleDirectory, OperationType.Apply);

public void StartRestore(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
    StartOperation(plan, snapshotId, bundleDirectory, OperationType.Restore);

private void StartOperation(OperationPlan plan, Guid snapshotId, string bundleDirectory, OperationType expectedType)
{
    if (plan.Type != expectedType)
        throw new ArgumentException($"This entry point requires a {expectedType}-type plan; got {plan.Type}.", nameof(plan));
    if (_active is not null && (!_active.Journal.IsTerminal || _active.RequiresRecovery))
        throw new InvalidOperationException("Another organizer operation is already in progress or requires recovery.");

    // ...unchanged body: build preparedJournal, checkpoint, transition to mutatingJournal,
    // checkpoint, construct _active, PublishState()...
}
```

**This also fixes a pre-existing gap in the shipped Plan B1/B2 `StartApply` guard**, not something
this plan introduces: the current guard checks only `!_active.Journal.IsTerminal`, never
`_active.RequiresRecovery`, even though the class's own doc comment states a journal can be
simultaneously terminal *and* `RequiresRecovery` ("a RecoveryRequired transition sets
`_active.RequiresRecovery`... retains every field of the context rather than clearing anything").
`PublishState`'s own `canStartNew = journal.IsTerminal && !_active.RequiresRecovery` already treats
these as one combined invariant — the guard should use the identical rule, not a weaker one. This
isn't reachable through the current UI flow (`Plugin.cs`'s own `_operationInProgress` never resets
while `RequiresRecovery` is true — see §6a), so it isn't a live bug today, but the controller's own
admission guard should be correct on its own terms rather than rely on a caller's separate,
independently-maintained flag to stay safe. Plan C is the first plan to add a second caller through
this exact guard, which is why fixing it belongs here.

Existing `StartApply` tests are unchanged (same public signature/behavior). New tests cover
`StartRestore` succeeding with a `Restore`-type plan and rejecting an `Apply`-type plan (mirroring the
existing `StartApply` rejection test with the type reversed), plus a new regression test:
**`StartRestore` (and `StartApply`) must reject a new operation while the previous `_active` operation
is `Journal.IsTerminal == true` but `RequiresRecovery == true`.**

## 4. `RestoreResultSeed`: durable classification metadata

`RollbackHistory.BuildRestorePlan` already computes the move list plus three additional
classification lists (`UnchangedIdentifiers`/`SkippedUninstalledIdentifiers`/`RootRelocatedIdentifiers`).
`RootRelocatedIdentifiers` is not a mutually-exclusive fifth category alongside `Moved` — it's an
annotation layered over a subset of `Moves` (a moved identifier whose restored destination is
Penumbra's plain root rather than the snapshot's exact stored path), which is why it's persisted
below as a *subset* list, not folded into a five-way partition. Only `Moves` maps onto an
`OperationPlan` directly (via `RecoveryTargets`). The other three lists have no home in
the plan/journal schema and would otherwise be discarded as local variables the moment
`StartRestoreOperation` returns — meaning a restart, or simply Plan E being implemented later, could
never reconstruct what a given Restore operation actually classified. New file
`Organizer/Operations/RestoreResultSeed.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// The parts of RollbackHistory.BuildRestorePlan's classification that don't fit OperationPlan's
/// schema, persisted into the operation's own bundle directory so a later plan can reconstruct the
/// full Moved/Unchanged/SkippedUninstalled/RootRelocated picture without depending on
/// organizer-history.json still holding the target entry or on Plugin.cs's local state surviving a
/// restart. "Moved" identifiers aren't repeated here - they're every identifier already present in
/// the accompanying OperationPlan's RecoveryTargets. RootRelocatedIdentifiers is a subset of those,
/// marking which moves target Penumbra's plain root rather than the snapshot's exact stored path.
/// TargetSnapshot carries the full RollbackSnapshot (not just its Id) for the same reason
/// OperationSnapshotCodec's own pre-restore snapshot copy does: self-contained, independent of
/// organizer-history.json (whose Delete action could otherwise leave a dangling reference).
/// </summary>
public sealed record RestoreResultSeed(
    RollbackSnapshot TargetSnapshot,
    IReadOnlyList<string> UnchangedIdentifiers,
    IReadOnlyList<string> SkippedUninstalledIdentifiers,
    IReadOnlyList<string> RootRelocatedIdentifiers);

/// <summary>
/// Mirrors OperationSnapshotCodec's shape exactly: atomic write, TryLoad never throws.
/// </summary>
public static class OperationRestoreResultSeedCodec
{
    public static void Save(string path, RestoreResultSeed seed) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(seed));

    // Validates structural completeness, not cross-field semantics (e.g. "every RootRelocated
    // identifier is also a moved identifier" is Plan E's concern when it interprets this file
    // against the accompanying OperationPlan, not this codec's). A malformed-but-parseable
    // payload (null target snapshot, null classification list) must not silently pass through as
    // valid data for a later reader to trip over.
    public static bool TryLoad(string path, out RestoreResultSeed? seed)
    {
        seed = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        RestoreResultSeed? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<RestoreResultSeed>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.TargetSnapshot is null || candidate.UnchangedIdentifiers is null
            || candidate.SkippedUninstalledIdentifiers is null || candidate.RootRelocatedIdentifiers is null)
            return false;

        seed = candidate;
        return true;
    }
}
```

`RollbackSnapshot` resolves unqualified here (defined in the immediately-enclosing
`PenumbraOrganizer.Plugin.Organizer` namespace, same as `OperationSnapshotCodec.cs`'s existing
unqualified use of it).

`OperationBundlePaths` gets one new path, alongside the existing `PlanPath`/`SnapshotPath`:

```csharp
public static string RestoreResultSeedPath(string bundleDirectory) => Path.Combine(bundleDirectory, "restore-result-seed.json");
```

## 5. `OperationPlanBuilder`: `NamedModMove`, `BuildNamedMoves`, `BuildRestoreOperationPlan`

Apply's `BuildApplyPlan` starts from `OrganizerModRow` (which already carries `Identifier`/
`CurrentPath`/`ProposedPath`/`Name` together). Restore's input is `RollbackHistory.RestorePlan.Moves`
— `IReadOnlyList<ModMove>`, which has no `Name` field — so a name has to be resolved from `currentMods`
(the `List<LiveMod>` `Plugin.cs` already reads before calling `BuildRestorePlan`). Rather than pass
two correlated collections into the plan builder (a caller could supply moves from one live-state read
and names from another), resolve the join into a single self-contained type first:

```csharp
public sealed record NamedModMove(string Identifier, string ModName, string CurrentPath, string TargetPath);

public static class OperationPlanBuilder
{
    // ...existing BuildApplyPlan unchanged...

    // currentMods is expected identifier-unique - the same invariant Plugin.cs's own
    // ReadCurrentModPaths() already relies on elsewhere (GetModListAdapter keys by Penumbra's own
    // directory identifier), but enforced explicitly here (unlike that existing call site) so a
    // violation fails with a clear diagnostic naming the offending identifiers, not a bare LINQ
    // ArgumentException from ToDictionary. Every move's identifier is guaranteed present in
    // currentMods by construction: RollbackHistory.BuildRestorePlan only ever emits a move for a
    // mod found in both the target snapshot and currentMods. The lookup below still throws with a
    // named identifier if that invariant is ever violated, rather than failing later with a bare
    // KeyNotFoundException.
    public static IReadOnlyList<NamedModMove> BuildNamedMoves(IReadOnlyList<ModMove> moves, IReadOnlyList<LiveMod> currentMods)
    {
        var duplicates = currentMods
            .GroupBy(m => m.Identifier, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Current mod list contains duplicate identifiers: {string.Join(", ", duplicates)}");

        var nameByIdentifier = currentMods.ToDictionary(m => m.Identifier, m => m.Name, StringComparer.Ordinal);
        return moves
            .Select(m => new NamedModMove(
                m.Identifier,
                nameByIdentifier.TryGetValue(m.Identifier, out var name)
                    ? name
                    : throw new InvalidOperationException($"Restore move for '{m.Identifier}' has no matching live mod."),
                m.CurrentPath, m.TargetPath))
            .ToList();
    }

    public static OperationPlan BuildRestoreOperationPlan(IReadOnlyList<NamedModMove> namedMoves)
    {
        var moves = namedMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList();
        var restoreSteps = ApplyPlanner.OrderMovesForApply(moves);

        var executionSteps = restoreSteps
            .Select((s, index) => new OperationExecutionStep(
                index, s.Identifier, s.TargetPath,
                s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
                s.GroupId))
            .ToList();

        var recoveryTargets = namedMoves
            .Select(m => new OperationRecoveryTarget(m.Identifier, m.CurrentPath, m.TargetPath, m.ModName))
            .ToList();

        return OperationPlan.Create(OperationType.Restore, executionSteps, recoveryTargets);
    }
}
```

Splitting `BuildNamedMoves` out from `BuildRestoreOperationPlan` keeps the identifier-resolution
failure mode independently unit-testable from plan construction. `LiveMod`/`ModMove` resolve
unqualified (both defined in the enclosing `PenumbraOrganizer.Plugin.Organizer` namespace, same
pattern the existing `BuildApplyPlan` already relies on).

`BuildRestoreOperationPlan` needs no separate duplicate check on `namedMoves` itself: I confirmed
`OperationPlan.Create`'s existing `Validate` already rejects a duplicate recovery-target identifier
with a precise diagnostic (`"Duplicate recovery target identifier '{t.Identifier}'."`, via
`targetByIdentifier.TryAdd` in `OperationPlan.cs`) — this path is already exercised by `BuildApplyPlan`
today and requires no new code, only a test confirming `BuildRestoreOperationPlan` surfaces the same
existing diagnostic when given duplicate `namedMoves` identifiers.

**Zero-move plans are a real, valid Restore outcome** (everything already matches, or every touched
mod was uninstalled/protected) and must be verified to work end-to-end through the real engine, not
just accepted by `OperationPlan.Validate`. Confirmed by reading `PathMutationOperation`'s step loop
(`while (index < _plan.ExecutionSteps.Count)`): a zero-length plan falls through on the very first
`Advance` call and reports `MutationFinished` immediately — a generic loop guard, not a special case.
But no existing test in this repo exercises a genuinely empty plan through the *real*
`OperationController`/`PathMutationOperation` pipeline end-to-end (only `OperationPlanBuilderTests`
exercises empty *construction*) — this is a previously-unexercised path in the shared engine that
Restore is the first caller to expose, and this plan adds the missing coverage at the
`OperationController` level (see §9).

## 6. `Plugin.cs`: `StartRestoreOperation`

Mirrors `StartApplyOperation()`'s shape, reusing everything `Restore(Guid)` already does to build a
`RestorePlan` — only the tail changes from synchronous `ExecuteOrderedMoves` to an async hand-off.
Two guards run before any side effect: `_operationInProgress` (the existing UI-level gate) and a new
`OperationController.State.CanStartRestore` preflight read directly from the controller's own
authoritative state, before the bundle directory or any file exists. The second guard is defense in
depth, not redundant — it does not itself prove `_operationInProgress` stays perfectly synchronized
with the controller (see §6a for why that synchronization is expected to hold, not assumed). A narrow
time-of-check/time-of-use gap remains between this preflight and `OperationController.StartRestore`'s
own admission a few lines later; closing it fully would require the controller to reserve an admission
slot atomically before any bundle file becomes externally visible, which this plan does not build
(the plugin's actual call pattern is single-threaded and UI-driven — both entry points only ever fire
from a button click on the UI thread — so the gap has no live trigger today, but it is not eliminated
structurally, and this document says so rather than claiming a guarantee the code doesn't provide).

Ordering is additionally deliberate: every pure computation and every bundle-local file write happens
*before* the pre-restore snapshot is appended to the user-visible `organizer-history.json`, so a
failure anywhere in plan/bundle construction cannot leave a "Snapshot before restoring..." history
entry with no accompanying restore (the actual data-loss-adjacent risk in the original draft of this
design). This narrows, rather than eliminates, the residue window: a failure between the history
append and a successful `OperationController.StartRestore` call can still leave an orphaned bundle
directory with no discoverable operation — accepted as the same class of residue `StartApplyOperation`
already carries today (full transactional bundle-staging is out of scope for this plan, matching B2's
own precedent). Likewise, a failure *partway through* the three bundle-local writes (plan/snapshot/
result-seed) can leave one or two files written and the third missing — this is bundle-local residue
with no history entry and no active controller operation, not the stronger "fully atomic bundle
write" some phrasing earlier in this design implied; §11's test list is worded to match that.

```csharp
internal void StartRestoreOperation(Guid snapshotId)
{
    if (_operationInProgress)
        throw new InvalidOperationException("Another organizer operation is already in progress.");
    // Defense-in-depth alongside _operationInProgress, not a replacement for it: reads the
    // controller's own authoritative state before any side effect below runs. A narrow TOCTOU gap
    // remains between this check and OperationController.StartRestore's own admission guard - see
    // this method's own doc comment for why that gap is accepted rather than closed with a
    // reservation API.
    if (!OperationController.State.CanStartRestore)
        throw new InvalidOperationException("Another organizer operation is already in progress or requires recovery.");

    var history = Organizer.RollbackHistory.Load(HistoryFilePath);
    var target = history.FirstOrDefault(s => s.Id == snapshotId)
        ?? throw new InvalidOperationException("Snapshot not found.");

    var currentMods = ReadCurrentMods();

    // Current protection state is deliberately never passed to BuildRestorePlan - unchanged
    // reasoning from the synchronous Restore() path (tester report, Bug 3).
    var restorePlan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);
    var namedMoves = Organizer.Operations.OperationPlanBuilder.BuildNamedMoves(restorePlan.Moves, currentMods);
    var plan = Organizer.Operations.OperationPlanBuilder.BuildRestoreOperationPlan(namedMoves);

    var preRestoreLabel = target.Label ?? target.CreatedAt.ToString("u");
    var preRestoreSnapshot = Organizer.RollbackHistory.CaptureSnapshot(
        currentMods, label: null, autoDescription: $"Snapshot before restoring to \"{preRestoreLabel}\"");

    var resultSeed = new Organizer.Operations.RestoreResultSeed(
        target, restorePlan.UnchangedIdentifiers, restorePlan.SkippedUninstalledIdentifiers, restorePlan.RootRelocatedIdentifiers);

    var bundleDirectory = Organizer.Operations.OperationBundlePaths.BundleDirectory(OperationsRoot, active: true, plan.OperationId);
    Organizer.Operations.OperationPlanCodec.Save(Organizer.Operations.OperationBundlePaths.PlanPath(bundleDirectory), plan);
    Organizer.Operations.OperationSnapshotCodec.Save(Organizer.Operations.OperationBundlePaths.SnapshotPath(bundleDirectory), preRestoreSnapshot);
    Organizer.Operations.OperationRestoreResultSeedCodec.Save(
        Organizer.Operations.OperationBundlePaths.RestoreResultSeedPath(bundleDirectory), resultSeed);

    // Everything above is pure computation or a bundle-local write; only after all of it succeeds
    // does the operation become visible in the user-facing history file - see this method's own
    // doc comment for why the ordering matters.
    Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, preRestoreSnapshot);

    _operationInProgress = true;
    try
    {
        OperationController.StartRestore(plan, preRestoreSnapshot.Id, bundleDirectory);
    }
    catch
    {
        _operationInProgress = false;
        throw;
    }
}
```

`Restore(Guid)` (the old synchronous method) stays in place, superseded by this method as the
History tab's entry point but not deleted this plan (see §8).

**Exception-safety retrofit to `StartApplyOperation`:** `StartApplyOperation()`'s existing
`_operationInProgress = true; OperationController.StartApply(...)` has the identical unguarded gap —
if `StartApply` throws, `_operationInProgress` stays permanently `true`, soft-locking every other
organizer operation until plugin reload. Since this plan is touching the exact same pattern for
Restore, the fix is a two-line change applied to `StartApplyOperation` in the same plan rather than
left as a known bug:

```csharp
_operationInProgress = true;
try
{
    OperationController.StartApply(plan, snapshot.Id, bundleDirectory);
}
catch
{
    _operationInProgress = false;
    throw;
}
```

## 6a. Where `_operationInProgress` resets on normal (non-throwing) completion

Neither `StartApplyOperation` nor `StartRestoreOperation` resets `_operationInProgress` back to
`false` on success — that already happens elsewhere, in `Plugin.OnFrameworkUpdate` (subscribed to
`Framework.Update`, runs every frame):

```csharp
private void OnFrameworkUpdate(IFramework framework)
{
    OperationController.Update();
    if (_operationInProgress && OperationController.State.CanStartApply)
        _operationInProgress = false; // the async Apply operation just reached a terminal stage
}
```

**This check already resets the flag correctly for a completed Restore today, with no code change
needed** — but its comment is now misleading and must be corrected, because the reason it works is
non-obvious. `OperationStateSnapshot.CanStartApply` and `CanStartRestore` are not independently
derived: `PublishState` sets every `CanStartX` field (`CanStartApply`, `CanStartRestore`, `CanScan`,
`CanIndex`, etc.) to the exact same `canStartNew` boolean, because there is only ever one `_active`
operation at a time — "can something new start" is one global fact, exposed under several
field names for each UI call site's convenience, not five independently-tracked permissions. Checking
`CanStartApply` is therefore equivalent to checking `CanStartRestore` today, and this plan relies on
that equivalence rather than adding a redundant `|| CanStartRestore` to the condition. The comment
changes to:

```csharp
if (_operationInProgress && OperationController.State.CanStartApply)
    _operationInProgress = false; // any async organizer operation (Apply or Restore) just reached
                                   // a terminal, non-recovery stage - CanStartApply/CanStartRestore
                                   // are guaranteed equal today (PublishState derives both from one
                                   // shared canStartNew), so checking either detects completion of
                                   // either operation type. If a future plan ever splits them apart
                                   // per-type, this check must be revisited.
```

If a future plan does split `CanStartApply`/`CanStartRestore` into independently-derived values, this
line becomes wrong silently — the doc comment is written to make that consequence explicit for
whoever makes that change, rather than leaving it to be rediscovered as a bug.

## 7. `Preview​Restore` — untouched

`Plugin.PreviewRestore(Guid)` (the read-only computation backing the confirmation popup) stays exactly
as-is: synchronous, no snapshot capture, no side effects. No change needed.

## 8. Dead code: mark obsolete once callers reach zero

B2 left `ApplyChanges()`/`ExecuteOrderedMoves()` in place as unused, uncommented dead code. Once
`StartRestoreOperation` replaces `Restore(Guid)` as the History tab's entry point,
`ExecuteOrderedMoves(...)` has zero remaining callers anywhere in the codebase (previously kept alive
by `Restore(Guid)`). This plan marks all three legacy methods obsolete-as-error rather than leaving
them silently callable:

```csharp
[Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
internal IReadOnlyList<Organizer.ApplyResult> ApplyChanges() { /* unchanged body */ }

[Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId) { /* unchanged body */ }

[Obsolete("Legacy synchronous path, superseded by the async operation engine. Do not call.", error: true)]
private Dictionary<string, string> ExecuteOrderedMoves(IReadOnlyList<Organizer.ModMove> moves) { /* unchanged body */ }
```

Build must stay clean (0 warnings/errors) with these attributes present and zero remaining callers —
that is the actual proof of unreachability, not just an assertion in a doc comment. If any caller is
found to remain (there should not be any after this plan's MainWindow changes in §9), that's a task
failure to fix, not a warning to suppress.

**`Restore(Guid)` calls `ExecuteOrderedMoves(...)` internally, and both are marked obsolete-as-error
here — this does not break the build.** Verified empirically, not assumed: a throwaway two-method
repro (`[Obsolete(error: true)] A()` calling `[Obsolete(error: true)] B()`) compiled with `dotnet
build` and produced 0 warnings/0 errors; the CS0619 diagnostic only fires when a *non-obsolete* caller
reaches an obsolete member, which is exactly the situation these three methods are in relative to each
other once nothing outside the trio calls into them. No fallback (unannotated private helper,
non-error `Obsolete`, outright deletion) is needed as a result.

## 9. `MainWindow`: History tab wiring

The Restore button's click handler changes from `_lastRestoreResults = _plugin.Restore(snapshotId);`
(synchronous, immediate) to a fire-and-return call plus local state reset:

```csharp
_plugin.StartRestoreOperation(snapshotId);
_lastRestoreResults = null;
_restoreOperationActive = true;
```

Clearing `_lastRestoreResults` on start matters: otherwise the History tab would keep showing a
*previous* restore's results while a new one is in flight, which is misleading state attribution, not
merely stale UI. A new `_restoreOperationActive` field (parallel to the existing
`_applyOperationActive`) drives completion detection:

```csharp
var operationState = _plugin.OperationController.State;
if (_restoreOperationActive && operationState.Kind == OperationType.Restore && operationState.CanStartRestore)
{
    _restoreOperationActive = false;
    _historyCache = null;
    RunScan();
}
```

**Why `Kind == OperationType.Restore` is required, not optional:** `OperationStateSnapshot.CanStartApply`
and `CanStartRestore` are, today, *literally the same value* — confirmed by reading
`OperationController.PublishState()`, both are set to one shared `canStartNew` derived from whichever
single operation is currently `_active`, with no per-type split. Gating this block on
`operationState.CanStartRestore` alone would also read `true` the moment an *Apply* completes,
causing the History tab to wrongly believe a Restore just finished. Combining with `Kind` (which
*is* set correctly per-operation by `PublishState`) closes that without adding a new field to a record
Plan B1 already shipped and reviewed. The identical misfiring is benign on the Apply tab today (a
redundant `RunScan()` call with no user-visible impact, since the completion-detection *rendering*
text is separately gated on `Kind`) — but the logic is unsound and future-proof requires the `Kind`
check symmetrically on both tabs regardless. This plan adds it to both.

Status text mirrors the Apply tab's structure with corrected copy and the same type gate:

```csharp
if (_restoreOperationActive)
{
    if (operationState.Kind == OperationType.Restore && !operationState.CanStartRestore && !operationState.RequiresRecovery)
        ImGui.TextUnformatted($"Restoring... {operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage}).");
    else if (operationState.Kind == OperationType.Restore && operationState.RequiresRecovery)
        ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");
}
```

`Config.LastRestore` is not updated by this new path and stays frozen at its last synchronous-Restore-
era value — the confirmed scope boundary from §1, matching `_lastApplyResults` under B2.

## 10. Why `RunScan()` runs unconditionally on completion

The completion block above calls `RunScan()` on every terminal, non-recovery reach, with no branching
on *which* terminal outcome (success / partial failure / cancelled). Two things justify this rather
than differentiating by outcome:

- `RequiresRecovery` already structurally prevents the block from firing at all — it's baked into
  `CanStartRestore`'s own derivation (`journal.IsTerminal && !RequiresRecovery`) — so "must not rescan
  during pending recovery" is already satisfied, not a gap this plan introduces.
- Apply's existing completion block (`364e105`) already reruns `RunScan()` unconditionally on every
  terminal-non-recovery outcome. Restore matching that is consistency with already-shipped, already-
  tested behavior, not a new risk. Outcome-differentiated post-processing (what a "partial" Restore
  should visually communicate) is Plan E's territory per the boundary in §1.

## 11. Testing

**Pure/xUnit-testable** (new tests, mirroring existing `StartApply`/`BuildApplyPlan` coverage):

- `OperationController.StartRestore`/`StartApply`: happy path with a `Restore`-type plan; rejects an
  `Apply`-type plan (mirrors the existing `StartApply` rejection test, type reversed); **rejects a new
  operation while the previous `_active` operation is `Journal.IsTerminal == true` but
  `RequiresRecovery == true`** (the guard fix in §3 — this is the most important new test in this
  plan, proving the controller's own admission rule matches its own `PublishState` derivation).
- **A genuinely empty `ExecutionSteps`/`RecoveryTargets` plan started via `StartRestore` reaches a
  terminal, UI-consumable state after the usual three `Update` calls, asserted in full**: `Kind ==
  OperationType.Restore`, `CanStartRestore == true`, `RequiresRecovery == false`, `ProcessedSteps ==
  0`, `TotalSteps == 0`. Verified empirically while writing the implementation plan, correcting an
  earlier assumption in this document: `Update()` advances at most one stage per call (each stage is
  its own early-return branch in `AdvanceActiveOperation`), so a zero-step plan needs the same three
  calls as any other plan (Mutating→Refreshing→Verifying→Completed) - it just has nothing to do
  during the first one. Refreshing/Verifying still call into the adapter even with zero recovery
  targets (confirmed by running the test with no adapter responses enqueued: it settles as
  `FailedBeforeMutation`, not `Completed`), so the test still needs a `RefreshResult.Success` and an
  empty `LiveModReadResult` enqueued. This single test both covers the zero-step gap identified in §5
  *and* proves the terminal-retention claim in §9 (`Kind` surviving into a terminal, UI-consumed
  snapshot) with a real assertion rather than an inference from a doc comment.
- `OperationPlanBuilder.BuildNamedMoves`: happy path resolves names correctly; throws naming the
  offending identifiers when `currentMods` contains a duplicate identifier; throws with a named
  identifier when a move's identifier isn't found in `currentMods`.
- `OperationPlanBuilder.BuildRestoreOperationPlan`: a cyclic restore produces correct temporary/final
  steps and `GroupId`s (mirrors `BuildApplyPlan`'s existing cycle test); recovery targets carry
  `CurrentPath`→`TargetPath`, never a temporary cycle-breaking hop path; duplicate `namedMoves`
  identifiers surface `OperationPlan.Create`'s existing "Duplicate recovery target identifier"
  diagnostic (confirms the existing check applies here too, per §5 — no new validation code needed).
- `RestoreResultSeed`/`OperationRestoreResultSeedCodec`: round-trips all four fields including the
  full `TargetSnapshot`; `TryLoad` rejects a payload with a null `TargetSnapshot` or a null
  classification list; a saved `OperationPlan` with `Type: OperationType.Restore` round-trips through
  `OperationPlanCodec` correctly (confirms `Restore` survives serialization, not just the
  previously-only-exercised `Apply`).

**Not automatable** (Dalamud-coupled `Plugin.cs`/`MainWindow.cs` orchestration — per the explicit
decision not to extract an injectable/testable orchestration service this plan, this stays
build-verified plus a manual checklist only; naming this here as accepted risk rather than an
unavoidable limitation, per the review that shaped this plan):

- Starting Restore while Apply is active is rejected by the `_operationInProgress`/`CanStartRestore`
  preflight in §6, before any bundle file or history entry is created — so this case produces no
  residue at all (the preflight runs first, before any write).
- A failure *partway through* the three bundle-local writes (plan/snapshot/result-seed) or between
  the last write and the history append leaves **no history entry and no active controller
  operation** (guaranteed by the ordering in §6) but **may leave partial bundle-local residue** — one
  or two of the three files written, the bundle directory itself present with no accompanying journal.
  This is weaker than "no residue at all" and the manual checklist should be worded to match: verify
  the absence of history/controller state, not the absence of any file on disk.
- Controller-start failure resets `_operationInProgress` (guaranteed by the try/catch in §6, same
  caveat — only exercisable live, not by an automated test).
- The History tab never shows Restore progress while an Apply is running, and vice versa.
- A Restore's `restore-result-seed.json` file exists in its bundle directory after a real in-game run
  (Plan D's job is reading it back; this plan's job is only making sure it's there to read).

## 12. What this plan still does not cover

- Interpreting `RestoreResultSeed` plus execution outcomes into a displayed `RestoreResult` list, or
  updating `Config.LastRestore` — Plan E's job, same boundary as Apply's `_lastApplyResults`. This
  plan's job is making sure the *facts* survive to be interpreted later, not doing the interpreting.
- Recovery resolution (Continue/Restore Previous State/Keep Current) for a `RequiresRecovery` Restore
  — Plan D, which was always scoped to cover both operation types generically.
- The real progress UI / recovery dialog for either tab — Plan E, unchanged from B2's own deferral.
- Deleting `ApplyChanges()`/`Restore(Guid)`/`ExecuteOrderedMoves()` outright — marked `[Obsolete(error:
  true)]` per §8, not removed.
- Extracting Restore's (or Apply's) `Plugin.cs` orchestration into an injectable, unit-testable
  service — explicitly declined; the untested-orchestration risk in §11 is accepted, not hidden.

## 13. Global constraints for the implementation plan

- `dotnet build` must remain 0 warnings/errors, including after the `[Obsolete(error: true)]`
  attributes land — this is the actual proof those methods have zero remaining callers.
- No automated test may attempt to mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC to force
  unit coverage onto `Plugin.cs`/`MainWindow.cs` — same documented limitation carried forward from
  Plan B1/B2.
- `PreviewRestore` and `RollbackHistory.BuildRestorePlan` are out of scope for modification — this plan
  consumes their existing output, it does not change their logic or signatures.
- `sealed record` for data types (`NamedModMove`, `RestoreResultSeed`), `static class` for pure
  stateless logic (`OperationRestoreResultSeedCodec`, extensions to `OperationPlanBuilder`) — carried
  forward from Plan B1/B2's own conventions.
