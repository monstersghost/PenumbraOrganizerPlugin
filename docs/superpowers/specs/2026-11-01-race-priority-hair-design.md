# Race-Priority Hair Sorting - Design

**Status:** Draft for review. Not implemented.
**Builds on:** `docs/superpowers/specs/2026-11-01-hair-number-sorting-design.md` (0.6.1.0, testing build).

## Problem

A hair mod that ships for every race is read as covering several hair numbers. Viera have their own
hair numbers, so a mod that gives Viera one hair and everyone else another reads as two numbers and
stays flat in `Hair`. That is correct, but it is worse than it needs to be: the mod does replace one
hair for the majority of races, and the user would like it filed under that.

The cause is in the reader. `ModHairFileReader` collects every `h####` it finds under any
`c####` race folder into one set. The race folder is already in the path, so the information needed
to prefer one race is present and discarded.

## Goals

1. Let the user pick a **priority race and gender** on the Sort tab. A hair mod is filed under the
   hair number that the chosen race uses, when the mod has one.
2. When the chosen race is not present in the mod, fall back to today's rule (one number across all
   races, or flat).
3. Offer a **Midlander fallback** checkbox. When the fallback rule still finds more than one number,
   use the Midlander number as the sorting source instead of staying flat.
4. Default behaviour is unchanged. With the hair split checkbox off, nothing moves.

## Non-goals

- No change to Apply, the proposal model, or the protection rules.
- No new hair-number source. Path and EST evidence are read exactly as in 0.6.1.0.
- No per-mod override. The priority is one global setting.

## Behaviour

Let `N(race)` be the set of hair numbers the mod supplies for `race`, read from its
`chara/human/cXXXX/obj/hair/hNNNN/` paths. Let `N(any)` be the union across races, which is the
0.6.1.0 input.

Resolution, in order:

1. If the priority race is set and `N(priority)` has exactly one number, file under it.
2. Otherwise, if `N(any)` has exactly one number, file under it (0.6.1.0 behaviour).
3. Otherwise, if the Midlander fallback is on and the mod supplies exactly one Midlander number,
   file under it.
4. Otherwise stay flat in `Hair`.

Notes:

- Step 1 applies only when the mod has a hair entry for the priority race at all. A mod with no
  Viera files, under a Viera priority, drops to step 2. This is the "not found, revert" rule.
- "Duplicate" in the request is read here as "more than one number". Two numbers for one race is a
  conflict, and step 3 is the tie-break. A mod that is Viera-only under a Midlander priority is not a
  duplicate; it has no Midlander entry and drops to step 2.
- Multiple numbers for the same race fall through to step 2, which then also sees more than one
  number and stays flat, as today.
- The Midlander fallback never overrides a unique priority-race match. It only breaks a tie that
  step 2 cannot.

## Race list and codes

The dropdown lists every playable race and gender. Each entry maps to a `cXXXX` folder code. The
mapping lives in one table in code, with one test per entry, so that a wrong code is caught by the
suite rather than by a user.

The table must be verified against Penumbra's own race-code data before it ships. This spec does not
assert the codes; the implementation plan must cite where each one was confirmed. Until then the
table is a placeholder and the feature does not ship.

Display: each entry reads race name, then a gender symbol. Male is U+2642 and female is U+2640. In
the C# source these are written as `♂` and `♀` escapes, never pasted glyphs. The repo has
hit pasted-codepoint bugs before; see the non-ASCII memory.

Options: the race list (Hyur Midlander, Hyur Highlander, Elezen, Miqo'te, Roegadyn, Lalafell, Au Ra,
Hrothgar, Viera), each in male and female. Plus a "None" entry, which disables step 1.

## UI

On the Sort tab, next to the existing split checkboxes:

- A **Priority race** dropdown, default "None".
- A **Midlander fallback** checkbox, default off. Disabled while the hair split checkbox is off,
  matching how the other split checkboxes disable with grouping.
- Both have Help topics in the same three places as `sort.split-hair`: `HelpTopics`, `HelpTab`,
  `HelpTopicUsage`.

## Data flow

- `ModHairFileReader` returns a per-race map, not a flat set. The flat union is derived from it, so
  step 2 is unchanged.
- `EnrichHairSubCategory` gains the priority race and the fallback flag, and applies steps 1-3. It
  keeps the single-owner rule: one place decides the number.
- The race and fallback settings travel with the sort selection, as `splitHair` does today.

## Testing

- Per-race reader tests: a mod with Viera and Midlander numbers, under each priority, gives the
  expected number. Includes a mod with no entry for the priority race.
- Each race-code table entry is pinned by a test.
- Fallback tests: tie resolved by Midlander when on; flat when off.
- Mutation-check each step's guard. The 0.6.1.0 work found three guards whose tests passed with the
  guard broken, so a guard is not done until a mutation fails a test.
- The priority setting off must leave `OrganizerState.Sort` output byte-identical to 0.6.1.0.

## Open questions

1. **Race codes.** Which source confirms the `cXXXX` mapping? Penumbra's source was not reachable
   during 0.6.1.0. A tester may need to confirm it in game.
2. **Reading of "duplicate".** This spec reads it as more than one number for the priority race.
   Confirm before building.
3. **Persistence.** Should the priority race persist across sessions, like the other Sort settings,
   or reset? Default proposed: persist, like the other Sort settings.
4. **Interaction with the 0.6.1.0 testing build.** This feature changes filing only when the new
   controls are used. It does not need the Est question resolved first, but it inherits that
   question's risk. Should it wait for the in-game confirmation?
