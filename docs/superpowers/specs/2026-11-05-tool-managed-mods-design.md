# Tool-managed mods: Proteus protection, and a mode for Heliosphere and Proteus

Status: design, awaiting author review (2026-11-05). Lands AFTER the Sort/Review UX pass
(`2026-11-05-sort-tab-ux-pass-design.md`), which edits the same Protect tab and protection code.

## Goal

Mods made or installed by a tool should not be scattered by an Apply that does not know about the
tool. Heliosphere (HS) mods are already protected by default, with an all-or-nothing toggle.
Proteus (PT) writes mods too, and files them in its own folder, so they need the same treatment. The
author's experience, and a read of Proteus's source, say both tools find their mods by directory and
never by Penumbra path, so moving them is safe; the useful middle ground is keeping them together
while still sorting them. Each tool therefore gets one saved mode instead of a toggle.

## What the research established

- **Proteus** (source read at `b20e72d`): finds mods by directory name through Penumbra IPC and never
  reads a mod's Penumbra path; none of its Penumbra calls is a path getter. It files a new refit mod
  once, under a folder whose name is localized, and the folder is "only where a new mod is filed,
  never how one is found". A Proteus-made mod has `Author` exactly `"Proteus"` in its `meta.json`;
  its own managed output mod lives in a directory named `Proteus`.
- **Heliosphere**: closed source and its docs are silent. The author's own experience, not verified in
  code, is that HS mods still update when they sit outside the Heliosphere folder, so it also finds
  mods by directory and id. In the author's library the 22 HS mods already sit in Gear, Body and NPC.
- The author reports that moving the Proteus managed mod never broke anything in their own use.
- The author's library held no Proteus-made mods when this was written, so Proteus paths below are
  tested by fixtures and verified in game only once a real Proteus mod exists.

## Decisions (made by the author)

1. **One mechanism for both tools.** Heliosphere gets the same three modes as Proteus.
2. **Three modes per tool:** Protected (default, never moved), Sort inside their folder, Treat as
   normal.
3. **The anchor for "sort inside" is the mod's current top folder**, so it works in any language and
   respects where the user put things.
4. **The Proteus managed mod** can either stay protected (the default) or be placed at the root, as
   its own choice.

**Proposed by the agent, to confirm on review**
- The managed mod is protected by default and is not subject to the three modes.
- Modes are saved in the config. A per-mod untick is an override that a rescan clears, as it is
  for Heliosphere today.
- Heliosphere's mode now persists across rescans. Today a rescan always re-protects HS mods; that
  contract rested on "Heliosphere owns their location", which the author's experience contradicts.
- The controls live on the Protect tab only.

## Detection

- `ProteusDetector`: a mod is **Proteus-made** when its author equals `Proteus` exactly (ordinal, as
  Proteus's own check is). The row's author already comes from Penumbra, so there is no extra disk
  read and no remembered-identifier set (Heliosphere needed one because its signal is a file that
  disappears mid-update).
- A mod whose directory name equals `Proteus` (ordinal-ignore-case) is the **Proteus managed mod**.
  If it also carries the author it is the managed mod, not a made mod.
- Mods Proteus imports (Onion, Eye, Luminis, emissive) keep the imported author, are not detected,
  and sort as ordinary mods.
- `OrganizerModRow` gains a `ManagedBy` value: None, Heliosphere, Proteus, ProteusManaged. The
  existing `HeliosphereManaged` flag keeps working and is derived from it, so nothing that reads it
  changes.

## The modes

`Configuration.HeliosphereMode` and `Configuration.ProteusMode` hold a `ToolMode`:
`Protected` (default), `SortInside`, `Normal`. An unknown stored value reads as `Protected`.

| Mode | Protected? | Where it is sorted |
|---|---|---|
| Protected | yes, unless overridden per mod | not sorted |
| Sort inside their folder | no | beneath its current top folder (below) |
| Treat as normal | no | like any other mod |

### Protection rule

The single protection rule (`OrganizerState.IsEffectivelyProtected`) gains the tool clause in place
of the Heliosphere one: a row is protected when its tool's mode is Protected and it is not in the
per-mod override set; the managed mod is protected unless its place-at-root option is on or it is in
the override set. An explicit mod protection or a protected folder still beats the override, exactly
as it does now. The override set is renamed from "Heliosphere unprotect overrides" to cover both
tools, is still not persisted, and is still cleared by a rescan.

### Sort inside their folder

For a row whose tool is in SortInside mode, after the normal sort path is computed:

1. The **anchor** is the first segment of the mod's current path. A mod at the root has no anchor
   and is sorted normally.
2. If the anchor is one of the sorter's own folders (the names of every mod category such as Gear,
   Body, Hair and NPC, plus "Animation and VFX" and "Review"), the mod is sorted normally. This is
   what prevents `Gear/Gear/...`.
3. If the normal path already starts with the anchor (case-insensitive), it is used as it is.
4. Otherwise the path becomes `<anchor>/<normal path>`, for example
   `Proteus Upscales/Gear/Rue+/Top/Name`.

The prefix is applied before collision disambiguation and pinning, so those see the final path.
Group by Creator works the same way. Template apply and Manual assign are explicit user actions and
may still move such a mod out of its folder.

### The Proteus managed mod

A checkbox "Place the Proteus managed mod at the root" (off by default). Off: the mod is protected.
On: it is not protected and is proposed at the root, under its own name, whatever the sort strategy
would have done; a name collision at the root is disambiguated like any other.

## Protect tab

- The "Toggle Heliosphere protection" button is replaced by a **Heliosphere mods** mode dropdown and a
  **Proteus mods** mode dropdown, each with a count of the mods it covers, such as "(12 Proteus
  mods)". Each is hidden while the library has none of that tool's mods.
- Under Proteus: the "Place the Proteus managed mod at the root" checkbox, shown only when the managed
  mod exists.
- Protected tool mods keep their note, "(Heliosphere)" or "(Proteus)", and Proteus mods sort next to
  the Heliosphere ones at the top of the list.
- "Toggle protect all" is unchanged: it still sets every mod, using the override set for tool mods.
- The mode dropdowns are greyed while another operation is running, like every control that stages
  changes.

## Code changes

- `ProteusDetector` (pure) and `ManagedBy` on the row; `ScanProcessor` sets it from the author and
  directory name it already has.
- `ToolMode` and the two config values.
- `OrganizerState`: the rule, the renamed override set, and the sort-inside prefixing as a pure helper
  (`ToolFolders.ApplyAnchor(currentPath, normalPath)`) so every edge case is a unit test.
- Protect tab: the dropdowns, count text and managed-mod checkbox. Help entries for the new controls.
- `PenumbraOperationsAdapter` and the Apply-time live read: no change. The live snapshot still records
  Heliosphere by its own signals; Proteus needs nothing there because Apply respects the staged rows.
- The export dump gains a `ManagedBy:` line, and the diagnostic header counts each tool's mods.

## Out of scope

- Detecting Proteus mods that keep an imported author.
- Making Template apply or Manual assign respect the modes.
- Reading Heliosphere's behaviour from code; its safety rests on the author's experience, and the
  Protected default keeps today's behaviour for anyone who does not change it.

## Testing

Unit tests: `ProteusDetector` (exact author, case-sensitive, managed directory, ordinary mod);
`ToolFolders.ApplyAnchor` (normal anchor, root mod, anchor is a sorter folder, path already starts
with the anchor, case differences, nested anchors, empty strings); the protection rule for every mode
and both tools, the override set, the managed mod, and a folder-protected tool mod; a sort under each
mode and each Group by strategy, including collisions; config round trip, default `Protected`, and an
unknown value; that a rescan clears the override and keeps the mode.

In game (the author), once a Proteus-made mod exists or a `meta.json` is hand-edited to author
`Proteus`:
1. Defaults: both tools Protected, nothing moves.
2. Sort inside: a mod in `Proteus Upscales` proposes `Proteus Upscales/Gear/...`; a mod already in
   `Gear` just sorts normally.
3. Treat as normal moves them like any other mod; a rescan keeps the chosen mode.
4. The managed mod stays put until its checkbox is ticked, then proposes the root.
5. A Heliosphere update after sorting inside still works and leaves the mod where it was filed.
