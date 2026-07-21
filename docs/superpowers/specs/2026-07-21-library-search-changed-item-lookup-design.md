# Design: Library Search — reverse changed-item lookup across the full mod library

**Status:** design approved by user in conversation (two review passes), awaiting final written-spec
review.

## Context

### The idea

The Sort tab's `SortByModType` already classifies every installed mod — active or not — into a
category (Gear, NPC, Mount, ...), because it's built on `GetChangedItemAdapterDictionary`, which
returns each mod's changed items "regardless of settings" (confirmed via
`Penumbra.Api.5.15.1`'s own XML doc comments), and `GetModListAdapter`, which returns *all installed
mods*, not just enabled ones. The user's premise — "Penumbra only shows changed items for active
mods" — is true of Penumbra's own in-game "Changed Items" panel (which aggregates what's currently
affecting your character, naturally filtered to enabled mods), but not of the IPC data this plugin
already reads. That data has been enable-state-independent from the start.

Given that, the user wants a genuinely new capability built on the same already-proven data: a
reverse lookup — "which installed mod(s) affect this item?" — searchable across the whole library,
mirroring Penumbra's own Changed Items tab's UI shape (category filter buttons, a search box), but
scoped to every installed mod rather than only enabled ones.

### Relationship to existing code

This is explicitly the "future 'path-based reclassification' project... a second signal source" idea
flagged as out-of-scope-for-later in the gear-slot classification design's Non-goals section
(`docs/superpowers/specs/2026-07-18-plugin-organizer-gear-slot-classification-design.md`). It is
being picked up now, but scoped narrowly: a **read-only browsing/search feature**, not a
reclassification of how Sort/Apply work. It reuses the same primitives Sort already depends on
(`ChangedItemKeyParser`, `ModTypeClassifier`'s internals, `ModEquipmentFileReader`,
`EquipmentSlotMapper`) without modifying their observable behavior for the sorter.

### Why a new namespace, not `Organizer/`

This is a standalone tool conceptually — a library-wide search/browse feature, not a sorting
mechanism. It lives in a new `PenumbraOrganizer.Plugin/LibrarySearch/` folder/namespace, sibling to
`Organizer/`, so it isn't tied conceptually to Scan/Sort/Apply/Protect/History. It references
`Organizer.Classification` types rather than nesting inside that namespace — and, per a second design
review, `GearSlotDiagnostic` (previously defined inside `Organizer/OrganizerModRow.cs`, a plugin *row
model* file) moves to `Organizer/Classification/GearSlotDiagnostic.cs`, sibling to the
`ModEquipmentFileReader` it already describes. Reusing an enum owned by a UI row model from an
intentionally independent namespace was backwards; this is a small, mechanical relocation
(`OrganizerModRow.cs` references it instead of defining it), not a new taxonomy.

### Two review passes

This design went through two rounds of detailed external review before reaching this version. The
first pass corrected the initial flat-entry model into the mod-centric shape below. The second pass
caught a real filtering bug (see "Filtering semantics"), several model/wording inconsistencies, and
pushed for a UI restructure (two-pane, not inline grouped rows). Both are folded in. Two specific
suggestions from the second pass were declined — an injected `IEquipmentSlotReader` abstraction and
an async/threading build pipeline — because they conflict with conventions this codebase has already
deliberately chosen elsewhere; see "Explicitly declined" at the end.

## Goal

A new "Search" tab that lets the user find every installed mod (enabled or not) whose changed items
match a query, filtered by category and (for Gear) equipment slot — without needing to enable mods
one at a time or rely on Penumbra's own enabled-mods-only Changed Items panel.

## Non-goals

- **Not a reclassification of Sort/Apply.** `ModTypeClassifier.Classify`'s first-match-wins
  mod-level `Category` is completely unchanged and untouched by this feature; this feature adds a
  parallel, multi-facet view for browsing, not a replacement.
- **Not item icons, not an enabled/disabled indicator, not a write feature.** No Dalamud
  texture/Lumina work, no "is this mod currently active" signal, no "enable this mod" or
  navigate-to-Penumbra action. Read-only IPC calls only, consistent with the plugin's existing scope
  boundaries (`[[plugin-mvp-scope-and-status]]` memory: no new write IPC beyond `SetModPath` without
  a fresh explicit decision — this feature needs none).
- **Not sharing UI state, caching, or a trigger with the Sort tab's Scan.** Reuses the same
  *pure logic and read-only IPC calls*, but never the same cached results, and never auto-runs as a
  side effect of clicking Scan.
- **Not per-item slot attribution.** Equipment slot data comes from disk files, item names come from
  `GetChangedItems` — there is no reliable join key between the two below the whole-mod level. A
  multi-piece outfit's slot match is a whole-mod fact, not a per-item one (see "Filtering semantics").
- **Not async, not threaded, not behind an injected I/O abstraction.** See "Explicitly declined."

## Architecture

### 1. Data model — mod-centric

**Correction caught during pre-implementation verification:** the shared `ModCategory` enum
(`PenumbraOrganizer.Core/Classification/ModCategory.cs`, linked from the standalone app) has no
`Unknown` member — the existing convention (`ModTypeClassifier.ClassificationResult.Unknown = new(null,
null, ...)`) represents "unclassified" as `Category: null`, not a sentinel enum value. `Facet` below
is therefore `ModCategory?`, and "unknown" is tracked via a separate `HasUnknownFacetItems` bool, the
same shape already used for `MatchedByNpcNameHeuristic` — not a synthetic enum member that doesn't
exist in the shared type this feature must not modify (it's used by the standalone app too).

```csharp
namespace PenumbraOrganizer.Plugin.LibrarySearch;

public sealed record IndexedChangedItem(string Key, ModCategory? Facet); // null = unrecognized shape

public sealed record IndexedMod(
    string Identifier,
    string Name,
    string Author,
    IReadOnlyList<IndexedChangedItem> ChangedItems,
    IReadOnlySet<ModCategory> Categories,      // union of non-null ChangedItems[].Facet — item evidence ONLY
    bool HasUnknownFacetItems,                 // true if any ChangedItems[].Facet is null
    bool MatchedByNpcNameHeuristic,            // separate provenance flag — see "Filtering semantics"
    IReadOnlySet<EquipmentSlot> EquipmentSlots,
    GearSlotDiagnostic SlotDiagnostic);

public sealed record ChangedItemIndex(
    IReadOnlyList<IndexedMod> Mods,      // only mods with >= 1 changed item — see rule below
    int TotalModsSeen,                   // every mod GetModListAdapter returned, including 0-item ones
    int OrphanedChangedItemEntryCount,   // dictionary entries whose identifier matched no mod (diagnostic only, expected to be 0)
    DateTime BuiltAt);
```

**Second-review fix:** `Categories` is strictly the union of per-item `Facet` values — it does
**not** fold in the NPC name-heuristic match the way the first draft did. That conflation was the
root cause of the first draft's ambiguous "which items do we show" problem (see below). The
heuristic match is now its own boolean, so "this mod matched because of its name" and "this mod
matched because one of its items is structurally NPC-shaped" are never confused.

**Zero-changed-item mods (second-review point):** a mod that resolves to zero changed items (either
absent from the changed-items dictionary, or present with an empty dictionary) is **not** added to
`Mods` — it can never satisfy a category or item-text filter, and the stated goal is searching by
changed items, not browsing the full mod list. It's still counted in `TotalModsSeen` so the refresh
summary can report "N of M mods indexed" honestly.

### 2. Per-key facet classification — extracted, not duplicated, from `ModTypeClassifier`

`ModTypeClassifier.Classify` already parses every key via `ChangedItemKeyParser.Parse` and evaluates
each one's `Shape` individually (confirmed by reading the current source: every rule body is a
`keys.Any(k => k.Shape == ...)` or `HasLiteral(keys, ...)` check against one key at a time) before
reducing to one mod-level `Category` via first-match-wins priority across *different* rules.

**New: `ClassifyKeyFacet(ChangedItemKey key)`**, extracted from the same per-key checks already
inline in `Classify` (placeholder lookup, Mount/Minion/Npc/Child-customization, Action/Emote/
Animation/Vfx literals, Housing/Sound literals, body-part mapping) — returns the single `ModCategory?`
(null for an unrecognized shape/body part) implied by *that one key alone*.

**Second-review caution, addressed:** a concern was raised that extracting per-key logic risks
silently changing `Classify`'s behavior if any rule secretly depends on cross-key context. Verified
against the actual code: every rule's condition is evaluated per-key (`k.Shape == X`); the only
cross-key logic in `Classify` is the *aggregation* — "does *any* key have shape Gear," and the
Action > Emote > (Vfx+Animation) > Vfx > Animation priority *among literal types*, which decides the
mod's single `SubCategory`, not any individual key's own category. `ClassifyKeyFacet` only needs to
answer "what category does this key alone imply" (no subcategory), which every rule already computes
per-key internally — so the extraction is mechanical, not a re-derivation. `Classify`'s own rule
*ordering* (Rules −1 through 9, first match wins, stop) is untouched; only the leaf "does this key
match this pattern" checks become a shared, named function instead of inline duplicates.
**Confirmed by new mixed-key regression tests** (see Testing) covering exactly the combinations where
`Classify`'s mod-level answer and the per-key facet union legitimately diverge — placeholder + real
gear, NPC-name match + non-NPC items, Housing + Gear, an unrecognized key alongside recognized ones —
proving the divergence is deliberate and localized to aggregation, not a parsing regression.

`IndexedMod.Categories` is the union of every non-null `IndexedChangedItem.Facet` across the mod —
**never** suppressed by Rule 0 (body-slot placeholder override) or Rule −1 (NPC name override) the way
`Classify`'s single result is. A mod with both a `Smallclothes` key and a real boots key gets
`Categories = { Body, Gear }` here, even though `Classify` alone returns `Category: Body` for Sort's
purposes. `HasUnknownFacetItems` is true whenever at least one key's `Facet` is null.
`MatchedByNpcNameHeuristic` is set independently from `NpcNameMatcher.Match(modName)`, never merged
into `Categories`.

### 3. Equipment slot detection — reused `ModEquipmentFileReader`, full set kept

`ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath)` runs once per mod whose `Categories`
contains `ModCategory.Gear` — same gating principle Sort uses (disk I/O only for Gear-facet mods),
keyed on the new facet-set membership, so a compilation mod carrying both a placeholder and real gear
still gets its slot data read.

Unlike `ModTypeClassifier.EnrichGearSubCategory` (which collapses to a single slot or nothing), this
feature keeps the **full resolved `IReadOnlySet<EquipmentSlot>`** — a different, legitimate use of
the same reader for a different consumer (browsing/discovery vs. unambiguous folder placement).

### 4. `LibrarySearch/ChangedItemIndexBuilder.cs` (new)

Builds a `ChangedItemIndex` from a mod list and a changed-items dictionary (both already fetched via
IPC by the caller — this class does no IPC itself). **This class performs real disk I/O** (via
`ModEquipmentFileReader`, gated to Gear-facet mods) and is not described as "pure" — that label was a
mislabeling in the first draft. It's tested the same way `ModEquipmentFileReader` itself already is:
real fixture files in real temp directories, no mocked filesystem, no injected interface (see
"Explicitly declined").

**Join contract (second-review question, already answered by existing code):** the join key is
`mod.Identifier` — exactly what `Plugin.cs`'s existing `RunScan()` already uses
(`allChangedItems.TryGetValue(mod.Identifier, out var changedItems)`), proven correct in production
across 2,270+ real mods. This is not a new integration risk; `ChangedItemIndexBuilder` uses the
identical lookup. A mod list entry absent from the changed-items dictionary contributes zero changed
items (see the zero-item exclusion rule above). A changed-items dictionary entry whose identifier
matches no mod in the list is skipped and counted in `OrphanedChangedItemEntryCount` — this should be
structurally impossible given both come from the same mod storage in the same call, but it costs
nothing to count defensively rather than assume. Duplicate identifiers are not handled specially:
Penumbra's own identifier contract already guarantees uniqueness, the same invariant the rollback
history feature already relies on elsewhere in this codebase.

For each mod: parse every changed-item key, compute its facet via `ClassifyKeyFacet`, union into
`Categories`; check `NpcNameMatcher.Match` for `MatchedByNpcNameHeuristic`; if `Categories` contains
`Gear`, call `ModEquipmentFileReader.ReadEquipmentSlots` and record `EquipmentSlots`/`SlotDiagnostic`.
Any per-mod disk-read failure is isolated to that mod (already `ModEquipmentFileReader`'s existing
fail-closed-per-mod contract — no new exception handling needed around it, and no broad catch-all is
added around the parsing/joining step, since neither `ChangedItemKeyParser.Parse` nor a dictionary
`TryGetValue` can throw under this codebase's existing contracts).

### 5. `LibrarySearch/ChangedItemIndexSummary.cs` (new, pure)

**Second-review fix:** rather than storing every summary count as a separate field on the index
(risking the model and the rendered text drifting apart, which is exactly what the first draft's
`ChangedItemIndexBuildResult` did), this is a single pure function that derives the summary line from
a built `ChangedItemIndex` on demand — one source of truth:

```
Indexed 2,263 of 2,270 mods · 42,817 changed items · 1,320 gear mods scanned
(1,144 single-slot, 164 multi-slot, 12 unresolved) · 4 missing directories · 3 read failures
```

Every number is computed from `Mods`/`TotalModsSeen`: total changed items is `Mods.Sum(m =>
m.ChangedItems.Count)`; gear-mod counts are grouped by `SlotDiagnostic` among mods whose `Categories`
contains `Gear` (`Single`/`Ambiguous` = multi-slot success, `ZeroEvidence` = unresolved,
`DirectoryMissing`/`ReadFailure` = the two failure counts). No sorter-flavored "could not be assigned
slots" language — `Ambiguous` is a **successful** multi-slot detection for this feature, not a
failure, and the summary says so plainly.

### 6. `Plugin.BuildChangedItemIndex()` (new)

```csharp
using var modList = GetModListAdapterIpc.Invoke();
var allChangedItems = new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary(PluginInterface).Invoke();
var index = ChangedItemIndexBuilder.Build(modList, allChangedItems, npcNameMatcher);
```

Same two bulk IPC calls `RunScan()` already makes — no new IPC surface. **Atomic replacement
(second-review fix):** the result is built into a local variable first; the plugin's cached
`_libraryIndex` field is only overwritten if the build completes without throwing, and
`_libraryIndexError` is cleared only on that same success path. If the build throws, the previous
`_libraryIndex` (and its `BuiltAt` timestamp) stays exactly as it was, and `_libraryIndexError` is set
so the UI can show "Refresh failed at {time}; showing index built at {previous BuiltAt}." This reuses
the same `_lastError`-style convention already used by Create Backup/Delete/Restore, rather than
introducing a new state-machine enum for what's really a two-outcome (success/failure) operation.

The refresh button is disabled while a build is in progress (a plain boolean guard, not a
threading/cancellation mechanism — see "Explicitly declined").

## Filtering semantics

### Mod-level match (second-review bug fix)

The first draft's rule — "if `Categories` contains `Gear`, the mod must also satisfy the slot
filter" — was wrong: it let an unrelated slot/Unresolved toggle state silently exclude a mod that
matched through a completely different category (e.g. an NPC+Gear+VFX mod, searching for NPC only,
with no gear slots selected). The corrected rule evaluates the Gear path and every other path
independently, then ORs them:

`LibrarySearchFilter.Categories` is `IReadOnlySet<ModCategory>` (the 12 real toggles), plus a
separate `bool IncludeUnknown` for the 13th toggle — mirroring `IndexedMod`'s own
`Categories`/`HasUnknownFacetItems` split, since `ModCategory` has no `Unknown` member to put in a set:

```csharp
bool MatchesCategoryFilter(IndexedMod mod, LibrarySearchFilter filter)
{
    var matchesNonGear =
        mod.Categories.Where(c => c != ModCategory.Gear).Any(filter.Categories.Contains)
        || (filter.Categories.Contains(ModCategory.NPC) && mod.MatchedByNpcNameHeuristic)
        || (filter.IncludeUnknown && mod.HasUnknownFacetItems);

    var matchesGear =
        filter.Categories.Contains(ModCategory.Gear)
        && mod.Categories.Contains(ModCategory.Gear)
        && MatchesGearSlotFilter(mod, filter);

    return matchesNonGear || matchesGear;
}

bool MatchesGearSlotFilter(IndexedMod mod, LibrarySearchFilter filter) =>
    (filter.IncludeUnresolved && mod.EquipmentSlots.Count == 0)
    || mod.EquipmentSlots.Overlaps(filter.Slots);
```

Explicit consequences, each with its own test (see Testing): an NPC+Gear mod matches on NPC
regardless of slot-toggle state; a Gear-only mod is excluded entirely if no slots and Unresolved are
selected; deselecting Gear entirely means slot toggles have no influence on any result, mixed or not.
Text filters (`Name`/item-key substring) are applied as a further AND on top of the category/slot
result, per the field-level rules already in place (`OrdinalIgnoreCase`, trimmed, whitespace-only
treated as empty).

### Displayed-item algorithm (second-review: made explicit, not left implicit)

For a mod that passes the match above, the specific `ChangedItems` shown are computed in this order:

1. Start with `mod.ChangedItems`.
2. If an item-text query is set, keep only items whose `Key` contains it (`OrdinalIgnoreCase`).
3. Keep only items whose own `Facet` is in the selected category set, **or** whose `Facet` is null
   and `IncludeUnknown` is selected.
4. **Exception:** if step 3 leaves zero items *and* the mod matched only via
   `MatchedByNpcNameHeuristic` (i.e. `NPC` is selected but no item's `Facet` is `NPC`), show the
   step-2 result instead — there is no specific item to narrow to, and hiding everything would be
   worse than showing everything. The result carries a flag so the UI can label this row "matched by
   mod name" rather than implying every listed item is individually NPC-shaped.
5. Slot/Unresolved matching never further narrows *which* items display (per Non-goals — no reliable
   per-item slot attribution); a mod that passed via the Gear path shows its full step-1-through-3
   result as-is.

This is a strict function of `(IndexedMod, LibrarySearchFilter)` with no hidden state, testable
directly.

## UI (second-review restructure: two-pane, not inline grouped rows)

- **Left pane**: a flat list of matching mod names (+ author) — one row per matching `IndexedMod`,
  in a scrollable `ImRaii.Child` region iterating every entry directly. **Correction:** an earlier
  draft of this section claimed this reuses "the same `ImGuiListClipper` approach the Review Changes
  table already uses" — verified against `PathTreeView.cs` (the actual Review Changes table
  implementation) and found false: it's a plain `ImGui.Table` with `Resizable | SizingStretchProp`,
  no `ImGuiListClipper`, and no `ImGuiListClipper` usage exists anywhere in this codebase. The real
  precedent this feature follows is simpler than first claimed: render every matching row directly,
  same as `PathTreeView` already does at comparable-or-larger scale (2,000+ mods) with no reported
  problem. Because this pane only ever renders short name/author strings (never the changed-item
  text), that's cheap enough to show fully populated by default (no artificial "type something first"
  gate needed). Actual `ImGuiListClipper` adoption is not designed here; it would be new machinery for
  this codebase, only worth introducing if real in-game testing surfaces an actual problem — same
  "revisit if measured, not speculatively" stance as the rest of this spec's performance posture.
- **Right pane**: the changed items for whichever mod is currently selected in the left pane,
  computed via the displayed-item algorithm above. Always small — bounded by one mod's own item
  count, never the whole library at once. Empty with a hint ("select a mod") when nothing is
  selected.
- **Category toggle buttons**: 12 real `ModCategory` values this classifier ever produces (Gear, NPC,
  Mount, Minion, Animation, VFX, Furniture, Sound, Face, Hair, Body, Skin), plus a 13th **Unknown**
  toggle backed by `IncludeUnknown`/`HasUnknownFacetItems` rather than a `ModCategory` value (all on
  by default) and **slot toggle buttons** (Head/"Hats",
  Top/"Tops", Hands, Legs/"Bottoms", Feet, Ears/"Earrings", Neck/"Necklaces", Wrists/"Bracelets",
  Rings, plus **Unresolved** — shown only while Gear is selected, all on by default) filter the left
  pane's mod list. Selected categories are ORed together; selected slots are ORed together within
  Gear; category/slot/name-text/item-text groups combine with AND; zero categories selected yields no
  results (an explicit, tested edge case, not an accident).
- **Two text inputs**: "Item contains" and "Mod name contains."
- **"Build/Refresh Index" button**, disabled mid-build, showing the derived summary line afterward
  (or the stale-index-plus-error message on a failed refresh).

This resolves the first draft's two open UI risks at once: grouped variable-height clipping (avoided
entirely — the left pane is fixed-height rows) and "showing the full unfiltered index by default is
noisy" (avoided — only names render by default; the expensive content is opt-in per mod via
selection).

## File layout

```
LibrarySearch/
├── ChangedItemIndex.cs          — IndexedChangedItem, IndexedMod, ChangedItemIndex records
├── ChangedItemIndexBuilder.cs   — build logic, Gear-gated disk reads, join/orphan handling
├── ChangedItemIndexSummary.cs   — pure, derives the summary line from a built index
└── LibrarySearchFilter.cs       — filter record + match/displayed-item algorithms
```

`MainWindow.cs` gets a `DrawSearchTab()` private method, consistent with how every other tab
(Scan/Protect/Sort/Review Changes/History) is already implemented directly in that file — introducing
a dedicated `LibrarySearchTab` UI class only for this one feature would be an inconsistent pattern,
not a real architectural improvement, since no other tab has one either.

## Error handling

| Situation | Behavior |
|---|---|
| `GetModListAdapter`/`GetChangedItemAdapterDictionary` IPC call throws during a *first* build | Surfaces the same way `RunScan()`'s existing IPC failures do today. No index exists yet, so there's nothing stale to preserve. |
| The same IPC call throws during a *refresh* | The previous `_libraryIndex` and its `BuiltAt` timestamp are preserved untouched; `_libraryIndexError` is set and shown alongside the stale results. |
| A mod's directory is missing (Gear-facet mod) | `SlotDiagnostic: DirectoryMissing`, `EquipmentSlots` empty, counted in the summary's missing-directory count. Mod still appears with its `ChangedItems`/`Categories` intact — only slot data is affected. |
| A mod's config file fails to read/parse | `SlotDiagnostic: ReadFailure`, same partial-degradation as above. |
| A changed-items dictionary entry matches no mod in the list | Skipped, counted in `OrphanedChangedItemEntryCount` — expected to always be 0 in practice. |
| A mod has zero changed items | Excluded from `Mods`, counted in `TotalModsSeen` only. |
| Empty query, all toggles on | Left pane shows every indexed mod; right pane stays empty until a mod is selected. Not an error. |
| Zero categories selected | No mods match, by design (explicit test case). |
| No matches | Left pane shows a plain "no mods found" message. |

## Testing

- **`ClassifyKeyFacet`** — every `ChangedItemKeyShape` variant maps to its expected facet, including
  the placeholder-override case (`Smallclothes`/Emperor's pieces → `Body`, not `Gear`) and the
  `(Child)` customization case (→ `NPC`). Unrecognized shapes/body parts → `null`, never a guessed
  category.
- **`ModTypeClassifier.Classify` regression** — full existing test suite passes unchanged after the
  extraction refactor (hard gate), **plus new mixed-key combination tests** that didn't exist before
  this feature: placeholder + real gear key together (Classify still returns `Body`, unchanged);
  NPC-name match + non-NPC item facets (`Classify` still returns `NPC` via Rule −1, unchanged); Body +
  VFX; Gear + Housing; an unrecognized key alongside recognized ones — proving the per-key extraction
  didn't perturb `Classify`'s cross-key aggregation.
- **`ChangedItemIndexBuilder.Build`** — correct per-mod `Categories` union (including the
  "placeholder + real gear → both facets present" case that deliberately diverges from `Classify`'s
  single answer); `MatchedByNpcNameHeuristic` set independently of `Categories`; correct Gear-only
  slot-read gating; a mod entirely missing from the changed-items dictionary excluded from `Mods` but
  present in `TotalModsSeen`; a changed-items entry with no matching mod counted as orphaned, not
  crashing; a mod's directory disappearing mid-build handled as `DirectoryMissing`, not fatal to the
  whole build.
- **`ChangedItemIndexSummary`** — derives correct counts from a hand-built `ChangedItemIndex`
  fixture, including the single/multi-slot/unresolved/missing-directory/read-failure breakdown.
- **`LibrarySearchFilter` matching** — the second-review bug fix as its own regression test (mixed
  `{Gear, NPC}` mod matches on `NPC` with every slot toggle off); Gear-only mod excluded when no slots
  and Unresolved are both off; non-Gear mod unaffected by any slot/Unresolved toggle state; zero
  categories selected yields no results; multi-slot mod matches every slot toggle it overlaps.
- **Displayed-item algorithm** — category filtering narrows to matching-facet items only; item-text
  filtering narrows to matching-key items only; the NPC-name-heuristic-only case shows all items
  labeled as name-matched, not item-matched; a null-facet item in a mixed mod (some items `Gear`, some
  `VFX`, one with a null `Facet`) shows only when the `Unknown` toggle (`IncludeUnknown`) is on, not
  merely because the mod matched some other category via its other items; a Gear-matched mod's slot
  filter never narrows which items display, only whether the mod appears at
  all.
- **String comparison** — `OrdinalIgnoreCase` throughout; whitespace-only query treated as empty;
  leading/trailing whitespace trimmed before comparison.
- **Atomic refresh** — a successful build replaces `_libraryIndex` and clears `_libraryIndexError`; a
  failed build (simulated IPC throw) leaves the previous `_libraryIndex`/`BuiltAt` untouched and sets
  `_libraryIndexError`.

## Explicitly declined (from the second review)

- **An injected `IEquipmentSlotReader` abstraction for testability.** `ModEquipmentFileReader` itself
  is a static class doing real disk I/O, tested against real fixture files in real temp directories —
  no mocked filesystem, no interface. Introducing dependency injection only for this feature's
  consumption of that same reader would be an inconsistent, one-off pattern; `ChangedItemIndexBuilder`
  is tested the same way its dependency already is.
- **An async/threaded build pipeline with progress state.** The gear-slot classification design (which
  this feature reuses the disk-read step from) explicitly declined the same speculative complexity
  for the same operation: "no performance test is planned... if a real slowdown surfaces during
  in-game verification against a large library, that's the trigger to revisit, not a speculative
  reason to add async/caching machinery now." The multi-save-point rollback design separately declined
  a more elaborate architecture from an earlier external review on the same grounds (over-engineered
  for a solo-user desktop plugin doing synchronous in-process IPC). This feature stays synchronous,
  with only a plain "disable the button while building" guard — no cancellation, no progress spinner,
  no background thread.

## Deferred ideas (not designed, not declined — future scope if ever wanted)

Exact/tokenized search, per-filter result counts, copy-to-clipboard actions, match highlighting,
sortable result columns, include/exclude (vs. on/off) toggle semantics, disk-persisted index with a
staleness indicator, and a future "select this mod in Penumbra" action (contingent on Penumbra
exposing a safe UI-navigation IPC call, unresearched).
