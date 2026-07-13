# Plugin organizer, Phase 1c: classify by mod type — Design

**Status:** proposed, pending user review. Not yet implemented.

## Context

`docs/superpowers/specs/2026-07-12-plugin-organizer-phase1-design.md` shipped Phase 1a (Scan/Protect/Review)
and 1b (By Creator sort) and gated 1c ("By mod type" sort) on a spike into `GetChangedItems`' key format.
That spike (`docs/superpowers/specs/2026-07-12-changed-items-format-spike-findings.md`, 10 mods) found the
assumed `"{Slot}, {Item name}"` convention does not hold, and explicitly deferred designing a replacement
classifier to "a separate brainstorming pass."

This spec is that pass. It is grounded in two much larger empirical samples gathered via a temporary
in-window "dump all changed items" spike button (see Implementation notes below): one across a ~226-mod
library, and a second across a ~2,035-mod library on a different machine — roughly 2,270 mods and 17,500+
changed-item keys in total, covering every `ModCategory` bucket the app's shared taxonomy defines except
`Weapon` and `Ornament` (which turned out to be indistinguishable from `Gear` by shape alone — see
Classification below) and `Pet` (which turned out to not exist as a distinct signal — see below).

## Goal

Classify each scanned mod into the shared `ModCategory` enum using only data obtainable from Penumbra's
`GetChangedItems`/`GetChangedItemAdapterDictionary` IPC, well enough to power a "By mod type" sort
strategy alongside the existing Manual and By Creator strategies. Never guess: an unrecognized shape maps
to `Unknown`, which routes to manual sort in Review, exactly like the app's existing behavior for
uncertain items.

## Non-goals

- Splitting `Gear` into `Weapon`/`Ornament`/etc. — no reliable shape-based signal exists for this
  distinction (see Classification, "Why bare names collapse to Gear").
- A distinct `Pet` category — every pet-appearance mod sampled across two mod sites and this plugin's own
  spike data classifies identically to `Minion` (same `(Battle NPC)`/`(Companion)`/`(Event NPC)` suffix
  shape). Treated as the same category, not a gap.
- A `BattleAction` (or similar) category distinguishing combat-skill VFX/animation mods from generic
  ones. Real signal exists for this (the `Action:` key prefix, see below) but it doesn't map onto any
  existing value in the shared `ModCategory` enum, and adding one is a taxonomy decision affecting the
  standalone app too. Deferred; the raw signal is preserved (not discarded) so this can be added later
  without re-gathering data.
- Gen3-vs-Bibo+ (or other body-mod-base) detection for gear mods. This needs actual file/texture-path
  inspection, a fundamentally different signal source than `GetChangedItems` (which only exposes
  display-name strings, and whose value objects are explicitly opaque/unsafe to inspect — confirmed
  against the Penumbra.Api 5.15.1 assembly). Parked for a later discovery pass.
- Fixing the pre-existing "By Creator" collision bug (see handoff doc) — out of scope, tracked separately.

## Architecture

Two pure, independently testable layers, added to the plugin project (no linked-file changes needed —
see Non-goals above regarding `ModCategory` itself, which stays linked from the app repo unchanged):

**Layer 1 — `ChangedItemKeyParser`.** Parses one raw key string into a structured record capturing every
field the string can reliably yield, not just what Layer 2 currently consumes:

```
Shape: Gear | Customization | Npc | Mount | Minion | Emote | Action | Icon | CategoryLiteral | Unrecognized
ItemName: string?       // Gear shape
Race: string?           // Customization shape, best-effort (absent for "Player Skin Textures")
Gender: string?         // Customization shape, best-effort
BodyPart: string?       // Customization shape — reliable, always present when Shape = Customization
Subtype: string?        // Customization shape, e.g. "(Iris)", "(Etc)", "(Skeleton)", "(Physics)"
Number: int?            // Customization shape, best-effort
CategoryLiteral: string? // "Animation" | "Vfx" | "Sound" | "Housing", when Shape = CategoryLiteral
Raw: string             // always kept
```

`Action:` and `Icon:` keys parse into their own `Shape` values with the text after the prefix kept on
`Raw` — they are never discarded, but Layer 2 does not currently act on them (see Classification).

**Layer 2 — `ModTypeClassifier`.** A pure reduction: given all of a mod's parsed key records, returns one
`ModCategory`. This is the only layer that changes if the classification rules are refined later; Layer 1
does not need to change to support that.

**Scan integration (Approach B).** `Plugin.RunScan()` calls `GetChangedItemAdapterDictionary()` once per
scan — a single bulk IPC call returning `IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>`
keyed by mod identifier (confirmed by reflection against the real Penumbra.Api 5.15.1 assembly; it is a
plain dictionary, not an `IDisposable` wrapper, so no `Disposed`-event cache-clearing is needed — simpler
than originally assumed in the Phase 1 spec). Each mod's key set is run through
`ChangedItemKeyParser` + `ModTypeClassifier` and the resulting `ModCategory` is stored on
`OrganizerModRow`, alongside the existing `HeliosphereManaged` flag.

**Sort strategy.** A new `ByModType` strategy alongside `Manual`/`ByCreator`, grouping by the stored
`ModCategory`, skipping protected rows — same pipeline shape as `ByCreator`.

## Classification

### Priority order (highest wins)

A mod's key set is reduced to one `ModCategory` in this order — the first rule that matches any key in
the set decides the whole mod:

1. **Gear** — any key with `Shape = Gear` (a bare, unprefixed, unsuffixed item name) anywhere in the set.
   Unconditional: beats every rule below regardless of how many non-Gear keys are also present.
2. **Mount** — any key with a `(Mount)` suffix (checked only if rule 1 didn't match).
3. **Minion** — any key with a `(Battle NPC)`, `(Companion)`, or `(Event NPC)` suffix (checked only if
   rules 1–2 didn't match). Covers what would otherwise be a separate `Pet` category.
4. **NPC** — any key shaped `{ItemName} (NPC, {id}, {slot})` (checked only if rules 1–3 didn't match).
5. If none of the above matched, resolve among the remaining shapes:
   - Bare `Vfx` key present, with no bare `Animation` key and no `Action:` key → **VFX**.
   - `Action:` key present, or `Vfx`+`Animation` both present, or bare `Animation` alone, or `Emote:`
     key present → **Animation**.
   - Bare `Housing` key present → **Furniture**.
   - Bare `Sound` key present alone → **Sound**.
   - Any `Customization` key present → sub-classify by `BodyPart` token: `Face`→`Face`, `Hair`→`Hair`,
     a token containing `Skin`→`Skin`, anything else (`Tail`, `Ears`, plain `Body`, `Body (Skeleton)`,
     `Body (Physics)`, etc.)→`Body`. The literal body-part value `Unknown`, or any unrecognized token,
     → `Unknown`.
   - Nothing recognized → **Unknown**.

`Action:` and `Icon:` keys never independently decide a category — every sampled mod containing either
one also contained a key matching one of the rules above (confirmed across all 40 `Action:` and 17
`Icon:` occurrences in the 2,035-mod sample). They ride along in the Layer 1 record for future use.

### Why bare names collapse to Gear (no Weapon/Ornament split)

Two mods in the sample (`青髓【皮肤武器】`, `夜煌【6星武器】`) are pure weapon-only reskins — nothing but bare
item names like `Moonward Samurai Blade` / `Moonward Samurai Blade (Sheathe)`. This confirms weapons
produce the identical bare-name shape as ordinary gear, with no distinguishing marker. The same is true
for jewelry/accessories (chokers, rings, earrings, bracelets are all bare names). Rule 1 above already
folds all of these into `Gear` — this is a deliberate simplification per the "good enough" bar for this
pass, not an oversight. A keyword-based Weapon/Ornament split was considered and rejected: real mods
(e.g. `[Dia] Zani`, 300+ bare-name items spanning weapons and jewelry) make clear that any such split
would need to be a per-item classification within a mod rather than a whole-mod category, which is a
larger scope change than this pass covers.

### Why the tie-break generalizes to "Gear wins" against every shape

Originally scoped as only a Customization-vs-Gear tie-break. Real compilation-pack mods forced
generalizing it further: `Carlotta's Outfit` (~30 Gear/Weapon/Ornament items + one incidental
`Archon Throne (Mount)` key) and `[Dia] Zani` (300+ Gear/Ornament items + one incidental
`Flying Chair (Mount)` key) are glamour packs that happen to bundle a bonus mount recolor — not mount
mods. Compare `Statice Flight`/`Yacht_V1.0`: pure mount reskins with only `Animation`/`Sound`/`Vfx` +
the `(Mount)` key, nothing else. Without a universal "Gear wins," the compilation packs would sort into
Mount/Minion/NPC folders because of one bundled extra. Confirmed by the user as the intended behavior:
if a user disagrees with this bias for a specific mod, they can still move it manually via Start
Manually sort (unaffected by this change).

## Data flow

Scan builds the in-memory model as before (mod → current path, protected flag, Heliosphere flag),
additionally calling `GetChangedItemAdapterDictionary()` once and running each mod's key set through
`ChangedItemKeyParser` + `ModTypeClassifier` to populate a new `Category: ModCategory` field on
`OrganizerModRow`. Selecting the `ByModType` sort strategy groups mods by that field to populate
`ProposedPath`, skipping protected rows. Review Changes computes the diff and runs `Validate()`
unchanged — no new collision-dedup logic (per the earlier scope decision to leave the By Creator
collision bug as its own separate task).

## Error handling

IPC failure calling `GetChangedItemAdapterDictionary()` (Penumbra not loaded) surfaces via the existing
inline error pattern in `MainWindow.cs`, same as `RunScan()`'s existing try/catch. Any key that doesn't
match a recognized shape, or a `Customization` key whose body-part token isn't recognized, classifies as
`Unknown` — never a guess, consistent with the app's existing behavior for uncertain items.

## Testing

`ChangedItemKeyParser` and `ModTypeClassifier` are pure functions (string/dictionary in, category out) —
unit-testable without a running game, same pattern as `CreatorCanonicalizer`. Test cases should cover
each rule in the priority order above, using real key strings captured in this spec and the two spike
dumps, including the specific compilation-pack cases (`Carlotta's Outfit`-shaped: Gear + incidental
Mount key) and pure single-purpose cases (`Statice Flight`-shaped: no Gear, only Mount + literal
category words). The `ByModType` grouping logic gets the same kind of test `ByCreator`'s grouping
already has. The real bulk IPC call and its data shape are only verifiable in-game, same as the rest of
this plugin's IPC surface.

## Implementation notes

A temporary "SPIKE: Dump changed items" button was added to the Scan tab (`MainWindow.cs`) and a
matching `Plugin.DumpChangedItemsSpike()` method, to gather the data this spec is based on. Both are
throwaway diagnostic code, explicitly not part of the shipped feature (matching the pattern of the
original Phase 1c format spike), and must be removed as part of implementing this spec.

## Open risks

1. `Action:` and `Icon:` keys are preserved but unused — if a mod ever produces one of these with *no*
   other recognized key, it falls through to `Unknown` untested (not observed in either sample; every
   real occurrence co-occurred with a decisive key).
2. The accessory/weapon collapse into `Gear` (see above) may prove too coarse in practice once users
   rely on "By mod type" — revisit with real Unknown/miscategorization complaints, not preemptively.
3. Sample size, while large (~2,270 mods), is drawn from two specific users' libraries and mod-site
   browsing; a systematically different library (e.g., heavy on housing/furniture, mounts, or minions
   relative to gear) could reveal further compilation-pack edge cases the priority order doesn't handle
   well.
