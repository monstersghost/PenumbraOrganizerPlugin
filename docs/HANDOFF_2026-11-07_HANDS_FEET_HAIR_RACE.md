# Handoff 2026-11-07: shared hands/feet, hair by race, 0.6.5.0

Shipped in 0.6.5.0 (stable). Spec `docs/superpowers/specs/2026-11-06-per-category-layout-settings-design.md`,
plan `docs/superpowers/plans/2026-11-06-hands-feet-and-hair-race-splits.md`.

## What exists

- `LayoutOptions(SharedHandsAndFeet, SplitHairByRace)` flows Sort tab -> `OrganizerState.Sort(..., options)` ->
  `SortFolderSelectors.Select` and `BuildGearLayout`. Saved as `Configuration.SortSharedHandsFeet` and
  `SortSplitHairByRace`; both default off.
- Shared hands/feet: gear rows whose subcategory is Hands or Feet skip the body folder and the gear layout's
  body list (`IsSharedSlot`), landing in `Gear/03. Hands` and `Gear/05. Feet`. Needs both gear splits.
- Hair by race: `HairRaceFolders.Tail` gives `<race>[/NNN]`, or `Multi-race[/NNN]` for several races or none.
- `OrganizerState.Sort` builds one gear and one body layout per anchor root (a Heliosphere/Proteus folder in
  sort-inside mode), so tool-managed bodies no longer consume numbers in plain folders.
- Help topics `sort.shared-hands-feet` and `sort.split-hair-by-race`; guide, HOW_SORTING_WORKS and checklist updated.

## Not done

- `TemplatePlanner` and the workbook export ignore both options (the template format is v1 and
  `WorkbookWorkflowService` is shared with the standalone app, so it must not be edited here).
- Not checked on the large library; hair-by-race has only been seen on one PC.
- Unverified rules: hands/feet `_glv/_sho`, `/obj/tail/`, `/obj/zear/` detection.

## Release mechanics worth remembering

- Help links are pinned to the tag (`blob/<version>/docs/USER_GUIDE.pdf`), so a docs-only fix needs the tag
  moved (force-push, done by the user) or a new release.
- The Discord workflow is enabled again and posts on every published release, prereleases included.
