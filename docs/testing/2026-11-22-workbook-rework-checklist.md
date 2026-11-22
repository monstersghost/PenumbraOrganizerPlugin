# Workbook rework: in-game checklist (2026-11-22)

Spec: docs/superpowers/specs/2026-11-22-workbook-rework-design.md
Plan: docs/superpowers/plans/2026-11-22-workbook-rework.md

Nothing here has been run against a real library until you tick it. The automated tests cover the logic,
not Excel itself or the in-game window.

## Review tab and the gate

- [ ] Review tab, Workbook group: the **Group workbook by** box is gone; **Export mode** offers As is and
      As suggested by Sort, with As is selected on a fresh start.
- [ ] As suggested by Sort is greyed with "Scan first." before a scan, "Press Sort first." after a scan,
      and becomes available after Sort.
- [ ] After changing any Sort setting, it greys again with "Sort settings changed, press Sort again."
- [ ] After a manual move, a template apply, a protection change or a workbook import it greys with
      "Staged paths changed since the last sort, press Sort again."
- [ ] A root-only sort leaves it greyed (the staged paths are not one full Sort).

## Export

- [ ] Export as suggested, open the file in Excel: Destination holds the staged folders (top-level moves
      included); protected mods are blank.
- [ ] The Category Mapping sheet has a red banner, 16 built-in rows, and code 13 reads
      `Animation and VFX/VFX`, 15 reads `Animation and VFX`, 16 reads `Review`.
- [ ] The Edit Destinations sheet opens without a repair prompt and the helper columns (_internal_key,
      _leaf, _exported) are hidden.
- [ ] Close the workbook in Excel, hold it open in Excel, export over it: the message says another program
      may be using it, and the previous file is intact.

## Import

- [ ] An untouched As is export imports with 0 moves ready and every path unchanged, including a mod
      whose last folder name is not its display name and a duplicate install staged as `Name (2)`.
- [ ] An untouched As suggested export imports with 0 moves ready; the staged path keeps `(2)` and Review
      shows no new collision.
- [ ] Add row 17 on Category Mapping (code 17, folder `Mine/{type}`), put `17/{creator}/{race}` in one
      mod's Destination, save, Import: the Review tab shows the expected path, the report names code 17
      with its mod count, and the result line says "Saved 1 custom category".
- [ ] Export again: row 17 is still there.
- [ ] Put `99/x` in a Destination: the error reads `Edit Destinations!G<row>: invalid destination for <id>:
      code 99 is not in the Category Mapping sheet`, and only that mod is held back.
- [ ] Use code 17 twice: both rows report `used twice`, mods using 17 are held back, the others import,
      and the saved list is not changed.
- [ ] A folder with an unknown token (`Mine/{typo}`) on row 17 is an error and is not saved.
- [ ] Edit built-in row 5 (or clear the last built-in row): a warning shows what changed and the built-in
      value is used.
- [ ] A mod that is protected by its folder: set its protected cell to FALSE and give it a destination.
      The report says the move could not be staged, and its protection is unchanged.
- [ ] An old (version 2) workbook still imports; `{creator}` in it stays literal text.
- [ ] A file that is not a workbook (rename a .txt to .xlsx) gives "This file could not be read as a
      Penumbra Organizer workbook."
- [ ] A workbook exported from another library is refused with "different Penumbra library".
- [ ] A very large stray cell far below the data (for example row 900000) is refused, not slow.

## Race tokens

- [ ] With the Race master switch OFF, `{race}` still fills in the race folder for a hair or face mod.
- [ ] A gear mod with `{race}` in its destination drops that level instead of leaving an empty folder.

## Headless results (2026-11-22, real libraries, read-only)

Same harness as the race-split checklist, using the real mod names (287 and 2557) as rows. It exercises the
workbook code through ClosedXML, not Excel itself.

- An untouched As is export re-imports with 0 errors, 0 warnings and 0 changed paths (287 and 2557 mods).
- An untouched As suggested by Sort export (after a real Sort with racial scaling on) re-imports with 0 errors and 0
  changed staged paths (287 and 2557 mods).
- Speed at 2557 mods: export 0.5 to 1.2 s, import 0.2 to 0.4 s, file 130 KB.
- Still needs Excel and the game: the Excel repair prompt, file held open, the Review tab states and greyed button,
  and every edit-then-import item.
