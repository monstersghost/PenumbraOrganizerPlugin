# 0.6.4.0 (testing build) - numbered gear folders and padded hair

With both **Split gear by equipment slot** and **Split gear by body type** on, `Gear/` is laid out as
`00. Sets`, `01. Head` to `09. Rings`, then numbered body folders from `10.` in Body priority order
(only bodies that have gear), each holding the same numbered slot folders. Hair folders are three
digits (`Hair/002`). With only one gear split on, the layout is unchanged.

## What moves and what stops matching

- a) The first sort after updating moves gear and hair folders once. History restores the old layout.
- b) Template `folderLabels` keyed to the old subfolders (`Gear/Legs`, `Gear/Bibo+`, `Hair/2`) stop matching, while a key on `Gear` itself still renames the root.
- c) A hair mod protected at `Hair/2` stays there beside the new `Hair/002`.
- d) Folder protection rules naming the old gear folders no longer match the renamed ones.
- e) Workbooks exported earlier use the old names.
- f) Reordering Body priority, or gaining or losing gear for a body, renumbers the body folders and moves those mods once.
- g) Hair numbers of 1000 or more are not padded (accepted limitation).

## Also in this build: Body folders and collection check

- With **Split body mods by body type** on, `Body/` is numbered: `00. Skeleton`, `01. Physics`, `02. Tails, Ears and Horns` (shared by every body), then bodies from `10.` in Body priority order, each holding `00. Replacer`, `01. SFW`, `02. Hands and Feet`. Mods with no body stay flat in `Body/`.
- **Check my collection** (Sort tab, Body priority) suggests a first-choice body from the mods enabled in your collection. It only suggests; you confirm.
- Fixed: the **Unprotect N inside** button on the Protect tab now receives clicks.
- The first sort moves Body mods once; History restores the old layout. Part detection (hands/feet, ears/horns) is name- and file-path based and may misfile some mods.

## Two more split options

- **Hands and feet in a shared folder** (needs the slot split): gloves, gauntlets, boots and nails go to `Gear/03. Hands` and `Gear/05. Feet` instead of under each body.
- **Split hair by race**: one-race hair goes under its race (`Hair/Viera`), several races or none under `Hair/Multi-race`.
- Both are off by default, so nothing moves unless you tick them.
