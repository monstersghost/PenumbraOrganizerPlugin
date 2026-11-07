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
- Emote names fall into two kinds. **Facial expressions** (the game's Expressions emote category, for example
  Endure, Ponder, Reflect, Big Grin) are *secondary*: they are usually bundled with a real emote, so they are
  ignored whenever the mod also replaces any other emote. Everything else (dances, sit, groundsit, change
  pose, and the rest) is *primary*.
- Layout, when the option is on and the row is `Animation`/`Emotes`:
  - exactly one distinct primary emote -> `Animation and VFX/Emotes/<name>`;
  - two or more primary emotes -> `Animation and VFX/Emotes/Multiple`;
  - no primary emote, only expressions -> `Animation and VFX/Emotes/Expressions`;
  - no emote name at all -> `Animation and VFX/Emotes` (unchanged).
- The expression names live in one small list in code, checked against a real scan (see below). An emote not on
  the list counts as primary, so an incomplete list only means an expression sometimes gets its own folder.
- Names go through the same folder-name sanitising as other segments (`KnownSegment`/`FixName`), which can
  change characters Penumbra cannot store but never shortens a name.
- Sort tab: one checkbox "Split emotes by emote" in a new small "Animation" section; saved with the other
  Sort settings; carried to Select through `LayoutOptions` as a third flag.
- Not applied to Battle Animation, VFX or "Other".

**Unknown to settle during implementation.** The author expects most emote mods to bundle several emotes, so
`Multiple` may dominate. The first implementation task therefore logs each distinct emote name and how many
mods use it (and how many mods land in each of the four outcomes above) to `dalamud.log` after a scan. The
author sends that, and the plan is adjusted (for example pose or dance grouping) BEFORE the option ships.
Reference only: https://ffxivcollect.com/emotes lists the game's emotes with a General/Special/Expressions
category; the plugin does not depend on it (no API, and Penumbra already supplies the names).

## Testing
- Unit tests: anchor generalisation (folder in set, folder protected, sorter-folder name refused, per-anchor
  numbering with two anchors); emote tail (one, several, none, sanitised name, option off = today's path).
- Help topics and checklist rows for both; guide and HOW_SORTING_WORKS updated; screenshots retaken in game.

## Out of scope
Template and workbook flows, emote grouping by type, per-folder sort settings, Heliosphere "Treat as normal".
