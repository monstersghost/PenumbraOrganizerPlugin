# Operation Recovery Classification and Keep Current (Plan D1)

Date: 2026-07-24 (revised after first review round)
Status: Design draft, revised, not yet re-reviewed
Builds on: `docs/superpowers/specs/2026-07-22-operation-controller-design.md` (§7-9, §13 scoped this
as "Plan D — Recovery"), the merged Plan A2
(`docs/superpowers/plans/2026-07-22-operation-storage-and-recovery-discovery.md`), Plan B1/B2, and
Plan C (`docs/superpowers/specs/2026-07-24-operation-restore-integration-design.md`)

## 1. Scope and relationship to prior work

Plan D was split in two, given its combined scope (classification, startup wiring, and all three
recovery resolutions) is comparable to prior split plans (A1/A2, B1/B2):

- **Plan D1 (this plan):** `RecoveryClassifier`, `RecoveryAssessment`, startup discovery wiring into
  `Plugin.cs`, the **Keep Current** resolution end-to-end, a bulk **"Accept Current State and Close
  All Interrupted Operations"** fallback for the `MultipleDisconnectedRoots`/`CycleDetected` case, and
  **a crude, minimal `MainWindow` recovery panel** exposing both. D1 cannot safely wire startup
  enforcement (disabling every organizer action whenever an interrupted operation is found) without
  giving the user *some* way to resolve it, for *every* case discovery can produce — not just the
  common single-authoritative one. An explanatory message with no action for the multi-root/cycle case
  is still a permanent, unrecoverable lockout with no escape hatch short of manual filesystem surgery;
  that's not an acceptable interim state even for a "crude" plan. This matches the same precedent
  every prior plan already set (B2/C both shipped a crude UI stub alongside their backend, never
  backend-only) — extended here to mean *every* state the backend can reach needs *some* resolution,
  not just the common one.
- **Plan D2 (later):** Continue and Restore Previous State — both start a *new* operation from
  residual moves, reusing `OperationController.StartApply`/`StartRestore` (Plan C) unchanged.

**A real, currently-live gap this plan closes:** grepped `Plugin.cs` for any reference to
`OperationBundleDiscovery`/`RequiresRecovery` — there is none. Plan A2 already built full startup
discovery (`OperationBundleDiscovery.RunStartupDiscovery`, `OperationRecoveryGraph.Analyze`) and it
has never been wired into anything. Today, if the plugin crashes mid-Apply/Restore and the game
restarts, the plugin has no idea a prior operation was interrupted.

**A second, independent bug found in already-shipped Plan A2 code while designing this plan's startup
wiring** (confirmed by hand-tracing `OperationRecoveryGraph.Analyze` against an empty journal list,
the ordinary clean-startup case): `leaves.Count switch { 1 => SingleAuthoritative, _ =>
MultipleDisconnectedRoots }` sends `leaves.Count == 0` into the same branch as `leaves.Count >= 2` —
**the normal "nothing to recover" case is currently misclassified as `MultipleDisconnectedRoots`**,
with empty ID lists. There is no status in the shipped `OperationRecoveryGraphStatus` enum
(`SingleAuthoritative`/`MultipleDisconnectedRoots`/`CycleDetected` only) for "no non-terminal journals
exist." This plan fixes it directly (§4a below) rather than working around it in D1's own code,
matching the precedent of Plan C fixing a Plan B1 bug it found during its own design phase — this
touches already-shipped, already-tested Plan A2 code, called out explicitly rather than folded in
silently.

## 2. A key simplification found while grounding this plan: Continue doesn't resume `PathMutationOperation`

Read `PathMutationOperation`'s constructor and `StepResultReconciler` before writing this plan,
expecting D2 (not in scope here, but the finding affects D1's architecture) would need to reconstruct
a `PathMutationOperation` from disk to resume a crashed operation mid-flight. It doesn't:
`PathMutationOperation`'s constructor takes only `(plan, adapter, clock, diagnostics, bundleDirectory)`
and starts its `_stepDispositions` empty — there is no "resume with prior progress" constructor
anywhere in this codebase. The original design's own §9 ordering confirms this is intentional, not a
gap: Continue "build[s] and validate[s] the continuation `OperationPlan` (**new** `OperationId`,
**fresh** `ExecutionSteps` from replanning residual moves, **fresh** `RecoveryTargets`)... Activate
the new operation" — i.e., Continue computes residual moves and starts an entirely new operation via
the already-generalized `StartApply`/`StartRestore`, exactly like any ordinary Apply/Restore. It does
not resume the interrupted operation's own `PathMutationOperation` in place.

**This directly answers an architecture question for D1's own scope:** a discovered interrupted
operation from a prior crash must **not** be shoehorned into the existing `_active`/
`ActiveOperationContext` slot (which exists to be actively advanced by `Update()` calling
`PathMutationOperation.Advance` every frame). Nothing progresses a discovered-but-unresolved recovery
frame-to-frame — it sits frozen until a human picks a resolution. It needs its own state, separate
from `_active`.

## 3. Architecture overview

```
Plugin() constructor
    │  OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot) — pure file I/O, no IPC
    ▼
OperationController.RegisterDiscoveredRecovery(discovery)
    │  explicit switch over the real (now 4-value, see §4a) OperationRecoveryGraphStatus:
    │    NoRecoveryNeeded  → no-op, controller starts Idle exactly as today
    │    SingleAuthoritative → stores _pendingRecovery (journal + bundle dir), RequiresRecovery=true,
    │                          CanResolveRecovery=true immediately (Keep Current needs no classification)
    │    MultipleDisconnectedRoots / CycleDetected → stores _blockedMultiRootGraph,
    │                          RequiresRecovery=true, CanResolveRecovery=true too (the bulk fallback
    │                          below is a real resolution, just a blunter one than the single-journal case)
    ▼
OperationController.Update() — called every Framework.Update tick, same as today
    │  NEW: if _pendingRecovery's ClassificationStatus is still WaitingForProvider, and the per-
    │  attempt throttle interval has elapsed, try to advance it (check plan/snapshot artifacts once
    │  ever, then call GetLiveMods() at most once per second); a permanently-invalid artifact or an
    │  InvalidData IPC response moves it straight to ClassificationUnavailable, never retried again
    ▼
MainWindow.Draw() — NEW, before the existing tab bar
    │  if RequiresRecovery: render a crude recovery panel (see §9) instead of/above normal tab content
    │  if IsBlockedByMultipleRoots: "Accept Current State and Close All Interrupted Operations" button
    │                          → Plugin.AcceptAllAndCloseInterruptedOperations()
    │  else: "Keep Current State" button → Plugin.ResolveKeepCurrent()
    ▼
Plugin.ResolveKeepCurrent() / AcceptAllAndCloseInterruptedOperations()
    │  Both share one commit-point rule (§7): persist each resolved (terminal) journal FIRST - that's
    │  the commit point - then best-effort relocate active/ → completed/ via a shared, collision-
    │  verifying helper (checks the existing destination's OperationId/terminality/Resolution actually
    │  match before treating it as already-done, never just its existence). ResolveKeepCurrent always
    │  clears its lock once the one journal is persisted, regardless of relocation outcome.
    │  AcceptAllAndCloseInterruptedOperations resolves every journal in the blocked graph (not only the
    │  authoritative leaves - an unresolved non-leaf ancestor would recreate the lockout at the next
    │  startup) and only clears its lock once ALL of them persisted successfully.
    ▼
    RunScan() - controller has no OrganizerState access, stays a Plugin.cs/MainWindow responsibility

(Plan D2: Continue / Restore Previous State - not in this plan, reuses StartApply/StartRestore
 unchanged per §2)
(Plan E: the real recovery dialog UI and root-selection - not in this plan)
```

## 4a. Fixing the Plan A2 bug: `OperationRecoveryGraphStatus` needs a fourth value

`OperationRecoveryGraph.cs` (Plan A2, out of scope for modification per that plan's own boundary, but
this specific fix is in scope for D1 per the decision in §1):

```csharp
public enum OperationRecoveryGraphStatus { NoRecoveryNeeded, SingleAuthoritative, MultipleDisconnectedRoots, CycleDetected }
```

In `Analyze`, before the existing `leaves.Count switch`:

```csharp
if (allIds.Count == 0)
    return new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.NoRecoveryNeeded, [], []);
```

Everything else in `Analyze`/`TryFindCycle`/`TryFindCoreCycle` is unchanged — this is a single early
return for the one previously-unhandled input shape, not a rewrite. The existing
`OperationRecoveryGraphTests.cs` needs one new test asserting `Analyze([])` returns
`NoRecoveryNeeded` with both ID lists empty — the exact clean-discovery result
`OperationBundleDiscovery.RunStartupDiscovery` produces when no interrupted bundles exist, matching
what the first review round specifically asked to be tested.

## 4. `RecoveryClassifier`: per-target classification against live state, plan only

**Confirmed dependency-only-on-Plan, not Snapshot** (this was flagged in review, verified now):
`Classify` below reads `plan.RecoveryTargets` (`SnapshotRawPath`/`FinalRawPath`, both embedded in the
plan at construction time — never re-read from the separate `snapshot.json` file) and
`plan.ExecutionSteps` (for `CycleBreakingTemporaryMove` targets). It never touches a `RollbackSnapshot`
at all. This means classification is possible with a valid plan and a missing/invalid snapshot file —
the dependency matrix in §7 reflects this.

**Deviation from the original design's literal enum, with reasoning, confirmed correct in review:**
the original draft's `ItemRecoveryState` included `MissingSnapshot`/`MissingPlan`; both are dropped
here as redundant with artifact-level status — a target cannot be "missing the plan" while being
iterated from that same plan, and `ArtifactCheckStatus` (§5) already covers whole-operation artifact
absence.

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ItemRecoveryState { AtSnapshot, AtIntended, AtBoth, AtKnownIntermediate, AtNeither, MissingLive }

public sealed record ItemRecoveryClassification(string Identifier, ItemRecoveryState State);

/// <summary>
/// Design doc section 8, reconciled against shipped code. Classifies each of the interrupted plan's
/// RecoveryTargets against live state, using PenumbraPathSemantics.AreEquivalent (never raw string
/// equality) for every path comparison - matching how OperationPlan's own integrity hash and every
/// existing planner already compares paths. Depends only on OperationPlan, never RollbackSnapshot -
/// every path this classifier needs (SnapshotRawPath, FinalRawPath, temporary hop targets) is already
/// embedded in the plan at construction time.
/// </summary>
public static class RecoveryClassifier
{
    public static IReadOnlyList<ItemRecoveryClassification> Classify(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
        // Exactly one temporary step per identifier is guaranteed by ApplyPlanner.OrderMovesForApply's
        // own structure, not assumed: each identifier appears in exactly one chain (the algorithm's
        // `visited` set prevents an identifier's CurrentPath from being entered twice), and only
        // chain[0] of a cycle - entered once - ever receives IsTemporary: true. Verified by reading
        // OrderMovesForApply directly (ApplyPlanner.cs lines ~77-108), not inferred from behavior.
        var temporaryTargetByIdentifier = plan.ExecutionSteps
            .Where(s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove)
            .ToDictionary(s => s.Identifier, s => s.TargetRawPath, StringComparer.Ordinal);

        return plan.RecoveryTargets
            .Select(t => new ItemRecoveryClassification(t.Identifier, ClassifyOne(t, liveSnapshot, temporaryTargetByIdentifier)))
            .ToList();
    }

    private static ItemRecoveryState ClassifyOne(
        OperationRecoveryTarget target, LiveModSnapshot liveSnapshot,
        IReadOnlyDictionary<string, string> temporaryTargetByIdentifier)
    {
        if (!liveSnapshot.Mods.TryGetValue(target.Identifier, out var live))
            return ItemRecoveryState.MissingLive;

        var atFinal = PenumbraPathSemantics.AreEquivalent(live.FullPath, target.FinalRawPath, target.ModName);
        var atSnapshot = PenumbraPathSemantics.AreEquivalent(live.FullPath, target.SnapshotRawPath, target.ModName);

        // AtBoth means live state is semantically equivalent to BOTH the snapshot and intended paths
        // (per PenumbraPathSemantics, not raw string identity) - not necessarily that the two raw
        // paths themselves are byte-identical. Continuation planning (D2) must keep using semantic
        // equivalence consistently here, never assume raw-path identity from this state alone.
        if (atFinal && atSnapshot)
            return ItemRecoveryState.AtBoth;
        if (atFinal)
            return ItemRecoveryState.AtIntended;
        if (atSnapshot)
            return ItemRecoveryState.AtSnapshot;
        if (temporaryTargetByIdentifier.TryGetValue(target.Identifier, out var tempPath)
            && PenumbraPathSemantics.AreEquivalent(live.FullPath, tempPath, target.ModName))
            return ItemRecoveryState.AtKnownIntermediate;

        return ItemRecoveryState.AtNeither;
    }
}
```

`AtKnownIntermediate` alone does not prove Continue is safe for that identifier — per design §8a,
that's proven by attempting to replan, which is D2's job.

**Duplicate live identifiers** (`liveSnapshot.DuplicateIdentifiers` non-empty) mean "live state can't
be trusted" per `LiveModSnapshot`'s own doc comment. `Classify` stays unconditional regardless — it's
the *resolution* layer's job to decide what to do with an untrustworthy assessment, and per §7's
dependency matrix, Keep Current explicitly tolerates this (it doesn't read the assessment at all).

## 5. Artifact checking: a real "not yet attempted" state, checked once

**Redesigned per review** (the original draft reused one `ArtifactStatus` enum for two differently-typed
fields — nothing prevented assigning `PlanMissing` to a snapshot-status field — and re-ran the
file-system check on every `Update()` tick indefinitely):

```csharp
public enum ArtifactCheckStatus { Unchecked, Valid, Missing, Invalid }

public static class ArtifactStatusChecker
{
    public static (ArtifactCheckStatus Status, OperationPlan? Plan) CheckPlan(string bundleDirectory)
    {
        var path = OperationBundlePaths.PlanPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactCheckStatus.Missing, null);
        return OperationPlanCodec.TryLoad(path, out var plan)
            ? (ArtifactCheckStatus.Valid, plan)
            : (ArtifactCheckStatus.Invalid, null);
    }

    public static (ArtifactCheckStatus Status, RollbackSnapshot? Snapshot) CheckSnapshot(string bundleDirectory)
    {
        var path = OperationBundlePaths.SnapshotPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactCheckStatus.Missing, null);
        return OperationSnapshotCodec.TryLoad(path, out var snapshot)
            ? (ArtifactCheckStatus.Valid, snapshot)
            : (ArtifactCheckStatus.Invalid, null);
    }
}
```

`PlanCheckStatus`/`SnapshotCheckStatus` on `PendingRecoveryContext` (§7) both start `Unchecked` and
are only ever checked once — `TryAdvanceClassification` only calls `CheckPlan`/`CheckSnapshot` while
the corresponding field is still `Unchecked`, never again afterward regardless of the result. A
`Missing`/`Invalid` result is permanent for that bundle's lifetime (nothing in this plan repairs a
corrupt file in place), so re-checking it would only ever reproduce the same answer while doing real
file I/O every frame for no benefit.

## 6. `RecoveryAssessment`: one atomic read, a complete live-state fingerprint

Renamed `SnapshotGenerationHash` → `LiveStateFingerprint` per review: "generation" implies a sequence
semantic Penumbra doesn't actually expose; this is a deterministic fingerprint of one `GetLiveMods()`
read, nothing more.

```csharp
public sealed record RecoveryAssessment(
    LiveModSnapshot LiveSnapshot,
    IReadOnlyList<ItemRecoveryClassification> Classifications,
    string LiveStateFingerprint);
```

Built from exactly one `IPenumbraOperations.GetLiveMods()` call — never two independent reads that
could disagree if the library changes mid-classification. Not consumed by anything in D1 (Keep Current
doesn't read the assessment at all), but D2's Continue needs to prove its residual replan used the
*same* live-state generation as the classification the user was shown, not a fresher read that could
silently disagree.

**Fingerprint now covers what review found missing**: the original draft hashed only identifier +
normalized path, which could hash identically for two reads with materially different trust state
(same selected dictionary entry, different `DuplicateIdentifiers`; same normalized path, different
`mod.Name`, which `PenumbraPathSemantics.Normalize` itself depends on). Now includes name and sorted
duplicate identifiers:

```csharp
public static class RecoveryAssessmentBuilder
{
    public static RecoveryAssessment Build(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
        var classifications = RecoveryClassifier.Classify(plan, liveSnapshot);
        var fingerprint = ComputeFingerprint(liveSnapshot);
        return new RecoveryAssessment(liveSnapshot, classifications, fingerprint);
    }

    private static string ComputeFingerprint(LiveModSnapshot liveSnapshot)
    {
        var sb = new System.Text.StringBuilder();
        void Field(string value) => sb.Append(System.Text.Encoding.UTF8.GetByteCount(value)
            .ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value);

        foreach (var (identifier, mod) in liveSnapshot.Mods.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Field(identifier);
            Field(mod.Name);
            Field(PenumbraPathSemantics.Normalize(mod.FullPath, mod.Name));
        }

        Field(liveSnapshot.DuplicateIdentifiers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var dup in liveSnapshot.DuplicateIdentifiers.OrderBy(d => d, StringComparer.Ordinal))
            Field(dup);

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
```

**The fingerprint's contract, stated explicitly per review:** it hashes `PenumbraPathSemantics.Normalize`d
paths, so it proves *semantic* live-state continuity (the same mods at the same semantically-equivalent
locations), not raw byte-for-byte path identity — a purely cosmetic raw-path change (e.g. Penumbra's
own `" (N)"` suffix reshuffling) will *not* change the fingerprint, by design. D2 must rely on this
fingerprint only to prove "the same classified generation," never as evidence of exact raw-path
continuity.

## 7. `OperationController`: `PendingRecoveryContext`, status-aware throttled classification, Keep Current, close-all fallback

**New: `RecoveryClassificationStatus`**, replacing the plain "is it pending" framing with one that
distinguishes a transient wait from a permanent failure — per review, retrying `GetLiveMods()` forever
for a response that will never succeed (`InvalidData`) is a real bug, not just an inefficiency, since
it would permanently masquerade as "still pending" with no way to tell the two apart:

```csharp
public enum RecoveryClassificationStatus { WaitingForProvider, Classified, ClassificationUnavailable }
```

- `WaitingForProvider`: no attempt has succeeded yet, but the last attempt's status suggests trying
  again might work (`TemporarilyUnavailable`, `ProviderUnavailable` at *startup* — unlike mid-operation,
  where `ProviderUnavailable` means the adapter itself is judged unusable, this is being read at plugin
  construction time before Penumbra may have finished loading, exactly the "waiting for Penumbra state
  to become available" window design doc section 9 describes).
- `Classified`: `GetLiveMods()` returned `Success`; `Assessment` is populated.
- `ClassificationUnavailable`: either the plan artifact is permanently `Missing`/`Invalid` (§5), or
  `GetLiveMods()` returned `InvalidData` (a response that parsed but doesn't make sense — retrying
  won't change that). Terminal for this bundle's lifetime; no further attempts are made.

New private state, parallel to `ActiveOperationContext` but structurally different (no `Mutation`,
nothing advanced per-frame). `Journal` is settable (`{ get; set; }`), matching
`ActiveOperationContext.Journal`'s own existing shape — not `init`, because `ResolveKeepCurrent` below
needs to update it in place after persisting a resolution:

```csharp
private sealed class PendingRecoveryContext
{
    public required OperationJournal Journal { get; set; }
    public required string BundleDirectory { get; init; }
    public required OperationRecoveryGraphResult Graph { get; init; }
    public ArtifactCheckStatus PlanCheckStatus { get; set; } = ArtifactCheckStatus.Unchecked;
    public OperationPlan? Plan { get; set; }
    public ArtifactCheckStatus SnapshotCheckStatus { get; set; } = ArtifactCheckStatus.Unchecked;
    public RollbackSnapshot? Snapshot { get; set; }
    public RecoveryClassificationStatus ClassificationStatus { get; set; } = RecoveryClassificationStatus.WaitingForProvider;
    public RecoveryAssessment? Assessment { get; set; } // null unless ClassificationStatus == Classified
    public long? LastClassificationAttemptTimestamp { get; set; } // IElapsedTimeSource.GetTimestamp() ticks, null until the first attempt
}

private PendingRecoveryContext? _pendingRecovery;
private OperationRecoveryGraphResult? _blockedMultiRootGraph;
private readonly string _operationsRoot;
```

`OperationController`'s constructor gains a fifth parameter, `string operationsRoot` — needed both to
rebuild the discovered journal's active bundle directory in `RegisterDiscoveredRecovery` and to build
the completed bundle directory in `ResolveKeepCurrent`. `Plugin.cs` already computes this value
(`OperationsRoot`) and passes it to `StartApplyOperation`/`StartRestoreOperation` today — this just
also hands it to the controller once at construction. **Real implementation cost:**
`OperationControllerTests.cs`'s `NewController(adapter, clock, diagnostics)` helper (used by every
test in that file) needs a fifth parameter threaded through — every existing test in that file is
touched by this change even though none of their actual behavior changes.

Registration, called once from `Plugin.cs`'s constructor after `RunStartupDiscovery` (pure, no IPC),
now an explicit switch over the real (post-§4a-fix) enum:

```csharp
public void RegisterDiscoveredRecovery(OperationDiscoveryResult discovery)
{
    switch (discovery.Graph.Status)
    {
        case OperationRecoveryGraphStatus.NoRecoveryNeeded:
            return; // controller stays Idle, exactly as today

        case OperationRecoveryGraphStatus.SingleAuthoritative:
            RegisterSingleAuthoritative(discovery);
            return;

        case OperationRecoveryGraphStatus.MultipleDisconnectedRoots:
        case OperationRecoveryGraphStatus.CycleDetected:
            // D1 builds no root-selection mechanism - design doc section 9a: "Blocked until one
            // root is chosen." There is no single journal to build a PendingRecoveryContext around
            // here, so this is its own separate field, not forced into that shape.
            _blockedMultiRootGraph = discovery.Graph;
            PublishState();
            return;

        default:
            throw new ArgumentOutOfRangeException(nameof(discovery), discovery.Graph.Status, "Unhandled OperationRecoveryGraphStatus.");
    }
}

private void RegisterSingleAuthoritative(OperationDiscoveryResult discovery)
{
    var authoritativeId = discovery.Graph.AuthoritativeOperationIds[0];
    if (!discovery.Journals.TryGetValue(authoritativeId, out var journal))
        return; // defensive - graph and journals dictionary are built together by RunStartupDiscovery

    var bundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, authoritativeId);
    _pendingRecovery = new PendingRecoveryContext { Journal = journal, BundleDirectory = bundleDirectory, Graph = discovery.Graph };
    PublishState();
}
```

`_pendingRecovery` and `_blockedMultiRootGraph` are mutually exclusive by construction (registration
sets exactly one or neither, never both) — `PublishState` doesn't need to reconcile a case where both
are set.

`Update()` gains a second thing to service, matching the original design's own warning that an early
return on `_active is null` would silently stall recovery classification forever. Both artifact checks
and the IPC read are throttled — artifacts check once ever (see §5), the IPC read at most once per
second using the existing `IElapsedTimeSource.GetTimestamp()`/`GetElapsedTime()` (there is no
`UtcNow` on this interface — checked directly — and its own doc comment states these ticks "must never
be persisted," so this throttle interval is in-process-only state, matching how frame-budget timing
already works elsewhere in this same class):

```csharp
public void Update()
{
    if (_pendingRecovery is { ClassificationStatus: RecoveryClassificationStatus.WaitingForProvider } pending)
        TryAdvanceClassification(pending);

    if (_active is null || _active.RequiresRecovery)
        return;

    // ...unchanged AdvanceActiveOperation dispatch...
}

private void TryAdvanceClassification(PendingRecoveryContext pending)
{
    var stateChanged = false;

    if (pending.PlanCheckStatus == ArtifactCheckStatus.Unchecked)
    {
        (pending.PlanCheckStatus, pending.Plan) = ArtifactStatusChecker.CheckPlan(pending.BundleDirectory);
        stateChanged = true;
    }
    if (pending.SnapshotCheckStatus == ArtifactCheckStatus.Unchecked)
    {
        (pending.SnapshotCheckStatus, pending.Snapshot) = ArtifactStatusChecker.CheckSnapshot(pending.BundleDirectory);
        stateChanged = true;
    }

    // Classification needs a valid Plan only - see section 4's confirmed dependency. A missing/
    // invalid Snapshot does not block classification (it blocks Restore Previous State, in D2).
    if (pending.PlanCheckStatus != ArtifactCheckStatus.Valid)
    {
        pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable; // permanent - never re-checked, see section 5
        PublishState();
        return;
    }

    if (pending.LastClassificationAttemptTimestamp is { } last && _clock.GetElapsedTime(last) < ClassificationRetryInterval)
    {
        if (stateChanged)
            PublishState();
        return; // throttle window not yet elapsed since the last attempt
    }

    pending.LastClassificationAttemptTimestamp = _clock.GetTimestamp(); // record this attempt regardless of outcome
    var liveResult = _adapter.GetLiveMods();

    switch (liveResult.Status)
    {
        case LiveModReadStatus.Success when liveResult.Snapshot is not null:
            pending.Assessment = RecoveryAssessmentBuilder.Build(pending.Plan!, liveResult.Snapshot);
            pending.ClassificationStatus = RecoveryClassificationStatus.Classified;
            break;

        case LiveModReadStatus.TemporarilyUnavailable:
        case LiveModReadStatus.ProviderUnavailable:
            // Retryable at startup specifically - Penumbra may simply not have finished loading yet.
            // pending.ClassificationStatus already is WaitingForProvider; nothing to change.
            break;

        case LiveModReadStatus.InvalidData:
        default:
            // A response that parsed but doesn't make sense won't be fixed by asking again - stop
            // retrying rather than let a permanent failure masquerade as "still pending" forever.
            pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
            break;
    }

    PublishState();
}
```

`ClassificationRetryInterval` is a new `private static readonly TimeSpan ClassificationRetryInterval =
TimeSpan.FromSeconds(1);` alongside the existing `SlowCallThreshold`-style constants already used
elsewhere in this codebase (e.g. `PathMutationOperation.SlowCallThreshold`). `GetElapsedTime(startTimestamp)`
returns the elapsed time *since* `startTimestamp` (confirmed from `IElapsedTimeSource`'s own doc
comment and its `StopwatchElapsedTimeSource` implementation, which passes through to
`Stopwatch.GetElapsedTime`) — so recording *when the last attempt happened* and checking "has the
interval elapsed since then" is the correct shape, not a "next allowed" timestamp compared against
zero. Exercised directly by `OperationControllerTests.cs`'s existing `FakeClock.Advance(TimeSpan)` in
the throttle test (§11): call `Update()` repeatedly without advancing the clock (only the first call
should reach `GetLiveMods()`), then `Advance(TimeSpan.FromSeconds(1))` and call `Update()` again (should
reach `GetLiveMods()` a second time).

**Precise commit-point rule, stated explicitly per review:** once a resolved (terminal) journal is
durably saved to disk, the resolving method must return success and clear the corresponding recovery
lock, even if the subsequent best-effort bundle relocation fails. A relocation failure is logged, never
rethrown, and never allowed to leave the resolution half-applied — the persisted journal alone is
authoritative, and `OperationBundleDiscovery`'s own startup relocation pass will finish moving any
terminal journal it later finds still sitting under `active/`. This rule governs both `ResolveKeepCurrent`
below and the new bulk fallback that follows it.

A shared helper implements the relocation, since both resolution paths need the identical
collision-safe logic — **checking more than existence**, per review, before treating a pre-existing
destination as already-handled:

```csharp
public enum KeepCurrentResolutionResult { ResolvedAndArchived, ResolvedArchiveDeferred }

private KeepCurrentResolutionResult TryRelocateToCompleted(string activeBundleDirectory, OperationJournal resolvedJournal)
{
    var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, resolvedJournal.OperationId);
    try
    {
        if (Directory.Exists(completedBundleDirectory))
        {
            // Blindly trusting "the GUID-named directory exists" as proof of a safe prior relocation
            // is not enough - verify it actually is the same, already-resolved operation before
            // treating the active copy as redundant. A mismatch here (same GUID directory present but
            // not matching) is left entirely alone on both sides and reported as deferred, never
            // deleted or overwritten - operation IDs are GUIDs, so a mismatch is not expected, but this
            // method must not assume it never happens.
            var matches = OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var existing)
                && existing is not null
                && existing.OperationId == resolvedJournal.OperationId
                && existing.IsTerminal
                && existing.Resolution == resolvedJournal.Resolution;
            if (matches)
                return KeepCurrentResolutionResult.ResolvedAndArchived; // already relocated by a prior attempt - nothing left to do

            Plugin.Log.Warning($"Keep Current: completed bundle directory for {resolvedJournal.OperationId} exists but doesn't match the resolved journal - leaving both copies in place.");
            return KeepCurrentResolutionResult.ResolvedArchiveDeferred;
        }

        Directory.CreateDirectory(OperationBundlePaths.CompletedDirectory(_operationsRoot));
        Directory.Move(activeBundleDirectory, completedBundleDirectory);
        return KeepCurrentResolutionResult.ResolvedAndArchived;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Plugin.Log.Warning(ex, $"Keep Current: journal resolved but bundle relocation failed for {resolvedJournal.OperationId}.");
        return KeepCurrentResolutionResult.ResolvedArchiveDeferred;
    }
}
```

Keep Current itself — available as soon as there's a single authoritative pending recovery, independent
of classification or artifact validity, per the review's resolution of the original open question:

```csharp
public KeepCurrentResolutionResult ResolveKeepCurrent()
{
    if (_pendingRecovery is not { } pending)
        throw new InvalidOperationException("No pending recovery to resolve.");

    var resolvedJournal = pending.Journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
    OperationJournalCodec.Save(OperationBundlePaths.JournalPath(pending.BundleDirectory), resolvedJournal);
    pending.Journal = resolvedJournal; // commit point - everything below is best-effort, per this section's opening rule

    var result = TryRelocateToCompleted(pending.BundleDirectory, resolvedJournal);
    _pendingRecovery = null;
    PublishState();
    return result;
}
```

**Note:** `Plugin.Log` is a static member on the `Plugin` class (`[PluginService] internal static
IPluginLog Log`) — `OperationController` doesn't currently have a logging dependency of its own
(it has `IDiagnosticsSink` for structured diagnostics, but that's a different concern from ordinary
Dalamud logging). Whether this warning goes through `Plugin.Log` directly (a new, narrow dependency
this class doesn't have today) or through `IDiagnosticsSink` (already injected, but designed for
structured per-operation events, not ad-hoc warnings) needs a decision at implementation time — not
resolved here, flagged so it isn't silently guessed either way.

**The `MultipleDisconnectedRoots`/`CycleDetected` fallback: "Accept Current State and Close All
Interrupted Operations."** Root selection stays deferred to Plan E, but D1 must not ship a state with
no escape hatch at all. This resolves *every* journal in the blocked graph, not only the "authoritative"
leaves — resolving only the leaves would leave any non-terminal ancestor journal still sitting under
`active/` (an ancestor only appears in this set at all when recovery from it was itself interrupted
before completing, per `OperationRecoveryGraph`'s own doc comment), which the *next* startup's
discovery pass would then treat as its own new leaf/root once its (now-terminal) child drops out of the
non-terminal set — silently recreating the exact lockout this method exists to close. Only unblocks the
organizer once every journal in the graph durably persists its resolution — a partial failure leaves the
lockout in place rather than silently under-resolving it:

```csharp
public IReadOnlyList<Guid> AcceptAllAndCloseInterruptedOperations()
{
    if (_blockedMultiRootGraph is not { } graph)
        throw new InvalidOperationException("No blocked multi-root recovery to resolve.");

    var unresolved = new List<Guid>();
    foreach (var operationId in graph.AllOperationIds)
    {
        var bundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, operationId);
        if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDirectory), out var journal) || journal is null)
        {
            unresolved.Add(operationId); // can't resolve what won't even load - leave it for a human
            continue;
        }

        var resolvedJournal = journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
        try
        {
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirectory), resolvedJournal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log.Warning(ex, $"Accept all: failed to persist resolution for {operationId}.");
            unresolved.Add(operationId);
            continue; // the journal write itself is this method's commit point per-operation - a failed
                       // write here is a real unresolved failure, not deferred to relocation like above
        }

        TryRelocateToCompleted(bundleDirectory, resolvedJournal); // best-effort, same rule as ResolveKeepCurrent
    }

    if (unresolved.Count > 0)
    {
        PublishState(); // still blocked - not every journal could be durably resolved
        return unresolved;
    }

    _blockedMultiRootGraph = null;
    PublishState();
    return [];
}
```

Returns the list of operation IDs that could *not* be resolved (empty on full success) rather than a
bare bool, so a future diagnostics surface can report exactly what's still stuck without the caller
needing to re-derive it.

`PublishState()` reads from `_pendingRecovery`/`_blockedMultiRootGraph` when `_active` is null:

```csharp
private void PublishState()
{
    if (_active is null && _pendingRecovery is null && _blockedMultiRootGraph is null)
    {
        State = OperationStateSnapshot.Idle;
        return;
    }

    if (_active is null && _blockedMultiRootGraph is not null)
    {
        State = OperationStateSnapshot.Idle with
        {
            RequiresRecovery = true,
            RecoveryClassificationPending = false, // nothing to classify per-item in this case - see section 4a/section 9
            // CanResolveRecovery is true here too, now that AcceptAllAndCloseInterruptedOperations
            // exists - MainWindow's panel (section 9) uses this exact field to decide which button to
            // show, so it must reflect "some resolution exists," not "root selection is possible."
            CanResolveRecovery = true,
            CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
            CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
        };
        return;
    }

    if (_active is null) // _pendingRecovery is not null
    {
        var pending = _pendingRecovery!;
        State = OperationStateSnapshot.Idle with
        {
            RequiresRecovery = true,
            // True only while genuinely waiting on IPC to become ready - a permanently degraded
            // artifact or an InvalidData response both move ClassificationStatus to
            // ClassificationUnavailable, which is NOT "pending" (it will never resolve on its own).
            RecoveryClassificationPending = pending.ClassificationStatus == RecoveryClassificationStatus.WaitingForProvider,
            // Keep Current needs neither classification nor a valid plan/snapshot - available the
            // moment a single authoritative interrupted journal is known. D1 temporarily defines
            // this single boolean as "Keep Current is available" - D2 will need to split this into
            // CanContinueRecovery/CanRestorePreviousState/CanKeepCurrent, since those three have
            // genuinely different availability rules (design doc section 9a's table). This is a
            // known, documented limitation of this field's D1 semantics, not an oversight.
            CanResolveRecovery = true,
            CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
            CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
        };
        return;
    }

    // ...unchanged _active branch, using CanStartNext exactly as Plan C left it...
}
```

`CanResolveRecovery` is now `true` in *both* the single-journal and blocked-multi-root branches, so
`MainWindow` (§9) needs a way to tell them apart to render the right button — a new controller method,
rather than another `OperationStateSnapshot` boolean for a distinction that's purely "which crude
action to offer," not a capability check in its own right. Both this and `GetRecoveryAssessment` are
small internal read methods so D1 is fully testable and Plan E doesn't need to reach into private
state later, without locking in a final dialog DTO shape:

```csharp
public RecoveryAssessment? GetRecoveryAssessment() => _pendingRecovery?.Assessment;

// Distinguishes which of the two CanResolveRecovery=true cases the crude panel (section 9) is in -
// a single recoverable journal (offer Keep Current) vs. a blocked multi-root/cycle graph (offer
// Accept All). Not itself an OperationStateSnapshot field, since it's purely "which crude action to
// render," not a capability distinct from CanResolveRecovery.
public bool IsBlockedByMultipleRoots => _blockedMultiRootGraph is not null;
```

## 8. `Plugin.cs`: startup wiring

```csharp
public Plugin()
{
    // ...existing field initialization, including OperationController's own construction, now with
    // the fifth `OperationsRoot` argument...

    var discovery = Organizer.Operations.OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot);
    OperationController.RegisterDiscoveredRecovery(discovery);

    // ...existing WindowSystem/event-subscription setup continues unchanged...
}

internal void ResolveKeepCurrent()
{
    OperationController.ResolveKeepCurrent();
    RunScan();
}

internal void AcceptAllAndCloseInterruptedOperations()
{
    OperationController.AcceptAllAndCloseInterruptedOperations();
    RunScan();
}
```

Exact placement within the constructor (relative to `OperationController`'s own construction and the
existing `GetModListAdapterIpc`/`SetModPathIpc` field initialization) needs to be checked against the
constructor's real current body at implementation time.

## 9. `MainWindow`: a crude, minimal recovery panel

**New in this revision — D1 cannot ship the lockout above without this.** Rendered at the top of
`Draw()`, before the existing tab bar, whenever `RequiresRecovery` is true. Deliberately crude,
matching every prior plan's own "not the real UI" polling-stub precedent — the real recovery dialog
(with per-mod classification detail, multi-root selection, Continue/Restore Previous State) is Plan
E's job:

```csharp
private void DrawRecoveryPanelIfNeeded()
{
    var operationState = _plugin.OperationController.State;
    if (!operationState.RequiresRecovery)
        return;

    ImGui.TextColored(PluginTheme.CollisionBad, "An interrupted organizer operation was found.");

    if (_plugin.OperationController.IsBlockedByMultipleRoots)
    {
        ImGui.TextWrapped(
            "Multiple interrupted operations were found, and picking which one to recover isn't " +
            "supported yet in this version. You can abandon all of them and accept whatever Penumbra " +
            "currently has as correct - this does not undo or redo any moves for any of them, it only " +
            "stops the plugin from blocking further actions. This is destructive: none of the " +
            "interrupted operations can be revisited afterward.");

        if (ImGui.Button("Accept Current State and Close All Interrupted Operations"))
            ImGui.OpenPopup("Close all interrupted operations?");

        if (ImGui.BeginPopupModal("Close all interrupted operations?"))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "This abandons every interrupted operation the plugin found. None of them can be " +
                "continued or rolled back after this - only Keep Current's outcome is possible for all of them.");
            if (ImGui.Button("Yes, Close All"))
            {
                _plugin.AcceptAllAndCloseInterruptedOperations();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.Separator();
        return;
    }

    ImGui.TextWrapped(
        "The plugin found a mod-organizing operation that didn't finish, likely from a crash or force-" +
        "quit mid-Apply or mid-Restore. Continuing it or fully rolling it back isn't supported yet. For " +
        "now, you can accept whatever Penumbra currently has as the correct state and move on - this " +
        "does not undo or redo any moves, it only stops the plugin from blocking further actions.");

    if (ImGui.Button("Keep Current State"))
        ImGui.OpenPopup("Keep current state?");

    if (ImGui.BeginPopupModal("Keep current state?"))
    {
        ImGui.TextUnformatted("This will mark the interrupted operation as resolved and unblock the plugin.");
        if (ImGui.Button("Yes, Keep Current"))
        {
            _plugin.ResolveKeepCurrent();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    ImGui.Spacing();
    ImGui.Separator();
}
```

`AcceptAllAndCloseInterruptedOperations`'s result (the unresolved-ID list) isn't surfaced in this crude
panel beyond implicitly — if any journal couldn't be resolved, `RequiresRecovery`/
`IsBlockedByMultipleRoots` both stay true and the same panel simply reappears next frame with nothing
having visibly changed, which is honest (the operation genuinely didn't fully succeed) even though it
isn't informative about *why*. A clearer partial-failure message is Plan E's job, not D1's.

Called as the first line of `Draw()`'s body, before the existing tab bar (`ImGui.BeginTabBar`/
whatever the current structure is — checked against the real file at implementation time). The rest
of the window still renders below it, not hidden entirely — the existing `CanStartApply`/`CanScan`/etc.
`false` values already gray out the now-blocked controls via each tab's existing `ImGui.BeginDisabled`
pattern, so there is no need to duplicate that lockout by hiding the tabs outright.

## 10. What D1 does not cover

- Continue and Restore Previous State — Plan D2, reusing `StartApply`/`StartRestore` unchanged per §2.
- **Root selection** for `MultipleDisconnectedRoots`/`CycleDetected` — choosing to resolve one specific
  interrupted lineage while continuing to investigate the others is Plan E's job. D1 only offers the
  all-or-nothing fallback (§7's `AcceptAllAndCloseInterruptedOperations`): abandon every interrupted
  operation in the graph at once. This is a real, if blunt, resolution — not a report-only lockout —
  which is the change from the previous revision.
- The real recovery dialog UI (per-mod classification detail) — Plan E.
- `RecoveryDialogSnapshot` (the original design's §7b large/rare-read record for the eventual dialog)
  — not built in this plan. `GetRecoveryAssessment()`/`IsBlockedByMultipleRoots` (§7) are D1's only
  externally-queryable surface beyond the existing boolean fields; Plan E will need to expand the
  controller's read API to build the real dialog, and this document does not claim that surface
  already exists.
- Diagnostics dump changes (§10 of the original design) — unrelated to this plan's scope.
- `CanContinueRecovery`/`CanRestorePreviousState`/`CanKeepCurrent` as separate fields — `CanResolveRecovery`
  is temporarily defined as "some crude resolution is available" for D1 only; D2 will need the real
  split, since Continue/Restore Previous State's availability rules genuinely differ from Keep Current's.

## 11. Testing

Pure/xUnit-testable:

**§4a fix, end-to-end, not just the direct graph method** (per review — the bug originated in the
integration between discovery and graph analysis, not merely the enum calculation):
- `OperationRecoveryGraph.Analyze([])` → `NoRecoveryNeeded`, both ID lists empty.
- `OperationBundleDiscovery.RunStartupDiscovery` against an empty (or nonexistent) `active/` directory
  → `NoRecoveryNeeded`.
- `OperationBundleDiscovery.RunStartupDiscovery` against an `active/` directory containing only
  already-terminal bundles → `NoRecoveryNeeded` (proves the relocation/exclusion pass and the graph fix
  compose correctly, not just each in isolation).
- `OperationController.RegisterDiscoveredRecovery` given a `NoRecoveryNeeded` discovery result → every
  `OperationStateSnapshot` field matches `Idle` exactly, and `_blockedMultiRootGraph`/`_pendingRecovery`
  are both never populated (assert via `IsBlockedByMultipleRoots`/`GetRecoveryAssessment()` both being
  "nothing here," not just `RequiresRecovery == false`).

**Classification and artifacts:**
- `RecoveryClassifier.Classify`: one test per `ItemRecoveryState` outcome, plus a duplicate-identifiers
  case proving `Classify` itself doesn't special-case it.
- `ArtifactStatusChecker`: missing file → `Missing`; corrupt file → `Invalid`; valid file → `Valid` with
  the parsed value returned, for both `CheckPlan` and `CheckSnapshot` independently.
- `RecoveryAssessmentBuilder.Build`: returned `Classifications` match a direct `Classify` call;
  `LiveStateFingerprint` is deterministic and order-independent; two reads differing only in
  `DuplicateIdentifiers` (same selected dictionary entries, different duplicate set) or only in
  `mod.Name` produce **different** fingerprints (the exact case the original draft's hash collapsed).

**`OperationController.RegisterDiscoveredRecovery`/`Update`'s classification path**, using the existing
`FakePenumbraOperations`/`FakeClock` test doubles:
- A discovered journal with a valid plan and snapshot → `CanResolveRecovery` true immediately (even
  before any `Update()` call), `RecoveryClassificationPending` true until `GetLiveMods()` succeeds,
  then false with a populated `GetRecoveryAssessment()` (`ClassificationStatus` transitions
  `WaitingForProvider` → `Classified`).
- A discovered journal with a **missing/invalid plan** → `CanResolveRecovery` stays true throughout
  (Keep Current unaffected), `RecoveryClassificationPending` stays permanently false,
  `ClassificationStatus` becomes `ClassificationUnavailable` on the first `Update()` call, `GetLiveMods()`
  is never called (assert via the fake adapter's call count).
- A discovered journal with a **missing/invalid snapshot but a valid plan** → classification still
  succeeds (proves the snapshot-independence fix).
- **Retryable vs. permanent `LiveModReadStatus` responses**: `TemporarilyUnavailable` and
  `ProviderUnavailable` both leave `ClassificationStatus == WaitingForProvider` and are retried on the
  next throttle-eligible `Update()` call; `InvalidData` moves `ClassificationStatus` to
  `ClassificationUnavailable` on the very next call and `GetLiveMods()` is never called again afterward
  (proves permanent failures stop retrying instead of masquerading as pending forever).
- `Update()` called many times in a row within the same simulated second → `GetLiveMods()` is called
  at most once (proves the IPC throttle, using `FakeClock.Advance` to control the interval precisely).
- Artifact checks are attempted exactly once even across many `Update()` calls with a
  permanently-missing file (proves the "checked once" fix — assert `PlanCheckStatus` never flips back
  to `Unchecked`, and a call-count assertion on the codec/`File.Exists` path if a test seam allows one).

**`ResolveKeepCurrent`/`TryRelocateToCompleted`:**
- Sets `Resolution`, relocates `active/` → `completed/`, `CanStartApply`/`CanStartRestore` true
  afterward, returns `ResolvedAndArchived`.
- Calling it a second time after the first succeeded (simulating a retry where the destination now
  genuinely matches — same `OperationId`, terminal, matching `Resolution`) does not throw, still ends
  with `_pendingRecovery` cleared, returns `ResolvedAndArchived`.
- A destination directory that exists but does **not** match (different/missing journal, or a matching
  ID that isn't terminal) → returns `ResolvedArchiveDeferred`, the mismatched directory is left
  untouched on both sides, `_pendingRecovery` is still cleared (the journal save is still the commit
  point — this proves the collision-verification fix doesn't accidentally also break the commit-point
  rule).
- Calling it with no pending recovery throws.
- `resolvedJournal.IsTerminal == true` after `Resolution = AcceptedCurrentState` — asserted directly
  against the real `OperationJournal.IsTerminal` property (confirmed: `Resolution != None` alone is
  sufficient regardless of `Stage`; this test exists to prove it holds, not to fix anything).

**`AcceptAllAndCloseInterruptedOperations`:**
- A blocked graph with 2+ journals, all loadable and writable → every journal gets
  `Resolution = AcceptedCurrentState`, every bundle relocated, `_blockedMultiRootGraph` cleared,
  `IsBlockedByMultipleRoots` false afterward, returns an empty list.
- One journal in the graph fails to load (simulated via a corrupt/missing file for just that ID) →
  that ID appears in the returned list, `_blockedMultiRootGraph` is **not** cleared, `RequiresRecovery`/
  `IsBlockedByMultipleRoots` both stay true (proves partial success does not silently unblock).
- Calling it with no blocked graph throws.
- `MultipleDisconnectedRoots`/`CycleDetected` registration, before any resolution is attempted:
  `RequiresRecovery` true, `RecoveryClassificationPending` false, `CanResolveRecovery` **true**
  (updated from the previous revision — a resolution now exists), `IsBlockedByMultipleRoots` true.

Not automatable: `Plugin.cs`'s constructor wiring and `MainWindow`'s new panel — same documented
Dalamud-coupled limitation as every prior plan. Verified by build + a manual checklist (crash mid-
Apply, restart, confirm the panel appears and Keep Current actually unblocks the plugin; a genuinely
clean prior session doesn't trigger anything; the panel's blocked-multi-root message and "Accept All"
button are distinguishable from the normal single-journal panel — this needs deliberately constructing
a multi-root scenario, likely by hand-editing bundle files, since it can't arise from ordinary use).

## 12. Global constraints for the implementation plan

- `dotnet build` must introduce no new warnings/errors beyond whatever the accepted baseline is at
  implementation time (re-verify at worktree setup, per Plan C's own precedent of the baseline
  drifting from what an earlier plan assumed).
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC — same carried-forward
  limitation.
- `OperationBundleDiscovery`, `OperationBundleRetention` are out of scope for modification.
  `OperationRecoveryGraph.cs` is explicitly **in scope** for the single fix in §4a — not the rest of
  that file.
- `PenumbraPathSemantics.AreEquivalent`/`Normalize` for every path comparison in new code — never raw
  string equality.
- `IElapsedTimeSource` (`GetTimestamp()`/`GetElapsedTime()`) for any in-process interval/throttle
  timing; `DateTimeOffset.UtcNow` for any persisted wall-clock journal field (`StartedAt`/`UpdatedAt`),
  matching the existing, already-shipped convention in `OperationController.StartOperation` — the two
  are not interchangeable and this plan must not blur them.
