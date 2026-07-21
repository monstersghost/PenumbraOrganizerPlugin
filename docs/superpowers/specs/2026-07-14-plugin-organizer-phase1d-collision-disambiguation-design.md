# Plugin organizer, Phase 1d: By Creator/By Mod Type collision disambiguation — Design

**Status:** approved, not yet implemented.

## Context

`docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md` and `docs/ROADMAP.md` both flag a known bug: mods that
share a display `Name` but differ only by Penumbra's own duplicate-install numbering collapse onto the
same `ProposedPath` under both `SortByCreator` and `SortByModType`
(`PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`). Both strategies build
`ProposedPath = "{folder}/{row.Name}"`, and `Name` (the mod's display name from its metadata) is
identical across duplicate installs of the same mod — only `Identifier` (Penumbra's own mod directory
name) differs. `Validate()` correctly flags the resulting collision, but neither sort strategy has any
dedup logic, so today's workaround is "sort, notice the validation error, fix manually."

This is scoped to plain manually-installed duplicates. Heliosphere-managed mods are always
auto-protected (`OrganizerState.LoadScan`), so they're excluded from both sort strategies'
`!m.Protected` filter and never reach this code path.

## Goal

Whenever `SortByCreator` or `SortByModType` would otherwise assign the same `ProposedPath` to more than
one mod **within that same sort call's touched-row set**, disambiguate automatically so none of the
generated paths collide with each other *or* with any other tentative path already present in that same
set — not just within the original collision group, since a generated suffix could otherwise land on an
unrelated, independently-named mod's path (see Algorithm). This is a neutral renumbering, without
asserting anything about *why* the mods share a name (they're usually accidental duplicate installs of
identical content, but occasionally two genuinely different mods that happen to share a display name —
see Open Risks).

**Explicit scope boundary:** this guarantees no collision among the rows the sort strategy actually
touches (unprotected, and for `SortByModType`, classified). It does not guarantee zero
`Validate().PathCollisions` across the *entire* mod set — a collision against a protected row's fixed
`CurrentPath`, or against a leftover `ProposedPath` on a row `SortByModType` didn't touch (`Category`
`null`), is a pre-existing gap this fix doesn't newly create and doesn't attempt to close (see Data
flow). `Validate()` remains the safety net for that residual case today, same as before this change.

## Non-goals

- Any change to `AssignManual`/manual sort. A collision a user creates by hand is a real mistake and
  must keep surfacing through `Validate()` unchanged — this spec only auto-resolves collisions produced
  by the two automatic sort strategies.
- Detecting or acting on whether colliding mods are *actually* identical content (e.g. via file-count
  comparison). The disambiguation is a neutral renumbering, not a judgment call — consistent with the
  Phase 1c classifier's "never guess" principle.
- Any new folder taxonomy (e.g. a "Duplicates" folder). Considered and rejected during brainstorming:
  it presumes the colliding mods are junk, which isn't always true, and introduces an organizational
  concept the app doesn't use anywhere else.
- Anything involving Apply/write support or deletion. This plugin's Sort strategies only ever set
  `ProposedPath` in memory (Phase 1 non-goal, still in force).

## Architecture

A new pure, static class, `Organizer/CollisionDisambiguator.cs` — flat under `Organizer/`, sibling to
`OrganizerState.cs`/`HeliosphereDetector.cs`, not nested under `Organizer/Classification/` (that folder
is specifically for the mod-type classification pair; this is a different concern: path deduplication,
not classification). Small, independently unit-testable, no dependency on `OrganizerState`'s internal
`Dictionary`, matching the general pure-helper pattern `ChangedItemKeyParser`/`ModTypeClassifier`
already establish elsewhere in this codebase.

```csharp
public static class CollisionDisambiguator
{
    public static void Disambiguate(IEnumerable<OrganizerModRow> rows);
}
```

Called from both `SortByCreator` and `SortByModType` immediately after each has assigned every touched
row's tentative `"{folder}/{Name}"`, passing the same `!m.Protected` (and, for `SortByModType`,
`m.Category is not null`) subset each method already iterates. One fix, both call sites — no
duplicated logic between the two strategies.

## Algorithm

1. Materialize `rows` into a list once (`rows.ToList()`) — the method reads each row's `ProposedPath`
   more than once (building the reservation set, then again per group), so a lazily-re-evaluated
   `IEnumerable` could see inconsistent state mid-run.
2. Build a case-insensitive set (`StringComparer.OrdinalIgnoreCase`, matching `Validate()`'s own
   comparer) of every row's tentative `ProposedPath` in the materialized list. This is the full set of
   "reserved" paths — not just the ones in a colliding group — so a generated suffix can never land on
   an unrelated, independently-named row's path (e.g. a mod literally named `Foo (2)` sitting alongside
   an unrelated `Foo`/`Foo_2` duplicate-install collision).
3. Group the materialized rows by `ProposedPath` (same comparer). Skip any group of size 1 — nothing to
   disambiguate.
4. Within a colliding group, pick the canonical (bare-path) row:
   - The member whose `Identifier` exactly equals its `Name` (`StringComparison.Ordinal`) — Penumbra's
     own signal that it's the original, non-suffixed install — if exactly one such member exists.
   - Otherwise (zero matches, e.g. every copy was manually renamed away from Penumbra's default; or,
     defensively, more than one match, which would mean two rows share an `Identifier` and therefore
     violates Penumbra's own identifier-uniqueness guarantee — an invalid state this code should
     tolerate without crashing, not one it needs to model correctly), fall back to the member with the
     lexicographically lowest `Identifier` (ordinal). The canonical row is identified by reference
     (`ReferenceEquals`) when excluding it from the "rest" of the group, not by value equality —
     `OrganizerModRow` is a `sealed class` with no `Equals` override, so this is just making the
     already-default reference semantics explicit.
   - `Identifier`/`Name` are non-nullable `required string` on `OrganizerModRow` (contrast with
     `Category`/`SubCategory`, which are `?`), so no null-handling is needed here — relying on that
     model invariant rather than defending against a state the type system already rules out.
5. Every other member of the group, processed in ascending `Identifier` order (ordinal), gets the first
   candidate suffix not already in the reserved set: try `{basePath} (2)`, and if that's already
   reserved (by another row's own bare path, or by a suffix already assigned earlier in this same loop),
   try `(3)`, `(4)`, ... until one is free. Add each assigned path to the reserved set immediately, so
   later members of the same group (or later groups) can't collide with it either.

```csharp
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
```

This is idempotent at two levels, both worth testing separately: calling `Disambiguate` twice directly
on an already-disambiguated list is a no-op (every path is already unique, so every group has size 1 on
the second call); and calling `SortByCreator`/`SortByModType` twice produces identical final paths both
times, since `ProposedPath` is recomputed fresh from `Name` at the top of each sort call before
`Disambiguate` runs — no suffix ever stacks onto a previous suffix (no `(2) (2)`).

Ordering among rows with genuinely equal `Identifier` values is intentionally left unspecified — that
state violates Penumbra's own identifier-uniqueness guarantee, so this design only commits to the
algorithm terminating and producing unique paths for it, not to which row "wins" the canonical slot.

Which collision group gets processed first (when two *separate* groups' generated suffixes could
otherwise overlap, e.g. one group of `Foo`/`Foo dup` and an unrelated pair of mods both independently
named `Foo (2)`) depends on `GroupBy`'s key-first-encounter order, which ultimately traces back to
`_mods`' `Dictionary` enumeration order — not something the BCL contractually guarantees. This doesn't
threaten uniqueness: `reserved` is a single set shared across every group in the same `Disambiguate`
call, so the final result is globally unique regardless of group-processing order. It only affects which
group ends up with the shorter suffix in that rare cross-group case — cosmetic, not correctness.

## Data flow

`SortByCreator`/`SortByModType` compute tentative paths exactly as they do today, then call
`CollisionDisambiguator.Disambiguate` on the same row subset before returning. `Validate()` itself is
unchanged — it still just detects collisions across the full `_mods` set. After this change, that set
should no longer include any collision *among the rows a given sort call touched* — per the Goal
section's explicit scope boundary, this does not extend to protected rows' fixed `CurrentPath`, or to
rows `SortByModType` left untouched because `Category` is `null`. A collision against one of those (a
separate, pre-existing edge case, not made worse by this change) would still surface via `Validate()`
as it does today; closing that gap would require passing reserved paths from outside the touched set
into the helper, which is out of scope for this fix (see Open Risks).

## Error handling

No new failure modes — this is pure in-memory renumbering over data the scan already produced. If a
group somehow contains rows with duplicate `Identifier` values (shouldn't happen; Penumbra guarantees
`Identifier` uniqueness at install time, which is the entire reason canonical selection works), the
algorithm still terminates and still produces unique paths — it just doesn't commit to which row lands
in the canonical slot, since that ordering is unspecified for an already-invalid input (see Algorithm).

## Testing

`CollisionDisambiguator` is a pure function (list in, mutated `ProposedPath`s out) — unit-testable
without a running game, same pattern as `ModTypeClassifier`. Test cases:

- Two-way collision with a clear canonical (one row's `Identifier == Name`) — canonical stays bare,
  other gets `(2)`.
- Three-plus-way collision, one canonical — others numbered `(2)`, `(3)`, `(4)` in `Identifier` order.
- No member has `Identifier == Name` (all manually renamed) — lowest-`Identifier` row stays bare, rest
  numbered from `(2)`.
- Non-colliding groups (unique `ProposedPath` per row) are left untouched.
- **Existing-suffix collision:** a group of `Foo`/`Foo duplicate` alongside an unrelated row already at
  `Foo (2)` — the duplicate must skip the taken `(2)` and land on `(3)`.
- **Case-insensitive suffix collision:** same as above but the unrelated row is at `FOO (2)` — suffix
  allocation must still recognize it as taken (`OrdinalIgnoreCase`).
- **Multiple occupied suffixes:** `Foo`/`Foo duplicate A`/`Foo duplicate B` alongside unrelated rows
  already at `Foo (2)` and `Foo (3)` — the two duplicates must land on `(4)` and `(5)`.
- **Cross-group collision (the scenario raised in review):** two independent collision groups where a
  naively generated suffix for one would land on the other's bare path — asserts the reserved-path set
  is built from the *entire* input, not per-group.
- Idempotency, direct: calling `Disambiguate` twice on the same already-disambiguated list is a no-op.
- Idempotency, sort-level: calling `SortByCreator` (and separately `SortByModType`) twice produces
  identical final paths both times.
- Lazy-enumerable input: pass a `Where(...)`-backed `IEnumerable` (not a pre-materialized list) and
  confirm correct, single-enumeration behavior.
- Invalid-state defense: two rows sharing an `Identifier` (or both matching `Identifier == Name`) still
  terminates and produces unique paths, without asserting which row wins the canonical slot.
- Integration: a `SortByCreator` test and a `SortByModType` test, each with a duplicate-install
  fixture, asserting `Validate().PathCollisions` is empty afterward for the touched rows.

## Open risks

1. **Colliding mods that aren't actually duplicates.** If two genuinely different mods happen to share
   a display `Name` (not observed in any spike dump so far, but not ruled out), this still assigns them
   distinct, valid paths — the neutral `(2)`/`(3)` numbering doesn't misrepresent them as "duplicates,"
   it just disambiguates. No special handling needed; flagged here only because it was raised and
   discussed during brainstorming, not because it's expected to cause a problem.
2. **`Identifier == Name` canonical-selection rule assumes Penumbra's own default naming.** If a user
   renames the *original* install's folder away from the bare `Name` while leaving a later duplicate at
   the Penumbra-default name, the fallback (lowest `Identifier`) picks whichever sorts first
   alphabetically rather than whichever was "actually" first — cosmetically arbitrary, but still
   produces a unique, valid path, which is the only hard requirement (Penumbra's sort order does not
   tolerate two mods sharing a full path).
3. **Deliberately not closing the protected-row/untouched-row collision gap.** Raised during review as
   "Option B": the helper could take a second `reservedPaths` parameter carrying paths from rows outside
   the touched set (protected rows' `CurrentPath`, `SortByModType`-excluded `Unknown` rows' leftover
   `ProposedPath`), giving the stronger guarantee that *no* automatically-assigned path collides with
   *anything*, not just with other automatically-assigned paths. Not adopted here: that gap already
   exists in `main` today, independent of this fix, is already called out in this doc's Data flow
   section, and `Validate()` already exists specifically to catch it before anything would ever be
   applied (which nothing in this plugin does yet — Apply stays disabled). Revisit if it ever causes a
   real reported problem, rather than closing it preemptively as part of a fix scoped to a narrower,
   concretely-observed bug.
