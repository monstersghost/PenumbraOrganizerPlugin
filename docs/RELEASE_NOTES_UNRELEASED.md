# Unreleased: numbered gear folders and padded hair

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
