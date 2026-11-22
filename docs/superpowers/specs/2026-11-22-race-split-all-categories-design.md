# Race split for race-specific categories (design)

Status: draft for the author's review, 2026-11-22, revised after two external reviews (see Review log).
Nothing here is built. First of two specs; the second is the workbook rework, which depends on this one for
its `{race}` token.

## Goal

Hair already files into a race folder (`Hair/Viera`, `Hair/Multi-race`). Extend that to every category whose
game files are tied to a race, and add the one thing hair did not need: a ranked **race priority** that
decides where a mod made for several races goes. Racial scaling mods, which the plugin cannot recognize
today, are detected and filed under Body so they can be split the same way.

## Decisions already made (author, 2026-11-22)

- One master switch, off by default, with a visible per-category list underneath.
- The rows: Hair, Face, Eyes, Skin, Tails/Ears/Horns, Racial scaling.
- Not offered, no row: Body meshes, Gear and accessories, Skeletons, Physics, Emotes and animations, Weapon,
  Monster, Demi-human, Housing, Doodad, VFX, UI, Maps, and face paint. Face paint files are shared decals
  (`chara/common/texture/decal_face/...`) and carry no race. Face means face meshes (heads, sculpts).
- Male and female are one folder. The folder is by race, never by gender.
- A **Send multi-race mods to Multi-race** toggle. On: a mod covering several races goes to `Multi-race`.
  Off: it goes to the folder of the highest-ranked race it covers (race priority). It applies to every row,
  hair included.
- Racial scaling is filed under Body, as a shared body part; it is not a new category. Attach offsets
  (`.atch`) are out of scope for now.
- Keyword rules and clan folders are out of scope.

## Correction to the record: what "race" means here

The plugin's race names are Penumbra's changed-item names (`HairRaceCodes`): **Midlander, Highlander, Elezen,
Miqo'te, Roegadyn, Lalafell, Au Ra, Hrothgar, Viera**. Hyur is therefore already two folders today
(`Hair/Midlander`, `Hair/Highlander`); there is no `Hyur` folder. The author chose "race only" on the premise
that Hyur merges; it does not. This spec keeps the nine existing names, so no existing hair folder renames.
Merging Midlander and Highlander into `Hyur` would be a separate, visible change to existing libraries and is
not part of this spec (see Open questions).

## 1. Settings

New config, all additive. Defaults reproduce today's output exactly.

- `RaceSplitEnabled` (bool, false): the master switch. It is a pure gate: it never changes the row selections.
- `RaceSplitRows` (set of row names): which rows are ticked. Names: `Hair`, `Face`, `Eyes`, `Skin`,
  `TailsEarsHorns`, `RacialScaling`. A fresh install has every row ticked, so switching the master on gives
  every supported category; the user unticks what they do not want (the "opt out" model). A row is in effect
  only when the master is on, the row is ticked, and the row's dependencies (below) hold.
- `RacePriority` (ordered list of race names, empty): used only to choose a folder for a multi-race mod.
  Stored as the nine names exactly as `HairRaceCodes.Race` spells them (not display text), normalized on
  load like `BodyPriority.Normalize` (unknown or duplicate entries dropped). It may be partial: ranked races
  win in order, unranked races never win.
- `SendMultiRaceToMultiRace` (bool, true): the toggle above. True is the default so existing and new
  libraries file multi-race mods exactly as hair does today.
- The existing hair **Priority race** (`HairRaceCode`) and **Midlander fallback** are not touched. Priority
  race is chosen per race and gender (the dropdown lists "Miqo'te male", "Miqo'te female", ...) and drives
  only the hair number, so it cannot be folded into the race-only folder priority without changing numbering.
  Two priorities exist: the old one for hair numbers, the new one for folders. The help text says so.

**Plumbing.** Three places read or write the old hair checkbox today and all three change: the Sort panel's
per-frame persistence of its choices (`PersistSortChoices`), `SortSelectionFactory.FromConfig`, and the
headless sort of newly installed mods (`PluginNewModHost`), which goes through the same factory. The new
settings travel in `SortSelection` and `LayoutOptions`, so an auto-sorted new mod follows the race split. The
Sort tab's "selection changed since the last sort" check compares those records by equality, so
`RacePriority` is held as a value with sequence equality (as `BodyPriority` is), not a plain list that
compares by reference.

**Migration.** Every config property is non-nullable with a default today, so "absent" cannot be told from
"default", and an older build that re-saves the file drops fields it does not know. So the new settings use
nullable storage: `RaceSplitEnabled` is a nullable bool, and null means "not migrated yet".

- On load, when `RaceSplitEnabled` is null: `SortSplitHairByRace == true` gives `RaceSplitEnabled = true` with
  `RaceSplitRows = { Hair }`, so nothing else moves; `false` gives master off with all rows ticked (the
  fresh-install state). `RacePriority` starts empty and `SendMultiRaceToMultiRace` true, so hair files as
  today. When it is not null, the saved values are used as they are.
- Legacy fields keep being written, derived from the effective state and not from the master alone, so a
  downgrade keeps the user's hair behavior: `SortSplitHairByRace = RaceSplitEnabled && Hair row in effect`.
  `HairRaceCode` and `MidlanderFallback` are never rewritten by this feature. This is the rollback-mirror
  idea the protection settings use, with one difference stated openly: the protection migration re-derives
  on every load because its legacy field is the full truth, whereas here the legacy field only carries the
  hair part.
- Known limit, stated in the tests: downgrade then upgrade again restores the hair behavior but resets the
  other rows, the race priority and the toggle to their defaults, because the older build cannot keep
  fields it does not know.

UI: a **Race** section in the Sort tab: the master switch (labelled for what it does, "Split supported
categories by race"), the six rows, the multi-race toggle, and a ranked race list modelled on Body priority
(reorder, nine races, unranked ones shown as not ranked). Dependencies:

- Under Group by Creator only, the whole section is shown but not editable, with the reason, as the hair
  option is today. The user's choices stay visible.
- The Tails/Ears/Horns and Racial scaling rows need the body split, because their folders do not exist
  without it. Their ticks are kept while the body split is off and come back into effect when it is on again;
  nothing is cleared.
- Help text states that the split is by race, not gender, so a male-only and a female-only mod of one race
  share a folder.

## 2. Folder rule

First, **applicability**: which mods have a race at all. It is a property of the mod, independent of every
switch: a mod is race-applicable when it is Hair, Face, Skin, a Body mod whose part is Tails/Ears/Horns, a
racial-scaling mod (section 4), or an iris-only Face mod. A Body replacer, Gear, a weapon and the rest are
not applicable. The switches and rows only decide whether the split is applied in Sort.

For a mod in a row that is in effect, with its race evidence (section 3) yielding a set R of race names:

1. `|R| = 1`, evidence complete: `<root>/<race>`.
2. `|R| > 1`, complete, toggle on: `<root>/Multi-race`.
3. `|R| > 1`, complete, toggle off: the highest-ranked race of R in `RacePriority`, `<root>/<that race>`.
   With `RacePriority = [Viera]` and `R = {Viera, Miqo'te}` the mod goes to Viera. If none of R is ranked,
   `<root>/Multi-race`.
4. `|R| = 0`, or evidence that could not be read completely: `<root>/Multi-race`, whatever the toggle. A
   priority pick is never made from incomplete evidence.

`<root>` is the category's current folder: `Hair`, `Face`, `Skin`, `Body/<tails-ears-horns folder>`,
`Body/<racial-scaling folder>`; Eyes is `Face/Eyes`. A template's rename of the root still applies to the root
only (for Body parts, to `Body` only, as today); the race tail is appended after it, as the hair tail is.

Hair numbers are unchanged and stay after the race tail. When rule 3 picks a race for a hair mod, the mod is
treated as a single-race mod of that race for numbering: it gets that race's number if that race has exactly
one, otherwise none. In `Hair/Multi-race` the number still follows Priority race and the Midlander fallback.

The folder rule is one pure function of (the mod's applicability, its evidence, the toggle, the priority)
that returns the race folder, or "not applicable". The workbook spec's `{race}` token calls this same
function, so Sort and the workbook cannot disagree.

Diagnostics: the mod's sort result records why it landed where it did: one race, several races (and which
rule chose), no race evidence, or unreadable evidence. All of the last three file under `Multi-race` for
compatibility, but the Review tab's diagnostics and the diagnostic dump distinguish them, so a detection
failure never looks like a genuine multi-race mod.

## 3. Evidence per row

Each row yields a race set plus whether the read was complete.

- **Hair:** unchanged. Race codes come from `chara/human/c####/obj/hair/` file paths only, as
  `ModHairFileReader` reads them; codes map to the nine names through `HairRaceCodes`. EST entries give hair
  numbers, never a race.
- **Face, Eyes, Skin, Tails/Ears/Horns:** the `Race` of the mod's customization changed-item keys
  (`ChangedItemKeyParser` already parses it), chosen by body part and subtype so a face mod's incidental skin
  keys never add a race. Match on (body part, subtype), not on the part name alone: Face is part `Face`
  without subtype `Iris`; Eyes is part `Face` with subtype `Iris`; Skin is any part containing `Skin` (real
  keys read `Skin Textures`, and the classifier already matches by containing); Tails/Ears/Horns is parts
  `Tail` and `Ears`. No fixture exists yet for ear or horn keys, so those names are confirmed in step 0. A key
  with no race (`Player Face`) is no evidence.
- **New scan data.** The keys exist only while a scan runs: `OrganizerModRow` keeps no changed-item keys
  today. This evidence therefore adds a row field (as `EmoteNames` is), filled at scan time, and takes effect
  only after a re-scan. Rows scanned before the upgrade have no such field and count as "no evidence"
  (`Multi-race`) until the next scan; the Race section says "Scan again to use race split for faces, skin,
  tails and ears". No new disk read is needed, so the read cannot fail.
- **Racial scaling:** the `SubRace` of the mod's `Rsp` manipulations, read with the existing
  `ModConfigFileWalker` manipulation hook (a new reader in the shape of `ModHairFileReader`, fail-closed:
  any unreadable config makes the evidence incomplete). Penumbra writes `SubRace` as the enum name, so the
  fold table is keyed on enum names, not display names: `Midlander`, `Highlander`, `Wildwood`, `Duskwight`,
  `Plainsfolk`, `Dunesfolk`, `SeekerOfTheSun`, `KeeperOfTheMoon`, `Seawolf`, `Hellsguard`, `Raen`, `Xaela`,
  `Helion`, `Lost`, `Rava`, `Veena` map to Midlander, Highlander, Elezen (two), Lalafell (two), Miqo'te
  (two), Roegadyn (two), Au Ra (two), Hrothgar (two), Viera (two). `Unknown`, a number instead of a name, or
  any other value makes the evidence incomplete. The exact enum spellings are confirmed against a real mod
  in step 0.
  Where these manipulations come from, checked in Penumbra's source (`TexToolsMeta.Rgsp.cs`): racial-scaling
  packs from TexTools (`.ttmp2`, entries like `chara/xls/charamake/rgsp/6-1.rgsp`, category "Racial
  Scaling") are converted by Penumbra on import into `Rsp` manipulations (sub-race index plus one, and a
  gender flag), so the plugin reads the manipulations, not the pack. Real sample, supplied by the author:
  "Variant Miqotes v2" has four `.rgsp` entries (Seeker and Keeper, male and female), so after import it
  carries Rsp entries for `SeekerOfTheSun` and `KeeperOfTheMoon` only, which fold to one race, Miqo'te. A mod
  folder that keeps a raw `.rgsp` file as a file entry instead (not converted) is an open check for step 0; if
  it exists, the path `chara/xls/charamake/rgsp/<index>-<gender>.rgsp` gives the same sub-race.

None of the non-hair evidence has been checked against real mods yet. That check is step 0 of the plan and
an entry criterion for implementation.

## 4. Order of evaluation, Eyes, and racial scaling

The existing category rules keep running first and are not reordered; the stored category of a mod does not
change. The new pieces act at sort time, in this order:

1. The existing classifier assigns the category at scan time (Gear wins, then the structural rules, then
   customization body-part priority), exactly as today.
2. At scan time a second, independent check records whether the mod is **racial scaling**. It does not change
   the category.
3. At sort time, **Eyes** applies to a mod already filed as Face, and **racial scaling** routing applies to a
   mod the scan flagged, both only when their rows are in effect.
4. The race tail is applied last, from section 2.

Racial scaling, and why it is a flag:

- Today a racial-scaling mod is filed as **Gear**. Penumbra reports Rsp changed items as
  `"<SubRace name> <attribute name>"` with no `Customization:` prefix, so `ChangedItemKeyParser` returns its
  Gear shape for them and Rule 1 ("Gear wins") files the mod under Gear. A bust tweak bundled in a body mod
  also files that mod under Gear today. Rewriting the category at scan time would move these mods whatever
  the settings, which would break "defaults reproduce today's output". So the scan only records a flag, and
  the sort routes flagged mods when the Racial scaling row is in effect; with it off (the default) they stay
  in Gear exactly as now.
- With the row in effect, a flagged mod takes a new shared Body part, `Racial Scaling`: no body folder, like
  Skeleton, Physics and Tails, Ears and Horns, in the numbered Body layout beside them (slots 03 to 09 are
  free; it takes the next one after `02.`), then the race tail. Adding the part touches every place that
  lists body parts: the un-numbered Body path in `SortFolderSelectors`, `BodyFolderLayout` (its shared-part
  test and part folder), `BodyPartFolders`, and the body-layout builder, so the part is never counted toward
  body numbers or thrown on as unknown.
- Flag definition (pure Rsp): across the whole mod (the default option and every option of every group, not
  one default view) there is at least one `Rsp` manipulation and no other category-defining content (no gear,
  hair, face, skin or other customization changed items or file redirects beyond what the Rsp entries
  themselves report). Previews, documentation and other non-game files do not count. A package whose options
  were not all read successfully is never flagged. A body, tail or gear mod that bundles a bust tweak is not
  flagged and keeps its own placement.

Eyes: a Face mod all of whose Face keys are `Face (Iris)` files under `Face/Eyes` when the Eyes row is in
effect, and the race tail then follows. A mixed mod (iris plus other face keys) stays in `Face` and follows
the Face row. With the Eyes row off, iris-only mods stay in `Face` and take a race folder only if the Face
row is in effect. Ticking Eyes therefore creates the `Face/Eyes` folder.

## 5. Out of scope

Attach offsets, clan folders, merging Midlander/Highlander into Hyur, Customize+ (a separate plugin whose
profiles are not Penumbra mods), face paint, Body meshes, gear,
Skeleton and Physics, emotes, the workbook and template exports (the workbook follows the Sort tab's staged
paths and the shared race function; templates pass no layout options today and ignore race, as they ignore
the emote split), keyword rules, and any release or announcement. Apply, collision handling, protected mods
and rollback are not changed: the new folders flow through the existing path pipeline and its safeguards
unchanged.

## 6. Testing and docs

- Folder rule: one race, several races with toggle on and off, empty and partial priority (including the
  `[Viera]` example), no evidence, incomplete evidence never picked by priority; applicability for each kind
  of mod; the root rename keeps working; hair numbering after a priority pick.
- Evidence: per-row selection by (part, subtype) (a face mod with incidental skin keys, a `Skin Textures`
  key, an iris-only mod, a mixed face and iris mod), `Player` keys without race, the Rsp fold for all sixteen
  enum names plus `Unknown` and numeric values, a fail-closed Rsp read, rows scanned before the upgrade, and
  the real "Variant Miqotes v2" pack imported into Penumbra as a fixture (expected: flagged as racial scaling,
  one race, Miqo'te).
- Migration: null `RaceSplitEnabled` migrates from the legacy checkbox and a saved value is never
  re-migrated; legacy fields are written from the effective state (Face-only enabled leaves
  `SortSplitHairByRace` false); downgrade then upgrade keeps hair and resets the rest; `HairRaceCode` and
  `MidlanderFallback` are never changed.
- Plumbing: the new fields reach `SortSelection`, `LayoutOptions` and the headless new-mod sort; changing the
  race priority marks the selection as changed since the last sort.
- Settings lifecycle: master off and on keeps the row ticks; body split off and on keeps the dependent ticks.
- Transitions on an already-sorted library: toggle on then off, reorder the priority, re-sort, and check the
  result is stable (a second sort moves nothing).
- Racial scaling: a pure Rsp mod with the row off (stays in Gear, as today) and on (Body part), an Rsp plus
  body mod and an Rsp plus gear mod (not flagged), an Rsp mod with a second non-Rsp option group, and the
  Body layout with the split off and on, including that the new part does not shift body numbers.
- Defaults reproduce the current layout (existing tests keep passing untouched).
- Docs: `HOW_SORTING_WORKS.md`, the user guide, help text and the in-game checklist, with each option.
- Not verified until run in game: the key-based race evidence for Face, Eyes, Skin, Tails and Ears, and the
  Rsp flag, on the author's real library.

## Review log

Two external reviews, 2026-11-22.

ChatGPT. Accepted: the evaluation order; complete versus incomplete evidence and no priority pick from
incomplete evidence; migration guard and legacy fields derived from the effective state; the master as a
pure gate with rows ticked by default; Eyes and mixed mods; the partial-priority comparator; package-wide
Rsp purity; dependent ticks kept; diagnostics that separate "several races", "no evidence" and "unreadable";
the transition and idempotency tests; a clearer master label; the race-not-gender help text. Accepted for a
reason the review could not see: keep Priority race separate (it is per race and gender in the code).
Declined: a complete default race order (the author wants an unranked race to fall back to Multi-race) and a
new move-safety contract (the existing pipeline is unchanged).

Opus, checked against the code. Accepted and changed: racial scaling is a scan-time flag routed at sort time,
because today those mods file as Gear and rewriting the category would break the defaults promise; a
presence check cannot work with non-nullable config, so storage is nullable and the downgrade limit is
stated; the three code paths that carry the hair checkbox and the new settings in `SortSelection`; key-based
evidence needs new scan data and a re-scan; the real key names (`Skin Textures`, iris is a subtype); the Rsp
fold keyed on enum names; the new body part in the un-numbered path and layout code; EST gives no race;
applicability defined independently of the switches.

## Open questions

1. **Hyur.** Keep the nine names (this spec) or merge to eight with a visible hair-folder change later. The
   first review agreed with keeping nine.
