# Race-Priority Hair Sorting - Design

**Status:** Revised draft, third revision: race table complete. Not implemented.
**Builds on:** `docs/superpowers/specs/2026-11-01-hair-number-sorting-design.md` (0.6.1.0, testing build).
**Evidence:** `C:\Mods\[R] Zutail Hair (2)`, read in full (see "Evidence from a real mod").

## Problem

A hair mod often replaces one hair for most races and a different hair for one race. Today the
reader pools every race's numbers into one set. A mod with two numbers is left flat in `Hair`.
Race priority lets the user name the race they care about and file the mod under that race's hair.

## Goals

1. A **Priority race** dropdown. Filing follows the chosen race when that race has exactly one hair
   number in the mod.
2. When the chosen race is absent or ambiguous, keep the existing detection.
3. A **Midlander fallback** checkbox for the case where detection finds different numbers across races.
4. Defaults change nothing. With the split checkbox off, nothing moves.

## Non-goals

- No change to Apply, proposals, or protection.
- No new number source for the number itself. Path and EST evidence are read as in 0.6.1.0.
- No per-mod override. The priority is one setting for the whole sort.

## Evidence from a real mod

`[R] Zutail Hair` (meta.json v4, author Rutabega). The description says it replaces "Hair 169" and
"Hair 147 (Viera)". Its files, grouped by race folder code (`chara/human/cXXXX/obj/hair/hNNNN/`):

| Race folder | Hair numbers in files | Option folder that holds them |
|---|---|---|
| c0201 (Midlander female) | 147, 169 | `Racial Options/Hyur - Midlander` (169) and `Racial Options/Viera` (147) |
| c0401 (Highlander female) | 169 | `Racial Options/Hyur - Highlander` |
| c0601 (Elezen female) | 169 | `Racial Options/Elezen` |
| c0801 (Miqo'te female) | 169 | `Miqo'te Options` |
| c1001 (Roegadyn female) | 169 | `Racial Options/Roegadyn` |
| c1201 (Lalafell female) | 169 | `Racial Options/Lalafell` |
| c1401 (Au Ra female) | 147, 169 | `Racial Options/Au Ra` (169) and `Racial Options/Viera` (147) |

What this shows:

- **Viera shares race folders with other races.** Its 147 files sit under c0201 and c1401, the same
  folders as Midlander and Au Ra. A path code cannot say "Viera".
- **Penumbra's own names do not say Viera either.** The Changed Items list for this mod shows
  "Midlander Female Hair 147", "Au Ra Female Hair 147", and "Midlander Female Hair 169" among its
  entries. It has no Viera entry. The race name in a changed-item key is the same information as the
  race folder, so it has the same blind spot.
- **Only the option folder name says Viera.** That is the one place in this mod the Viera
  association is written down. It is a folder name chosen by the author, not a Penumbra fact.
- **The mod has no `Manipulations` section.** Nothing here touches the EST/SetId question from
  0.6.1.0. That question stays open for mods that do have manipulations.
- **169 is the majority number.** It appears under seven of the eight race folders. 147 appears under
  two.

## Evidence from a second mod: Devilry Hair With Bow

`♱ MDM - Devilry Hair With Bow` (meta.json, no manipulations, imported from a TexTools pack). Its files:

| Race folder | Hair numbers |
|---|---|
| c0201 (Midlander female) | 108 |
| c0401 (Highlander female) | 108 |
| c0801 (Miqo'te female) | 108 |
| c1401 (Au Ra female) | 108 |
| c1801 (Viera female) | 3 |

This mod is the case the feature is for. Viera has its own race folder, `c1801`, and its hair is 3. The
other races use 108. Viera is identifiable here from the folder code alone, so the Zutail conclusion
does not hold in general: a mod that ships a real Viera hair uses Viera's own code. Zutail's Viera
variant was built on Midlander and Au Ra codes and cannot be identified that way.

Worked results for this mod:

- Priority Midlander ♀: c0201 has exactly one number, 108, so `Hair/108`.
- Priority Viera ♀: c1801 has exactly one number, 3, so `Hair/3`.
- Priority None: two numbers across races, fallback off, so `Hair`.

## Evidence from the PMP set

Read from the zip archives without extracting them. These fixtures cover the rules:

- **Winter Berry for Lalafell:** Lalafell ♂ and Midlander use 135. Highlander ♂ uses 5. Different numbers
  across races, so the Midlander rule decides.
- **Lumi's Hairstyle V2:** Midlander ♀ and Lalafell ♀ use 110. Midlander ♂ and Lalafell ♂ use 111. Male and
  female Midlanders disagree, so the Midlander rule stays flat.
- **Sampo Middle Part:** Midlander ♂ uses four numbers (122, 162, 163, 164). Miqo'te ♂ uses two (3 and 164).
  Multiple numbers within one race, so rule 6 applies.
- **Viera Male Hair 182:** Viera ♂ uses 182, which confirms c1701.
- **Imeris - Solar:** hair 115 for every race except Viera ♀, which uses 3. With no priority it stays flat.
  Priority Viera ♀ gives `Hair/3`. Priority Midlander ♀ gives `Hair/115`.
- **Devilry Hair With Bow:** hair 108 for most races, Viera ♀ uses 3.

## Terms

- **Number set for a race/gender:** the distinct hair numbers the mod supplies for that race/gender,
  read from the `hNNNN` folders under that race's `cXXXX` folder.
- **Detected set:** the union of number sets across all races. This is the 0.6.1.0 input.
- **Different numbers across races:** the detected set has more than one number.
- **Multiple numbers within one race/gender:** one race/gender supplies more than one number, as
  c0201 does here (147 and 169). This is a separate case from different numbers across races.

## Race identity

Race and gender come from the `cXXXX` folder code. Penumbra's changed-item keys carry the same race and
gender names, and `ChangedItemKeyParser` already reads them. The dropdown lists the race and gender names
Penumbra uses, with the symbols written as `\u2642` and `\u2640` escapes, plus None.

## Race codes

Every race and gender has a folder code confirmed by at least one mod in our test set. Names are the
standard names for each race; the codes are from the mods.

| Race | ♂ code | ♀ code |
|---|---|---|
| Hyur Midlander | c0101 | c0201 |
| Hyur Highlander | c0301 | c0401 |
| Elezen | c0501 | c0601 |
| Miqo'te | c0701 | c0801 |
| Roegadyn | c0901 | c1001 |
| Lalafell | c1101 | c1201 |
| Au Ra | c1301 | c1401 |
| Hrothgar | c1501 | c1601 |
| Viera | c1701 | c1801 |

The table is pinned by one test per entry. Hrothgar female (c1601) is the weakest: it comes from a mod
titled "all Females" and not from a mod labelled Hrothgar. Confirm it in game when the in-game check runs.

## Behaviour

Resolution runs in order and stops at the first step that resolves a number.

1. **Priority race has exactly one number.** If the priority race/gender is set and its number set has
   exactly one number, file under it. Other races' numbers are ignored.
2. **Priority race absent.** If the mod has no entry for the priority race/gender, go to step 3.
   This includes a priority of None.
3. **Detected set has one number.** File under it. This is the 0.6.1.0 rule.
4. **Different numbers across races, fallback on.** The Midlander rule below yields one number. File
   under it.
5. **Otherwise.** Stay flat in `Hair`, as 0.6.1.0 does.

Step 1 never consults the fallback. The fallback only decides between numbers that step 3 cannot settle.

**Multiple numbers within one race/gender** (c0201 here) never picks an arbitrary number. If the
priority race is that race and gender, it is "not exactly one", so the rule falls through to step 3,
where the detected set is 147 and 169 and so has more than one number. It then reaches step 4 or 5.

## Midlander fallback

The Midlander rule works from the Midlander entries in the mod: Hyur Midlander male and female.

- **Both genders present, same single number:** use it.
- **Both genders present, different numbers:** no single Midlander number. Stay flat.
- **Only one gender present, with exactly one number:** use it.
- **Only one gender present, with more than one number:** stay flat. This is the case for
  Zutail: female only, with 147 and 169.
- **No Midlander entries:** the fallback does not apply.

## Worked results for Zutail

| Priority | Step that resolves | Result |
|---|---|---|
| Miqo'te ♀ | Step 1: one number, 169 | `Hair/169` |
| Elezen ♀, Roegadyn ♀, Lalafell ♀, Highlander ♀ | Step 1: one number, 169 | `Hair/169` |
| Au Ra ♀ | Step 1: two numbers (147 and 169) | not exactly one, falls to step 3, detection gives two, fallback off: `Hair` |
| Midlander ♀ | Step 1: two numbers | falls to step 3, detection gives two; Midlander fallback: only female, two numbers, so flat: `Hair` |
| Viera ♀ | Zutail has no `c1801` folder, so step 2: detection gives two numbers, flat | `Hair` |
| None | Step 3: two numbers, fallback off | `Hair` |

Note that **Midlander ♀ with the fallback on still stays flat** for this mod. The user will likely
expect 169, the majority, and the spec does not give it that.

## Settings and governance

- The feature is governed by the existing **Split hair by hair number** checkbox. With it off, the
  priority race and the fallback do nothing, and the controls are disabled.
- **Persistence:** session-only, like the other split checkboxes (`_splitGear`, `_splitNpc`,
  `_splitHair` are plain panel fields). The priority and the fallback reset each session, with the
  fallback off and the priority None.

## UI

On the Sort tab, in the Hair group (see the layout spec):

- **Priority race** dropdown, default None. Disabled while Split hair is off.
- **Midlander fallback** checkbox, default off. Disabled while Split hair is off.
- Each has a Help topic in `HelpTopics`, `HelpTab` and `HelpTopicUsage`.

## Data flow

- `ModHairFileReader` returns a map from race folder to number set. The detected set is derived from it,
  so step 3 is unchanged.
- The map is keyed by race and gender as Penumbra names them, so that `EnrichHairSubCategory` can match
  a priority against it without a code table.
- The race list covers all eighteen codes in the table above, matched by folder code.
- `EnrichHairSubCategory` takes the priority and the fallback flag, and applies steps 1-5. It remains the
  single place that decides the number.
- The priority and the fallback travel with the sort selection, as `splitHair` does.

## Testing

- A reader test using a trimmed copy of the Zutail layout: c0201 with 147 and 169, and the other race
  folders with 169. Assert the worked-results table row by row.
- One step test per rule: priority unique; priority absent; detection one number; fallback resolves; fallback
  off stays flat; priority with two numbers falls through.
- Midlander tests: both genders agree; both differ; female only with one number; female only with two.
- Mutation-check each step's guard. A guard is not done until a mutation fails a test.
- Split hair off must leave `OrganizerState.Sort` output byte-identical to 0.6.1.0.

## Open questions

2. **A tiebreak for two numbers in one race.** The Midlander rule is the only tiebreak in this draft, and it
   leaves Zutail flat for Midlander ♀. A principled option is the number that appears under the most race
   folders (169 here, under seven of eight). It is not arbitrary, but it is a new rule. Adopt it, or keep
   the draft?
3. **Timing.** This feature changes filing only when its controls are used. It does not depend on the EST
   question, since Zutail has no manipulations. Should it still wait for the in-game confirmation?
