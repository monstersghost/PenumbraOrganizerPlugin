# How Sorting Works

Two independent concerns combine to produce a mod's proposed folder path.

Classification decides what category a mod belongs to (Gear, NPC, Mount, Face, and so on). It runs
once per mod during Scan, from structural signal in Penumbra's own data, and never guesses:
anything the rules below don't recognize stays unclassified.

Sort strategy decides what folder layout to build, given a mod's category and creator (see "Sort
strategies" below). You choose one explicitly on the Sort tab: a "Group by" dropdown picks the
strategy, two checkboxes decide whether Gear and NPC mods get subfolders, and the Sort button
applies the combination.

Classification happens once during `RunScan()` and is stored on each `OrganizerModRow`. Sorting
just reads `Category` and `SubCategory` back off that row afterward. Re-sorting doesn't
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

`NpcNameMatcher` uses a **first-token index**, not a regex — it contains no `Regex` at all, and a
test asserts that no field of it ever holds one. Every known name is normalized, split into tokens,
and stored in a `Dictionary<string, NpcNameEntry[]>` keyed on its first token. Matching a mod title
tokenizes it the same way and does one dictionary lookup per token position, so "test 20,115
alternatives" becomes one lookup plus a median of one comparison. The earlier implementation built
one combined alternation regex per category; at a full wiki scrape that is a 205KB pattern per
category, seconds of JIT on first use, and tens of megabytes.

Names are stored merged rather than in three parallel structures. The same name routinely appears
in more than one category — 848 of 857 bosses are also Enemies — so each entry carries a `[Flags]
NpcNameKinds` of every category it belongs to. Without that, one name would occupy several slots in
one bucket and category precedence would fall out of sort order by accident.

**Category order is the outer loop** of the match. All NPC names are tried at every position before
any Boss name is tried anywhere, then Bosses before Enemies. Scanning positions outermost instead
would make the earliest-positioned name win regardless of category, so `"Titan Slaying Y'shtola"`
would classify as Boss rather than NPC — and with 679 bosses against 133 NPCs in the shipped list,
that would silently refile a great many mods.

Matching is whole-token. A token is a maximal run of Unicode letters or digits, iterated by `Rune`,
so underscore is a separator and `_Zenos_` still matches. A longer name is preferred over a shorter
one it contains, with character length and then an ordinal comparison breaking ties, so results
never depend on the order names appear in the list file.

Three matching behaviours differ from the regex implementation, each pinned by a test:

- **Separators between tokens are interchangeable.** The regex matched the literal `Y'shtola` only;
  token-sequence comparison also matches `Y-shtola` and `Y shtola`. A deliberate loosening.
- **Non-BMP letters are letters.** The regex tested UTF-16 surrogates individually, neither of which
  is `\p{L}`, so it found a word boundary in the middle of a single character. A tightening.
- **Case folding is ordinal.** The regex used `RegexOptions.IgnoreCase` without `CultureInvariant`,
  so it followed the current culture and diverged on Turkish dotted/dotless I.

`NpcNameMatch.Name` is now the list's canonical spelling rather than the text as it appeared in the
mod title. Nothing reads it today; `ModTypeClassifier` uses only `.Kind`.

The name list ships as a curated static list embedded in the plugin
(`npc-name-list-static.json`: 133 NPCs, 15 enemies, 679 bosses), and that is the default and only
source. The wiki scrape is a separate opt-in list in the config directory
(`npc-name-list-scraped.json`), unioned with the static one when enabled; it is **disabled in
0.6.0**, both in the UI and behind a compile-time gate the backend also consults. See
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

Given each mod's `Category`, `SubCategory` (from classification above), and `Author`, one entry
point builds a `ProposedPath` for every unprotected mod:

```csharp
OrganizerState.Sort(SortStrategy strategy, bool splitGear, bool splitNpc,
                    Func<string, string> canonicalizeCreator)
```

`strategy` comes from the Group by dropdown and picks the path shape:

| `SortStrategy` | Dropdown label | Path shape |
|----------------|----------------|-----------|
| `CreatorOnly` | Creator | `{Creator}/{Name}` |
| `TypeOnly` | Mod type | `{Category}[/{SubCategory}]/{Name}` |
| `TypeThenCreator` | Type then creator | `{Category}[/{SubCategory}]/{Creator}/{Name}` |
| `CreatorThenType` | Creator then type | `{Creator}/{Category}[/{SubCategory}]/{Name}` |

The two flags come from the checkboxes and decide whether `[/{SubCategory}]` survives, per
category. They are independent of each other and applied in sequence, which is what makes every
combination reachable without writing a variant of each strategy:

| Flag | Off | On |
|------|-----|----|
| `splitGear` | Gear mods get plain `Gear` | `Gear/Feet`, `Gear/Head`, and so on where the slot resolved |
| `splitNpc` | **NPC mods get plain `NPC`** | `NPC/NPCs`, `NPC/Bosses`, `NPC/Enemies` |

`splitNpc: false` is new. NPC subdivision used to be unconditional, so no button ever produced a
bare `NPC` folder. The selection space is 1 + (3 × 2 × 2) = 13 — `CreatorOnly` ignores both flags
because it never consults the category — of which the seven buttons offered seven. **The six new
ones are the whole `splitNpc: false` column**, and the checkbox defaults to on so an upgrading user
keeps the old shape unless they change it.

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
per-strategy behavior: grouping by creator used to drop such mods bare at Penumbra's root, and
grouping by mod type used to skip them silently.

Protected mods are never touched by a sort. Their proposed path stays exactly as last set, normally
equal to the current path.

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
