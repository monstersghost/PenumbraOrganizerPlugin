# Phase 1e: Combined Sort Strategies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `SortByTypeThenCreator` and `SortByCreatorThenType` to `OrganizerState`, and unify all
four sort strategies' handling of unknown creator/type under one consistent `Review/{Name}` fallback
rule, per the approved spec
`docs/superpowers/specs/2026-07-14-plugin-organizer-phase1e-combined-sort-strategies-design.md`.

**Architecture:** Two small private static helpers on `OrganizerState` — `KnownFolder` (normalizes
null/empty/whitespace to `null`) and `BuildPath` (composes a path from up to two optional folder
values, falling back to `Review/{Name}` when neither is known). All four `SortBy*` methods call
`BuildPath` with different argument order/slots; the two new methods are otherwise a straight copy of
the existing `SortByCreator`/`SortByModType` shape (protected-row filter, `CollisionDisambiguator` at
the end).

**Tech Stack:** C# / .NET 10, `Dalamud.NET.Sdk/15.0.0`, xunit (existing test project
`PenumbraOrganizer.Plugin.Tests`).

## Global Constraints

- No change to `AssignManual`/manual sort or to `CollisionDisambiguator` itself — both are reused
  unmodified.
- No new folder taxonomy beyond the single literal `Review` fallback — no per-strategy variants of it.
- No content-based guessing, no keyword heuristics on item/mod names — this plan hard-codes exactly
  one fallback rule per the spec, no configuration surface.
- `PreserveAndClean` and detailed gear-slot (Head/Top/Hand/Legs/Feet) sorting are explicitly out of
  scope for this plan — tracked separately in `docs/ROADMAP.md`.
- `SortByModType`'s `Category is not null` row-filter is removed — every unprotected row now receives
  a `ProposedPath` from every strategy, at minimum `Review/{Name}`.
- `KnownFolder`/`BuildPath` are private static helpers on `OrganizerState`, not a separate class.
- No write IPC of any kind — this phase remains read-only; Apply stays disabled.
- Build must stay at 0 warnings / 0 errors; all existing 97 tests must keep passing (this plan adds
  new tests and replaces one existing test — see each task's test count; treat the actual
  `dotnet test` summary line as ground truth over any arithmetic in this plan if they ever disagree).
- Run all commands from the repo root `C:\Repo\PenumbraOrganizer.Plugin`.

---

### Task 1: `KnownFolder`/`BuildPath` helpers, refactor `SortByCreator`/`SortByModType`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs:47-77`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Consumes: nothing new — `ModTypeFolders.GetFolder` (existing, from Phase 1c) and
  `CollisionDisambiguator.Disambiguate` (existing, from Phase 1d) are reused unmodified.
- Produces: `private static string? OrganizerState.KnownFolder(string? folder)` and
  `private static string OrganizerState.BuildPath(string? primaryFolder, string? secondaryFolder, string name)`
  — both consumed by Task 2's two new methods. Public signatures of `SortByCreator`/`SortByModType` are
  unchanged (same parameters, same return type); only their internal behavior for unknown
  creator/type changes.

- [ ] **Step 1: Write the failing tests**

In `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`, replace the existing
`SortByModType_SkipsUnknownCategory` test (currently at lines 235-245) with:

```csharp
    [Fact]
    public void SortByModType_UnknownCategory_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByModType();

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }
```

Then add a new test directly after it (still inside the `OrganizerStateTests` class):

```csharp
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SortByCreator_UnknownOrWhitespaceCreator_GoesToReviewFolder(string canonicalized)
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var count = state.SortByCreator(_ => canonicalized);

        Assert.Equal(1, count);
        Assert.Equal("Review/Apple", state.Mods.Single().ProposedPath);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: `SortByModType_UnknownCategory_GoesToReviewFolder` FAILS (today's code still skips the row,
leaving it at `Unsorted/Mystery Mod`, count `0`). Both `SortByCreator_UnknownOrWhitespaceCreator_GoesToReviewFolder`
cases FAIL (today's code produces bare `Apple`, not `Review/Apple`). All other existing tests still
pass.

- [ ] **Step 3: Add the helpers and refactor both methods**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, replace `SortByCreator` and `SortByModType`
(currently lines 47-77) with:

```csharp
    public int SortByCreator(Func<string, string> canonicalizeCreator)
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var creatorFolder = KnownFolder(canonicalizeCreator(row.Author));
            row.ProposedPath = BuildPath(creatorFolder, null, row.Name);
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
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var typeFolder = row.Category is null
                ? null
                : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, row.SubCategory));
            row.ProposedPath = BuildPath(typeFolder, null, row.Name);
            touched.Add(row);
            count++;
        }

        CollisionDisambiguator.Disambiguate(touched);
        return count;
    }

    private static string? KnownFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : folder;

    private static string BuildPath(string? primaryFolder, string? secondaryFolder, string name)
    {
        if (primaryFolder is not null && secondaryFolder is not null)
            return $"{primaryFolder}/{secondaryFolder}/{name}";
        if (primaryFolder is not null)
            return $"{primaryFolder}/{name}";
        if (secondaryFolder is not null)
            return $"{secondaryFolder}/{name}";
        return $"Review/{name}";
    }
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`; `Passed! - Failed: 0, Passed: 99` (97 pre-existing,
minus the one rewritten test's old assertion no longer applying — same test count since it's a
rewrite, not a removal — plus 2 new cases from the `Theory`: 97 + 2 = 99).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat(1e): unify unknown-creator/unknown-type fallback under Review folder"
```

---

### Task 2: `SortByTypeThenCreator` and `SortByCreatorThenType`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Consumes: `KnownFolder`/`BuildPath` from Task 1; `ModTypeFolders.GetFolder`,
  `CollisionDisambiguator.Disambiguate` (existing).
- Produces: `public int SortByTypeThenCreator(Func<string, string> canonicalizeCreator)` and
  `public int SortByCreatorThenType(Func<string, string> canonicalizeCreator)` — consumed by Task 3's
  UI wiring.

- [ ] **Step 1: Write the failing tests**

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`, inside the
`OrganizerStateTests` class (e.g. after `SortByCreator_UnknownOrWhitespaceCreator_GoesToReviewFolder`
from Task 1):

```csharp
    [Fact]
    public void SortByTypeThenCreator_BothKnown_BuildsTypeSlashCreatorPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("Gear/SOMEAUTHOR/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_OnlyTypeKnown_UsesTypeAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_OnlyCreatorKnown_UsesCreatorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_NeitherKnown_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.SortByTypeThenCreator(name => name.ToUpperInvariant());

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Guarded Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.SortByTypeThenCreator(name => name);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByCreatorThenType_BothKnown_BuildsCreatorSlashTypePath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByCreatorThenType(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_OnlyCreatorKnown_UsesCreatorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByCreatorThenType(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_OnlyTypeKnown_UsesTypeAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByCreatorThenType(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_NeitherKnown_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByCreatorThenType(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.SortByCreatorThenType(name => name.ToUpperInvariant());

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Guarded Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.SortByCreatorThenType(name => name);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByTypeThenCreatorAndSortByCreatorThenType_DifferOnlyInOrder()
    {
        var typeThenCreatorState = new OrganizerState();
        typeThenCreatorState.LoadScan(
            [MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());
        typeThenCreatorState.SortByTypeThenCreator(name => name.ToUpperInvariant());

        var creatorThenTypeState = new OrganizerState();
        creatorThenTypeState.LoadScan(
            [MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());
        creatorThenTypeState.SortByCreatorThenType(name => name.ToUpperInvariant());

        Assert.Equal("Gear/SOMEAUTHOR/Cool Jacket", typeThenCreatorState.Mods.Single().ProposedPath);
        Assert.Equal("SOMEAUTHOR/Gear/Cool Jacket", creatorThenTypeState.Mods.Single().ProposedPath);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: compilation failure — `SortByTypeThenCreator`/`SortByCreatorThenType` do not exist yet.

- [ ] **Step 3: Add the two new methods**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, add below `SortByModType` (and above the
`KnownFolder`/`BuildPath` helpers added in Task 1):

```csharp
    public int SortByTypeThenCreator(Func<string, string> canonicalizeCreator)
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var typeFolder = row.Category is null
                ? null
                : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, row.SubCategory));
            var creatorFolder = KnownFolder(canonicalizeCreator(row.Author));
            row.ProposedPath = BuildPath(typeFolder, creatorFolder, row.Name);
            touched.Add(row);
            count++;
        }

        CollisionDisambiguator.Disambiguate(touched);
        return count;
    }

    public int SortByCreatorThenType(Func<string, string> canonicalizeCreator)
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var typeFolder = row.Category is null
                ? null
                : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, row.SubCategory));
            var creatorFolder = KnownFolder(canonicalizeCreator(row.Author));
            row.ProposedPath = BuildPath(creatorFolder, typeFolder, row.Name);
            touched.Add(row);
            count++;
        }

        CollisionDisambiguator.Disambiguate(touched);
        return count;
    }
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`; `Passed! - Failed: 0, Passed: 112` (99 from Task 1
+ 13 new: 6 `SortByTypeThenCreator*` + 6 `SortByCreatorThenType*` + 1 order-differs test).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat(1e): add SortByTypeThenCreator and SortByCreatorThenType"
```

---

### Task 3: Sort-tab buttons, update docs

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:117-130` (`DrawSortTab`)
- Modify: `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: `OrganizerState.SortByTypeThenCreator`/`SortByCreatorThenType` from Task 2;
  `_creatorCanonicalizer.Canonicalize` (existing field already used by the `By Creator` button).
- Produces: two new Sort-tab buttons. This task has no new automated test — UI wiring is a one-line
  call per button, exercised the same way `By Creator`/`By Mod Type` already are; no new IPC surface,
  so no in-game verification step is required here either (unlike Phase 1c's `RunScan` change, this
  plan touches no IPC call).

- [ ] **Step 1: Add the two new buttons**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, `DrawSortTab()`, replace:

```csharp
        if (ImGui.Button("By Creator"))
            _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.SameLine();
        if (ImGui.Button("By Mod Type"))
            _plugin.OrganizerState.SortByModType();
```

with:

```csharp
        if (ImGui.Button("By Creator"))
            _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.SameLine();
        if (ImGui.Button("By Mod Type"))
            _plugin.OrganizerState.SortByModType();

        ImGui.SameLine();
        if (ImGui.Button("By Type Then Creator"))
            _plugin.OrganizerState.SortByTypeThenCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.SameLine();
        if (ImGui.Button("By Creator Then Type"))
            _plugin.OrganizerState.SortByCreatorThenType(_creatorCanonicalizer.Canonicalize);
```

- [ ] **Step 2: Build and run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `0 Warning(s) 0 Error(s)`; all 112 tests still PASS (this step adds no new tests, just
confirms the UI change compiles cleanly against the same `OrganizerState` API Task 2 produced).

- [ ] **Step 3: Update the handoff doc**

In `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`, the `## Known limitations, not fixed here` section
currently (lines 19-29) reads:

```markdown
## Known limitations, not fixed here

- **`CollisionDisambiguator` only covers the rows a sort call touches.** A collision against a
  protected row's fixed `CurrentPath`, or against an `Unknown`-category row `SortByModType` left
  untouched, isn't auto-resolved — `Validate()` still catches it, same as before Phase 1d. Deliberate
  scope boundary, not a bug (spec: `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1d-collision-disambiguation-design.md`, Open Risks #3).
- **Phase 1c (by mod type) is implemented.** Scan classifies every mod from Penumbra's
  changed-items IPC per
  `docs/superpowers/specs/2026-07-13-plugin-organizer-phase1c-classification-design.md`; the Sort
  tab has a "By Mod Type" button. Unknown-category mods are left in place for manual sorting by
  design. The temporary SPIKE dump button has been removed.
```

Replace it with (this fixes two now-stale claims — `SortByModType` no longer leaves Unknown-category
rows untouched, so the `CollisionDisambiguator` bullet's parenthetical about it and the Phase 1c
bullet's "left in place for manual sorting" claim are both wrong after this plan — and adds the new
Phase 1e bullet):

```markdown
## Known limitations, not fixed here

- **`CollisionDisambiguator` only covers the rows a sort call touches.** A collision against a
  protected row's fixed `CurrentPath` isn't auto-resolved — `Validate()` still catches it, same as
  before Phase 1d. Deliberate scope boundary, not a bug (spec: `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1d-collision-disambiguation-design.md`, Open Risks #3).
- **Phase 1c (by mod type) is implemented.** Scan classifies every mod from Penumbra's
  changed-items IPC per
  `docs/superpowers/specs/2026-07-13-plugin-organizer-phase1c-classification-design.md`; the Sort
  tab has a "By Mod Type" button. The temporary SPIKE dump button has been removed.
- **Phase 1e (combined sort strategies) is implemented.** `SortByTypeThenCreator`/
  `SortByCreatorThenType` add `Type/Creator` and `Creator/Type` sort buttons. All four sort
  strategies now share one fallback rule: a mod with an unresolvable creator and/or type goes to
  `Review/{Name}` instead of being silently skipped or dropped bare at Penumbra's root — this changes
  `SortByCreator`'s and `SortByModType`'s previously-shipped unknown-creator/unknown-type behavior.
  `PreserveAndClean` and detailed gear-slot (Head/Top/Hand/Legs/Feet) sorting remain out of scope —
  see `docs/ROADMAP.md`. Design:
  `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1e-combined-sort-strategies-design.md`.
```

Also update the test-count line above that section — it currently reads:

```markdown
97 tests pass (`dotnet test PenumbraOrganizer.Plugin.Tests`), build is clean.
```

Change `97` to the actual total confirmed by Step 2's `dotnet test` run (expected `112`, per this
plan's arithmetic — use the real number if it differs).

- [ ] **Step 4: Update the roadmap**

In `docs/ROADMAP.md`, the `## Where we are` section currently (lines 8-21) ends with:

```markdown
- **Phase 1d (collision disambiguation) — shipped.** `SortByCreator`/`SortByModType` no longer
  produce colliding paths for Penumbra duplicate installs sharing a display name.
```

Add immediately after it (still inside the same bullet list):

```markdown
- **Phase 1e (combined sort strategies) — shipped.** Adds `SortByTypeThenCreator`/
  `SortByCreatorThenType`; unifies all four sort strategies' unknown-creator/unknown-type fallback
  under one `Review/{Name}` rule.
```

Then, still in `docs/ROADMAP.md`, the `## Phase 1d — done` section currently (lines 32-39) is
immediately followed by `## Phase 2 — Apply ...`. Insert a new section between them:

```markdown
## Phase 1e — done

Shipped: `SortByTypeThenCreator`/`SortByCreatorThenType`
(`PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`) plus two new Sort-tab buttons. All four sort
strategies now share `OrganizerState.BuildPath`'s fallback rule — unresolvable creator/type goes to
`Review/{Name}` instead of being skipped or dropped bare at root. Design:
`docs/superpowers/specs/2026-07-14-plugin-organizer-phase1e-combined-sort-strategies-design.md`. Plan:
`docs/superpowers/plans/2026-07-14-plugin-organizer-phase1e-combined-sort-strategies.md`.
`PreserveAndClean` and detailed gear-slot sorting remain deferred — see their own sections below.
```

(Leave the existing `## Detailed gear-slot sorting (parking lot)` section untouched — it's a separate,
still-deferred item this plan doesn't ship.)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md docs/ROADMAP.md
git commit -m "feat(1e): add Sort-tab buttons for the two combined strategies"
```

---

## Self-review notes

- **Spec coverage:** `KnownFolder`/`BuildPath` and the fallback table (both/one/neither known, per
  strategy) — Task 1 (existing two strategies) + Task 2 (two new strategies). Protected-row invariant
  — every new/changed test that checks skip-protected behavior, unchanged from existing precedent.
  Collision-disambiguation-timing requirement (compose first, disambiguate once at the end) — Task 2's
  duplicate-install tests, and the code itself keeps the same `touched` list + single
  `CollisionDisambiguator.Disambiguate(touched)` call pattern Task 1 preserves from Phase 1d. UI — Task
  3. Docs — Task 3. Non-goals (`PreserveAndClean`, detailed gear-slot, no config surface) are honored
  by construction — nothing in any task adds them.
- **Type consistency check:** `KnownFolder(string? folder) : string?` and
  `BuildPath(string? primaryFolder, string? secondaryFolder, string name) : string` — same signatures
  in Task 1's implementation and Task 2's two call sites. `SortByTypeThenCreator`/
  `SortByCreatorThenType` both take `Func<string, string> canonicalizeCreator` and return `int`,
  matching `SortByCreator`'s existing shape exactly, as the spec requires.
- **No placeholders:** every step has complete, runnable code; doc-update steps show exact before/after
  text.
