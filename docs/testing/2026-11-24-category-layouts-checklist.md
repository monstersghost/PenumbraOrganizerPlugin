# Category layouts (spec 1) - in-game checklist

Build: feat/category-layouts. Keep a copy of `pluginConfigs/PenumbraOrganizer.Plugin.json` before starting.

- [ ] Update over an existing config: the Sort tab shows the same ticks as before; Sort proposes no move for a library that was already sorted with those settings.
- [ ] After that update, the saved config has a `CategoryLayouts` list (Gear, Hair, Body, Animation for someone on the old defaults) and the old `SortSplit*` fields are unchanged.
- [ ] Fresh install (rename the config file away, reload): the Sort tab opens with gear slot, gear body type, shared hands and feet, NPC kind, hair number, body mods and emotes ticked; race master off; Group by Type then creator.
- [ ] Fresh install, press Sort on a real library and note in the Review tab how many mods would move (the realistic number the headless harness cannot give).
- [ ] Fresh-config caveat: a config wipe, a reinstall or a config that fails to load also counts as a fresh install. Such a user gets Recommended and the next Sort proposes moving most of the library. Confirm the Review tab shows that before anything is applied, and say so in the release notes whenever Recommended changes (categories never stored as overrides follow the new Recommended).
- [ ] Untick and re-tick a split: it saves at once (reload the plugin, the tick is kept); re-ticking back to Recommended removes that category from `CategoryLayouts`.
- [ ] Select all splits, confirm: every split and race row ticked, shared hands and feet off; reload keeps it.
- [ ] Hand-edit the config: give Gear `"Folder": "Equipment/Gear"`; reload. The tab shows "Custom (read-only here): Gear." and the gear checkboxes greyed with the read-only reason; Select all does not change them.
- [ ] With that rename, Sort proposes `Equipment/Gear/...` for gear mods only; Apply moves them; nothing else moves.
- [ ] With that rename applied, press Sort again: nothing is proposed (the renamed folder is the sorter's own folder, not a sort-inside anchor).
- [ ] With Heliosphere set to Sort inside and Gear renamed `Kit`, a Heliosphere gear mod already under `Kit/...` is not filed under `Kit/Kit/...`, and its body folder takes the same number as the other gear for that body.
- [ ] With `Equipment` chosen as a sort-inside folder (Protect tab) and Gear renamed `Equipment/Gear`, gear inside and outside `Equipment` share one set of numbered body folders under `Equipment/Gear`; a second Sort proposes nothing.
- [ ] Protect tab, Sort inside folders: the renamed folder (`Equipment/Gear`, `Kit`) and its subfolders are not offered; a folder above it (`Equipment`) still is.
- [ ] Hand-edit Gear `"Creator": "None"` with Group by Type then creator: gear mods go to `Gear/...` without a creator folder; other categories keep the creator.
- [ ] Workbook export (As is): the Category Mapping sheet row for code 1 shows `Equipment/Gear`. Type `1/Test` as a destination, import: the mod is staged under `Equipment/Gear/Test`, no "built-in code was edited" warning.
- [ ] Export a workbook, then rename Gear in the config and reload, then import: an untouched destination keeps its exported folder; an edited `1/Test` goes under the new folder; the import says "code 1 now files under '...' (exported as '...')" and not "built-in code 1 was edited".
- [ ] A Sort rule `type = 'Gear' -> Mine/{type}` files a gear mod at `Mine/Equipment/Gear/<mod>` (the type field still says Gear).
- [ ] "As suggested by Sort" greys after changing a split or the hand-edited folder until Sort is pressed again.
- [ ] The new-mod popup's Sort uses the same layout (install a gear mod; it lands under `Equipment/Gear`).
- [ ] Downgrade: install the previous release over this config; its Sort tab shows the same ticks (the mirror); upgrade again; nothing moves and the rename is gone only if the older build re-saved the file.
- [ ] Race: with the master on and a face mod scanned before this update, the existing rescan hint still shows.

## Notes

Status note for the author's memory (category layouts, spec 1). Not written to the memory folder; copy it there if wanted.

- Branch `feat/category-layouts`. Spec `docs/superpowers/specs/2026-11-24-category-layouts-design.md`, plan `docs/superpowers/plans/2026-11-24-category-layouts.md` (its "Review amendments" R1-R13 override the task text).
- Decisions that matter later: the split order is fixed (catalog folder-depth order); Body has one "Part and body type" split; the race master, multi-race toggle and race priority stay global; Group by stays global (a category's creator level is null = follow Group by, and Creator-only grouping ignores every level); the fingerprint appends `|cats=<SHA-256>` only when a category has a non-default folder or its own creator level, and the rules digest in the fingerprint is now a SHA-256 too (R8); Sort's Others root is `Others` while workbook code 16 and `{type}` stay `Review` until Others is renamed; the template fallback keeps the default folders (a template rename plus a placement throws).
- Renamed category roots count as sorter folders (R3): a row already inside one has no sort-inside or tool anchor, an anchor above a row's renamed root shares the plain numbered layout, and the Protect tab does not offer renamed roots as sort-inside folders.
- Workbook: codes 1-16, built-in names and the Category Mapping rows follow renamed folders; the rules `type` field does not; an untouched exported cell stays literal; version 2 files ignore renames. The export records the renamed built-in folders in the hidden metadata (`categoryFolders`, only when something is renamed) so an import after a later rename reports "code N now files under 'X' (exported as 'Y')" instead of an edit warning.
- Not in spec 1: there is no screen to rename a folder or set a creator level; only a hand-edited config can, and the guide does not document either yet (spec 2).
- Verified: unit tests (full suite green), the equivalence golden unchanged since its own commit, Release build. Harness on `C:\Mods` (287 mods, headless, no Penumbra changed-item keys): a migrated legacy config sorts with 0 differences over 287 mods; Recommended against today's default layout would move 283 of 287 mods, nearly all `Gear/X` to `Gear/00. Sets/X` because headless runs cannot read gear slots (A13); renaming Gear to `Equipment/Gear` changes only the root of Gear paths (PASS).
- Not verified: everything in the checklist above, in game.
