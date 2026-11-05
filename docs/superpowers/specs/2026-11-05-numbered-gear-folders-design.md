# Numbered gear folders and padded hair numbers - design

Date: 2026-11-05. Status: approved in conversation, awaiting written review.
Source: community feedback (`docs/HANDOFF_2026-11-05_COMMUNITY_SORTING_FEEDBACK.md`, on branch
`ccr-02b1b2ec-v6favf`) plus the author's own numbering idea.

## Problem

With **Split gear by equipment slot** and **Split gear by body type** both on, `Gear/` directly
contains body folders (`Rue+`, `Bibo+`), slot folders (`Head`, `Top`) and loose mods, sorted
alphabetically together. Separately, Penumbra sorts folder names as text, so `Hair/` lists
`185, 2, 4, 53, 55`.

## Decisions (all confirmed by the author)

- **Number format `NN. Name`** (two digits, a dot, a space): `01. Head`, `10. Rue+`.
- **Scope:** gear slot folders and gear body folders only. Root folders and every other category
  stay as they are. Hair gets plain 3-digit padding instead of a prefix.
- **Only when both gear splits are on.** With one split on, today's layout is already clean and
  stays exactly as it is, with no numbers.
- **Body folders are numbered by Body priority**, only for bodies that have gear in this sort, no
  gaps, starting at 10. The first-choice body present is `10`.
- **Full outfits and other gear with no single slot go in `00. Sets`**, so nothing sits loose.

## Resulting layout (both splits on)

```
Gear/
  00. Sets          no single slot, no body found
  01. Head  02. Top  03. Hands  04. Legs  05. Feet
  06. Ears  07. Neck  08. Wrists  09. Rings     single slot, no body found
  10. Rue+          bodies by priority, only those with gear
      00. Sets  02. Top  03. Hands  04. Legs  05. Feet   same slot numbers inside
  11. Bibo+  ...
```

Slot numbers follow the order of `ModTypeFolders`' enumerated gear slots: Head, Top, Hands, Legs,
Feet, Ears, Neck, Wrists, Rings. Only Top, Hands, Legs and Feet ever carry a body
(`ModTypeClassifier.BodyTypeApplies`), so Head, Ears, Neck, Wrists and Rings are always directly
under `Gear/` as `01.`, `06.`-`09.`; inside a body folder only `00.` and `02.`-`05.` occur.
A gear mod whose slots could not be resolved or that touches several slots goes to `00. Sets`,
under its body folder when a body was found.

## Hair

`Hair/{n}` becomes `Hair/{n:D3}` for n below 1000 (`002`, `053`, `185`); 1000 and above are written
as is. Formatted when the folder is built (`ModTypeFolders.GetFolder`), so `SubCategory`, the hair
priority logic and the diagnostic dump keep plain integers. `IsHairNumber` keeps validating the
plain input.

## Design

- **`GearFolderLayout`** (new, pure, public): built once per sort from the rows being sorted, the
  body priority and the two split flags. Holds `IReadOnlyDictionary<string,int> BodyNumbers`:
  every body that at least one sorted gear mod resolves to (same resolver the sort uses, including
  `nameFirst` for Body rows is not relevant here - gear only), ordered by `BodyPriority.Rank` then
  ordinal label, numbered from 10. Exposes `Folder(slot, body)` returning the `Gear/...` path.
  Null layout means today's behaviour exactly.
- **`SortFolderSelectors.Select`** gains an optional `GearFolderLayout? gearLayout`. For a Gear row
  with a layout it asks the layout for the folder instead of `TypeFolder`; everything else is
  unchanged. Because the template planner's fallback uses the same selector, templates inherit the
  layout with no extra code, once the planner builds a layout from its rows.
- **`OrganizerState.Sort`** builds the layout (when both gear flags are on and a body priority was
  supplied) before the per-row loop and passes it to `Select`.
- **`TemplatePlanner`** builds a layout from the unprotected rows with the importer's body priority
  and fallback flags.
- **`ModTypeFolders.GetFolder`** pads hair. Gear numbering lives in the layout, not here, so
  `GetFolder` keeps its fail-fast validation untouched.
- The body label in a folder name still passes through the existing label validation.

## Effects, stated for the release notes

- The first sort after updating moves gear once. History restores it.
- Folder protection rules and template `folderLabels` written against `Gear/Legs`, `Gear/Bibo+`,
  `Hair/2` and similar stop matching.
- Reordering Body priority, or gaining/losing gear for a body, renumbers body folders and moves
  those mods once.
- Workbooks exported earlier use the old names.
- Hair mods protected at `Hair/2` stay there beside a new `Hair/002`.

## Tests

- `GearFolderLayoutTests`: numbering from 10 in priority order, gaps closed for bodies with no gear,
  tie by ordinal label, `00. Sets` for unresolved slots, slot numbers, non-body slots never under a
  body, null layout equals today's output.
- `OrganizerStateSortBodyTests`: both flags on gives the layout; either flag alone is unchanged.
- Template fallback test with both flags on.
- Hair: `Hair/002`, `Hair/115`, `Hair/1000`, with the stored `SubCategory` unchanged.
- A golden test on a small mixed library mirroring the author's `Gear/` counts.

## Out of scope (separate pieces)

Splitting Body mods by part (skeletons, physics, hands, feet); detecting the preferred body from the
active collection; writing Penumbra priorities; Kerrigain's "Single Slot" dropdown (the numbered
slots make it unnecessary); a template-label editor.

## To verify before release

- No vanilla hair number is 1000 or more (padding to three digits would then sort wrongly again).
- Penumbra keeps `01. Head` style folder names exactly as written.
