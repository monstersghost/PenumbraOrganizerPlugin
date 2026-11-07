# Sort inside chosen folders, and emote split - design

Two independent options for the release after 0.6.5.0. Both are off by default; nothing moves unless the
user turns them on.

## 1. Sort inside a folder the user picks

**Goal.** Let any top-level folder (for example `.Character specific mods`, `halloween+`) behave like
Heliosphere and Proteus in "Sort inside their folder" mode: the folder stays as the top level and its mods
are sorted beneath it by the current Sort tab settings (`.Character specific mods/Gear/...`).

**Decisions (from the author).**
- Mods under the folder are **re-sorted completely**; the author's own sub-folders are replaced.
- Protection still applies inside: a protected mod or protected sub-folder is not moved, exactly as today
  (protect/exclude/inherit rules, deepest wins).

**Design.**
- Config: a saved set of folder names (`SortInsideFolders`), case-insensitive, normalised like protection rules.
  Absent in old configs reads as empty. No rollback mirror needed (additive).
- Protect tab: a per-folder control on top-level folders only, next to the existing tool-mode dropdowns,
  labelled "Sort inside". A folder cannot be both fully protected and sort-inside: protection stays the
  stronger rule, and the control is shown disabled with a tooltip while the folder is protected.
- `ToolFolders.AnchorOf` already returns a mod's top folder when it is not one of the sorter's own folders.
  The anchor rule generalises: a row is anchored when its tool mode is SortInside **or** its top folder is in
  `SortInsideFolders`. `OrganizerState.AnchorRootOf` and `ToolModeOf` gain that second source; numbering
  stays per anchor root, so these folders never take numbers from plain ones.
- Templates and the workbook export ignore this setting for now (same stance as the two 0.6.5.0 options).

**Edge cases.** A folder named like a sorter folder (`Gear`) is refused. A mod at the root has no anchor and
is unaffected. Removing a folder from the set takes effect on the next sort; earlier moves stay (History
undoes them).

## 2. Emote split ("Split emotes by emote")

**Goal.** Break the flat `Animation and VFX/Emotes` folder by the emote a mod replaces.

**Decisions (from the author).** Folder names are the emote names **exactly as Penumbra reports them**; no
shortening or clean-up.

**Design.**
- Scan: collect the names from `Emote: <name>` changed-item keys into a new `EmoteNames` set on the mod row
  (`ChangedItemKeyParser` already yields `ItemName` for the Emote shape). No new IPC calls.
- Layout, when the option is on and the row is `Animation`/`Emotes`:
  - exactly one distinct emote name -> `Animation and VFX/Emotes/<name>`;
  - two or more -> `Animation and VFX/Emotes/Multiple`;
  - none -> `Animation and VFX/Emotes` (unchanged).
- Names go through the same folder-name sanitising as other segments (`KnownSegment`/`FixName`), which can
  change characters Penumbra cannot store but never shortens a name.
- Sort tab: one checkbox "Split emotes by emote" in a new small "Animation" section; saved with the other
  Sort settings; carried to Select through `LayoutOptions` as a third flag.
- Not applied to Battle Animation, VFX or "Other".

**Unknown to settle during implementation.** How many of the 191 emote mods in the author's library carry a
single emote key; read it from a scan and report it before the release, because it decides whether
`Multiple` is a small or a dominant folder.

## Testing
- Unit tests: anchor generalisation (folder in set, folder protected, sorter-folder name refused, per-anchor
  numbering with two anchors); emote tail (one, several, none, sanitised name, option off = today's path).
- Help topics and checklist rows for both; guide and HOW_SORTING_WORKS updated; screenshots retaken in game.

## Out of scope
Template and workbook flows, emote grouping by type, per-folder sort settings, Heliosphere "Treat as normal".
