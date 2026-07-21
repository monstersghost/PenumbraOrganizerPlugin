# How Sorting Works

Two independent concerns combine to produce a mod's proposed folder path.

Classification decides what category a mod belongs to (Gear, NPC, Mount, Face, and so on). It runs
once per mod during Scan, from structural signal in Penumbra's own data, and never guesses:
anything the rules below don't recognize stays unclassified.

Sort strategy decides what folder layout to build, given a mod's category and creator (see "Sort
strategies" below). You choose one explicitly by clicking a button on the Sort tab.

Classification happens once during `RunScan()` and is stored on each `OrganizerModRow`. Sort
strategies just read `Category` and `SubCategory` back off that row afterward. Re-sorting doesn't
reclassify; only a new Scan does.

## Classification: rule priority order

`ModTypeClassifier.Classify(modName, changedItemKeys, npcNameMatcher)` runs every rule below in
order and returns on the first match. Nothing after a match is evaluated for that mod.

| # | Rule | Category | Signal |
|---|------|----------|--------|
| −1 | Mod name matches a known NPC, enemy, or boss name | `NPC` (subcategory NPCs, Enemies, or Bosses) | Name heuristic, described below. Outranks everything, including Rule 0. |
| 0 | A changed item is a known body-slot placeholder (`Smallclothes`, the five "Emperor's New ..." pieces) | `Body` | Structural, unconditional. Even beats real Gear. |
| 1 | Any changed item is a real equipment item | `Gear` | "Gear wins." Compilation packs bundle incidental extras, so a mount, minion, or emote alongside real gear still classifies as Gear. |
| 2 | Any changed item ends in ` (Mount)` | `Mount` | Structural. |
| 3 | Any changed item ends in ` (Battle NPC)`, ` (Companion)`, or ` (Event NPC)` | `Minion` | Structural. |
| 4 | Any changed item ends in `(NPC, ...)` | `NPC` | Structural. This is a genuinely NPC-targeted gear reskin, distinct from Rule −1's name heuristic. |
| 5 | Any customization key carries the `(Child)` race-variant marker | `NPC` | Structural. No playable character can be a child model, so this is an unconditional NPC signal, described below. |
| 6 | Any `Action:`/`Emote:` key, or the bare `Animation`/`Vfx` literal | `Animation` or `VFX` (subcategories Battle Animation, Emotes, Other, Animation, VFX) | Structural, with its own internal order: Action beats Emote, which beats Vfx combined with Animation, which beats Vfx alone, which beats bare Animation. |
| 7 | The bare `Housing` literal | `Furniture` | Structural. |
| 8 | The bare `Sound` literal | `Sound` | Structural. |
| 9 | Any `Customization:` key with a recognized body part | `Face`, `Hair`, `Body`, or `Skin` | Structural. Most specific wins, described below. |
| none | Nothing matched | `Unknown` (`ClassificationResult.Unknown`) | Never guessed. |

### Rule −1: NPC, enemy, and boss name heuristic

Some single-named-NPC face and skin mods carry no structural signal at all: their changed items
look like ordinary customization keys, with nothing to distinguish them from a player-facing skin
mod. The only signal available is the mod's own display name matching a known character. That is
why this rule runs first, ahead of even Rule 0's always-wins body-slot override. It's a deliberate,
user-confirmed trade-off. A mod combining a bare `Smallclothes` key with an NPC-suffixed key still
resolves to `Body`, not `NPC`, because Rule 0 wins over Rules 1 and later; a pure name match, on
the other hand, wins over everything.

`NpcNameMatcher` builds one combined alternation regex per category (NPCs, Enemies, Bosses)
instead of one `Regex` per name, because a full wiki scrape can reach five figures of names.
Matching is whole-word, defined as "not adjacent to a Unicode letter or digit" rather than `\b`,
which treats underscore as a word character and would wrongly match inside `_Zenos_`. A longer
name is preferred over a shorter one it contains. The name list itself ships with a small seed set
and can be expanded from the Sort tab's "Refresh NPC list from wiki" button; see
[USER_GUIDE.md](USER_GUIDE.md).

### Rule 5: child race-variant customization

FFXIV customization keys normally follow the shape `"{Race} {Gender} {BodyPart}[ (Subtype)][
Number]"`, where the subtype, if present, trails the body part, as in `Face (Iris)`. The `(Child)`
race-variant marker breaks that pattern: it appears leading, right after gender and before the
body part, as in `"Elezen Female (Child) Face 201"`. `ChangedItemKeyParser` checks for a leading
parenthesized token first, and falls back to the normal trailing check when there isn't one. No
playable character can be a child model in FFXIV; only NPCs use child-sized customization. So once
parsed, any key with `Subtype == "Child"` is an unconditional NPC signal, checked right after the
structural NPC-suffix rule.

### Rule 9: customization body-part priority

A single mod's `Customization:` keys often span multiple body parts, since nearly every face or
hair mod also touches Skin Textures as a side effect. When more than one recognized part is
present, the most specific one wins: Face beats Hair, which beats Body, which beats Skin. `Body`,
`Tail`, and `Ears` as customization keys (distinct from the `Ears` equipment slot) all map to the
`Body` category here. Anything unmapped, including the literal body part `"Unknown"`, contributes
nothing, and the mod falls through to `ClassificationResult.Unknown` if no other part matched.

## Gear sub-classification (equipment slot)

`Classify` alone only ever returns `Category: Gear` with no subcategory, since Gear's
`GetChangedItems` signal carries no per-slot data. A separate second-pass step,
`ModTypeClassifier.EnrichGearSubCategory`, runs only for mods already classified `Gear`, reading
disk once per Gear mod and never for any other category.

First, `ModEquipmentFileReader.ReadEquipmentSlots(modPath)` reads that mod's `default_mod.json`
and every `group_*.json`, recursively walking each option group's `Files`/`FileSwaps` (matched
against `chara/equipment/` and `chara/accessory/` paths only) and `Manipulations`. Manipulations
are filtered by `Type` first: `Eqp` and `Eqdp` carry the slot in a field called `Slot`, `Imc`
carries it in a field called `EquipSlot`, and every other `Type`, including `Est`, is excluded.
`Est` has its own unrelated `Slot` field, meaning a customization slot like `Hair` or `Face`, not
equipment.

The read is fail-closed: any single config file that can't be read, parsed, or enumerated
invalidates the whole mod's result (`null`). It never produces a confident answer built from only
the files that happened to succeed.

`EnrichGearSubCategory` assigns a `SubCategory` only when the read succeeded and resolved to
exactly one distinct slot. Zero slots, more than one slot, or a failed read all leave the mod as
plain `Gear`, never a guess.

Slot names (`Head`, `Top`, `Hands`, `Legs`, `Feet`, `Ears`, `Neck`, `Wrists`, `Rings`) come from
`EquipmentSlotMapper`, shared with the standalone app. Penumbra's own manipulation slot, literally
named `"Body"`, maps to `Top` here, deliberately distinct from this plugin's unrelated
`ModCategory.Body` bucket for Smallclothes and skin mods.

## Sort strategies

Given each mod's `Category`, `SubCategory` (from classification above), and `Author`, the four
Sort tab buttons build a `ProposedPath` for every unprotected mod (`OrganizerState.SortBy*`):

| Strategy | Path shape |
|----------|-----------|
| By Creator | `{Creator}/{Name}` |
| By Mod Type | `{Category}[/{SubCategory}]/{Name}` |
| By Type Then Creator | `{Category}[/{SubCategory}]/{Creator}/{Name}` |
| By Creator Then Type | `{Creator}/{Category}[/{SubCategory}]/{Name}` |

`{Category}[/{SubCategory}]` comes from `ModTypeFolders.GetFolder`, an explicit
`(category, subCategory)` to folder-string mapping rather than an open-ended interpolation, so an
unrecognized pairing throws immediately during development instead of silently producing garbage.
`NPC` plus NPCs, Enemies, or Bosses becomes `NPC/{sub}`. `Gear` plus any of the nine slot names
becomes `Gear/{sub}`. `Animation` or `VFX` plus a subcategory becomes `Animation and VFX/{sub}`.
Every other category with no subcategory just uses its own name, for example plain `Gear`, `Body`,
or `Mount`.

`{Creator}` is the mod's author, passed through `CreatorCanonicalizer.Canonicalize` first, a small
hand-maintained alias table (for example `"illy does things"` becomes `Soft Bun`, and
`"konekomods"` becomes `Koneko`) for creators whose displayed name varies across their own mods.

A mod missing a signal it needs for the chosen strategy, whether an unresolvable category, no
creator, or both, falls back to `Review/{Name}`. That's a single, consistent landing spot across
all four strategies, easy to find and re-sort by hand. It replaced an older, inconsistent
per-strategy behavior: By Creator used to drop such mods bare at Penumbra's root, and By Mod Type
used to skip them silently.

Protected mods are never touched by any Sort strategy. Their proposed path stays exactly as last
set, normally equal to the current path.

## Collision disambiguation

Two Penumbra installs can share the same display `Name`, which a sort strategy would otherwise
collapse onto one identical `ProposedPath`. After every automatic sort strategy runs,
`CollisionDisambiguator.Disambiguate` reserves every proposed path across the whole touched-row
set up front, not just within each individual collision group, then appends the first free `(2)`,
`(3)`, and so on to every row beyond the first in each colliding group. The canonical,
un-suffixed row in a collision is the one whose Penumbra identifier exactly matches its display
name, if exactly one such row exists, or otherwise the lowest identifier by ordinal sort. Manual
assignment, from the Sort tab's Assign button, deliberately skips this step. A collision created
by hand is treated as a real mistake and stays visible as a `Validate()` error instead of being
silently renumbered.

## Where category comes from at Apply time

Category and subcategory are computed once per Scan and never recomputed by a Sort strategy.
Running a second sort strategy without rescanning reuses the same classification, just in a
different path shape. That means installing a new mod, or a mod's changed items changing (for
instance when a Penumbra update adds new files to an existing mod), requires a fresh Scan before
its category reflects reality.
