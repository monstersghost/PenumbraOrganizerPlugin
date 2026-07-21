# Phase 1d: Collision Disambiguation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `SortByCreator`/`SortByModType` from producing colliding `ProposedPath`s when Penumbra duplicate installs share a display `Name`, per the approved spec `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1d-collision-disambiguation-design.md`.

**Architecture:** One new pure, static class, `CollisionDisambiguator`, called from both `SortByCreator` and `SortByModType` right after each assigns its tentative `"{folder}/{Name}"` paths. It reserves every tentative path across the whole touched-row set up front, then renumbers each colliding group's non-canonical members with the first free `(N)` suffix — so a generated suffix can never land on an unrelated row's path, not just on another member of its own original collision group.

**Tech Stack:** C# / .NET 10, `Dalamud.NET.Sdk/15.0.0`, xunit (existing test project `PenumbraOrganizer.Plugin.Tests`).

## Global Constraints

- No change to `AssignManual`/manual sort — a user-created collision must keep surfacing through `Validate()` unchanged.
- No new folder taxonomy (no "Duplicates" folder) — disambiguation is a neutral `(2)`/`(3)` renumbering, never a judgment about whether the colliding mods are actually the same content.
- No content-based guessing (e.g. comparing changed-item keys to detect "real" duplicates) — consistent with the Phase 1c classifier's "never guess" principle.
- This guarantees no collision among the rows a single sort call touches. It does not reach into protected rows' `CurrentPath` or `SortByModType`-excluded `Unknown` rows' leftover `ProposedPath` — that's a separate, pre-existing gap, deliberately out of scope (spec, Open Risks #3).
- All string comparisons: `StringComparer.OrdinalIgnoreCase` for path grouping/reservation (matches `Validate()`), `StringComparison.Ordinal` for `Identifier`/`Name` equality and sort order.
- `Identifier`/`Name` on `OrganizerModRow` are non-nullable `required string` — no null-handling needed.
- No write IPC of any kind — this phase remains read-only; Apply stays disabled.
- Build must stay at 0 warnings / 0 errors; all existing 82 tests must keep passing.
- Run all commands from the repo root `C:\Repo\PenumbraOrganizer.Plugin`.

---

### Task 1: `CollisionDisambiguator`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/CollisionDisambiguator.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/CollisionDisambiguatorTests.cs`

**Interfaces:**
- Consumes: `OrganizerModRow` (existing, `PenumbraOrganizer.Plugin.Organizer` namespace — same namespace, no new `using` needed).
- Produces: `static void CollisionDisambiguator.Disambiguate(IEnumerable<OrganizerModRow> rows)` — mutates `ProposedPath` on colliding rows in place; non-colliding rows are untouched.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/CollisionDisambiguatorTests.cs`:

```csharp
using System.Collections;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class CollisionDisambiguatorTests
{
    private static OrganizerModRow MakeRow(string identifier, string name, string proposedPath) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = proposedPath,
        ProposedPath = proposedPath,
    };

    // Wraps a list and throws if GetEnumerator() is called more than once, proving
    // Disambiguate materializes its input instead of enumerating it repeatedly.
    private sealed class SingleEnumerationGuard(IReadOnlyList<OrganizerModRow> rows) : IEnumerable<OrganizerModRow>
    {
        private bool _enumerated;

        public IEnumerator<OrganizerModRow> GetEnumerator()
        {
            if (_enumerated)
                throw new InvalidOperationException("Enumerated more than once.");
            _enumerated = true;
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Disambiguate_TwoWayCollisionWithExactIdentifierMatch_CanonicalStaysBareOtherGetsSuffix()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([canonical, duplicate]);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_ThreeWayCollisionOneCanonical_OthersNumberedSequentially()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var dupA = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var dupB = MakeRow("Foo_3", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([canonical, dupA, dupB]);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", dupA.ProposedPath);
        Assert.Equal("Creator/Foo (3)", dupB.ProposedPath);
    }

    [Fact]
    public void Disambiguate_NoExactIdentifierMatch_LowestIdentifierStaysBareRestNumbered()
    {
        // Neither row's Identifier equals "Foo" - both copies were manually renamed
        // away from Penumbra's default, so there's no "original" signal to key off.
        var rowZeta = MakeRow("Zeta", "Foo", "Creator/Foo");
        var rowAlpha = MakeRow("Alpha", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([rowZeta, rowAlpha]);

        Assert.Equal("Creator/Foo", rowAlpha.ProposedPath);
        Assert.Equal("Creator/Foo (2)", rowZeta.ProposedPath);
    }

    [Fact]
    public void Disambiguate_NonCollidingGroups_AreLeftUntouched()
    {
        var apple = MakeRow("a", "Apple", "Creator/Apple");
        var banana = MakeRow("b", "Banana", "Creator/Banana");

        CollisionDisambiguator.Disambiguate([apple, banana]);

        Assert.Equal("Creator/Apple", apple.ProposedPath);
        Assert.Equal("Creator/Banana", banana.ProposedPath);
    }

    [Fact]
    public void Disambiguate_ExistingSuffixAlreadyTaken_SkipsToNextFreeSuffix()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var unrelated = MakeRow("c", "Foo (2)", "Creator/Foo (2)");

        CollisionDisambiguator.Disambiguate([canonical, duplicate, unrelated]);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", unrelated.ProposedPath);
        Assert.Equal("Creator/Foo (3)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_ExistingSuffixCaseInsensitive_StillSkipped()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var unrelated = MakeRow("c", "FOO (2)", "Creator/FOO (2)");

        CollisionDisambiguator.Disambiguate([canonical, duplicate, unrelated]);

        Assert.Equal("Creator/Foo (3)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_MultipleOccupiedSuffixes_SkipsAllOfThem()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var dupA = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var dupB = MakeRow("Foo_3", "Foo", "Creator/Foo");
        var occupiedTwo = MakeRow("c", "Foo (2)", "Creator/Foo (2)");
        var occupiedThree = MakeRow("d", "Foo (3)", "Creator/Foo (3)");

        CollisionDisambiguator.Disambiguate([canonical, dupA, dupB, occupiedTwo, occupiedThree]);

        var dupPaths = new[] { dupA.ProposedPath, dupB.ProposedPath };
        Assert.Contains("Creator/Foo (4)", dupPaths);
        Assert.Contains("Creator/Foo (5)", dupPaths);
    }

    [Fact]
    public void Disambiguate_CrossGroupCollision_ReservesAcrossEntireInput()
    {
        // The scenario raised in review: a naive per-group suffix would collide with
        // an independently-named mod's own bare path (also named "Foo (2)").
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var independentlyNamed = MakeRow("c", "Foo (2)", "Creator/Foo (2)");

        CollisionDisambiguator.Disambiguate([canonical, duplicate, independentlyNamed]);

        Assert.Equal("Creator/Foo (2)", independentlyNamed.ProposedPath);
        Assert.Equal("Creator/Foo (3)", duplicate.ProposedPath);
        Assert.NotEqual(independentlyNamed.ProposedPath, duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_CalledTwice_IsIdempotent()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var rows = new[] { canonical, duplicate };

        CollisionDisambiguator.Disambiguate(rows);
        CollisionDisambiguator.Disambiguate(rows);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_LazyEnumerableInput_EnumeratedExactlyOnce()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var guarded = new SingleEnumerationGuard([canonical, duplicate]);

        CollisionDisambiguator.Disambiguate(guarded);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_DuplicateIdentifiers_TerminatesWithUniquePaths()
    {
        // Invalid state - Penumbra guarantees Identifier uniqueness at install time.
        // The design only commits to termination and uniqueness here, not to which
        // row wins the canonical slot.
        var rowA = MakeRow("Foo", "Foo", "Creator/Foo");
        var rowB = MakeRow("Foo", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([rowA, rowB]);

        Assert.NotEqual(rowA.ProposedPath, rowB.ProposedPath);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~CollisionDisambiguatorTests"`
Expected: compilation failure — `CollisionDisambiguator` does not exist.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/CollisionDisambiguator.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Renumbers ProposedPath collisions produced by the automatic sort strategies
/// (SortByCreator, SortByModType) when Penumbra duplicate installs share a display
/// Name. Never touches AssignManual's collisions - those are real user mistakes and
/// stay visible through OrganizerState.Validate() unchanged.
/// </summary>
public static class CollisionDisambiguator
{
    public static void Disambiguate(IEnumerable<OrganizerModRow> rows)
    {
        var materialized = rows.ToList();

        var reserved = new HashSet<string>(
            materialized.Select(r => r.ProposedPath),
            StringComparer.OrdinalIgnoreCase);

        var groups = materialized
            .GroupBy(r => r.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(r => r.Identifier, StringComparer.Ordinal).ToList();
            var exactMatches = ordered
                .Where(r => string.Equals(r.Identifier, r.Name, StringComparison.Ordinal))
                .ToList();
            var canonical = exactMatches.Count == 1 ? exactMatches[0] : ordered[0];
            var basePath = canonical.ProposedPath;
            var suffix = 2;

            foreach (var row in ordered.Where(r => !ReferenceEquals(r, canonical)))
            {
                string candidate;
                do { candidate = $"{basePath} ({suffix++})"; }
                while (!reserved.Add(candidate));
                row.ProposedPath = candidate;
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~CollisionDisambiguatorTests"`
Expected: all 11 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/CollisionDisambiguator.cs PenumbraOrganizer.Plugin.Tests/Organizer/CollisionDisambiguatorTests.cs
git commit -m "feat(1d): add CollisionDisambiguator"
```

---

### Task 2: Wire into `SortByCreator`/`SortByModType`, update docs

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs:47-71`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`
- Modify: `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: `CollisionDisambiguator.Disambiguate(IEnumerable<OrganizerModRow>)` from Task 1.
- Produces: `SortByCreator`/`SortByModType` now call `Disambiguate` on the rows they touched, before returning. No signature change to either method.

- [ ] **Step 1: Write the failing tests**

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs` (inside the `OrganizerStateTests` class, e.g. after `SortByModType_SkipsProtectedMods`):

```csharp
    [Fact]
    public void SortByCreator_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("Foo", "Foo"), MakeRow("Foo_2", "Foo")],
            new HashSet<string>());

        state.SortByCreator(name => name);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByCreator_CalledTwice_ProducesIdenticalPaths()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("Foo", "Foo"), MakeRow("Foo_2", "Foo")],
            new HashSet<string>());

        state.SortByCreator(name => name);
        var firstRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        state.SortByCreator(name => name);
        var secondRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        Assert.Equal(firstRun, secondRun);
    }

    [Fact]
    public void SortByModType_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.SortByModType();

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByModType_CalledTwice_ProducesIdenticalPaths()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.SortByModType();
        var firstRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        state.SortByModType();
        var secondRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        Assert.Equal(firstRun, secondRun);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: the four new tests FAIL (`SortByCreator_DuplicateInstallsWithSameName_AreDisambiguated` and its ModType counterpart fail because `Validate().HasIssues` is `true` — the collision isn't resolved yet; the two idempotency tests may incidentally pass already since the *bug* produces identical, if colliding, output on both runs — that's fine, they'll stay green and are still worth having once Step 3 below changes the code path). All pre-existing tests still pass.

- [ ] **Step 3: Wire `CollisionDisambiguator` into both sort strategies**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, replace `SortByCreator` and `SortByModType`:

```csharp
    public int SortByCreator(Func<string, string> canonicalizeCreator)
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var folder = canonicalizeCreator(row.Author);
            row.ProposedPath = string.IsNullOrEmpty(folder) ? row.Name : $"{folder}/{row.Name}";
            touched.Add(row);
            count++;
        }

        CollisionDisambiguator.Disambiguate(touched);
        return count;
    }

    public int SortByModType()
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected && m.Category is not null))
        {
            var folder = ModTypeFolders.GetFolder(row.Category!.Value, row.SubCategory);
            row.ProposedPath = $"{folder}/{row.Name}";
            touched.Add(row);
            count++;
        }

        CollisionDisambiguator.Disambiguate(touched);
        return count;
    }
```

`CollisionDisambiguator` lives in the same `PenumbraOrganizer.Plugin.Organizer` namespace as `OrganizerState`, so no new `using` is needed.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed! - Failed: 0, Passed: 97` (82 pre-existing + 11 from Task 1 + 4 from this task's Step 1 — recount actual total from the test runner's summary line rather than trusting this arithmetic if the tools disagree, and treat the runner's number as ground truth).

- [ ] **Step 5: Update the handoff doc**

In `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`, `## Known limitations, not fixed here`, remove the "By Creator sort can collide" bullet entirely (it's fixed, not a known limitation anymore) and update the test count. The section currently reads:

```markdown
## Known limitations, not fixed here

- **By Creator sort can collide.** Mods that share a display name but differ only by Penumbra's own
  numeric suffix (duplicate installs) collapse onto the same proposed path. `Validate()` catches it
  correctly; the sort logic itself has no dedup strategy. Needs a design decision before fixing.
- **Phase 1c (by mod type) is implemented.** Scan classifies every mod from Penumbra's
```

Replace the first bullet and the test-count line above the section with:

```markdown
82 tests pass (`dotnet test PenumbraOrganizer.Plugin.Tests`), build is clean.
```
→
```markdown
97 tests pass (`dotnet test PenumbraOrganizer.Plugin.Tests`), build is clean.
```

and

```markdown
## Known limitations, not fixed here

- **By Creator sort can collide.** Mods that share a display name but differ only by Penumbra's own
  numeric suffix (duplicate installs) collapse onto the same proposed path. `Validate()` catches it
  correctly; the sort logic itself has no dedup strategy. Needs a design decision before fixing.
```
→
```markdown
## Known limitations, not fixed here

- **`CollisionDisambiguator` only covers the rows a sort call touches.** A collision against a
  protected row's fixed `CurrentPath`, or against an `Unknown`-category row `SortByModType` left
  untouched, isn't auto-resolved — `Validate()` still catches it, same as before Phase 1d. Deliberate
  scope boundary, not a bug (spec: `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1d-collision-disambiguation-design.md`, Open Risks #3).
```

(Use the actual test-runner total from Step 4 if it differs from 97.)

- [ ] **Step 6: Update the roadmap**

In `docs/ROADMAP.md`, replace the `## Phase 1d (parking lot) — fix the By Creator collision bug` section:

```markdown
## Phase 1d (parking lot) — fix the By Creator collision bug

**Status: known bug, no design yet.** From `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`: mods that
share a display name but differ only by Penumbra's own numeric duplicate-install suffix collapse
onto the same `ProposedPath` under By Creator sort. `OrganizerState.Validate()` correctly flags the
resulting collision, but the sort itself has no dedup strategy, so today's fix is "sort, notice the
validation error, sort manually instead."

Needs a design decision before implementation: append the numeric suffix to the proposed path?
Append author-relative index? Skip the mod like Phase 1c skips Unknown? Small enough to fold into
whatever session picks it up — brainstorm the tie-break rule, then a short plan (2-3 tasks) mirroring
`SortByModType`'s shape.
```

with:

```markdown
## Phase 1d — done

Shipped: `CollisionDisambiguator` (`PenumbraOrganizer.Plugin/Organizer/CollisionDisambiguator.cs`)
renumbers `SortByCreator`/`SortByModType` path collisions between Penumbra duplicate installs sharing
a display name. Design: `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1d-collision-disambiguation-design.md`.
Plan: `docs/superpowers/plans/2026-07-14-plugin-organizer-phase1d-collision-disambiguation.md`.
Deliberately doesn't extend to protected/`Unknown`-row collisions — see the handoff doc's Known
limitations.
```

Also update the `## Where we are` status line at the top of the file — after the Phase 1c bullet, add:

```markdown
- **Phase 1d (collision disambiguation) — shipped.** `SortByCreator`/`SortByModType` no longer
  produce colliding paths for Penumbra duplicate installs sharing a display name.
```

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md docs/ROADMAP.md
git commit -m "feat(1d): disambiguate SortByCreator/SortByModType collisions"
```

---

## Self-review notes

- **Spec coverage:** algorithm (reserve-then-allocate, canonical selection, ordinal comparisons) — Task 1. Wiring into both sort strategies, scope boundary preserved (only touched rows), idempotency at both levels — Task 2. Non-goals (no `AssignManual` change, no new folder taxonomy, no content-guessing) are honored by construction — nothing in either task touches `AssignManual`, introduces a new path segment beyond the existing `(N)` suffix, or inspects mod content.
- **Test coverage cross-check against spec's Testing section:** two-way/three-way/no-exact-match/non-colliding/existing-suffix/case-insensitive/multiple-occupied/cross-group/idempotent-direct/lazy-enumerable/invalid-state — all in Task 1. Idempotent-sort-level and both integration tests — Task 2. All 13 items from the spec's Testing list are covered.
- **Type consistency check:** `CollisionDisambiguator.Disambiguate(IEnumerable<OrganizerModRow> rows)` — same signature in Task 1's implementation and Task 2's call sites. `OrganizerModRow.Identifier`/`.Name`/`.ProposedPath` — same properties used throughout, matching the existing class (no modification to `OrganizerModRow` needed in this plan).
- **No placeholders:** every step has complete, runnable code; doc-update steps show exact before/after text rather than describing the change.
