# How Sorting Works

Two independent concerns combine to produce a mod's proposed folder path.

Classification decides what category a mod belongs to (Gear, NPC, Mount, Face, and so on). It runs
once per mod during Scan, from structural signal in Penumbra's own data, and never guesses:
anything the rules below don't recognize stays unclassified.

Sort strategy decides what folder layout to build, given a mod's category and creator (see "Sort
strategies" below). You choose one explicitly on the Sort tab: a "Group by" dropdown picks the
strategy, checkboxes decide whether Gear, NPC, hair and body-type splits apply, and the Sort button
applies the combination. All of these choices are saved between sessions.

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

## Numbered gear folders

When both **Split gear by equipment slot** and **Split gear by body type** are on, `Gear/` is numbered
(`GearFolderLayout`). With only one on, the layout is the old unnumbered one.

```
Gear/
  00. Sets          no single slot, no body found
  01. Head  02. Top  03. Hands  04. Legs  05. Feet
  06. Ears  07. Neck  08. Wrists  09. Rings     single slot, no body found
  10. Rue+          bodies by priority, only those with gear
      00. Sets  02. Top  03. Hands  04. Legs  05. Feet   same slot numbers inside
  11. Bibo+  ...
```

Body folders are numbered from 10 in Body priority order, only for bodies that have gear in this
sort, with no gaps. Gear whose slot is unresolved or that touches several slots goes to `00. Sets`
(under its body folder when a body was found). Hair folders are padded to three digits (`Hair/002`);
numbers of 1000 or more are written as is.

### Hands and feet in a shared folder

The **Hands and feet in a shared folder** option (off by default) needs the slot split, and it only does anything while **Split gear by body type** is also on. Gear whose
single resolved slot is `Hands` or `Feet` (gloves, gauntlets, boots, nails) then skips the body
folders and goes to `Gear/03. Hands` or `Gear/05. Feet`. Tops, legs, sets and every other slot keep
their body folder. A body that only had hands or feet gear no longer gets a folder, and the body
numbers close up. With the option off, hands and feet sit under each body as before.

## Hair sub-classification (hair number)

A mod classified `Hair` (its `Customization:` keys point at hair, which beats body and skin) gets
its folder number from a second pass, the same shape as Gear: `ModTypeClassifier.EnrichHairSubCategory`
runs only for mods already classified `Hair`, reading the mod's config once per Scan.

`ModHairFileReader.ReadHairNumbers` collects vanilla hair numbers from two places in the mod's own
Penumbra config JSON, and nowhere else:

- `Files` game paths matching `chara/human/c####/obj/hair/h####/`. The number is the `h####` part.
  The race code is matched to verify the path shape, then discarded, so a hair shipped for eight
  clans is one number.
- `Est` manipulations whose `Slot` is `Hair`. The number is the manipulation's `SetId`. `Entry` on
  the same object is the EST skeleton row, not the hair, and is never read.

The mod's name and its changed-item keys are never consulted. Numbers outside 1 to 9999 are
discarded as evidence without failing the read. The read is fail-closed, as the equipment reader is:
a config file that cannot be read, parsed, or enumerated makes the whole result unknown.

Exactly one distinct number gives a subfolder: `Hair/115`. Zero numbers, two or more, or an unknown
result all leave the mod directly in `Hair/`. There is no `Mixed` bucket. A mod can legitimately
carry an `Est` entry for one hair and replacement paths for another; that reads as two numbers and
files flat.

The `Est` source rests on `SetId` naming the hair number. The repo's fixture and the implementation
agree with that reading, but no real library has confirmed it yet. The manual checklist at
`docs/testing/2026-11-01-hair-number-sorting-checklist.md` is where that happens.

The **Split hair by hair number** checkbox defaults off and is disabled when Group by is Creator.

### Split hair by race

The **Split hair by race** checkbox (off by default) adds a race folder below `Hair/`, built by
`HairRaceFolders.Tail` from the per-race numbers the same reader collects (race codes map to a race
name, so male and female folders of one race count as that race):

- Hair files for exactly one race go under that race: `Hair/Viera`. Priority race and the Midlander
  fallback do not apply here.
- Hair files for several races, or with no race found, go under `Hair/Multi-race`.
- With Split hair by hair number also on, a one-race mod that uses exactly one number gets it
  appended (`Hair/Viera/115`, padded to three digits); a one-race mod with several numbers stays in the
  race folder. In `Hair/Multi-race` the number comes from the Priority race rule when one is set, and
  otherwise from the number stored at scan time (`Hair/Multi-race/115`).
- With the number split off, you get just the race folder.

## Split emotes by emote

The **Split emotes by emote** checkbox (Sort tab, Animation section; off by default, greyed out under
Creator grouping) breaks the flat `Animation and VFX/Emotes` folder up by the emote a mod replaces. It
reads the `Emote: <name>` entries Penumbra reports for the mod and uses the names exactly as Penumbra
gives them (only characters Penumbra cannot store in a folder name are changed). Facial expressions
(Endure, Ponder, Reflect, Big Grin) are treated as secondary, since they usually ride along with a
real emote. The outcomes:

- One real emote: `Animation and VFX/Emotes/<emote name>`.
- Two or more real emotes: `Animation and VFX/Emotes/Multiple`.
- Only facial expressions: `Animation and VFX/Emotes/Expressions`.
- No emote name found: `Animation and VFX/Emotes/Undetermined`.

Expressions are ignored whenever the mod also replaces another emote. No mod stays flat in `Emotes`.
Battle Animation, VFX and Other are not affected. A temporary `[emote-diag]` line in `dalamud.log` after
each scan lists the counts per outcome and per emote name; it will be removed before release.

## Sort inside folders

On the Protect tab you can pick any top-level folder (for example `.Character specific mods`) and add it
to **Sort inside folders**. It then behaves like a Heliosphere or Proteus folder in Sort inside their
folder mode:

- The folder stays where it is as the top level, and every mod beneath it is sorted again by the
  current Sort tab settings, giving `.Character specific mods/Gear/...`. Your own sub-folders under it
  are replaced.
- Protection still applies: a protected mod, or a mod in a protected sub-folder, is not moved.
- Numbering is kept per folder, so these folders never take numbers from your plain folders and do not
  leave gaps in them.
- The sorter's own folder names (`Gear`, `Hair` and so on) cannot be chosen.
- Removing a folder from the list returns its mods to the normal folders on the next sort. Moves that
  already happened stay until you undo them from History.

## Applying the plan: mods and folders share a namespace

Penumbra gives mods and folders one name space. When a mod sits at a path the sort needs as a
FOLDER (the mod `Bibo+` at `Body/Bibo+`, while the sort files mods under `Body/Bibo+/...`), nothing
can move beneath that path until the mod has left it, and the mod's own move would otherwise fail
with `PathRenameFailed`. `ApplyPlanner.OrderMovesForApply` therefore moves such a mod to a temporary
path first, then to its real target, before everything else.

## Tool-managed mods (Heliosphere and Proteus)

`OrganizerState.IsEffectivelyProtected` is the single protection rule. A per-tool mode
(`Protected` / `Sort inside` / `Normal`, saved in `Configuration`) decides whether a tool's mods are
protected by default; an explicit mod protection or a protected folder still wins. `Sort inside`
anchors a mod to the first segment of its CURRENT path (`ToolFolders.ApplyAnchor`), unless it is at
the root, already in one of the sorter's own folders, or already starts with that segment. Templates
go through the same `ToolAdjustedPath`. Proteus-made mods are those whose `meta.json` author is
exactly `Proteus`; its managed mod additionally has the directory name `Proteus`, and can be placed at
the root. See `docs/superpowers/specs/2026-11-05-tool-managed-mods-design.md`.

## Body-type sub-classification (which body gear was made for)

Unlike the gear and hair passes, this one cannot verify the body from the mesh: a garment made for
Bibo+ replaces its own equipment set, not the body's models. Proteus establishes the fact by mesh and
UV correspondence against the body's own meshes, which is far more work than a sort should do. So a
match here is a STATED intent, not a verified one. The statement is looked for in three tiers, and a
later tier is used only when the earlier ones found no body:

1. **Inside the mod.** Option group names, option names, and the FIRST folder of the mod-relative
   file paths in its config. The mod's own title is not part of this tier.
2. **The mod's name.** This was the only source before body priority existed. (For a `Body`-category
   mod the order of tiers 1 and 2 is swapped: a body replacer's own name says which body it IS,
   while its option groups often name the other bodies it is merely compatible with, so "Yet Another
   Body+" with a Bibo+ option group files under YAB. `BodyPriorityResolver.Resolve(nameFirst: true)`.)
3. **The library folders above the mod.** Least reliable (a Heliosphere folder, for example), so last.

For file paths only the first folder counts:

| Path | Counts? |
|---|---|
| `Prestige YAB+/top.mdl` | yes, first folder names YAB+ |
| `Bibo+/chest.tex` | yes |
| `shared/Bibo/file.tex` | no, Bibo is not the first folder |
| `common/textures/rue.tex` | no |
| `rue_top.mdl` (root file) | no, no folder |

When a mod's content supports more than one body, the user's body priority list (Sort tab, Body
priority section) picks the highest-ranked. The list is applied when Sort is pressed, not at scan
time, so reordering it and sorting again re-files mods without a rescan. A mod whose config cannot
be read falls back to its name. A mod with no body anywhere gets no body subfolder; ordinary sorting
can still move it.

The diagnostic dump shows why a mod landed where it did: a `BodyEvidence:` line per mod (the
candidates found, by tier) and, after a sort, a `StagedBody:` line plus a "Body priority used for
the last sort" header line.

Two things gate it, in this order, in `ScanProcessor.Process`:

1. `ModTypeClassifier.BodyTypeApplies` asks whether a body type means anything for this mod at all.
   `Body` rows always qualify - the category already says the mod is a body. A `Gear` row qualifies
   only if its equipment slots include `Top`, `Hands`, `Legs` or `Feet`: the four model halves a body
   replacer authors (`_top`, `_glv`, `_dwn`, `_sho`), and the same four Proteus's own
   `BodySizeCatalog.KnownSlots` carries. Head, Ears, Neck, Wrists and Rings are excluded with the
   weapons, because nothing about a helmet or a ring is cut to a body's shape. A weapon mod is the
   case that motivates this: it replaces `chara/weapon/...`, resolves no equipment slot, and yet
   classifies as `Gear` like any other item. Slots that could not be read count as no evidence, not
   as permission. This needs no extra disk read - the gear pass above already resolved these slots.
2. Only then is the evidence above gathered, with `BodyTypeMatcher.Match` doing the name matching.

The matcher lower-cases invariantly and splits on everything that is not a letter, a digit or `+`.
Keeping `+` attached is the whole reason it does not reuse `NpcNameMatcher`'s tokenizer, which treats
every non-alphanumeric as a boundary and would fold `Bibo+` into `Bibo` and `Rue+` into `Rue`.
Everything else is a boundary, `&` and `-` included, so one alias covers `Tight&Firm` and
`Tight & Firm`, and `TBSE-X` and `TBSEX`. Runes, not chars, because mod titles routinely carry emoji.

Aliases are tried longest first, so a variant name cannot be beaten by its parent's shorter one. No
shipped pair of labels currently differs on that basis - the variants that used to, YAB Buff and
TBSE-X, are aliases of their parents now - so the ordering is load-bearing for the next variant added
as its own body rather than for the current list. `Match_LongestAliasWinsRegardlessOfPosition` pins
it on synthetic entries so the guarantee does not depend on the data exercising it.

A match is stored on the row as `BodyType`, its own field rather than a value in `SubCategory`,
because a gear mod is both a `Top` and a Bibo+ garment and needs to carry both. No match leaves it
null; nothing is guessed.

The list is `Organizer/BodyTypes/body-type-list-static.json`, an embedded resource: 30 bodies and 120
aliases, every one corroborated against either Proteus's source or the community Body Mod Compendium.
`BodyTypeListStore` unions it with the user's optional `body-type-list.json`, where an entry matching
a shipped `Label` REPLACES it so a bad alias can be removed and not merely added to. That file is
capped at 500 bodies - the lesson from the NPC name list, applied where it can still recur.

Roughly twenty Compendium names are deliberately not aliased. Each is a word common in ordinary gear
titles - `vanilla`, `succulent`, `grande`, `stocky`, `cherie`, `seraphim` - and a false-positive sweep
showed them mis-filing unrelated mods. The body is still in the list; only the risky spelling is not,
and `KnownNonBodies_AreNotAliases` fails if one is re-added. `artemis` is the single knowing
exception, because it is the only name that body has.

The **Split gear and body mods by body type** checkbox defaults off and is disabled when Group by is
Creator.

**Check my collection** sits in the Sort tab's Body priority section. It reads the mods enabled in
your active collection (the one that affects your character, or Penumbra's selected collection when
that is unavailable) and counts only body replacer mods. If exactly one body is enabled it names it
and offers **Make first choice**; with none or several it says so and changes nothing. It says which
collection it read. It counts enabled replacer mods from their metadata; it does not prove which
body's files win in game, because mod priority and options can still decide that.
 With it on, the Body folder is numbered so it sorts the same regardless of the alphabet: `00. Skeleton`, `01. Physics` and `02. Tails, Ears and Horns` are shared and sit on top, then your bodies from `10.` in your Body priority order (only bodies that have body mods), each holding `00. Replacer`, `01. SFW` (clothing that covers the body, like lingerie) and `02. Hands and Feet`, for example `Body/10. Rue+/01. SFW/<mod>`. A mod that is not clearly a part stays in Replacer, and a body mod with no body stays directly in `Body`. A mod that is not clearly a part stays in Replacer. For example `Body/Rue+/Skeleton/<mod>`.

## Sort strategies

Given each mod's `Category`, `SubCategory` (from classification above), and `Author`, one entry
point builds a `ProposedPath` for every unprotected mod:

```csharp
OrganizerState.Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair,
                    Func<string, string> canonicalizeCreator, HairPriority? hairPriority = null,
                    bool splitBody = false)
```

`strategy` comes from the Group by dropdown and picks the path shape:

A body type, where one matched and `splitBody` is on, is inserted directly after
`{Category}` and above `{SubCategory}`: `Gear/Bibo+/Top/{Name}`.

| `SortStrategy` | Dropdown label | Path shape |
|----------------|----------------|-----------|
| `CreatorOnly` | Creator | `{Creator}/{Name}` |
| `TypeOnly` | Mod type | `{Category}[/{SubCategory}]/{Name}` |
| `TypeThenCreator` | Type then creator | `{Category}[/{SubCategory}]/{Creator}/{Name}` |
| `CreatorThenType` | Creator then type | `{Creator}/{Category}[/{SubCategory}]/{Name}` |

The three flags come from the checkboxes and decide whether `[/{SubCategory}]` survives, per
category. They are independent of each other and applied in sequence, which is what makes every
combination reachable without writing a variant of each strategy:

| Flag | Off | On |
|------|-----|----|
| `splitGear` | Gear mods get plain `Gear` | `Gear/Feet`, `Gear/Head`, and so on where the slot resolved |
| `splitNpc` | **NPC mods get plain `NPC`** | `NPC/NPCs`, `NPC/Bosses`, `NPC/Enemies` |
| `splitHair` | Hair mods get plain `Hair` | `Hair/115` where exactly one hair number resolved |
| `splitBody` | No body folders at all | `Gear/Bibo+/Top` and `Body/Rue+` where a body matched |

`splitNpc: false` arrived in 0.6.0. NPC subdivision used to be unconditional, so no button ever
produced a bare `NPC` folder. At that point the selection space became 1 + (3 x 2 x 2) = 13 -
`CreatorOnly` ignores the flags because it never consults the category - of which the seven
original buttons offered seven. **The six new ones were the whole `splitNpc: false` column**, and
that checkbox defaults to on so an upgrading user keeps the old shape unless they change it.

`splitHair` then took the space to 1 + (3 x 2 x 2 x 2) = 25. Unlike `splitNpc` it defaults to
**off**, so an unchanged install keeps producing flat `Hair/` and nobody's library reorganises
itself because they updated the plugin.

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

## Hair priority worked examples (real mods)

These rows come from the race-priority hair design. Each row names the step of the priority rules
that decides the result. Step 1 is the priority race with exactly one number, step 2 is a priority
race with no entry in the mod, step 3 is one number in the whole mod, step 4 is the Midlander
fallback, and step 5 is the flat `Hair` result. Race folders are matched by their folder code.

### Devilry Hair With Bow

Hair 108 for most races; the Viera female folder (c1801) uses 3.

| Priority | Step that resolves | Result |
|---|---|---|
| Midlander female | Step 1: one number, 108 | `Hair/108` |
| Viera female | Step 1: one number, 3 | `Hair/3` |
| None | Step 3: two numbers across races, fallback off | `Hair` |

### Zutail

Female folders only, with two numbers: 147 and 169. The mod has no Viera folder.

| Priority | Step that resolves | Result |
|---|---|---|
| Miqo'te female | Step 1: one number, 169 | `Hair/169` |
| Elezen, Roegadyn, Lalafell or Highlander female | Step 1: one number, 169 | `Hair/169` |
| Au Ra female | Step 1: two numbers (147 and 169), not exactly one; falls to step 3, which gives two numbers, fallback off | `Hair` |
| Midlander female | Step 1: two numbers; falls to step 3, which gives two; Midlander fallback: only female, two numbers, so step 5 | `Hair` |
| Viera female | Step 2: no Viera folder, so step 3 gives two numbers, flat | `Hair` |
| None | Step 3: two numbers, fallback off | `Hair` |

Midlander female with the fallback on still stays flat for this mod, because the fallback needs a
single number from the Midlander entries, and the female-only Midlander entries carry two.
