# Plugin organizer, Phase 1e: combined sort strategies — Design

**Status:** approved, not yet implemented.

## Context

The standalone app (`docs/PROJECT_CONTEXT.md` in `C:\Repo\PenumbraOrganizer`) defines six organization
strategies: `CreatorOnly`, `TypeOnly`, `TypeThenCreator`, `CreatorThenType`, `PreserveAndClean`,
`Custom`. The plugin currently has three: Manual (≈ `Custom`), `SortByCreator` (≈ `CreatorOnly`),
`SortByModType` (≈ `TypeOnly`). This spec adds the two missing combined strategies,
`SortByTypeThenCreator` and `SortByCreatorThenType`.

`PreserveAndClean` (orphaned/empty-folder cleanup after a sort) is explicitly out of scope — raised
and deferred during brainstorming as its own, separate design problem, not a quick follow-on.

## Goal

Add `SortByTypeThenCreator` (`{Type}/{Creator}/{Name}`) and `SortByCreatorThenType`
(`{Creator}/{Type}/{Name}`) to `OrganizerState`, each with its own one-click Sort-tab button, matching
the existing `SortByCreator`/`SortByModType` pattern exactly (same signature shape, same
`CollisionDisambiguator` integration, same protected-row exclusion).

Along the way, unify how all four sort strategies handle missing information (unknown creator and/or
unknown mod-type category) under one consistent rule, replacing two asymmetric behaviors that existed
only because they were never designed together:

- `SortByCreator` today drops an unknown-creator mod bare at Penumbra's root (no folder at all).
- `SortByModType` today skips an unknown-category mod entirely, leaving it at `CurrentPath`.

## Non-goals

- `PreserveAndClean` — separate design problem (see Context).
- Workbook import/export — tracked separately in `docs/ROADMAP.md` Phase 3, gated behind Apply
  shipping.
- Any change to `AssignManual`/manual sort, or to `CollisionDisambiguator` itself — both are reused
  unmodified.
- Any write IPC — this phase remains read-only; Apply stays disabled.
- A user-configurable "unknown creator/type behavior" setting, unlike the standalone app's
  `unknownCreatorBehavior`/`unknownTypeBehavior` preferences. The plugin has no settings UI and this
  spec picks one fixed default (see Algorithm) rather than adding configuration surface for it.

## Algorithm

**Folder resolution, per row, per strategy call:**

```csharp
private static string? KnownFolder(string? folder) =>
    string.IsNullOrWhiteSpace(folder) ? null : folder;
```

`KnownFolder` normalizes `null`, empty, and whitespace-only strings all down to `null` — without it, an
empty (but non-null) creator or type string would produce a malformed path like `/Mod Name` (leading
slash, no folder segment). Applied at the point each folder value is computed:

```csharp
var creatorFolder = KnownFolder(canonicalizeCreator(row.Author));
var typeFolder = row.Category is null
    ? null
    : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, row.SubCategory));
```

**Path composition**, shared by all four strategies:

```csharp
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

`primaryFolder`/`secondaryFolder` are generic slots, not literally "type"/"creator" — each strategy
decides which real value goes in which slot, and in what order, by how it calls `BuildPath`:

| Strategy | Call | Both known | One known | Neither known |
|---|---|---|---|---|
| `SortByCreator` | `BuildPath(creatorFolder, null, row.Name)` | n/a (only one dimension) | `{Creator}/{Name}` | `Review/{Name}` |
| `SortByModType` | `BuildPath(typeFolder, null, row.Name)` | n/a | `{Type}/{Name}` | `Review/{Name}` |
| `SortByTypeThenCreator` | `BuildPath(typeFolder, creatorFolder, row.Name)` | `{Type}/{Creator}/{Name}` | whichever is known, alone | `Review/{Name}` |
| `SortByCreatorThenType` | `BuildPath(creatorFolder, typeFolder, row.Name)` | `{Creator}/{Type}/{Name}` | whichever is known, alone | `Review/{Name}` |

The two combined strategies differ **only** in which folder is `primaryFolder` when both are known
(hierarchy order) — every other branch (one known, neither known) is identical between them, since
`BuildPath` doesn't care which real-world dimension occupies which slot.

**Public method signatures** are unchanged in shape from the existing two — no new parameters, no
change to what callers pass in:

```csharp
public int SortByCreator(Func<string, string> canonicalizeCreator)
public int SortByModType()
public int SortByTypeThenCreator(Func<string, string> canonicalizeCreator)
public int SortByCreatorThenType(Func<string, string> canonicalizeCreator)
```

`BuildPath` and `KnownFolder` are private static helpers on `OrganizerState` — not extracted into a
separate class like `CollisionDisambiguator`. They're small (a handful of lines each), exercised
indirectly through the four public `SortBy*` methods' own tests, and have no reuse need outside
`OrganizerState`; a dedicated pure-testable class isn't warranted the way `CollisionDisambiguator`'s
cross-cutting collision algorithm was.

## Protected-row invariant

Unchanged from today: every `SortBy*` method filters to `_mods.Values.Where(m => !m.Protected)` before
computing anything. Protected rows never reach `BuildPath` and never have their `ProposedPath` touched
by any of the four strategies — this spec doesn't add, remove, or alter that filter, only what happens
to the *unprotected* rows that pass it. `SortByModType`'s current additional filter,
`m.Category is not null` (today's skip-Unknown mechanism), is removed — every unprotected row now
receives a `ProposedPath` from `BuildPath`, at minimum `Review/{Name}`.

## Data flow

Each `SortBy*` method: filter to unprotected rows, compute each row's folder value(s) via
`KnownFolder`, call `BuildPath` to set `ProposedPath`, collect the touched rows into a list — then call
`CollisionDisambiguator.Disambiguate(touched)` once, after every row's tentative path has been set, over
the complete result set for that call. This is the same order `SortByCreator`/`SortByModType` already
use today (path composition first, disambiguation once at the end) — Phase 1e doesn't change that
sequencing, just what `BuildPath` computes before disambiguation runs. This guarantees two mods that
both land on `Review/{Name}` (or any other coincidental collision `BuildPath` produces) get
disambiguated identically to any other collision — `CollisionDisambiguator` has no awareness of *why*
two rows share a path, only that they do.

## UI

Two new buttons on the Sort tab in `MainWindow.cs`'s `DrawSortTab()`, `By Type Then Creator` and
`By Creator Then Type`, matching the existing `By Creator`/`By Mod Type` one-click pattern exactly. Both
wire to the same `_creatorCanonicalizer.Canonicalize` already used by the `By Creator` button.

## Testing

Mirrors the existing `OrganizerStateTests.cs` style (`MakeRow`/`MakeCategorizedRow` helpers, one
`[Fact]` per behavior). Required cases:

| Strategy | Required cases |
|---|---|
| `SortByCreator` | creator known → `{Creator}/{Name}`; creator unknown → `Review/{Name}` |
| `SortByModType` | type known → `{Type}/{Name}`; type unknown → `Review/{Name}` (**replaces** the existing `SortByModType_SkipsUnknownCategory` test, which currently asserts skip/leave-in-place — that assertion becomes false under this spec and must be rewritten, not just extended) |
| `SortByTypeThenCreator` | both known; type only; creator only; neither known |
| `SortByCreatorThenType` | both known; creator only; type only; neither known |

Plus, across the combined strategies specifically: at least one assertion proving the *order* differs
when both are known — e.g. the same row's known `Type`/`Creator` pair produces `Type/Creator/Name`
under `SortByTypeThenCreator` and `Creator/Type/Name` under `SortByCreatorThenType`.

Unrelated-to-fallback behavior that must still hold and gets its own tests: protected rows are excluded
from all four strategies (mirrors existing `SortByCreator_SkipsProtectedMods`/
`SortByModType_SkipsProtectedMods`); a collision between two `Review/{Name}`-bound rows (or any other
coincidental collision) gets disambiguated via the existing, unmodified `CollisionDisambiguator`, same
as any other collision — one integration test per new strategy is enough here, mirroring the two
`SortBy*_DuplicateInstallsWithSameName_AreDisambiguated` tests from Phase 1d.

## Open risks

1. **`SortByModType`'s behavior change is user-visible and not backward compatible.** A mod previously
   left untouched by `By Mod Type` (Unknown category) will now move to `Review/{Name}` the next time
   that button is clicked. This is a deliberate, explicitly-confirmed decision (see brainstorming), not
   an oversight — flagged here only so it's visible in the handoff doc update alongside the shipped
   change, matching how Phase 1d documented its own scope boundary.
2. **The standalone app's richer model isn't ported.** The app supports per-strategy configurable
   unknown-handling (`unknownCreatorBehavior`, `unknownTypeBehavior`: place under known dimension,
   preserve current folder, or send to Review) as user preferences. This spec hard-codes one fixed
   choice (send to `Review`, or fall back to whichever single dimension is known) rather than exposing
   configuration, consistent with the plugin's no-settings-UI design so far. Revisit only if real usage
   shows the fixed choice doesn't fit — not preemptively.
