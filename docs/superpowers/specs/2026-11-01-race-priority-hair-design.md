# Race-Priority Hair Sorting - Design

**Status:** Revised draft. Not implemented.
**Builds on:** `docs/superpowers/specs/2026-11-01-hair-number-sorting-design.md` (0.6.1.0, testing build).

## Problem

A hair mod often replaces one hair for most races and a different hair for one race. Example: hair
115 for several races, hair 7 for Viera. Today the reader pools every race's numbers into one set.
That set has two numbers, so the mod is left flat in `Hair`. The sorter is correct to see two
numbers, but it has no way to say which one the mod is "about".

The race folder (`chara/human/cXXXX/...`) is already in each path, so the information exists and is
discarded. Race priority lets the user name the race they care about and file the mod under that
race's hair.

## Goals

1. A **Priority race** dropdown of race and gender combinations. Filing follows the chosen race when
   the mod has one hair number for it.
2. When the chosen race is absent, or no single number can be chosen, keep the existing detection.
3. A **Midlander fallback** checkbox that breaks a tie between different numbers across races by using
   the Midlander number.
4. Defaults change nothing. With the split checkbox off, nothing moves.

## Non-goals

- No change to Apply, proposals, or protection.
- No new number source. Path and EST evidence are read as in 0.6.1.0.
- No per-mod override. The priority is one setting for the whole sort.

## Terms

- **Number set for a race/gender:** the distinct hair numbers the mod supplies for that race/gender,
  read from its `chara/human/cXXXX/obj/hair/hNNNN/` paths.
- **Detected set:** the union of number sets across all races. This is the 0.6.1.0 input.
- **Different numbers across races:** the detected set has more than one number, each coming from a
  different race/gender. This is the case the fallback serves.
- **Multiple numbers within one race/gender:** one race/gender supplies more than one number. This is
  a separate edge case, covered by step 6 below. It is not "duplicate" in this spec.

## Behaviour

Resolution runs in order and stops at the first step that resolves a number.

1. **Priority race has exactly one number.** If the priority race/gender is set and its number set
   has exactly one number, file under that number. Other races' numbers are ignored. Example: 115
   for Miqo'te, 7 for Viera, priority Miqo'te: file under 115.
2. **Priority race absent.** If the priority race/gender has no number set (the mod has no files for
   it), go to step 3. This includes a priority of "None".
3. **Detected set has one number.** File under it. This is the 0.6.1.0 rule.
4. **Different numbers across races, fallback on.** If the detected set has more than one number,
   the Midlander fallback is on, and the Midlander rule below yields one number, file under it.
5. **Otherwise, no single number.** Stay flat in `Hair`, as 0.6.1.0 does.
6. **Multiple numbers within one race/gender, priority absent or not unique.** This edge case is
   governed by the rules above. It never picks an arbitrary number. If it reaches step 5, it stays
   flat.

Step 1 never consults the fallback. The fallback only decides between numbers that step 3 cannot
settle.

## Midlander fallback

The Midlander rule works from the Midlander entries in the mod: Hyur Midlander male (`c0101`) and
female (`c0201`). Codes are subject to the verification in "Race codes" below.

- **Both genders present, same number:** use it.
- **Both genders present, different numbers:** no single Midlander number. Go to step 5, stay flat.
  Picking the male or female number would be arbitrary.
- **Only one gender present:** use its number if that gender has exactly one number. Otherwise step 5.
- **No Midlander entries at all:** step 5. The fallback does not apply.

A Midlander number that exists only in one gender is still a Midlander number. The rule does not
require both genders, which is why a male-only Midlander mod still resolves.

## Race list and labels

The dropdown lists playable race and gender combinations. Labels read `Miqo'te ♂` and `Miqo'te ♀`.
In C# source the symbols are written as `♂` (male) and `♀` (female) escapes, never pasted
glyphs. Earlier work in this repo was bitten by pasted codepoints.

The dropdown includes a **None** entry, which disables steps 1 and 2 (the priority is absent).

Each entry maps to a `cXXXX` folder code through one table in code. Each entry has its own test, so
a wrong code fails the suite rather than a user's sort.

## Race codes

The table must be verified against Penumbra's own race-code data before it ships. This spec does
not assert the codes. The implementation plan must name the source that confirmed each code. If the
source cannot be reached, the table ships only after an in-game check by a tester. Until then the
feature does not ship.

## Settings and governance

- The feature is governed by the existing **Split hair by hair number** checkbox. With it off, the
  priority race and the fallback do nothing, and the controls are disabled, as the other split
  options are disabled when grouping does not apply.
- **Persistence:** session-only, like the other split checkboxes (`_splitGear`, `_splitNpc`,
  `_splitHair` are plain panel fields, not saved). The priority race and the Midlander fallback reset
  each session, with the fallback defaulting to off and the priority to None. This matches the
  existing Sort settings rather than inventing a second model.

## UI

On the Sort tab, under the split checkboxes:

- **Priority race** dropdown, default None. Disabled while the split hair checkbox is off.
- **Midlander fallback** checkbox, default off. Disabled while the split hair checkbox is off.
- Each has a Help topic, registered in `HelpTopics`, `HelpTab` and `HelpTopicUsage`, as
  `sort.split-hair` is.

## Data flow

- `ModHairFileReader` returns a map from race/gender to number set. The detected set is derived from
  it, so step 3 is unchanged.
- `EnrichHairSubCategory` takes the priority race and the fallback flag, and applies steps 1-6. It
  remains the single place that decides the number.
- The priority and the fallback travel with the sort selection, as `splitHair` does.

## Testing

- Reader tests: a map per race/gender, including a mod with no entry for the priority race.
- Step tests, one per step: priority unique wins even when other races differ; absent priority falls
  to detection; different numbers with fallback on resolve to Midlander; with fallback off, stay
  flat.
- Midlander tests: both genders agree; both differ (flat); male only; female only; no Midlander.
- One test per race-code table entry.
- Mutation-check each step's guard. A guard is not done until a mutation fails a test.
- Split hair off must leave `OrganizerState.Sort` output byte-identical to 0.6.1.0.

## Open questions

1. **Race codes.** Which source confirms the `cXXXX` mapping for each race and gender? Penumbra's
   source was not reachable during 0.6.1.0.
2. **Timing.** This feature changes filing only when the new controls are used. It inherits the Est
   question from 0.6.1.0 but does not depend on it. Should it wait for the in-game confirmation
   anyway?
