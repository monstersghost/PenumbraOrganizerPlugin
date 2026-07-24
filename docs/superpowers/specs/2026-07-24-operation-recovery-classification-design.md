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
  `Plugin.cs`, the **Keep Current** resolution end-to-end, and — added in this revision — **a crude,
  minimal `MainWindow` recovery panel**. D1 cannot safely wire startup enforcement (disabling every
  organizer action whenever an interrupted operation is found) without giving the user *some* way to
  resolve it; shipping only the backend and leaving the lockout live with no UI anywhere to call it
  would be a real regression, not a deferral. This matches the same precedent every prior plan already
  set (B2/C both shipped a crude UI stub alongside their backend, never backend-only).
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
    │                          RequiresRecovery=true, CanResolveRecovery=false (no resolution exists yet)
    ▼
OperationController.Update() — called every Framework.Update tick, same as today
    │  NEW: if _pendingRecovery has an unchecked artifact or unclassified assessment, and the
    │  per-attempt throttle interval has elapsed, try to advance it one step (check plan, check
    │  snapshot, or call GetLiveMods()) - each artifact is checked AT MOST ONCE per registration,
    │  not every frame; IPC reads are throttled to at most once per second
    ▼
MainWindow.Draw() — NEW, before the existing tab bar
    │  if RequiresRecovery: render a crude recovery panel (see §9) instead of/above normal tab content
    │  if CanResolveRecovery: "Keep Current State" button → Plugin.ResolveKeepCurrent()
    │  else (blocked multi-root): a plain message explaining this build can't resolve it yet
    ▼
Plugin.ResolveKeepCurrent()
    │  OperationController.ResolveKeepCurrent(): persists the resolved (terminal) journal FIRST -
    │  that's the commit point - then best-effort relocates active/ → completed/ (idempotent-skip if
    │  the destination already exists, matching OperationBundleDiscovery's own precedent), clears
    │  _pendingRecovery regardless of whether relocation succeeded, since the persisted journal is
    │  authoritative and a failed relocation self-heals on the next startup's discovery pass
    ▼
    RunScan() - controller has no OrganizerState access, stays a Plugin.cs/MainWindow responsibility

(Plan D2: Continue / Restore Previous State - not in this plan, reuses StartApply/StartRestore
 unchanged per §2)
(Plan E: the real recovery dialog UI, replacing this plan's crude panel - not in this plan)
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

## 7. `OperationController`: `PendingRecoveryContext`, throttled classification, Keep Current

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
    public RecoveryAssessment? Assessment { get; set; } // null until classification succeeds
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
    if (_pendingRecovery is { Assessment: null } pending)
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
        if (stateChanged)
            PublishState();
        return; // permanently blocked for this bundle - re-checked never again, see section 5
    }

    if (pending.LastClassificationAttemptTimestamp is { } last && _clock.GetElapsedTime(last) < ClassificationRetryInterval)
    {
        if (stateChanged)
            PublishState();
        return; // throttle window not yet elapsed since the last attempt
    }

    pending.LastClassificationAttemptTimestamp = _clock.GetTimestamp(); // record this attempt regardless of outcome
    var liveResult = _adapter.GetLiveMods();
    if (liveResult.Status != LiveModReadStatus.Success || liveResult.Snapshot is null)
    {
        if (stateChanged)
            PublishState(); // IPC not ready yet - retried after the throttle interval, no error surfaced
        return;
    }

    pending.Assessment = RecoveryAssessmentBuilder.Build(pending.Plan!, liveResult.Snapshot);
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

Keep Current — available as soon as there's a single authoritative pending recovery, independent of
classification or artifact validity, per the review's resolution of the original open question:

```csharp
public void ResolveKeepCurrent()
{
    if (_pendingRecovery is not { } pending)
        throw new InvalidOperationException("No pending recovery to resolve.");

    // The persisted journal is the commit point, not the directory relocation below. Once this
    // write succeeds, the recovery decision is durable and authoritative regardless of what happens
    // next - a failed relocation self-heals on the next startup's discovery pass (which already
    // relocates any terminal journal it finds sitting under active/), so this method does not need
    // to leave the user re-blocked over a filesystem-move failure it can't itself repair.
    var resolvedJournal = pending.Journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
    OperationJournalCodec.Save(OperationBundlePaths.JournalPath(pending.BundleDirectory), resolvedJournal);
    pending.Journal = resolvedJournal;

    var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, resolvedJournal.OperationId);
    try
    {
        // Idempotent-skip on an existing destination, matching OperationBundleDiscovery's own
        // RelocateTerminalActiveBundles precedent ("already relocated by something else - don't
        // clobber it") rather than throwing - operation IDs are GUIDs, so the only realistic way
        // this destination already exists is a retry after this same relocation already succeeded
        // once before (e.g. a prior call that failed after the journal save but before this method
        // returned).
        if (!Directory.Exists(completedBundleDirectory))
        {
            Directory.CreateDirectory(OperationBundlePaths.CompletedDirectory(_operationsRoot));
            Directory.Move(pending.BundleDirectory, completedBundleDirectory);
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Relocation failure does not undo the resolution above - see this method's own comment.
        // Surfaced to the ordinary Dalamud log; not rethrown, since the recovery decision itself
        // already succeeded and durable-recovery discovery will finish the relocation on next startup.
        Plugin.Log.Warning(ex, $"Keep Current: journal resolved but bundle relocation failed for {resolvedJournal.OperationId}.");
    }

    _pendingRecovery = null;
    PublishState();
}
```

**Note:** `Plugin.Log` is a static member on the `Plugin` class (`[PluginService] internal static
IPluginLog Log`) — `OperationController` doesn't currently have a logging dependency of its own
(it has `IDiagnosticsSink` for structured diagnostics, but that's a different concern from ordinary
Dalamud logging). Whether this warning goes through `Plugin.Log` directly (a new, narrow dependency
this class doesn't have today) or through `IDiagnosticsSink` (already injected, but designed for
structured per-operation events, not ad-hoc warnings) needs a decision at implementation time — not
resolved here, flagged so it isn't silently guessed either way.

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
            RecoveryClassificationPending = false, // nothing pending - blocked on a root choice D1 can't collect
            CanResolveRecovery = false, // no resolution exists for this case yet
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
            // Reflects only whether IPC-backed classification has completed - purely informational
            // in D1 (nothing yet reads it to gate Continue/Restore Previous State; that's D2). A
            // permanently degraded artifact (Missing/Invalid) is NOT "pending" - it will never
            // resolve on its own, which is exactly why this is false in that case, not true forever.
            RecoveryClassificationPending = pending.PlanCheckStatus == ArtifactCheckStatus.Valid && pending.Assessment is null,
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

A small internal read method, so D1 is fully testable and Plan E doesn't need to reach into private
state later, without locking in a final dialog DTO shape:

```csharp
public RecoveryAssessment? GetRecoveryAssessment() => _pendingRecovery?.Assessment;
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

    if (!operationState.CanResolveRecovery)
    {
        ImGui.TextWrapped(
            "Multiple interrupted operations were found and can't be automatically resolved in this " +
            "version of the plugin - every organizer action is disabled until this is fixed in a " +
            "future update. This is rare; if you need this unblocked sooner, check the plugin log for " +
            "the operations folder path.");
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

Called as the first line of `Draw()`'s body, before the existing tab bar (`ImGui.BeginTabBar`/
whatever the current structure is — checked against the real file at implementation time). The rest
of the window still renders below it, not hidden entirely — the existing `CanStartApply`/`CanScan`/etc.
`false` values already gray out the now-blocked controls via each tab's existing `ImGui.BeginDisabled`
pattern, so there is no need to duplicate that lockout by hiding the tabs outright.

## 10. What D1 does not cover

- Continue and Restore Previous State — Plan D2, reusing `StartApply`/`StartRestore` unchanged per §2.
- The real recovery dialog UI (per-mod classification detail) and the root-selection UI for
  `MultipleDisconnectedRoots`/`CycleDetected` — Plan E. D1's crude panel explicitly cannot resolve the
  multi-root case; it only reports it clearly and blocks the same as the single-root case would.
- `RecoveryDialogSnapshot` (the original design's §7b large/rare-read record for the eventual dialog)
  — not built in this plan. `GetRecoveryAssessment()` (§7) is D1's only externally-queryable surface
  beyond the existing boolean fields; Plan E will need to expand the controller's read API to build the
  real dialog, and this document does not claim that surface already exists.
- Diagnostics dump changes (§10 of the original design) — unrelated to this plan's scope.
- `CanContinueRecovery`/`CanRestorePreviousState`/`CanKeepCurrent` as separate fields — `CanResolveRecovery`
  is temporarily defined as "Keep Current is available" for D1 only; D2 will need the real split.

## 11. Testing

Pure/xUnit-testable:
- `OperationRecoveryGraph.Analyze([])` → `NoRecoveryNeeded`, both ID lists empty (the §4a fix).
- `RecoveryClassifier.Classify`: one test per `ItemRecoveryState` outcome, plus a duplicate-identifiers
  case proving `Classify` itself doesn't special-case it.
- `ArtifactStatusChecker`: missing file → `Missing`; corrupt file → `Invalid`; valid file → `Valid` with
  the parsed value returned, for both `CheckPlan` and `CheckSnapshot` independently.
- `RecoveryAssessmentBuilder.Build`: returned `Classifications` match a direct `Classify` call;
  `LiveStateFingerprint` is deterministic and order-independent; two reads differing only in
  `DuplicateIdentifiers` (same selected dictionary entries, different duplicate set) or only in
  `mod.Name` produce **different** fingerprints (the exact case the original draft's hash collapsed).
- `OperationController.RegisterDiscoveredRecovery`/`Update`'s classification path/`ResolveKeepCurrent`,
  using the existing `FakePenumbraOperations`/`FakeClock` test doubles:
  - `NoRecoveryNeeded` discovery → stays Idle.
  - A discovered journal with a valid plan and snapshot → `CanResolveRecovery` true immediately (even
    before any `Update()` call), `RecoveryClassificationPending` true until `GetLiveMods()` succeeds,
    then false with a populated `GetRecoveryAssessment()`.
  - A discovered journal with a **missing/invalid plan** → `CanResolveRecovery` stays true throughout
    (Keep Current unaffected), `RecoveryClassificationPending` stays permanently false (not "pending" -
    it will never classify), `GetLiveMods()` is never called (assert via the fake adapter's call count).
  - A discovered journal with a **missing/invalid snapshot but a valid plan** → classification still
    succeeds (proves the snapshot-independence fix).
  - `Update()` called many times in a row within the same simulated second → `GetLiveMods()` is called
    at most once (proves the IPC throttle actually throttles, using `FakeClock.Advance` to control the
    interval precisely).
  - Artifact checks are attempted exactly once even across many `Update()` calls with a
    permanently-missing file (proves the "checked once" fix, via a call-count assertion on a test
    double around `File.Exists`/the codec, or by asserting `PlanCheckStatus` never flips back to
    `Unchecked`).
  - `ResolveKeepCurrent`: sets `Resolution`, relocates `active/` → `completed/`, `CanStartApply`/
    `CanStartRestore` true afterward; calling it twice in a row (simulating a destination that already
    exists) does not throw and still ends with `_pendingRecovery` cleared; calling it with no pending
    recovery throws.
  - `resolvedJournal.IsTerminal == true` after `Resolution = AcceptedCurrentState` — asserted directly
    against the real `OperationJournal.IsTerminal` property, not inferred from the enum name (per
    review: `IsTerminal => Resolution != OperationResolution.None || TerminalStages.Contains(Stage)`,
    confirmed by reading the property's actual current definition; the `Resolution != None` branch
    alone is sufficient regardless of `Stage`, so this genuinely already holds without further code
    changes — this test exists to prove it, not to fix anything).
  - `MultipleDisconnectedRoots`/`CycleDetected` registration: `RequiresRecovery` true,
    `RecoveryClassificationPending` false, `CanResolveRecovery` false — permanently, proven by calling
    `Update()` many times and asserting nothing changes.

Not automatable: `Plugin.cs`'s constructor wiring and `MainWindow`'s new panel — same documented
Dalamud-coupled limitation as every prior plan. Verified by build + a manual checklist (crash mid-
Apply, restart, confirm the panel appears and Keep Current actually unblocks the plugin; a genuinely
clean prior session doesn't trigger anything; the panel's blocked-multi-root message is distinguishable
from the normal Keep Current panel — this needs deliberately constructing a multi-root scenario, likely
by hand-editing bundle files, since it can't arise from ordinary use).

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
