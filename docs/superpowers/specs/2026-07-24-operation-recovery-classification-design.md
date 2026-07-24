# Operation Recovery Classification and Keep Current (Plan D1)

Date: 2026-07-24
Status: Design draft, not yet reviewed
Builds on: `docs/superpowers/specs/2026-07-22-operation-controller-design.md` (§7-9, §13 scoped this
as "Plan D — Recovery"), the merged Plan A2
(`docs/superpowers/plans/2026-07-22-operation-storage-and-recovery-discovery.md`), Plan B1/B2, and
Plan C (`docs/superpowers/specs/2026-07-24-operation-restore-integration-design.md`)

## 1. Scope and relationship to prior work

Plan D was split in two, given its combined scope (classification, startup wiring, and all three
recovery resolutions) is comparable to prior split plans (A1/A2, B1/B2):

- **Plan D1 (this plan):** `RecoveryClassifier`, `RecoveryAssessment`, startup discovery wiring into
  `Plugin.cs`, and the **Keep Current** resolution end-to-end (the only one of the three that doesn't
  start a new operation, so it doesn't touch the execution engine at all).
- **Plan D2 (later):** Continue and Restore Previous State — both start a *new* operation from
  residual moves, reusing `OperationController.StartApply`/`StartRestore` (Plan C) unchanged.

**A real, currently-live gap this plan closes:** grepped `Plugin.cs` for any reference to
`OperationBundleDiscovery`/`RequiresRecovery` — there is none. Plan A2 already built full startup
discovery (`OperationBundleDiscovery.RunStartupDiscovery`, `OperationRecoveryGraph.Analyze`) and it
has never been wired into anything. Today, if the plugin crashes mid-Apply/Restore and the game
restarts, the plugin has no idea a prior operation was interrupted — `OperationController` starts
`Idle` on every fresh `Plugin()` construction regardless of what's sitting in `operations/active/`.

**Original design terminology has drifted from shipped code** — this plan reconciles rather than
literally transcribes the 2026-07-22 draft, which predates Plan B1/B2/C's actual implementation:
`_activeOperation` → shipped as `_active` (`ActiveOperationContext?`); `CurrentIdentifier`/
`CompletedTargets` on `OperationStateSnapshot` → shipped as `LastProcessedIdentifier`/
`ProcessedTargets` (renamed during Plan B1's own second review round, per that class's doc comment);
`StartApply`/`StartRestore`'s guard → now `OperationController.CanStartNext` (Plan C). Code in this
document matches what's actually on `main` today, not the original draft.

## 2. A key simplification found while grounding this plan: Continue doesn't resume `PathMutationOperation`

Read `PathMutationOperation`'s constructor and `StepResultReconciler` before writing this plan,
expecting D2 (not in scope here, but the finding affects D1's architecture) would need to reconstruct
a `PathMutationOperation` from disk to resume a crashed operation mid-flight. It doesn't:
`PathMutationOperation`'s constructor takes only `(plan, adapter, clock, diagnostics, bundleDirectory)`
and starts its `_stepDispositions` empty — there is no "resume with prior progress" constructor
anywhere in this codebase. The original design's own §9 ordering confirms this is intentional, not a
gap: Continue "build[s] and validate[s] the continuation `OperationPlan` (**new** `OperationId`,
**fresh** `ExecutionSteps` from replanning residual moves, **fresh** `RecoveryTargets`)... Activate
the new operation" — i.e., Continue computes residual moves (which mods still need to move) and
starts an entirely new operation via the already-generalized `StartApply`/`StartRestore`, exactly like
any ordinary Apply/Restore. It does not resume the interrupted operation's own `PathMutationOperation`
in place.

**This directly answers an architecture question for D1's own scope:** a discovered interrupted
operation from a prior crash must **not** be shoehorned into the existing `_active`/
`ActiveOperationContext` slot (which exists to be actively advanced by `Update()` calling
`PathMutationOperation.Advance` every frame). Nothing progresses a discovered-but-unresolved recovery
frame-to-frame — it sits frozen until a human picks a resolution. It needs its own state, separate
from `_active`, matching the original design's `_pendingRecoveryClassification` concept (renamed
below to match this plan's own naming).

## 3. Architecture overview

```
Plugin() constructor
    │  OperationBundleDiscovery.RunStartupDiscovery(OperationsRoot) — pure file I/O, no IPC
    ▼
OperationController.RegisterDiscoveredRecovery(OperationDiscoveryResult)
    │  if a SingleAuthoritative non-terminal journal was found: stores it as _pendingRecovery,
    │  RecoveryClassificationPending = true, RequiresRecovery = true, does NOT call GetLiveMods() yet
    │  if MultipleDisconnectedRoots/CycleDetected: same lockout, but classification can never
    │  proceed until Plan E's root-selection UI exists (D1 detects and reports this, doesn't resolve it)
    │  if no non-terminal journal: no-op, controller starts Idle as it does today
    ▼
OperationController.Update() — called every Framework.Update tick, same as today
    │  NEW: if _pendingRecovery is not null and not yet classified, attempt classification
    │  (calls _adapter.GetLiveMods() - if the provider isn't ready yet, this just fails softly and
    │  retries next tick, exactly the "waiting for Penumbra state" window design doc section 9 describes)
    │  once classification succeeds: RecoveryAssessment + per-target ItemRecoveryState computed,
    │  RecoveryClassificationPending flips false, RequiresRecovery stays true until resolved
    ▼
(Plan D2: Continue / Restore Previous State - not in this plan)
(Plan E: the real recovery dialog UI - not in this plan; this plan's job is only that the
 underlying state is correct and queryable, matching every prior plan's UI-deferral pattern)

Plugin.ResolveKeepCurrent() — the one resolution this plan implements end-to-end
    │  OperationController.ResolveKeepCurrent(): Resolution = AcceptedCurrentState on the discovered
    │  journal, relocate its bundle from active/ to completed/ (immediate, not waiting for next
    │  startup's discovery pass), _pendingRecovery cleared, RequiresRecovery/RecoveryClassificationPending
    │  both false again - CanStartApply/CanStartRestore/CanScan return true
    ▼
Plugin.ResolveKeepCurrent() also triggers RunScan() - controller has no OrganizerState access,
so this stays a caller-side responsibility, same layering Plugin.cs already uses for Apply/Restore
completion (RunScan lives in MainWindow, called from Plugin/MainWindow, never from the controller)
```

## 4. `RecoveryClassifier`: per-target classification against live state

**Deviation from the original design's literal enum, with reasoning — flagging this explicitly for
review rather than silently resolving it:** the original draft listed `ItemRecoveryState` with 8
members including `MissingSnapshot`/`MissingPlan`. Both are already covered at the *artifact* level
by `ArtifactStatus` (§5) — if `plan.json` itself fails to load, there are no `RecoveryTargets` to
iterate over, so no per-item classification is attempted at all; the whole assessment degrades via
`ArtifactStatus.PlanMissing`, not via a per-item fallback state. I could not find a consistent
interpretation of these two members that isn't already redundant with `ArtifactStatus`, so this plan
drops them from the per-item enum. If review disagrees and has a concrete scenario where a *specific
identifier* (not the whole artifact) is meaningfully "missing snapshot" while the rest of the plan
classifies normally, that's a real gap to add back — I want that checked, not assumed away.

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ItemRecoveryState { AtSnapshot, AtIntended, AtBoth, AtKnownIntermediate, AtNeither, MissingLive }

public sealed record ItemRecoveryClassification(string Identifier, ItemRecoveryState State);

/// <summary>
/// Design doc section 8, reconciled against shipped code. Classifies each of the interrupted plan's
/// RecoveryTargets against live state, using PenumbraPathSemantics.AreEquivalent (never raw string
/// equality) for every path comparison - matching how OperationPlan's own integrity hash and every
/// existing planner already compares paths, since Penumbra's own name-trimming/" (N)" suffix
/// reshuffling means raw equality would misclassify a no-op path difference as a real divergence.
/// </summary>
public static class RecoveryClassifier
{
    public static IReadOnlyList<ItemRecoveryClassification> Classify(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
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

        if (atFinal && atSnapshot)
            return ItemRecoveryState.AtBoth; // snapshot and final coincide - a no-op move for this identifier
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
that's proven by attempting to replan, which is D2's job. D1's classifier only reports the raw
observation; it makes no safety judgment about intermediate states.

**Duplicate live identifiers** (`liveSnapshot.DuplicateIdentifiers` non-empty): per
`LiveModSnapshot`'s own doc comment, this already means "live state can't be trusted." `Classify`
itself doesn't special-case this — `RecoveryAssessment` (§6) surfaces `DuplicateIdentifiers` alongside
the classification list, and it's the *resolution* logic's job to refuse Continue/Restore Previous
State when duplicates are present (§9a of the original design: "Duplicate live identifiers → Disabled,
Disabled, Enabled with a warning" for Continue/Restore/Keep Current respectively) — D1's `Classify`
stays a pure, unconditional function; the "is this assessment trustworthy enough to act on" judgment
lives one layer up, in whichever resolution consumes it (Keep Current explicitly tolerates duplicates
per that table, which is consistent with D1 only needing to gate Keep Current, not the other two).

## 5. `ArtifactStatus`: whole-operation validity, checked before per-item classification runs

```csharp
public enum ArtifactStatus { Valid, PlanMissing, PlanInvalid, SnapshotMissing, SnapshotInvalid }
```

Dropped `JournalInvalid` from the original draft's enum: by the time this code runs, the journal has
already been successfully loaded and validated by `OperationBundleDiscovery`/`OperationRecoveryGraph`
(a journal that fails `OperationJournalCodec.TryLoad` is excluded from the discovery set entirely,
never reaching this far) — there is no code path where `ArtifactStatus` is computed against an
invalid journal. Keeping a permanently-unreachable enum member matches this codebase's own
"defense-in-depth, not currently reachable" convention (see `OperationPlan.Validate`'s Invariant
10/11 comments) only when there's a plausible future path to reachability; here there isn't one I can
identify, so it's cut rather than carried as dead weight — flag if review disagrees.

```csharp
public static class ArtifactStatusChecker
{
    public static (ArtifactStatus Status, OperationPlan? Plan) CheckPlan(string bundleDirectory)
    {
        var path = OperationBundlePaths.PlanPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactStatus.PlanMissing, null);
        return OperationPlanCodec.TryLoad(path, out var plan)
            ? (ArtifactStatus.Valid, plan)
            : (ArtifactStatus.PlanInvalid, null);
    }

    public static (ArtifactStatus Status, RollbackSnapshot? Snapshot) CheckSnapshot(string bundleDirectory)
    {
        var path = OperationBundlePaths.SnapshotPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactStatus.SnapshotMissing, null);
        return OperationSnapshotCodec.TryLoad(path, out var snapshot)
            ? (ArtifactStatus.Valid, snapshot)
            : (ArtifactStatus.SnapshotInvalid, null);
    }
}
```

`OperationPlanCodec.TryLoad`/`OperationSnapshotCodec.TryLoad` already distinguish "file absent" from
"file present but fails to parse/validate" is not quite right — both currently return `false` for
*either* case via `AtomicFile.TryReadValidated`. The `File.Exists` pre-check above is what actually
separates Missing from Invalid, matching the distinction `ArtifactStatus` needs to make and the
existing codecs don't make on their own.

## 6. `RecoveryAssessment`: one atomic read feeding both classification and (later) D2's replanning

```csharp
public sealed record RecoveryAssessment(
    LiveModSnapshot LiveSnapshot,
    IReadOnlyList<ItemRecoveryClassification> Classifications,
    string SnapshotGenerationHash);
```

Built from exactly one `IPenumbraOperations.GetLiveMods()` call — never two independent reads that
could disagree if the library changes mid-classification. `SnapshotGenerationHash` isn't consumed by
anything in D1 (Keep Current doesn't need it), but it's part of this record's shape because D2's
Continue needs to prove its residual replan used the *same* live-state generation as the
classification the user was shown, not a fresher one that could silently disagree — computing it now,
while the `LiveModSnapshot` is in hand, costs nothing and avoids D2 needing to touch this record's
shape later. Computed the same way `OperationPlan.ComputeIntegrityHash` normalizes and hashes path
data (length-prefixed field encoding, `PenumbraPathSemantics.Normalize` per path) — reusing that
established pattern rather than inventing a new one:

```csharp
public static class RecoveryAssessmentBuilder
{
    public static RecoveryAssessment Build(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
        var classifications = RecoveryClassifier.Classify(plan, liveSnapshot);
        var hash = ComputeGenerationHash(liveSnapshot);
        return new RecoveryAssessment(liveSnapshot, classifications, hash);
    }

    private static string ComputeGenerationHash(LiveModSnapshot liveSnapshot)
    {
        var sb = new System.Text.StringBuilder();
        void Field(string value) => sb.Append(System.Text.Encoding.UTF8.GetByteCount(value)
            .ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value);

        foreach (var (identifier, mod) in liveSnapshot.Mods.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Field(identifier);
            Field(PenumbraPathSemantics.Normalize(mod.FullPath, mod.Name));
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
```

## 7. `OperationController`: `PendingRecoveryContext`, startup registration, lazy classification, Keep Current

New private state, parallel to `ActiveOperationContext` but structurally different (no `Mutation`,
nothing advanced per-frame):

```csharp
private sealed class PendingRecoveryContext
{
    public required OperationJournal Journal { get; init; }
    public required string BundleDirectory { get; init; }
    public required OperationRecoveryGraphResult Graph { get; init; }
    public ArtifactStatus PlanStatus { get; set; }
    public OperationPlan? Plan { get; set; }
    public ArtifactStatus SnapshotStatus { get; set; }
    public RollbackSnapshot? Snapshot { get; set; }
    public RecoveryAssessment? Assessment { get; set; } // null until classification succeeds
}

private PendingRecoveryContext? _pendingRecovery;
```

**Resolved decision, not left open:** both `RegisterDiscoveredRecovery` (to rebuild the discovered
journal's active bundle directory) and `ResolveKeepCurrent` below (to build the *completed* bundle
directory it relocates into) need an operations-root path. Rather than threading it through two
separate call sites inconsistently, `OperationController`'s constructor gains a fifth parameter,
`string operationsRoot`, stored as `_operationsRoot` — the same value `Plugin.cs` already computes via
its existing `OperationsRoot` property and passes to `StartApplyOperation`/`StartRestoreOperation`
today, just also handed to the controller once at construction. This is the one constructor-signature
change in this plan; flagging it clearly here since it touches already-shipped composition-root code
in `Plugin.cs`, not because the decision itself is uncertain. **Real implementation cost this creates:**
`OperationControllerTests.cs`'s existing `NewController(adapter, clock, diagnostics)` helper (used by
every test in that file) needs a fifth parameter threaded through, meaning every existing test in that
file is touched by this change even though none of their actual behavior changes — the implementation
plan should account for this as part of the constructor-change task, not treat it as scope creep when
it shows up in the diff.

Registration, called once from `Plugin.cs`'s constructor after `RunStartupDiscovery` (pure, no IPC):

```csharp
public void RegisterDiscoveredRecovery(OperationDiscoveryResult discovery)
{
    if (discovery.Graph.Status != OperationRecoveryGraphStatus.SingleAuthoritative)
    {
        // MultipleDisconnectedRoots / CycleDetected: block starting anything new, but D1 does not
        // build the root-selection mechanism - that's Plan E's job (design doc section 9a: "Blocked
        // until one root is chosen"). There is no single journal to build a PendingRecoveryContext
        // around here, so this is tracked as its own separate flag, not forced into that shape.
        _blockedMultiRootGraph = discovery.Graph;
        PublishState();
        return;
    }

    var authoritativeId = discovery.Graph.AuthoritativeOperationIds[0];
    if (!discovery.Journals.TryGetValue(authoritativeId, out var journal))
        return; // defensive - graph and journals dictionary are built together by RunStartupDiscovery

    var bundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, authoritativeId);
    _pendingRecovery = new PendingRecoveryContext
    {
        Journal = journal, BundleDirectory = bundleDirectory, Graph = discovery.Graph,
        PlanStatus = ArtifactStatus.PlanMissing, SnapshotStatus = ArtifactStatus.SnapshotMissing,
    };
    PublishState();
}
```

A new private field `private OperationRecoveryGraphResult? _blockedMultiRootGraph;` sits alongside
`_pendingRecovery` for exactly this case. `PublishState()` (below) checks both: `_pendingRecovery`
drives the normal single-journal classification flow; `_blockedMultiRootGraph` drives a permanent
lockout with `RecoveryClassificationPending = false` (there is nothing to classify yet — it isn't
*pending*, it's *blocked* on a choice D1 has no mechanism to collect) and `CanResolveRecovery = false`
(D1 has no resolution for this case at all). Both are mutually exclusive by construction (registration
sets exactly one or neither, never both) — `PublishState` doesn't need to reconcile a case where both
are set simultaneously.

`Update()` gains a second thing to service, matching the original design's own warning that an early
return on `_active is null` would silently stall recovery classification forever:

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
    if (pending.PlanStatus != ArtifactStatus.Valid)
        (pending.PlanStatus, pending.Plan) = ArtifactStatusChecker.CheckPlan(pending.BundleDirectory);
    if (pending.SnapshotStatus != ArtifactStatus.Valid)
        (pending.SnapshotStatus, pending.Snapshot) = ArtifactStatusChecker.CheckSnapshot(pending.BundleDirectory);

    if (pending.PlanStatus != ArtifactStatus.Valid || pending.SnapshotStatus != ArtifactStatus.Valid)
    {
        PublishState(); // degraded artifact state is itself worth publishing, even without a live read yet
        return;
    }

    var liveResult = _adapter.GetLiveMods();
    if (liveResult.Status != LiveModReadStatus.Success || liveResult.Snapshot is null)
    {
        PublishState(); // IPC not ready yet - retried next Update() tick, no error surfaced
        return;
    }

    pending.Assessment = RecoveryAssessmentBuilder.Build(pending.Plan!, liveResult.Snapshot);
    PublishState();
}
```

Keep Current, the one resolution D1 implements:

```csharp
public void ResolveKeepCurrent()
{
    if (_pendingRecovery is not { } pending)
        throw new InvalidOperationException("No pending recovery to resolve.");

    var resolvedJournal = pending.Journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
    OperationJournalCodec.Save(OperationBundlePaths.JournalPath(pending.BundleDirectory), resolvedJournal);

    // Relocate immediately rather than waiting for the next startup's discovery pass to do it -
    // avoids a mid-session window where a now-terminal journal still sits under active/, which
    // OperationBundleDiscovery's own relocation logic already treats as "should have been relocated,
    // defensively excluded either way" (a comment written for exactly this kind of timing gap).
    var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, resolvedJournal.OperationId);
    if (!Directory.Exists(completedBundleDirectory))
    {
        Directory.CreateDirectory(OperationBundlePaths.CompletedDirectory(_operationsRoot));
        Directory.Move(pending.BundleDirectory, completedBundleDirectory);
    }

    _pendingRecovery = null;
    PublishState();
}
```

`PublishState()` needs to read from `_pendingRecovery` when `_active` is null, not just fall through
to `Idle`:

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
            RecoveryClassificationPending = false, // nothing to classify - blocked on a root choice D1 can't collect
            CanResolveRecovery = false,
            CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
            CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
        };
        return;
    }

    if (_active is null) // _pendingRecovery is not null
    {
        var pending = _pendingRecovery!;
        var classified = pending.Assessment is not null;
        State = OperationStateSnapshot.Idle with
        {
            RequiresRecovery = true,
            RecoveryClassificationPending = !classified,
            CanResolveRecovery = classified, // Keep Current is only offered once classification succeeds - see open question below
            CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
            CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
        };
        return;
    }

    // ...unchanged _active branch, using CanStartNext exactly as Plan C left it...
}
```

**Open question for review:** should `CanResolveRecovery` (and thus whether the eventual Keep Current
button is enabled) require classification to have succeeded, or should Keep Current remain available
even mid-classification (accepting an *unclassified* current state, arguably still meaningful since
Keep Current's whole point is "don't inspect further, just stop blocking")? The original design's
§9a table lists Keep Current as available in nearly every degraded row *except* "IPC unavailable" —
which reads as "classification pending" should still block it. This draft follows that table (blocks
until classified), but the reasoning given there ("can't scan current state to accept it") is thin —
Keep Current doesn't scan anything, it just marks the journal resolved. Flagging this as a real
judgment call, not silently deciding it.

## 8. `Plugin.cs`: startup wiring

```csharp
public Plugin()
{
    // ...existing field initialization...

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

Exact placement within the constructor (before or after `OperationController` itself is constructed,
and relative to the existing `GetModListAdapterIpc`/`SetModPathIpc` field initialization) needs to be
checked against the constructor's real current body at implementation time — not guessed here, since
`OperationController` must exist before `RegisterDiscoveredRecovery` can be called on it, and
`OperationsRoot` (a property, already exists) must be resolvable before `RunStartupDiscovery` runs.

`ResolveKeepCurrent()`'s `RunScan()` call is deliberate, matching the original design's "trigger a
fresh RunScan()" — even though no UI button calls this method yet (Plan E's job), the backend entry
point exists now, matching every prior plan's own pattern (`StartApplyOperation`/
`StartRestoreOperation` both existed with only a crude `MainWindow` stub before their real UI shipped).

## 9. What D1 does not cover

- Continue and Restore Previous State — Plan D2, reusing `StartApply`/`StartRestore` unchanged per §2.
- The real recovery dialog UI, and the root-selection UI for `MultipleDisconnectedRoots`/
  `CycleDetected` — Plan E, same deferral pattern as every prior plan's UI work.
- `RecoveryDialogSnapshot` (the original design's §7b large/rare-read record for the eventual dialog)
  — not built in this plan. D1 exposes what it needs through `OperationStateSnapshot`'s existing
  `RequiresRecovery`/`RecoveryClassificationPending`/`CanResolveRecovery` fields only; a
  `RecoveryDialogSnapshot`-shaped read (classifications, per-target detail, artifact status for
  display) is deferred to whichever plan actually renders the dialog, so its shape isn't locked in
  prematurely against a UI that doesn't exist yet.
- Diagnostics dump changes (§10 of the original design) — unrelated to this plan's scope, still Plan E
  territory per the original sequencing.

## 10. Testing

Pure/xUnit-testable:
- `RecoveryClassifier.Classify`: one test per `ItemRecoveryState` outcome (`AtBoth`/`AtIntended`/
  `AtSnapshot`/`AtKnownIntermediate`/`AtNeither`/`MissingLive`), plus a duplicate-identifiers case
  proving `Classify` itself doesn't special-case it (that's the resolution layer's job, per §4).
- `ArtifactStatusChecker`: missing file → `PlanMissing`/`SnapshotMissing`; corrupt file → `PlanInvalid`/
  `SnapshotInvalid`; valid file → `Valid` with the parsed value returned.
- `RecoveryAssessmentBuilder.Build`: the returned `Classifications` match a direct `Classify` call;
  `SnapshotGenerationHash` is deterministic (same input twice → same hash) and order-independent
  (shuffling `liveSnapshot.Mods`' enumeration order doesn't change the hash, since it's sorted before
  hashing).
- `OperationController.RegisterDiscoveredRecovery`/`Update`'s classification path/`ResolveKeepCurrent`:
  using the existing `FakePenumbraOperations` test double, covering: no discovered journal → stays
  Idle; a discovered journal with valid artifacts → `RecoveryClassificationPending` true until
  `GetLiveMods()` succeeds, then false with a populated `Assessment`; missing/corrupt plan or snapshot
  → stays `RecoveryClassificationPending` true forever (never calls `GetLiveMods()`), `PlanStatus`/
  `SnapshotStatus` reflect the degradation; `ResolveKeepCurrent` sets `Resolution`, relocates the
  bundle from `active/` to `completed/`, and afterward `CanStartApply`/`CanStartRestore` are true
  again; calling `ResolveKeepCurrent` with no pending recovery throws.
- `MultipleDisconnectedRoots`/`CycleDetected` registration: `RequiresRecovery` becomes `true`,
  `RecoveryClassificationPending` stays `false` (nothing is pending — there's no single journal to
  classify), `CanResolveRecovery` stays `false` (D1 has no resolution for this case) — this is the one
  case D1 needs to prove it locks out cleanly and permanently, even though it can't resolve it.

Not automatable: `Plugin.cs`'s constructor wiring itself — same documented Dalamud-coupled limitation
as every prior plan. Verified by build + a manual checklist (crash mid-Apply, restart, confirm the
plugin correctly reports `RequiresRecovery`; a genuinely clean prior session's completed operations
don't trigger anything; Keep Current actually unblocks Apply/Restore afterward).

## 11. Global constraints for the implementation plan

- `dotnet build` must introduce no new warnings/errors beyond whatever the accepted baseline is at
  implementation time (re-verify at worktree setup, per Plan C's own precedent of the baseline
  drifting from what an earlier plan assumed).
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC — same carried-forward
  limitation.
- `OperationBundleDiscovery`, `OperationRecoveryGraph`, `OperationBundleRetention` are out of scope for
  modification — this plan consumes their existing output unchanged.
- `PenumbraPathSemantics.AreEquivalent`/`Normalize` for every path comparison in new code — never raw
  string equality, matching this codebase's established, hard-learned convention.
