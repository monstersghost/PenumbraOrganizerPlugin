# Tester Feedback v0.4.0 Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three bugs reported in the v0.4.0 tester feedback report (Fellicia [NⅸOS], 2026-07-20): the History tab not showing an automatically created backup until an unrelated action refreshes it; `Restore` letting current protection/Heliosphere status silently prevent reproduction of a mod's historical path; and Folder Cleanup detection appearing stale with no way for the user to tell whether it's actually stale or just needs a nudge. Also close the report's diagnostics gap by persisting the last operation-result summaries across a plugin reload, and close a gap surfaced by the prior folder-protection review: the Protect tab must let a user protect an entire ancestor directory (e.g. `Gear`), not only a folder that happens to be some mod's immediate parent.

**Architecture:** All five fixes are narrow, surgical changes to existing code paths — no new subsystems, with one exception: Task 4 introduces two small new types (structured operation-summary records and a pure diagnostic-formatting helper) after review found the original string-based design too brittle to be worth building. (1) is a one-line cache-invalidation fix in `MainWindow`, already placed outside the try/catch so it runs on both success and failure paths. (2) removes a protection-based skip path from `RollbackHistory.BuildRestorePlan` so a mod present in both the target snapshot and the current library is always moved to its historical path, regardless of current protection/Heliosphere status; a mod present currently but absent from the target snapshot keeps its existing (pre-plan, unchanged) root-relocation behavior — that policy is out of scope for this plan (confirmed with the user, 2026-07-20). Task 2 also adds a read-only `Plugin.PreviewRestore` so the restore-confirmation popup can show exactly how many currently-protected/Heliosphere-managed mods will move, keeping protection visible as warning metadata even though it no longer blocks the operation. (3) cannot fix Penumbra's own on-disk staleness (an external constraint this plugin cannot force — see Task 3's Evidence Note for what is and isn't demonstrated), so it relabels the action and timestamp to describe exactly what happens (a re-read of the on-disk file), rather than implying a live-state refresh. (4) persists structured per-operation summary records (not preformatted strings) to `Configuration`, written via a helper that also captures failure outcomes, and adds a pure `DiagnosticSummaryFormatter` so the fallback-vs-session-precedence logic is unit-testable without touching `MainWindow`. (5) expands `OrganizerState.KnownFolders` to emit every ancestor prefix of each mod's folder path, computed once per scan (not per ImGui frame) and cached, so the Protect tab can offer a checkbox for any level of the tree — the underlying recursive matching (`IsUnderAnyProtectedFolder`) already protects an entire subtree correctly once a folder is checked; this task only fixes which folders are offered as checkboxes.

**Tech Stack:** C#/.NET (Dalamud.NET.Sdk 15.0.0), xUnit for tests, ImGui via `Dalamud.Bindings.ImGui` for UI.

## Revision Note

This plan was reviewed before implementation began (no task had been executed). The review's Critical/High findings and this plan's resolution:

- **Root-relocation semantics for snapshot-absent mods** (review Critical #1): confirmed with the user to be **out of scope** — that behavior predates this plan, the tester never reported it as broken, and changing it is a distinct, unreviewed product decision. Task 2's wording no longer claims "exact... for every mod present in it"; it precisely scopes what changes (mods present in both sets) vs. what doesn't (mods absent from the snapshot, unchanged).
- **No confirmation boundary when protection is ignored** (review Critical #2): resolved by adding `Plugin.PreviewRestore` (Task 2, Step 4) and enhancing the restore-confirmation popup (Task 2, Step 6) to show counts of currently-protected/Heliosphere-managed mods that will nevertheless move — visible warning metadata, without reintroducing protection filtering into `BuildRestorePlan` itself.
- **Restore validation for collisions/cycles** (review Critical #3): verified, not re-litigated — `Restore` and `Apply` already share `ExecuteOrderedMoves` → `Organizer.ApplyPlanner.OrderMovesForApply`, which has existing tests for two-way swaps and three-way rotations (`ApplyPlannerTests.cs:232,250`). No new collision-handling logic is needed; Task 2 does not touch `ExecuteOrderedMoves`.
- **Folder Cleanup "refresh" wording overclaims** (review High #4): resolved — Task 3 renames the action and timestamp to describe a re-read of the on-disk file, not a live-state refresh.
- **Folder Cleanup root cause asserted without evidence** (review High #5): resolved by softening the claim to what was actually verified (code inspection: no in-plugin cache, design-doc cross-reference) vs. what wasn't (no live file-hash/timestamp evidence was gathered — this environment has no running Penumbra instance to gather it against). The Manual Validation Matrix at the end of this plan includes the exact steps to gather that evidence in-game.
- **Persisted diagnostic strings are weak data modeling, no failure-path persistence, weak tests** (review High #6, #7, #8): resolved — Task 4 is redesigned around structured `record` summaries, failure-path persistence via `try`/`catch` around the risky span of each operation, and a pure `DiagnosticSummaryFormatter` with real precedence/fallback tests, plus `Configuration` round-trip and legacy-JSON-compatibility tests.
- **`SkippedProtected` enum removal safety** (review High #12): verified, not asserted — only `RollbackSnapshot` is ever JSON-serialized to disk (`RollbackHistory.cs:31,41`); `RestoreResult`/`RestoreOutcome` are pure in-memory session state. Removal has no storage-compatibility risk.
- **Misleadingly-named protection test** (review Medium #11): renamed with an honest comment describing it as an API-shape test, not an end-to-end guarantee; the true end-to-end check (`Plugin.Restore` actually ignoring `Config.ProtectedModIdentifiers`/`HeliosphereManaged`) is not automatable — `Plugin.cs` requires live Dalamud/Penumbra services and has no unit-test coverage anywhere in this codebase (established convention) — so it's covered in the Manual Validation Matrix instead.
- **Ancestor-path splitting robustness, `KnownFolders` per-frame allocation** (review Medium #13, #14): resolved in Task 5 — `StringSplitOptions.RemoveEmptyEntries`, and the list is computed once in `LoadScan()` and cached, not recomputed on every property read.
- **`FormatElapsed` edge cases** (review Medium #15): resolved — handles negative spans (clock adjustment), hours, and days.
- **Repo-wide reference search** (review #17): added as an explicit step to Tasks 2, 4, and 5 — compilation catches code references but not stale comments/docs.
- **Task 2 commit granularity** (review #18): kept as one commit. Splitting `BuildRestorePlan`'s signature change from its call-site update would leave an intermediate commit that doesn't compile — worse for review than one atomic, clearly-described commit.
- **Test-first sequencing "red via compile error"** (review Medium #10): kept as-is — this matches the identical, already-reviewed-and-approved convention used throughout the merged folder-protection-and-search plan (every task there expects "FAIL (compile error — X doesn't exist yet)"). Not treated as a new deficiency specific to this plan.
- **Task 1 failure-path placement** (review Medium #9): verified already correct, not changed — `MainWindow.ApplyChanges()`'s structure puts `RefreshOrphanedFolders()` (and now `_historyCache = null`) *after* the `try`/`catch` block, unconditionally, so it already runs whether `_plugin.ApplyChanges()` succeeds or throws.

## Global Constraints

- Items 4 (multi-select Manual Assign) and 5 (search on Protect/Manual Assign) from the tester report are already shipped on `main` (folder-protection-and-search plan, merged 2026-07-20) — no task in this plan touches them.
- Bug 3's fix ("Restore ignores current protection state") is a scoped reversal of a prior explicit design decision (multi-save-point rollback plan, 2026-07-19: "current protection always wins over historical snapshot content"), confirmed by the user (2026-07-20). Scope: a mod present in both the target snapshot and the current library is always moved to its historical path — current protection state (`Config.ProtectedModIdentifiers`, `Config.ProtectedFolderPaths`, `HeliosphereManaged`) must never by itself block that move. Root-relocation behavior for a mod present currently but absent from the target snapshot is unchanged and explicitly out of scope for this plan (see Revision Note).
- `MainWindow.cs` has no isolated unit tests anywhere in this codebase (established convention) — every `MainWindow.cs`-only task is verified by build + full existing test suite staying green, not new tests. Where review found this convention was hiding real logic worth testing (Task 4's diagnostic formatting), that logic is extracted into a pure, tested helper rather than the convention being broken in place.
- `RollbackHistory.BuildRestorePlan`'s signature change (dropping the `protectedIdentifiers` parameter) is a breaking change to that method and to `RestorePlan`/`RestoreOutcome` — every call site and every existing test referencing `SkippedProtectedIdentifiers` / `RestoreOutcome.SkippedProtected` must be updated in the same task, not left inconsistent.
- Comparer for folder/identifier sets throughout: `StringComparison.Ordinal` / `StringComparer.Ordinal`, matching the rest of this codebase's established convention.
- `Plugin.cs` requires live Dalamud/Penumbra services and has no unit-test coverage anywhere in this codebase (established convention) — every `Plugin.cs`-only change is verified by build + full test suite, plus the Manual Validation Matrix for behavior that can only be observed against a running Penumbra instance.

---

### Task 1: Fix automatic-backup History-tab staleness

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `_historyCache` (existing private field, same file), `RefreshOrphanedFolders()` (existing, same file).
- Produces: no new public members.

Root cause (confirmed by investigation): `MainWindow.ApplyChanges()` calls `_plugin.ApplyChanges()`, which internally captures and persists an automatic pre-apply snapshot (`Plugin.cs`, inside `ApplyChanges()`) — but `MainWindow.ApplyChanges()` never nulls `_historyCache`, so the History tab's `_historyCache ??= _plugin.LoadHistory();` lazy-load never re-fetches it. Only `RestoreSnapshot`, `CreateBackup`, and `DeleteHistorySnapshot` null the cache today. This task adds the same invalidation to `ApplyChanges()`, placed after the method's `try`/`catch` block (matching the existing `RefreshOrphanedFolders()` call already there) so it runs whether `_plugin.ApplyChanges()` succeeds or throws — a snapshot may already have been captured before a downstream failure, and the cache must not stay stale in that case either.

No isolated unit test — `MainWindow.cs` is ImGui rendering code with no existing test coverage in this codebase. Verification is build + full test suite, plus the Manual Validation Matrix's History Cache checklist (covers the failure-path case, which cannot be exercised without a live Penumbra instance to force a failure).

- [ ] **Step 1: Add the missing cache invalidation**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, find `private void ApplyChanges()` and replace:

```csharp
        RefreshOrphanedFolders(); // ApplyChanges() ran RunScan() internally — occupancy changed
    }
```

(the final two lines of that method) with:

```csharp
        _historyCache = null; // ApplyChanges() also captures a pre-apply snapshot — history changed
        RefreshOrphanedFolders(); // ApplyChanges() ran RunScan() internally — occupancy changed
    }
```

- [ ] **Step 2: Build and run the full test suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS (no regressions).

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "fix: refresh History tab immediately after an automatic Apply snapshot"
```

---

### Task 2: Restore must reproduce a snapshot's historical paths regardless of current protection

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryBuildRestorePlanTests.cs`

**Interfaces:**
- Produces: `RollbackHistory.BuildRestorePlan(RollbackSnapshot target, IReadOnlyList<LiveMod> currentMods) : RestorePlan` (the `protectedIdentifiers` parameter is removed). `RestorePlan` no longer has a `SkippedProtectedIdentifiers` member. `RestoreOutcome` no longer has a `SkippedProtected` member. `Plugin.PreviewRestore(Guid snapshotId) : Organizer.RestorePlan` (new, read-only — computes what a Restore would do without capturing a snapshot or moving anything).
- Consumed by: `Plugin.Restore` (same task, updated call site), `MainWindow`'s Restore-result display and restore-confirmation popup (same task, updated). `Plugin.PreviewRestore` is also consumed by Task 4 (unaffected — Task 4 only touches `Restore`'s body, not `PreviewRestore`).

**Scope, precisely:** a mod present in both the target snapshot's `ModPaths` and the current library is always moved to its historical path — current protection state is never consulted. A mod present in the current library but *absent* from the target snapshot keeps the pre-existing, unchanged root-relocation behavior (moved to `PenumbraPathSemantics.FixName(mod.Name)` at the Penumbra root) — this plan does not touch that policy; see the Revision Note for why. A move is only withheld when the mod isn't present in both sets (`SkippedUninstalledIdentifiers`, unchanged) or when the live `SetModPathIpc` call fails at execution time (already surfaced by the caller as `RestoreOutcome.Failed` — this task does not change that path; collision/cycle safety for the underlying moves is already covered by `ApplyPlanner.OrderMovesForApply`'s existing tests, shared by both Apply and Restore via `ExecuteOrderedMoves`).

- [ ] **Step 1: Update the failing/changed tests first**

Replace the full contents of `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryBuildRestorePlanTests.cs` with:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class RollbackHistoryBuildRestorePlanTests
{
    private static RollbackSnapshot Snapshot(params (string Id, string Path)[] entries) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "n/a",
            entries.ToDictionary(e => e.Id, e => e.Path, StringComparer.Ordinal));

    [Fact]
    public void BuildRestorePlan_MatchingModDifferentPath_ProducesMove()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Gear/Mod A", move.CurrentPath);
        Assert.Equal("Creators/Alice/Mod A", move.TargetPath);
        Assert.Empty(plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MatchingModSamePath_IsUnchanged()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MatchingModPathDiffersOnlyByDuplicateMarker_IsUnchanged()
    {
        // Penumbra discards a transient " (N)" duplicate-marker suffix on save and re-deals it
        // arbitrarily on every load - the historical snapshot path and the live path here are
        // the SAME persisted location, so this must classify as Unchanged, not a proposed Move
        // (see PenumbraPathSemantics.AreEquivalent's doc comment for why raw string equality
        // is wrong here).
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A (2)", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInSnapshot_IsSkippedUninstalled()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod>();

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.SkippedUninstalledIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInCurrentState_MovesToRoot()
    {
        // Pre-existing, unchanged policy: a mod absent from the target snapshot is root-relocated.
        // This plan does not alter this behavior (see the plan's Revision Note) - kept here only
        // to pin the existing contract while Task 2 changes the protection-related classification
        // elsewhere in this method.
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Creators/Alice/Mod A", move.CurrentPath);
        Assert.Equal("Mod A", move.TargetPath);
        Assert.Equal(["mod-a"], plan.RootRelocatedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInCurrentStateAlreadyAtRoot_IsUnchanged()
    {
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModWithDifferentHistoricalPath_MovesRegardlessOfCallerProtectionState()
    {
        // API-shape test only: BuildRestorePlan no longer accepts a protected-identifiers set at
        // all, so there is nothing for a caller to pass that could block this move - this test
        // confirms the method's contract, not that Plugin.Restore's caller correctly refrains
        // from filtering before calling in. That end-to-end guarantee (Plugin.Restore actually
        // ignoring Config.ProtectedModIdentifiers/ProtectedFolderPaths/HeliosphereManaged) can't
        // be automated - Plugin.cs requires live Dalamud/Penumbra services and has no unit-test
        // coverage anywhere in this codebase (established convention). See this plan's Manual
        // Validation Matrix, "Exact Restore" section, for the real end-to-end check.
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Creators/Alice/Mod A", move.TargetPath);
    }

    [Fact]
    public void BuildRestorePlan_HeliosphereManagedModNotInSnapshot_MovesToRootLikeAnyOtherMod()
    {
        // Confirms the pre-existing root-relocation policy (see BuildRestorePlan_ModOnlyInCurrentState_MovesToRoot)
        // now applies uniformly regardless of HeliosphereManaged - previously this specific case
        // was diverted into "skipped protected" purely because HeliosphereManaged was true. This
        // does NOT introduce a new destructive policy: root-relocation for snapshot-absent mods
        // already existed before this plan: this test only confirms HeliosphereManaged no longer
        // special-cases it.
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: true) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Mod A", move.TargetPath);
        Assert.Equal(["mod-a"], plan.RootRelocatedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MultipleMods_ClassifiesEachIndependently()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"), ("mod-c", "Gear/Mod C"));
        var current = new List<LiveMod>
        {
            new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false), // move
            new("mod-b", "Mod B", "Gear/Mod B", HeliosphereManaged: false), // root-relocated
        };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Equal(["mod-a", "mod-b"], plan.Moves.Select(m => m.Identifier).OrderBy(id => id));
        Assert.Equal(["mod-b"], plan.RootRelocatedIdentifiers);
        Assert.Equal(["mod-c"], plan.SkippedUninstalledIdentifiers);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryBuildRestorePlanTests`
Expected: FAIL (compile error — `BuildRestorePlan` still requires a third `protectedIdentifiers` argument). This matches the same "red via compile error" convention already used and approved throughout the merged folder-protection-and-search plan, since every test in this file calls the method whose signature is changing.

- [ ] **Step 3: Update `RollbackHistory.cs`**

In `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`, replace this line:

```csharp
public enum RestoreOutcome { Moved, Unchanged, SkippedUninstalled, RootRelocated, SkippedProtected, Failed }
```

with:

```csharp
public enum RestoreOutcome { Moved, Unchanged, SkippedUninstalled, RootRelocated, Failed }
```

Replace:

```csharp
public sealed record RestorePlan(
    IReadOnlyList<ModMove> Moves,
    IReadOnlyList<string> UnchangedIdentifiers,
    IReadOnlyList<string> SkippedUninstalledIdentifiers,
    IReadOnlyList<string> RootRelocatedIdentifiers,
    IReadOnlyList<string> SkippedProtectedIdentifiers);
```

with:

```csharp
public sealed record RestorePlan(
    IReadOnlyList<ModMove> Moves,
    IReadOnlyList<string> UnchangedIdentifiers,
    IReadOnlyList<string> SkippedUninstalledIdentifiers,
    IReadOnlyList<string> RootRelocatedIdentifiers);
```

Replace the full `BuildRestorePlan` method (everything from its doc comment through its closing brace):

```csharp
    // Every currently-installed mod is classified into exactly one bucket by comparing it
    // against the target snapshot's ModPaths and the live "is this mod locked right now" set
    // (protected or Heliosphere-managed). Current protection always wins over historical
    // snapshot content - see design spec, Restore section: a snapshot must never be a way to
    // move a mod the user has since locked. Mods present only in the snapshot (uninstalled
    // since capture) are reported, never moved.
    public static RestorePlan BuildRestorePlan(
        RollbackSnapshot target, IReadOnlyList<LiveMod> currentMods, IReadOnlySet<string> protectedIdentifiers)
    {
        var moves = new List<ModMove>();
        var unchanged = new List<string>();
        var rootRelocated = new List<string>();
        var skippedProtected = new List<string>();

        foreach (var mod in currentMods)
        {
            var isLocked = protectedIdentifiers.Contains(mod.Identifier) || mod.HeliosphereManaged;

            if (target.ModPaths.TryGetValue(mod.Identifier, out var historicalPath))
            {
                if (PenumbraPathSemantics.AreEquivalent(mod.FullPath, historicalPath, mod.Name))
                    unchanged.Add(mod.Identifier);
                else if (isLocked)
                    skippedProtected.Add(mod.Identifier);
                else
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, historicalPath));
            }
            else
            {
                var rootPath = PenumbraPathSemantics.FixName(mod.Name);
                if (PenumbraPathSemantics.AreEquivalent(mod.FullPath, rootPath, mod.Name))
                {
                    unchanged.Add(mod.Identifier);
                }
                else if (isLocked)
                {
                    skippedProtected.Add(mod.Identifier);
                }
                else
                {
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, rootPath));
                    rootRelocated.Add(mod.Identifier);
                }
            }
        }

        var currentIdentifiers = currentMods.Select(m => m.Identifier).ToHashSet(StringComparer.Ordinal);
        var skippedUninstalled = target.ModPaths.Keys
            .Where(id => !currentIdentifiers.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new RestorePlan(moves, unchanged, skippedUninstalled, rootRelocated, skippedProtected);
    }
```

with:

```csharp
    // A mod present in both the target snapshot and the current library is always moved to its
    // historical path here - current protection state (individual, folder, or Heliosphere-
    // managed) is deliberately NOT consulted. A snapshot captured while a mod was movable must
    // remain restorable even if the user has since protected it (tester report, Bug 3: "Restore
    // should operate from snapshot data, not current sorting protection policy"). A move is only
    // withheld when the mod isn't present in both sets (SkippedUninstalledIdentifiers, below) or
    // when Penumbra's own SetModPath rejects it at execution time - that failure is surfaced by
    // the caller (Plugin.Restore) as RestoreOutcome.Failed, not by this method.
    //
    // A mod present in the current library but absent from the target snapshot is root-relocated
    // (rootRelocated, below) - this is PRE-EXISTING, UNCHANGED behavior, not new to this method:
    // it predates the protection-removal change above and is out of scope for this plan (the
    // tester never reported it, and changing it is a distinct product decision - see the plan's
    // Revision Note). It is documented here only so a future reader doesn't mistake it for a
    // consequence of the change directly above it.
    //
    // Mods present only in the snapshot (uninstalled since capture) are reported, never moved.
    public static RestorePlan BuildRestorePlan(RollbackSnapshot target, IReadOnlyList<LiveMod> currentMods)
    {
        var moves = new List<ModMove>();
        var unchanged = new List<string>();
        var rootRelocated = new List<string>();

        foreach (var mod in currentMods)
        {
            if (target.ModPaths.TryGetValue(mod.Identifier, out var historicalPath))
            {
                if (PenumbraPathSemantics.AreEquivalent(mod.FullPath, historicalPath, mod.Name))
                    unchanged.Add(mod.Identifier);
                else
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, historicalPath));
            }
            else
            {
                var rootPath = PenumbraPathSemantics.FixName(mod.Name);
                if (PenumbraPathSemantics.AreEquivalent(mod.FullPath, rootPath, mod.Name))
                {
                    unchanged.Add(mod.Identifier);
                }
                else
                {
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, rootPath));
                    rootRelocated.Add(mod.Identifier);
                }
            }
        }

        var currentIdentifiers = currentMods.Select(m => m.Identifier).ToHashSet(StringComparer.Ordinal);
        var skippedUninstalled = target.ModPaths.Keys
            .Where(id => !currentIdentifiers.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new RestorePlan(moves, unchanged, skippedUninstalled, rootRelocated);
    }
```

- [ ] **Step 4: Update `Plugin.cs`'s call site and add `PreviewRestore`**

In `PenumbraOrganizer.Plugin/Plugin.cs`, inside `Restore(Guid snapshotId)`, replace:

```csharp
            // Restore doesn't scan (OrganizerState/row.Protected may be stale or empty), so it
            // can't rely on the derived boolean - it must independently combine explicit
            // individual protection with folder protection here. BuildRestorePlan already ORs
            // in mod.HeliosphereManaged internally, so that term isn't duplicated.
            var lockedIdentifiers = Config.ProtectedModIdentifiers
                .Union(currentMods
                    .Where(m => Organizer.OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(m.FullPath, Config.ProtectedFolderPaths))
                    .Select(m => m.Identifier))
                .ToHashSet(StringComparer.Ordinal);
            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods, lockedIdentifiers);
```

with:

```csharp
            // Current protection state (individual, folder, or Heliosphere) is deliberately
            // never passed to BuildRestorePlan for mods present in the snapshot - see its doc
            // comment and this plan's Global Constraints for why (tester report, Bug 3).
            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);
```

Then, in the same method, replace:

```csharp
            var results = new List<Organizer.RestoreResult>();
            foreach (var identifier in plan.UnchangedIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.Unchanged, null));
            foreach (var identifier in plan.SkippedUninstalledIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedUninstalled, null));
            foreach (var identifier in plan.SkippedProtectedIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedProtected, null));
```

with:

```csharp
            var results = new List<Organizer.RestoreResult>();
            foreach (var identifier in plan.UnchangedIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.Unchanged, null));
            foreach (var identifier in plan.SkippedUninstalledIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedUninstalled, null));
```

Then, directly after the closing brace of `Restore(Guid snapshotId)` (before `ExecuteOrderedMoves`), add a new method:

```csharp
    // Read-only: computes what a Restore would do without capturing a snapshot or moving
    // anything, so the confirmation popup can show currently-protected/Heliosphere-managed mods
    // that will nevertheless move under this plan's Bug 3 fix, before the user commits to it.
    internal Organizer.RestorePlan PreviewRestore(Guid snapshotId)
    {
        var history = Organizer.RollbackHistory.Load(HistoryFilePath);
        var target = history.FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new InvalidOperationException("Snapshot not found.");
        var currentMods = ReadCurrentMods();
        return Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);
    }
```

- [ ] **Step 5: Update `MainWindow.cs`'s Restore-result summary text**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, inside `DrawHistoryTab()`, replace:

```csharp
        if (_lastRestoreResults is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            var moved = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Moved);
            var rootRelocated = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.RootRelocated);
            var skippedUninstalled = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedUninstalled);
            var skippedProtected = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedProtected);
            var failed = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Failed);
            ImGui.TextUnformatted(
                $"Restore: {moved} moved, {rootRelocated} relocated to root, {skippedUninstalled} skipped (uninstalled), " +
                $"{skippedProtected} skipped (protected), {failed} failed.");
            foreach (var failure in _lastRestoreResults.Where(r => r.Outcome == Organizer.RestoreOutcome.Failed))
                ImGui.TextColored(PluginTheme.CollisionBad, $"  {failure.Identifier}: {failure.FailureReason}");
        }
```

with:

```csharp
        if (_lastRestoreResults is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            var moved = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Moved);
            var rootRelocated = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.RootRelocated);
            var skippedUninstalled = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedUninstalled);
            var failed = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Failed);
            ImGui.TextUnformatted(
                $"Restore: {moved} moved, {rootRelocated} relocated to root, {skippedUninstalled} skipped (uninstalled), " +
                $"{failed} failed.");
            foreach (var failure in _lastRestoreResults.Where(r => r.Outcome == Organizer.RestoreOutcome.Failed))
                ImGui.TextColored(PluginTheme.CollisionBad, $"  {failure.Identifier}: {failure.FailureReason}");
        }
```

- [ ] **Step 6: Enhance the restore-confirmation popup with an accurate preview and a protection warning**

Still in `DrawHistoryTab()`, replace:

```csharp
            if (_pendingRestoreSnapshotId == snapshot.Id && ImGui.BeginPopupModal("Restore snapshot?"))
            {
                var currentIdentifiers = _plugin.OrganizerState.Mods.Select(m => m.Identifier).ToHashSet(StringComparer.Ordinal);
                var willMove = currentIdentifiers.Count(id2 => snapshot.ModPaths.ContainsKey(id2));
                var missingFromSnapshot = currentIdentifiers.Count(id2 => !snapshot.ModPaths.ContainsKey(id2));
                var uninstalledSinceSnapshot = snapshot.ModPaths.Keys.Count(id2 => !currentIdentifiers.Contains(id2));

                ImGui.TextUnformatted($"Restore to: {title}");
                ImGui.TextUnformatted($"Up to {willMove} mods known to this snapshot may move.");
                ImGui.TextUnformatted($"Up to {missingFromSnapshot} mods installed since this snapshot may be moved to the Penumbra root.");
                ImGui.TextUnformatted($"{uninstalledSinceSnapshot} mods from this snapshot are no longer installed and will be skipped.");
                ImGui.TextColored(ImGuiColors.DalamudYellow, "Currently protected or Heliosphere-managed mods are never moved.");

                if (ImGui.Button("Yes, Restore"))
```

with:

```csharp
            if (_pendingRestoreSnapshotId == snapshot.Id && ImGui.BeginPopupModal("Restore snapshot?"))
            {
                // Exact preview via PreviewRestore, not an ad-hoc estimate: the previous
                // "Up to N mods... may move" counts only checked snapshot membership, not whether
                // the historical path actually differs from the current one - this replaces that
                // with the real plan the Restore button will execute.
                var preview = _plugin.PreviewRestore(snapshot.Id);
                var modsByIdentifier = _plugin.OrganizerState.Mods.ToDictionary(m => m.Identifier, m => m);
                var protectedMovingCount = preview.Moves.Count(move =>
                    modsByIdentifier.TryGetValue(move.Identifier, out var mod) && mod.Protected);
                var heliosphereMovingCount = preview.Moves.Count(move =>
                    modsByIdentifier.TryGetValue(move.Identifier, out var mod) && mod.HeliosphereManaged);

                ImGui.TextUnformatted($"Restore to: {title}");
                ImGui.TextUnformatted($"{preview.Moves.Count} mods will move to their snapshot path.");
                ImGui.TextUnformatted($"{preview.UnchangedIdentifiers.Count} mods are already at their snapshot path.");
                ImGui.TextUnformatted($"{preview.RootRelocatedIdentifiers.Count} mods installed since this snapshot will be moved to the Penumbra root.");
                ImGui.TextUnformatted($"{preview.SkippedUninstalledIdentifiers.Count} mods from this snapshot are no longer installed and will be skipped.");
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "Exact Restore: this reproduces the snapshot's historical paths, including for mods that are "
                    + "currently protected or Heliosphere-managed.");
                if (protectedMovingCount > 0 || heliosphereMovingCount > 0)
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        $"{protectedMovingCount} currently protected and {heliosphereMovingCount} Heliosphere-managed "
                        + "mod(s) among these will move despite their current protection status.");

                if (ImGui.Button("Yes, Restore"))
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryBuildRestorePlanTests`
Expected: PASS (all tests in this file)

- [ ] **Step 8: Repo-wide reference search**

Run: `grep -rn "SkippedProtected" --include=*.cs .`
Expected: no matches anywhere in `PenumbraOrganizer.Plugin` or `PenumbraOrganizer.Plugin.Tests` (compilation alone would catch code references, but not stray comments or dead code excluded by build configuration — this confirms neither exists).

- [ ] **Step 9: Build and run the full test suite to confirm no regressions**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors (confirms `Plugin.cs` and `MainWindow.cs` compile against the new `BuildRestorePlan`/`RestorePlan`/`RestoreOutcome` shapes and the new `PreviewRestore` method).

Run: `dotnet test`
Expected: All tests PASS.

- [ ] **Step 10: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryBuildRestorePlanTests.cs
git commit -m "fix: Restore reproduces snapshot paths regardless of current protection, with a preview warning"
```

---

### Task 3: Folder Cleanup — visible last-read indicator, and wording that doesn't overclaim

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `RefreshOrphanedFolders()` (existing, same file), `Organizer.FolderDetectionStatus` (existing, `PenumbraOrganizer.Plugin/Organizer/FolderCleanupResult.cs`, unchanged).
- Produces: no new public members.

**Evidence note (what this diagnosis is and isn't based on):** code inspection confirms `_orphanedFolders` is unconditionally overwritten by `RefreshOrphanedFolders()` on every Scan/Apply/Restore/CleanUp/Rollback, and `Plugin.DetectOrphanedFolders()` reads `organization.json` fresh from disk on every call — there is no in-plugin cache to invalidate. The hypothesis that the remaining staleness traces to Penumbra's own on-disk `organization.json` not reflecting Penumbra's live folder tree until a Penumbra-internal trigger is *consistent with* this codebase's own design doc (`docs/superpowers/specs/2026-07-15-plugin-organizer-folder-cleanup-design.md`, "Live-tree propagation" section) and with the tester's report (the file only updated after a manual folder move in Penumbra's own UI). It was **not** independently confirmed with byte-level evidence (organization.json's `LastWriteTimeUtc` or content hash, captured before and after a live Penumbra folder move) — this environment has no running Penumbra instance to gather that against. The Manual Validation Matrix at the end of this plan includes the exact steps to gather that evidence in-game. Given that uncertainty, this task does not attempt a code fix for the underlying staleness (there would be nothing to fix in this plugin if the hypothesis is right) — it makes the staleness visible and unambiguous regardless of which diagnosis turns out correct, and stops implying that a button labeled "Refresh" can force fresher-than-disk data.

No isolated unit test — `MainWindow.cs` is ImGui rendering code with no existing test coverage in this codebase. Verification is build + full test suite, plus the Manual Validation Matrix's Folder Cleanup checklist.

- [ ] **Step 1: Add the last-read field**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, find:

```csharp
    private Organizer.FolderDetectionResult? _orphanedFolders;
```

and add directly after it:

```csharp
    private Organizer.FolderDetectionResult? _orphanedFolders;
    private DateTimeOffset? _organizationJsonLastReadAt;
```

- [ ] **Step 2: Record the timestamp on every read**

Find `RefreshOrphanedFolders()`:

```csharp
    private void RefreshOrphanedFolders()
    {
        try
        {
            _orphanedFolders = _plugin.DetectOrphanedFolders();
            _selectedOrphans.Clear();
            if (_orphanedFolders.Status == Organizer.FolderDetectionStatus.Detected)
                foreach (var path in _orphanedFolders.PlainEmpty)
                    _selectedOrphans.Add(path);
        }
        catch (Exception ex)
        {
            _lastError = $"Orphaned-folder detection failed: {ex.Message}";
        }
    }
```

Replace with:

```csharp
    private void RefreshOrphanedFolders()
    {
        try
        {
            _orphanedFolders = _plugin.DetectOrphanedFolders();
            _organizationJsonLastReadAt = DateTimeOffset.Now;
            _selectedOrphans.Clear();
            if (_orphanedFolders.Status == Organizer.FolderDetectionStatus.Detected)
                foreach (var path in _orphanedFolders.PlainEmpty)
                    _selectedOrphans.Add(path);
        }
        catch (Exception ex)
        {
            _lastError = $"Orphaned-folder detection failed: {ex.Message}";
        }
    }
```

- [ ] **Step 3: Add a small elapsed-time formatter**

Directly after `RefreshOrphanedFolders()`, add:

```csharp
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero; // defends against a backward system-clock adjustment

        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours}h";

        return $"{(int)elapsed.TotalDays}d";
    }
```

- [ ] **Step 4: Show the re-read button and last-read timestamp in the Orphaned Folders section**

In `DrawOrphanedFoldersSection()`, find:

```csharp
        var total = detection.PlainEmpty.Count + detection.CustomizedEmpty.Count;

        if (_folderReloadRequired)
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "Waiting on Rediscover Mods — the list below reflects organization.json on disk, "
                + "not Penumbra's confirmed live state. Click Rediscover Mods in Penumbra, then Scan here, to re-check.");

        ImGui.TextUnformatted($"Orphaned Folders ({total} detected)");

        if (total > 0)
        {
```

Replace with:

```csharp
        var total = detection.PlainEmpty.Count + detection.CustomizedEmpty.Count;

        if (_folderReloadRequired)
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "Waiting on Rediscover Mods — the list below reflects organization.json on disk, "
                + "not Penumbra's confirmed live state. Click Rediscover Mods in Penumbra, then Scan here, to re-check.");

        ImGui.TextUnformatted($"Orphaned Folders ({total} detected)");

        if (ImGui.Button("Re-read organization.json##orphan-reread"))
            RefreshOrphanedFolders();

        ImGui.SameLine();
        ImGui.TextDisabled(_organizationJsonLastReadAt is { } readAt
            ? $"Last read: {readAt.ToLocalTime():HH:mm:ss} ({FormatElapsed(DateTimeOffset.Now - readAt)} ago)"
            : "Not yet read this session.");

        ImGui.TextDisabled(
            "This reflects organization.json on disk as of the last read above, not Penumbra's live folder tree. "
            + "If Penumbra hasn't written a change to disk yet, re-reading won't show it — move a folder in "
            + "Penumbra's own UI (or use Rediscover Mods) to make Penumbra flush its tree, then re-read again.");

        if (total > 0)
        {
```

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS (no regressions).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "fix: describe Folder Cleanup detection as a file re-read, with a visible last-read time"
```

---

### Task 4: Persist structured operation-result summaries across plugin reload

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/OperationSummaries.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/DiagnosticSummaryFormatter.cs`
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/DiagnosticSummaryFormatterTests.cs` (new)

**Interfaces:**
- Produces: `OperationCompletionStatus` enum (`Succeeded`, `PartiallySucceeded`, `Failed`); `ApplyOperationSummary`, `RestoreOperationSummary` records (using `OperationCompletionStatus`); `FolderCleanupOperationSummary`, `FolderCleanupRollbackOperationSummary` records (reusing the existing `FolderCleanupStatus`/`FolderRollbackStatus` enums, since those are already more precise than a generic status). `Configuration.LastApply/.LastRestore/.LastFolderCleanup/.LastFolderCleanupRollback` (nullable record properties). `DiagnosticSummaryFormatter.FormatApplySection/.FormatRestoreSection/.FormatFolderCleanupSection/.FormatFolderCleanupRollbackSection` (pure static string-producing methods, each taking the in-session result and the persisted summary, session taking precedence).
- Consumed by: `Plugin.cs` (writes summaries after each operation, including on failure), `MainWindow.CreateDiagnosticDump` (calls the formatter instead of inlining the fallback logic).

The tester's diagnostic dump showed "no Apply run this session" etc. even though those operations had been performed — confirmed root cause: `MainWindow`'s `_lastApplyResults`/`_lastRestoreResults`/`_lastCleanupResult`/`_lastFolderRollbackResult` are plain instance fields on `MainWindow`, which is recreated every time the plugin loads, so they're always lost on reload. This task persists a lightweight structured summary per operation (not the full result objects, to avoid bloating the config file, and not preformatted strings, which review found couples persisted data to current UI wording and can't be tested semantically). Each operation's write happens as soon as its outcome is known — before the trailing `RunScan()` call in `ApplyChanges`/`Restore`, so a `RunScan()` failure afterward doesn't erase a summary of the operation that already completed — and a failure inside the risky span of the operation itself (move execution) is caught and persisted as `Failed` before rethrowing, rather than being silently lost.

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs`. First, add this using directive at the very top of the file:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests;
```

(replacing the existing `namespace PenumbraOrganizer.Plugin.Tests;` line — this file currently has no `using` directives).

Then add these tests inside the existing `ConfigurationTests` class:

```csharp
    [Fact]
    public void DefaultConfiguration_HasNullLastOperationSummaries()
    {
        var config = new Configuration();

        Assert.Null(config.LastApply);
        Assert.Null(config.LastRestore);
        Assert.Null(config.LastFolderCleanup);
        Assert.Null(config.LastFolderCleanupRollback);
    }

    [Fact]
    public void Configuration_RoundTripsOperationSummariesThroughJson()
    {
        var config = new Configuration
        {
            LastApply = new ApplyOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:00:00Z"), OperationCompletionStatus.PartiallySucceeded, 3, 1),
            LastRestore = new RestoreOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:05:00Z"), OperationCompletionStatus.Succeeded, 2, 1, 0, 1, 0),
            LastFolderCleanup = new FolderCleanupOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:10:00Z"), FolderCleanupStatus.Success, 5, 0),
            LastFolderCleanupRollback = new FolderCleanupRollbackOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:15:00Z"), FolderRollbackStatus.Restored),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json);

        Assert.Equal(config.LastApply, roundTripped!.LastApply);
        Assert.Equal(config.LastRestore, roundTripped.LastRestore);
        Assert.Equal(config.LastFolderCleanup, roundTripped.LastFolderCleanup);
        Assert.Equal(config.LastFolderCleanupRollback, roundTripped.LastFolderCleanupRollback);
    }

    [Fact]
    public void Configuration_DeserializesLegacyJsonWithoutSummaryFields()
    {
        const string legacyJson = """{"Version":1,"ProtectedModIdentifiers":["a"],"ProtectedFolderPaths":["Gear"]}""";

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(legacyJson);

        Assert.NotNull(config);
        Assert.Contains("a", config!.ProtectedModIdentifiers);
        Assert.Null(config.LastApply);
        Assert.Null(config.LastRestore);
        Assert.Null(config.LastFolderCleanup);
        Assert.Null(config.LastFolderCleanupRollback);
    }
```

Add a new file `PenumbraOrganizer.Plugin.Tests/Organizer/DiagnosticSummaryFormatterTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class DiagnosticSummaryFormatterTests
{
    [Fact]
    public void FormatApplySection_SessionResultsPresent_FormatsCountsAndFailures()
    {
        var results = new List<ApplyResult> { new("a", true, null), new("b", false, "PenumbraApiEc.NothingChanged") };

        var text = DiagnosticSummaryFormatter.FormatApplySection(results, persisted: null);

        Assert.Equal("1 succeeded, 1 failed\n  FAILED: b: PenumbraApiEc.NothingChanged", text);
    }

    [Fact]
    public void FormatApplySection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new ApplyOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"), OperationCompletionStatus.PartiallySucceeded, 3, 1);

        var text = DiagnosticSummaryFormatter.FormatApplySection(sessionResults: null, persisted);

        Assert.Equal(
            "(no Apply run this session; last known from a prior session: 2026-07-20 10:00:00Z — PartiallySucceeded, 3 succeeded, 1 failed)",
            text);
    }

    [Fact]
    public void FormatApplySection_NeitherSessionNorPersisted_ReportsNoApplyRun()
    {
        var text = DiagnosticSummaryFormatter.FormatApplySection(sessionResults: null, persisted: null);

        Assert.Equal("(no Apply run this session)", text);
    }

    [Fact]
    public void FormatApplySection_SessionResultPresent_TakesPrecedenceOverPersisted()
    {
        var results = new List<ApplyResult> { new("a", true, null) };
        var persisted = new ApplyOperationSummary(DateTimeOffset.UtcNow, OperationCompletionStatus.Failed, 0, 99);

        var text = DiagnosticSummaryFormatter.FormatApplySection(results, persisted);

        Assert.Equal("1 succeeded, 0 failed", text);
    }

    [Fact]
    public void FormatRestoreSection_SessionResultsPresent_GroupsByOutcomeInDeterministicOrder()
    {
        var results = new List<RestoreResult>
        {
            new("a", RestoreOutcome.Moved, null),
            new("b", RestoreOutcome.Unchanged, null),
            new("c", RestoreOutcome.Failed, "PenumbraApiEc.PathRenameFailed"),
        };

        var text = DiagnosticSummaryFormatter.FormatRestoreSection(results, persisted: null);

        Assert.Equal("  Moved: 1\n  Unchanged: 1\n  Failed: 1\n  FAILED: c: PenumbraApiEc.PathRenameFailed", text);
    }

    [Fact]
    public void FormatRestoreSection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new RestoreOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:05:00Z"), OperationCompletionStatus.Succeeded, 2, 1, 0, 1, 0);

        var text = DiagnosticSummaryFormatter.FormatRestoreSection(sessionResults: null, persisted);

        Assert.Equal(
            "(no Restore run this session; last known from a prior session: 2026-07-20 10:05:00Z — Succeeded, "
            + "2 moved, 1 unchanged, 0 skipped uninstalled, 1 relocated to root, 0 failed)",
            text);
    }

    [Fact]
    public void FormatRestoreSection_NeitherSessionNorPersisted_ReportsNoRestoreRun()
    {
        var text = DiagnosticSummaryFormatter.FormatRestoreSection(sessionResults: null, persisted: null);

        Assert.Equal("(no Restore run this session)", text);
    }

    [Fact]
    public void FormatFolderCleanupSection_SessionResultPresent_FormatsStatusAndCounts()
    {
        var result = new FolderCleanupResult(["Gear/Old"], [], FolderCleanupStatus.Success);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupSection(result, persisted: null);

        Assert.Equal("Status=Success, Pruned=1, SkippedStale=0", text);
    }

    [Fact]
    public void FormatFolderCleanupSection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new FolderCleanupOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:10:00Z"), FolderCleanupStatus.Success, 5, 0);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupSection(sessionResult: null, persisted);

        Assert.Equal(
            "(no Folder Cleanup run this session; last known from a prior session: 2026-07-20 10:10:00Z — Status=Success, Pruned=5, SkippedStale=0)",
            text);
    }

    [Fact]
    public void FormatFolderCleanupSection_NeitherSessionNorPersisted_ReportsNoCleanupRun()
    {
        var text = DiagnosticSummaryFormatter.FormatFolderCleanupSection(sessionResult: null, persisted: null);

        Assert.Equal("(no Folder Cleanup run this session)", text);
    }

    [Fact]
    public void FormatFolderCleanupRollbackSection_SessionResultPresent_FormatsStatus()
    {
        var result = new FolderRollbackResult(FolderRollbackStatus.Restored);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(result, persisted: null);

        Assert.Equal("Status=Restored", text);
    }

    [Fact]
    public void FormatFolderCleanupRollbackSection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new FolderCleanupRollbackOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:15:00Z"), FolderRollbackStatus.NoBackup);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(sessionResult: null, persisted);

        Assert.Equal(
            "(no Folder Cleanup Rollback run this session; last known from a prior session: 2026-07-20 10:15:00Z — Status=NoBackup)",
            text);
    }

    [Fact]
    public void FormatFolderCleanupRollbackSection_NeitherSessionNorPersisted_ReportsNoRollbackRun()
    {
        var text = DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(sessionResult: null, persisted: null);

        Assert.Equal("(no Folder Cleanup Rollback run this session)", text);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ConfigurationTests|FullyQualifiedName~DiagnosticSummaryFormatterTests"`
Expected: FAIL (compile error — `LastApply`/`ApplyOperationSummary`/`DiagnosticSummaryFormatter` etc. don't exist yet)

- [ ] **Step 3: Create `OperationSummaries.cs`**

Create `PenumbraOrganizer.Plugin/Organizer/OperationSummaries.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

public enum OperationCompletionStatus { Succeeded, PartiallySucceeded, Failed }

public sealed record ApplyOperationSummary(
    DateTimeOffset CompletedAt, OperationCompletionStatus Status, int Succeeded, int Failed);

public sealed record RestoreOperationSummary(
    DateTimeOffset CompletedAt,
    OperationCompletionStatus Status,
    int Moved,
    int Unchanged,
    int SkippedUninstalled,
    int RootRelocated,
    int Failed);

// Reuses the existing, more precise FolderCleanupStatus/FolderRollbackStatus enums (FolderCleanupResult.cs)
// rather than the generic OperationCompletionStatus above - Folder Cleanup and its rollback already have
// dedicated status enums covering exactly their own failure modes.
public sealed record FolderCleanupOperationSummary(
    DateTimeOffset CompletedAt, FolderCleanupStatus Status, int Pruned, int SkippedStale);

public sealed record FolderCleanupRollbackOperationSummary(
    DateTimeOffset CompletedAt, FolderRollbackStatus Status);
```

- [ ] **Step 4: Create `DiagnosticSummaryFormatter.cs`**

Create `PenumbraOrganizer.Plugin/Organizer/DiagnosticSummaryFormatter.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

// Pure formatting, deliberately separated from MainWindow (which has no test coverage in this
// codebase) so the session-vs-persisted precedence and fallback formatting can be verified
// directly, rather than only by inspecting MainWindow's diagnostic-dump output.
public static class DiagnosticSummaryFormatter
{
    public static string FormatApplySection(IReadOnlyList<ApplyResult>? sessionResults, ApplyOperationSummary? persisted)
    {
        if (sessionResults is not null)
        {
            var succeeded = sessionResults.Count(r => r.Success);
            var sb = new System.Text.StringBuilder();
            sb.Append($"{succeeded} succeeded, {sessionResults.Count - succeeded} failed");
            foreach (var failure in sessionResults.Where(r => !r.Success))
                sb.Append($"\n  FAILED: {failure.Identifier}: {failure.FailureReason}");
            return sb.ToString();
        }

        return persisted is null
            ? "(no Apply run this session)"
            : $"(no Apply run this session; last known from a prior session: {persisted.CompletedAt:u} — "
              + $"{persisted.Status}, {persisted.Succeeded} succeeded, {persisted.Failed} failed)";
    }

    public static string FormatRestoreSection(IReadOnlyList<RestoreResult>? sessionResults, RestoreOperationSummary? persisted)
    {
        if (sessionResults is not null)
        {
            var outcomeLines = sessionResults
                .GroupBy(r => r.Outcome)
                .OrderBy(g => g.Key)
                .Select(g => $"  {g.Key}: {g.Count()}");
            var failureLines = sessionResults
                .Where(r => r.Outcome == RestoreOutcome.Failed)
                .OrderBy(f => f.Identifier, StringComparer.Ordinal)
                .Select(f => $"  FAILED: {f.Identifier}: {f.FailureReason}");
            return string.Join("\n", outcomeLines.Concat(failureLines));
        }

        return persisted is null
            ? "(no Restore run this session)"
            : $"(no Restore run this session; last known from a prior session: {persisted.CompletedAt:u} — "
              + $"{persisted.Status}, {persisted.Moved} moved, {persisted.Unchanged} unchanged, "
              + $"{persisted.SkippedUninstalled} skipped uninstalled, {persisted.RootRelocated} relocated to root, {persisted.Failed} failed)";
    }

    public static string FormatFolderCleanupSection(FolderCleanupResult? sessionResult, FolderCleanupOperationSummary? persisted)
    {
        if (sessionResult is not null)
            return $"Status={sessionResult.Status}, Pruned={sessionResult.Pruned.Count}, SkippedStale={sessionResult.SkippedStale.Count}";

        return persisted is null
            ? "(no Folder Cleanup run this session)"
            : $"(no Folder Cleanup run this session; last known from a prior session: {persisted.CompletedAt:u} — "
              + $"Status={persisted.Status}, Pruned={persisted.Pruned}, SkippedStale={persisted.SkippedStale})";
    }

    public static string FormatFolderCleanupRollbackSection(
        FolderRollbackResult? sessionResult, FolderCleanupRollbackOperationSummary? persisted)
    {
        if (sessionResult is not null)
            return $"Status={sessionResult.Status}";

        return persisted is null
            ? "(no Folder Cleanup Rollback run this session)"
            : $"(no Folder Cleanup Rollback run this session; last known from a prior session: {persisted.CompletedAt:u} — Status={persisted.Status})";
    }
}
```

- [ ] **Step 5: Add the fields to `Configuration.cs`**

Replace the full contents of `PenumbraOrganizer.Plugin/Configuration.cs` with:

```csharp
using Dalamud.Configuration;

namespace PenumbraOrganizer.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public HashSet<string> ProtectedModIdentifiers { get; set; } = [];

    public HashSet<string> ProtectedFolderPaths { get; set; } = [];

    public Organizer.ApplyOperationSummary? LastApply { get; set; }

    public Organizer.RestoreOperationSummary? LastRestore { get; set; }

    public Organizer.FolderCleanupOperationSummary? LastFolderCleanup { get; set; }

    public Organizer.FolderCleanupRollbackOperationSummary? LastFolderCleanupRollback { get; set; }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ConfigurationTests|FullyQualifiedName~DiagnosticSummaryFormatterTests"`
Expected: PASS

- [ ] **Step 7: Persist the Apply summary, including on failure**

In `PenumbraOrganizer.Plugin/Plugin.cs`, replace the full `ApplyChanges()` method:

```csharp
    internal IReadOnlyList<Organizer.ApplyResult> ApplyChanges()
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var validation = OrganizerState.Validate();
            if (validation.HasIssues)
                throw new InvalidOperationException("Cannot Apply while Validate() reports issues.");

            // Equivalence, not raw string equality: a path differing only by a transient " (N)"
            // duplicate marker (or Penumbra's own name-trimming) is the same persisted location —
            // moving it would be a no-op write that Penumbra reshuffles on the next reload anyway.
            var touchedRows = OrganizerState.Mods
                .Where(m => !m.Protected && !Organizer.PenumbraPathSemantics.AreEquivalent(m.CurrentPath, m.ProposedPath, m.Name))
                .ToList();

            var folderCollisions = Organizer.ApplyPlanner.FolderPathCollisions(touchedRows, ReadExistingOrganizationFolderPaths());
            if (folderCollisions.Count > 0)
                throw new InvalidOperationException(
                    "Cannot Apply: the proposed path for the following mods matches an existing (likely orphaned) " +
                    "folder entry in Penumbra's organization.json, which Penumbra's own SetModPath will reject: " +
                    $"{string.Join(", ", folderCollisions)}. Run Folder Cleanup on the Review Changes tab to prune " +
                    "orphaned folders, then try Apply again.");

            var currentMods = ReadCurrentMods();
            var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label: null, $"{touchedRows.Count} mods moved");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);

            var moves = touchedRows
                .Select(r => new Organizer.ModMove(r.Identifier, r.CurrentPath, r.ProposedPath))
                .ToList();
            var failureByIdentifier = ExecuteOrderedMoves(moves);
            var results = touchedRows
                .Select(r => new Organizer.ApplyResult(
                    r.Identifier, !failureByIdentifier.ContainsKey(r.Identifier), failureByIdentifier.GetValueOrDefault(r.Identifier)))
                .ToList();

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }
```

with:

```csharp
    internal IReadOnlyList<Organizer.ApplyResult> ApplyChanges()
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var validation = OrganizerState.Validate();
            if (validation.HasIssues)
                throw new InvalidOperationException("Cannot Apply while Validate() reports issues.");

            // Equivalence, not raw string equality: a path differing only by a transient " (N)"
            // duplicate marker (or Penumbra's own name-trimming) is the same persisted location —
            // moving it would be a no-op write that Penumbra reshuffles on the next reload anyway.
            var touchedRows = OrganizerState.Mods
                .Where(m => !m.Protected && !Organizer.PenumbraPathSemantics.AreEquivalent(m.CurrentPath, m.ProposedPath, m.Name))
                .ToList();

            var folderCollisions = Organizer.ApplyPlanner.FolderPathCollisions(touchedRows, ReadExistingOrganizationFolderPaths());
            if (folderCollisions.Count > 0)
                throw new InvalidOperationException(
                    "Cannot Apply: the proposed path for the following mods matches an existing (likely orphaned) " +
                    "folder entry in Penumbra's organization.json, which Penumbra's own SetModPath will reject: " +
                    $"{string.Join(", ", folderCollisions)}. Run Folder Cleanup on the Review Changes tab to prune " +
                    "orphaned folders, then try Apply again.");

            var currentMods = ReadCurrentMods();
            var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label: null, $"{touchedRows.Count} mods moved");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);

            // From here on, a snapshot has already been captured - an unexpected exception must
            // still leave a diagnostic trail behind (tester report: prior-session Apply results
            // were silently lost on reload), not just bubble up with the outcome unrecorded.
            List<Organizer.ApplyResult> results;
            try
            {
                var moves = touchedRows
                    .Select(r => new Organizer.ModMove(r.Identifier, r.CurrentPath, r.ProposedPath))
                    .ToList();
                var failureByIdentifier = ExecuteOrderedMoves(moves);
                results = touchedRows
                    .Select(r => new Organizer.ApplyResult(
                        r.Identifier, !failureByIdentifier.ContainsKey(r.Identifier), failureByIdentifier.GetValueOrDefault(r.Identifier)))
                    .ToList();
            }
            catch (Exception)
            {
                Config.LastApply = new Organizer.ApplyOperationSummary(
                    DateTimeOffset.Now, Organizer.OperationCompletionStatus.Failed, Succeeded: 0, Failed: touchedRows.Count);
                PluginInterface.SavePluginConfig(Config);
                throw;
            }

            var applySucceeded = results.Count(r => r.Success);
            var applyStatus = results.Count == 0 || applySucceeded == results.Count
                ? Organizer.OperationCompletionStatus.Succeeded
                : applySucceeded == 0
                    ? Organizer.OperationCompletionStatus.Failed
                    : Organizer.OperationCompletionStatus.PartiallySucceeded;
            Config.LastApply = new Organizer.ApplyOperationSummary(
                DateTimeOffset.Now, applyStatus, applySucceeded, results.Count - applySucceeded);
            PluginInterface.SavePluginConfig(Config);

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }
```

- [ ] **Step 8: Persist the Restore summary, including on failure**

In `PenumbraOrganizer.Plugin/Plugin.cs`, replace the full `Restore(Guid snapshotId)` method (as it stands after Task 2's edits):

```csharp
    internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var history = Organizer.RollbackHistory.Load(HistoryFilePath);
            var target = history.FirstOrDefault(s => s.Id == snapshotId)
                ?? throw new InvalidOperationException("Snapshot not found.");

            var currentMods = ReadCurrentMods();

            // Pre-restore snapshot makes the restore itself undoable - captured and persisted
            // before any moves happen, same as Apply's own pre-operation capture.
            var preRestoreLabel = target.Label ?? target.CreatedAt.ToString("u");
            var preRestoreSnapshot = Organizer.RollbackHistory.CaptureSnapshot(
                currentMods, label: null, autoDescription: $"Snapshot before restoring to \"{preRestoreLabel}\"");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, preRestoreSnapshot);

            // Current protection state (individual, folder, or Heliosphere) is deliberately
            // never passed to BuildRestorePlan for mods present in the snapshot - see its doc
            // comment and this plan's Global Constraints for why (tester report, Bug 3).
            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);
            var failureByIdentifier = ExecuteOrderedMoves(plan.Moves);

            var results = new List<Organizer.RestoreResult>();
            foreach (var identifier in plan.UnchangedIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.Unchanged, null));
            foreach (var identifier in plan.SkippedUninstalledIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedUninstalled, null));

            var rootRelocatedIds = plan.RootRelocatedIdentifiers.ToHashSet(StringComparer.Ordinal);
            foreach (var move in plan.Moves)
            {
                var failed = failureByIdentifier.TryGetValue(move.Identifier, out var reason);
                var outcome = failed
                    ? Organizer.RestoreOutcome.Failed
                    : rootRelocatedIds.Contains(move.Identifier)
                        ? Organizer.RestoreOutcome.RootRelocated
                        : Organizer.RestoreOutcome.Moved;
                results.Add(new Organizer.RestoreResult(move.Identifier, outcome, failed ? reason : null));
            }

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }
```

with:

```csharp
    internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var history = Organizer.RollbackHistory.Load(HistoryFilePath);
            var target = history.FirstOrDefault(s => s.Id == snapshotId)
                ?? throw new InvalidOperationException("Snapshot not found.");

            var currentMods = ReadCurrentMods();

            // Pre-restore snapshot makes the restore itself undoable - captured and persisted
            // before any moves happen, same as Apply's own pre-operation capture.
            var preRestoreLabel = target.Label ?? target.CreatedAt.ToString("u");
            var preRestoreSnapshot = Organizer.RollbackHistory.CaptureSnapshot(
                currentMods, label: null, autoDescription: $"Snapshot before restoring to \"{preRestoreLabel}\"");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, preRestoreSnapshot);

            // Current protection state (individual, folder, or Heliosphere) is deliberately
            // never passed to BuildRestorePlan for mods present in the snapshot - see its doc
            // comment and this plan's Global Constraints for why (tester report, Bug 3).
            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods);

            // From here on, a pre-restore snapshot has already been captured - an unexpected
            // exception must still leave a diagnostic trail behind, same reasoning as ApplyChanges().
            List<Organizer.RestoreResult> results;
            try
            {
                var failureByIdentifier = ExecuteOrderedMoves(plan.Moves);

                results = new List<Organizer.RestoreResult>();
                foreach (var identifier in plan.UnchangedIdentifiers)
                    results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.Unchanged, null));
                foreach (var identifier in plan.SkippedUninstalledIdentifiers)
                    results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedUninstalled, null));

                var rootRelocatedIds = plan.RootRelocatedIdentifiers.ToHashSet(StringComparer.Ordinal);
                foreach (var move in plan.Moves)
                {
                    var failed = failureByIdentifier.TryGetValue(move.Identifier, out var reason);
                    var outcome = failed
                        ? Organizer.RestoreOutcome.Failed
                        : rootRelocatedIds.Contains(move.Identifier)
                            ? Organizer.RestoreOutcome.RootRelocated
                            : Organizer.RestoreOutcome.Moved;
                    results.Add(new Organizer.RestoreResult(move.Identifier, outcome, failed ? reason : null));
                }
            }
            catch (Exception)
            {
                Config.LastRestore = new Organizer.RestoreOperationSummary(
                    DateTimeOffset.Now, Organizer.OperationCompletionStatus.Failed,
                    Moved: 0, Unchanged: 0, SkippedUninstalled: 0, RootRelocated: 0, Failed: plan.Moves.Count);
                PluginInterface.SavePluginConfig(Config);
                throw;
            }

            var failedCount = results.Count(r => r.Outcome == Organizer.RestoreOutcome.Failed);
            var restoreStatus = failedCount == 0
                ? Organizer.OperationCompletionStatus.Succeeded
                : failedCount == plan.Moves.Count
                    ? Organizer.OperationCompletionStatus.Failed
                    : Organizer.OperationCompletionStatus.PartiallySucceeded;
            Config.LastRestore = new Organizer.RestoreOperationSummary(
                DateTimeOffset.Now,
                restoreStatus,
                Moved: results.Count(r => r.Outcome == Organizer.RestoreOutcome.Moved),
                Unchanged: results.Count(r => r.Outcome == Organizer.RestoreOutcome.Unchanged),
                SkippedUninstalled: results.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedUninstalled),
                RootRelocated: results.Count(r => r.Outcome == Organizer.RestoreOutcome.RootRelocated),
                Failed: failedCount);
            PluginInterface.SavePluginConfig(Config);

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }
```

- [ ] **Step 9: Persist the Folder Cleanup and Folder Cleanup Rollback summaries**

In `PenumbraOrganizer.Plugin/Plugin.cs`, replace:

```csharp
    internal Organizer.FolderCleanupResult CleanUpFolders(IReadOnlySet<string> selectedPaths)
    {
        // Fresh IPC read at write time — OrganizerState is only as fresh as the last scan and
        // can't see mods moved via Penumbra's own UI since then. Deliberately NOT RunScan(),
        // which would reset every ProposedPath and wipe staged sort proposals. If this throws
        // (Penumbra unavailable), nothing has been written: a clean abort surfaced by the
        // caller's error handling.
        using var modList = GetModListAdapterIpc.Invoke();
        var occupied = OccupiedFolders(modList.Select(m => m.FullPath));

        return Organizer.FolderCleanupExecutor.Execute(
            OrganizationJsonPath, FolderBackupFilePath, selectedPaths, occupied);
    }

    internal Organizer.FolderRollbackResult RollbackFolderCleanup() =>
        Organizer.FolderCleanupExecutor.ExecuteRollback(OrganizationJsonPath, FolderBackupFilePath);
```

with:

```csharp
    internal Organizer.FolderCleanupResult CleanUpFolders(IReadOnlySet<string> selectedPaths)
    {
        // Fresh IPC read at write time — OrganizerState is only as fresh as the last scan and
        // can't see mods moved via Penumbra's own UI since then. Deliberately NOT RunScan(),
        // which would reset every ProposedPath and wipe staged sort proposals. If this throws
        // (Penumbra unavailable), nothing has been written: a clean abort surfaced by the
        // caller's error handling.
        using var modList = GetModListAdapterIpc.Invoke();
        var occupied = OccupiedFolders(modList.Select(m => m.FullPath));

        var result = Organizer.FolderCleanupExecutor.Execute(
            OrganizationJsonPath, FolderBackupFilePath, selectedPaths, occupied);

        Config.LastFolderCleanup = new Organizer.FolderCleanupOperationSummary(
            DateTimeOffset.Now, result.Status, result.Pruned.Count, result.SkippedStale.Count);
        PluginInterface.SavePluginConfig(Config);

        return result;
    }

    internal Organizer.FolderRollbackResult RollbackFolderCleanup()
    {
        var result = Organizer.FolderCleanupExecutor.ExecuteRollback(OrganizationJsonPath, FolderBackupFilePath);

        Config.LastFolderCleanupRollback = new Organizer.FolderCleanupRollbackOperationSummary(DateTimeOffset.Now, result.Status);
        PluginInterface.SavePluginConfig(Config);

        return result;
    }
```

- [ ] **Step 10: Use the formatter in the diagnostic dump**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, inside `CreateDiagnosticDump()`, replace:

```csharp
        sb.AppendLine("== Last Apply result ==");
        if (_lastApplyResults is null)
        {
            sb.AppendLine("(no Apply run this session)");
        }
        else
        {
            var succeeded = _lastApplyResults.Count(r => r.Success);
            sb.AppendLine($"{succeeded} succeeded, {_lastApplyResults.Count - succeeded} failed");
            foreach (var failure in _lastApplyResults.Where(r => !r.Success))
                sb.AppendLine($"  FAILED: {failure.Identifier}: {failure.FailureReason}");
        }
        sb.AppendLine();

        sb.AppendLine("== Last Restore result ==");
        if (_lastRestoreResults is null)
        {
            sb.AppendLine("(no Restore run this session)");
        }
        else
        {
            foreach (var group in _lastRestoreResults.GroupBy(r => r.Outcome))
                sb.AppendLine($"  {group.Key}: {group.Count()}");
            foreach (var failure in _lastRestoreResults.Where(r => r.Outcome == Organizer.RestoreOutcome.Failed))
                sb.AppendLine($"  FAILED: {failure.Identifier}: {failure.FailureReason}");
        }
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup result ==");
        sb.AppendLine(_lastCleanupResult is null
            ? "(no Folder Cleanup run this session)"
            : $"Status={_lastCleanupResult.Status}, Pruned={_lastCleanupResult.Pruned.Count}, SkippedStale={_lastCleanupResult.SkippedStale.Count}");
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup Rollback result ==");
        sb.AppendLine(_lastFolderRollbackResult is null
            ? "(no Folder Cleanup Rollback run this session)"
            : $"Status={_lastFolderRollbackResult.Status}");
        sb.AppendLine();
```

with:

```csharp
        sb.AppendLine("== Last Apply result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatApplySection(_lastApplyResults, _plugin.Config.LastApply));
        sb.AppendLine();

        sb.AppendLine("== Last Restore result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatRestoreSection(_lastRestoreResults, _plugin.Config.LastRestore));
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatFolderCleanupSection(_lastCleanupResult, _plugin.Config.LastFolderCleanup));
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup Rollback result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(
            _lastFolderRollbackResult, _plugin.Config.LastFolderCleanupRollback));
        sb.AppendLine();
```

- [ ] **Step 11: Repo-wide reference search**

Run: `grep -rn "LastApply\|LastRestore\|LastFolderCleanup\|LastFolderCleanupRollback" --include=*.cs .`
Expected: matches only in `Configuration.cs` (declarations), `Plugin.cs` (writes), `MainWindow.cs` (reads via `_plugin.Config.*`), and the two test files added/modified in this task — every reference uses the same four names, no leftover references to any earlier naming.

- [ ] **Step 12: Build and run the full test suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors (confirms `_plugin.Config` — an `internal` member — is accessible from `MainWindow`, which is in the same assembly).

Run: `dotnet test`
Expected: All tests PASS.

- [ ] **Step 13: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OperationSummaries.cs PenumbraOrganizer.Plugin/Organizer/DiagnosticSummaryFormatter.cs PenumbraOrganizer.Plugin/Configuration.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs PenumbraOrganizer.Plugin.Tests/Organizer/DiagnosticSummaryFormatterTests.cs
git commit -m "fix: persist structured operation summaries, including failures, so diagnostics survive a plugin reload"
```

---

### Task 5: KnownFolders must offer every ancestor folder, computed once per scan

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Modifies: `OrganizerState.KnownFolders : IReadOnlyList<string>` (existing property, same shape and name — only its contents and computation timing change). Consumed by `MainWindow.DrawProtectTab` (unchanged in this task — it already unions `KnownFolders` with `ProtectedFolders` and renders one checkbox per entry, so it needs no code change to display the new ancestor rows).

Surfaced by the final whole-branch review of the folder-protection-and-search plan (merged 2026-07-20): `KnownFolders` only emits each scanned mod's *direct* virtual parent (via `GetVirtualParent`), so a mod at `Gear/Feet/Boots` only ever contributes `Gear/Feet` to the list — `Gear` itself never appears as a checkbox unless some other mod happens to live directly under `Gear`. The underlying matching, `OrganizationCleanupPlanner.IsUnderAnyProtectedFolder` (Task 1 of the folder-protection-and-search plan), already matches recursively at any depth — checking `Gear` today would already correctly protect everything under `Gear/Feet/...` if the checkbox existed. This task expands which folders are offered, by emitting every ancestor prefix of each mod's folder path, computed once per `LoadScan()` call and cached (review found the original per-access computation allocates on every ImGui frame the Protect tab is open).

- [ ] **Step 1: Write the failing tests**

In `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`, replace the existing test:

```csharp
    [Fact]
    public void KnownFolders_DerivesDistinctParentsFromScannedMods()
    {
        var state = new OrganizerState();
        var a = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        var b = MakeRow("b", "Hat", currentPath: "Gear/Feet/Hat");
        var c = MakeRow("c", "Root", currentPath: "RootMod");
        state.LoadScan([a, b, c], new HashSet<string>());

        Assert.Equal(["Gear/Feet"], state.KnownFolders);
    }
```

with:

```csharp
    [Fact]
    public void KnownFolders_DerivesDistinctParentsFromScannedMods()
    {
        var state = new OrganizerState();
        var a = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        var b = MakeRow("b", "Hat", currentPath: "Gear/Feet/Hat");
        var c = MakeRow("c", "Root", currentPath: "RootMod");
        state.LoadScan([a, b, c], new HashSet<string>());

        Assert.Equal(["Gear", "Gear/Feet"], state.KnownFolders);
    }

    [Fact]
    public void KnownFolders_IncludesEveryAncestorPrefixOfADeeplyNestedMod()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Sub/Boots");
        state.LoadScan([row], new HashSet<string>());

        Assert.Equal(["Gear", "Gear/Feet", "Gear/Feet/Sub"], state.KnownFolders);
    }

    [Fact]
    public void KnownFolders_ProtectingAnAncestorOfferedByThisExpansion_ProtectsTheDeepMod()
    {
        // End-to-end confirmation that the newly offered ancestor row actually works through the
        // existing (unchanged) recursive matching in SetFolderProtected/IsUnderAnyProtectedFolder.
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Sub/Boots");
        state.LoadScan([row], new HashSet<string>());
        Assert.Contains("Gear", state.KnownFolders);

        state.SetFolderProtected("Gear", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void KnownFolders_IsRecomputedOnEachLoadScan_NotStaleFromAPriorScan()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots")], new HashSet<string>());
        Assert.Equal(["Gear", "Gear/Feet"], state.KnownFolders);

        state.LoadScan([MakeRow("b", "Hat", currentPath: "Face/Hat")], new HashSet<string>());

        Assert.Equal(["Face"], state.KnownFolders);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizerStateTests`
Expected: FAIL — `KnownFolders_DerivesDistinctParentsFromScannedMods` now expects `["Gear", "Gear/Feet"]` but the current implementation still returns `["Gear/Feet"]`; the three new tests fail the same way (the ancestor rows don't exist yet).

- [ ] **Step 3: Write the implementation**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, replace:

```csharp
    private readonly Dictionary<string, OrganizerModRow> _mods = new();
    private readonly HashSet<string> _protectedModIdentifiers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _protectedFolders = new(StringComparer.Ordinal);
```

with:

```csharp
    private readonly Dictionary<string, OrganizerModRow> _mods = new();
    private readonly HashSet<string> _protectedModIdentifiers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _protectedFolders = new(StringComparer.Ordinal);
    private readonly List<string> _knownFolders = [];
```

Replace:

```csharp
    // Distinct virtual-parent folders among currently scanned mods — deliberately does NOT
    // include persisted-but-currently-empty ProtectedFolders entries; callers that need the
    // union (the Protect tab's folder list) build it themselves from both properties.
    public IReadOnlyList<string> KnownFolders =>
        _mods.Values
            .Select(m => OrganizationCleanupPlanner.GetVirtualParent(m.CurrentPath))
            .Where(f => f is not null)
            .Select(f => f!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
```

with:

```csharp
    // Every ancestor prefix of every scanned mod's virtual-parent folder — not just each mod's
    // immediate parent — so the Protect tab can offer a checkbox for any level of the tree a user
    // might want to protect (e.g. "Gear" when mods only live at "Gear/Feet/..."). The recursive
    // matching in IsUnderAnyProtectedFolder already protects an entire subtree correctly once a
    // folder is checked; this property only controls which folders are offered as checkboxes.
    // Recomputed once per LoadScan() (below), not on every access - this is read every ImGui
    // frame the Protect tab is open, and CurrentPath (which it depends on) is fixed for the
    // lifetime of a scan. Deliberately does NOT include persisted-but-currently-empty
    // ProtectedFolders entries; callers that need the union (the Protect tab's folder list) build
    // it themselves from both properties.
    public IReadOnlyList<string> KnownFolders => _knownFolders;

    // "Gear/Feet/Sub" -> ["Gear", "Gear/Feet", "Gear/Feet/Sub"].
    private static IEnumerable<string> AncestorChain(string folder)
    {
        var segments = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
            yield return string.Join('/', segments.Take(i + 1));
    }
```

Then, in the same file, replace `LoadScan`:

```csharp
    // previouslyProtectedFolders defaults to null (treated as empty) so every existing call
    // site across this test project that predates folder protection keeps compiling unchanged.
    public void LoadScan(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlySet<string>? previouslyProtectedFolders = null)
    {
        HasScanned = true;
        _mods.Clear();
        _protectedModIdentifiers.Clear();
        _protectedModIdentifiers.UnionWith(previouslyProtectedIdentifiers);
        _protectedFolders.Clear();
        _protectedFolders.UnionWith(previouslyProtectedFolders ?? new HashSet<string>(StringComparer.Ordinal));

        foreach (var row in scanned)
        {
            row.Protected = IsEffectivelyProtectedFull(row);
            row.ProposedPath = row.CurrentPath;
            _mods[row.Identifier] = row;
        }
    }
```

with:

```csharp
    // previouslyProtectedFolders defaults to null (treated as empty) so every existing call
    // site across this test project that predates folder protection keeps compiling unchanged.
    public void LoadScan(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlySet<string>? previouslyProtectedFolders = null)
    {
        HasScanned = true;
        _mods.Clear();
        _protectedModIdentifiers.Clear();
        _protectedModIdentifiers.UnionWith(previouslyProtectedIdentifiers);
        _protectedFolders.Clear();
        _protectedFolders.UnionWith(previouslyProtectedFolders ?? new HashSet<string>(StringComparer.Ordinal));

        foreach (var row in scanned)
        {
            row.Protected = IsEffectivelyProtectedFull(row);
            row.ProposedPath = row.CurrentPath;
            _mods[row.Identifier] = row;
        }

        _knownFolders.Clear();
        _knownFolders.AddRange(
            _mods.Values
                .Select(m => OrganizationCleanupPlanner.GetVirtualParent(m.CurrentPath))
                .Where(f => f is not null)
                .Select(f => f!)
                .SelectMany(AncestorChain)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizerStateTests`
Expected: PASS (all tests in this file, including the new ones — this includes every other pre-existing test in the file too, since no other call site's behavior changed)

- [ ] **Step 5: Repo-wide reference search**

Run: `grep -rn "KnownFolders" --include=*.cs .`
Expected: matches only in `OrganizerState.cs` (field, property, `LoadScan`) and `MainWindow.cs` (`DrawProtectTab`'s existing consumption) — confirms no other consumer assumed the old direct-parent-only, computed-on-every-access contents.

- [ ] **Step 6: Run the full test suite to confirm no regressions**

Run: `dotnet test`
Expected: All tests PASS.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: offer every ancestor folder for protection, computed once per scan"
```

---

## Manual Validation Matrix

Several changes in this plan can only be fully verified against a running Dalamud/Penumbra instance (`Plugin.cs` and `MainWindow.cs` have no automated test coverage in this codebase — see Global Constraints). Run this checklist in-game after all five tasks are implemented and merged.

**History cache (Task 1):**
1. Open History, note current snapshots.
2. Apply a move. Return to History without creating a manual backup. Confirm the new automatic snapshot appears immediately.
3. Force an Apply failure if possible (e.g. a mod path Penumbra will reject) after the pre-apply snapshot is captured. Confirm History still shows the new snapshot despite the failure.

**Exact Restore (Task 2):**
1. Restore a snapshot containing: an individually-protected mod, a folder-protected mod, and a Heliosphere-managed mod, each with a historical path different from their current path. Confirm all three move to their historical path, and confirm the confirmation popup's protected/Heliosphere warning counts matched what actually happened.
2. Restore a snapshot where some mods were added after capture (confirm root-relocation, unchanged from prior behavior) and some were removed since capture (confirm skipped-as-uninstalled).
3. Force a partial IPC failure mid-restore if possible. Confirm the pre-restore snapshot makes that partial state recoverable via a second Restore.

**Folder Cleanup on-disk freshness (Task 3):**
1. Read detection, note the count and "Last read" timestamp.
2. Move a folder in Penumbra's own UI without using Rediscover Mods.
3. Click "Re-read organization.json". Record: did the count change? Did `organization.json`'s on-disk `LastWriteTimeUtc` change? This directly tests the plan's Evidence Note hypothesis — record the actual result for future reference regardless of outcome.
4. Trigger Rediscover Mods in Penumbra, then re-read again. Confirm the count now reflects the change from step 2.

**Diagnostics persistence (Task 4):**
1. Apply a change. Reload the plugin (disable/enable). Generate a diagnostic dump. Confirm it shows the prior-session Apply summary via the "last known from a prior session" fallback text.
2. Run a new Apply in the same (reloaded) session. Generate another dump. Confirm the current-session result is shown, not the persisted fallback.

**Ancestor folder protection (Task 5):**
1. With mods only under a deep folder (e.g. `Gear/Feet/Sub/...`), confirm `Gear` and `Gear/Feet` both appear as checkboxes on the Protect tab.
2. Check the top-level ancestor (`Gear`). Confirm every mod under it, at any depth, shows as protected.
3. Uncheck the ancestor. Confirm protection is correctly removed (unless another source — individual, a different folder, or Heliosphere — still protects a given mod).

## Self-Review

**Spec coverage:** every bug and the diagnostics gap from the tester report has a task — Bug 1 (Task 1), Bug 3 (Task 2, scoped per the Revision Note), Bug 2 (Task 3, scoped to what's actually fixable given the confirmed external Penumbra-side constraint), diagnostics persistence (Task 4). Feature requests 4 and 5 are already shipped (documented in Global Constraints, no task needed). Task 5 covers the ancestor-folder-protection gap surfaced by the prior plan's final review, per explicit user direction ("full directory protection, subfolders and mods in them should be protected"). Every Critical/High finding from the pre-implementation review is addressed or explicitly deferred with reasoning in the Revision Note above.

**Placeholder scan:** no TBD/TODO; every step shows complete code.

**Type consistency:** `RollbackHistory.BuildRestorePlan`'s new two-argument signature is used identically in Task 2's test file, in `Plugin.Restore`'s call site, and in the new `Plugin.PreviewRestore` (also Task 2). `RestorePlan`'s four-field shape (no `SkippedProtectedIdentifiers`) and `RestoreOutcome`'s five-value shape (no `SkippedProtected`) are consistent between Task 2's `RollbackHistory.cs` edit, `Plugin.cs`'s consumption of `plan.*` in both `Restore` and `PreviewRestore`, and `MainWindow.cs`'s consumption of `RestoreOutcome.*`. Task 4's four `Configuration` properties (`LastApply`, `LastRestore`, `LastFolderCleanup`, `LastFolderCleanupRollback`) and their record types (`ApplyOperationSummary`, `RestoreOperationSummary`, `FolderCleanupOperationSummary`, `FolderCleanupRollbackOperationSummary`) are named and typed identically between `OperationSummaries.cs`, `Configuration.cs`, their write sites in `Plugin.cs`, their read sites in `DiagnosticSummaryFormatter.cs` (consumed by `MainWindow.CreateDiagnosticDump`), and both test files. Task 5's `KnownFolders` keeps its existing name, type (`IReadOnlyList<string>`), and consumer (`MainWindow.DrawProtectTab`'s existing union-with-`ProtectedFolders` logic) unchanged — only its contents and computation timing change — so no downstream signature needs updating.
