# User Guide

How to use the Penumbra Organizer plugin, tab by tab. For what the plugin is and why it exists,
see the main [README.md](../README.md). For the classification and sort logic mentioned below,
see [HOW_SORTING_WORKS.md](HOW_SORTING_WORKS.md).

## Getting the plugin

Most users should install through the self-hosted plugin repository described in
[README.md](../README.md#installing). That's the supported path, and it auto-updates. If you're
building from source or loading a local dev build instead, see
[DEVELOPMENT.md](DEVELOPMENT.md).

## Requirements

Dalamud, via XIVLauncher. Penumbra installed and enabled: every tab depends on Penumbra's IPC
being available, and if Penumbra isn't running, Refresh mod list shows a connection error instead
of a mod list.

## The six tabs

### Scan

Refresh mod list re-reads every installed mod from Penumbra (`GetModList`) along with its full
changed-items set (`GetChangedItems`), then classifies each one. See
[HOW_SORTING_WORKS.md](HOW_SORTING_WORKS.md) for how classification works. Run this first, and run
it again whenever mods are installed, removed, or moved outside the plugin. Nothing else on this
tab, or any other tab, updates automatically.

The scan runs in the background, so the game keeps responding while it works. A progress line
replaces the button while it's running, with a Cancel button next to it. Cancelling leaves your
previously loaded library exactly as it was.

Anything that would collide with a running scan is disabled while it runs, with a note explaining
why: Apply, Restore, Create Backup, Folder Cleanup, the sort strategy buttons, and Import Workbook.

If you add, remove, or move a mod in Penumbra while a scan is running, its results no longer match
your library, so they're discarded rather than shown. You'll be told the mod list changed and asked
to run it again.

The mod count and tree view below the button show what's currently loaded. A live event log at the
bottom reports `ModAdded`, `ModDeleted`, and `ModMoved` events as Penumbra reports them. This log
is informational only; it doesn't trigger a re-scan.

### Protect

A search box filters both lists below by mod name, author, identifier, current path, or folder
path. Clear it to see everything again.

**Folders** lists every folder your mods currently occupy, plus any folder you've protected that's
now empty. Checking a folder protects it and everything under it, at any depth. Checking `Gear`
protects `Gear/Feet`, `Gear/Feet/Sub`, and so on, without checking each subfolder individually.
Folders already covered by a protected ancestor show a note explaining which ancestor covers them,
instead of their own checkbox state being meaningful. This list is resizable: drag the thin bar
just below it up or down.

**Mods** lists every scanned mod with a checkbox, in its own scrollable area below the Folders
list. A protected mod is excluded from every sort strategy and from Apply: its proposed path
always stays equal to its current path, and if anything does touch a protected mod, Review Changes
flags it as a "protected mod changed" error. A mod protected by something other than its own
checkbox (a folder, or Heliosphere) shows a note next to it explaining why.

Toggle protect all is a true toggle: it protects everything if anything is currently unprotected,
and unprotects everything otherwise. Toggle Heliosphere protection does the same, scoped to
[Heliosphere](https://heliosphere.app/)-managed mods (detected by the `hs-` directory prefix, or
by a `heliosphere.json` file in the mod folder; see [TECHNICAL_SPEC.md](TECHNICAL_SPEC.md)).
Heliosphere mods are re-protected on every scan no matter how that toggle was last left, because
Heliosphere owns their location and the plugin never proposes moving them during Sort or Apply.
Manually unprotecting a non-Heliosphere mod does persist across scans; that choice, along with your
protected folders, is saved to the plugin's own config.

Protection governs Sort and Apply only. Restoring a snapshot from the History tab reproduces its
recorded paths regardless of current protection. See History below.

### Sort

Five buttons compute a proposed folder path for every unprotected mod, without moving anything
yet. Only Apply, on the Review Changes tab, actually calls Penumbra:

- By Creator: `{Creator}/{ModName}`
- By Mod Type: `{Category}[/{SubCategory}]/{ModName}`
- By Mod Type Detailed: like By Mod Type, but Gear mods are further split by equipment slot
  (`Gear/Feet`, `Gear/Head`, and so on) wherever the slot can be determined from a single,
  unambiguous match; anything else falls back to the plain `Gear` folder.
- By Type Then Creator: `{Category}[/{SubCategory}]/{Creator}/{ModName}`
- By Creator Then Type: `{Creator}/{Category}[/{SubCategory}]/{ModName}`

A mod with no resolvable creator, category, or both, falls back to `Review/{ModName}`, so it's
easy to find and sort by hand instead of landing somewhere unexpected.

Import Workbook opens a file picker for an `.xlsx` file, exported either from this plugin or the
standalone app, and applies its per-mod destination folders as proposed paths. Use this instead of
the five built-in strategies when you want to hand-edit an assignment list in Excel first. A
summary line, plus any errors or warnings, appears below the button once the import finishes.

Refresh NPC list from wiki updates the bundled NPC, enemy, and boss name list by scraping the
FFXIV wiki. A full run can take a few minutes, and the button disables itself while one is in
flight. This step is optional: a small seed list ships with the plugin, and NPC-name
classification works from the very first scan without ever clicking this button. Refreshing only
widens name coverage over time. Results are shown per category, with an added-name count for each,
or a failure reason if a category's scrape didn't complete.

The bottom of this tab is manual assignment. Its own search box filters the mod list below it, the
same way the Protect tab's does. Check the mods you want (protected mods never appear in this
list), type a destination folder, and click "Assign N selected mods": each checked mod's proposed
path becomes `{destination}/{ModName}`. A summary line reports how many were assigned and how many
were skipped (for example, because a mod stopped being eligible after you checked it, or you left
the folder box empty). Your checked selection survives changes to the search box, so you can filter
down, check some mods, clear the filter, and check more without losing what you already picked.

### Review Changes

Shows the result of `Validate()`. Any protected-mod-changed or path-collision errors are listed in
red. The tree view below shows current and proposed paths side by side, so you can review every
change before committing to it.

Export writes every mod's full field set, plus the validation result, to a plain-text file
(`organizer-export.txt`, in the plugin's config directory, overwritten each time). If any of your
mods are Gear, it also includes a one-line summary of how many resolved to a single slot, how many
were ambiguous (a real multi-piece outfit), and how many had no equipment evidence in their config
files at all, which is useful for figuring out why a mod didn't land where you expected under By
Mod Type Detailed. It's a quick snapshot to review, not something meant to be re-imported later.

Export Workbook writes an `.xlsx` file (`organizer-workbook.xlsx`) using whichever strategy is
selected in the dropdown as the suggested-destination column. It's interoperable with the
standalone app's own workbook feature: edit the file in Excel, then use Import Workbook on the Sort
tab to bring your edits back in.

Show Config File opens Explorer with the plugin's own settings file (your protected mods and
protected folders) pre-selected, so you can copy or attach it directly. Create Diagnostic Dump
writes a plain-text state summary (mod and protection counts, the last Apply/Restore/Folder
Cleanup result, including one from a previous session if nothing's run yet this session, and the
session's event log) to `organizer-diagnostics.txt`, then a button next to it opens Explorer
with that file pre-selected.

Protect & Skip All Blocking Mods is a one-click fix for validation errors. Every mod currently
blocking Apply, whether it's protected-but-changed or on the losing side of a collision, gets its
proposed path reset to its current path and gets protected. Apply becomes available again without
you having to track down each offender by hand.

Apply is disabled while any validation issue exists. When it's enabled and you click it, a
confirmation popup shows how many mods will move; confirming calls Penumbra's `SetModPath` for
every mod whose proposed path differs from its current path. The result shows a succeeded and
failed count, with Penumbra's own error code as the reason for each failure (for example
`PathRenameFailed`). Apply also captures a snapshot of your library just before it runs, so you can
always get back to where you started. See History below for how to use it.

Below a separator, the Orphaned Folders section appears once you've scanned at least once. It
detects folder entries left behind in Penumbra's own `organization.json` after a mod moved out of
them. A "Re-read organization.json" button and a "Last read" timestamp sit above the list. This
re-reads the file from disk; it does not ask Penumbra to refresh its own live folder tree. So if
the count looks wrong right after a big Sort or Restore, try moving one folder in Penumbra's own UI
(or clicking Penumbra's Rediscover Mods) and re-reading again. Plain, empty folders are pre-checked;
folders that still carry your own customization (an icon, color, or name override) are shown
unchecked, for you to review before pruning. Clean Up Selected Folders removes the checked entries,
with its own backup and rollback pair, separate from Apply's history. After cleanup, a banner
reminds you to click Rediscover Mods in Penumbra's own settings, since this plugin has no IPC call
that can trigger that reload itself.

### History

Every Apply and every Restore automatically captures a snapshot of your library just before it
runs, so both are undoable. You can also click Create Backup at any time, with an optional label,
to save a snapshot on demand.

Each entry in the list shows when it was captured, its label (or an auto-generated description like
"1723 mods moved"), and how many mods it covers. Clicking Restore opens a confirmation popup with an
exact preview: how many mods will move, how many are already at their snapshot path, how many
(installed since the snapshot was taken) will be relocated to the Penumbra root, and how many
(uninstalled since) will be skipped. If any mods in that preview are currently protected or
Heliosphere-managed, a warning line tells you how many of those will move anyway.

Restore reproduces a snapshot's recorded paths exactly for every mod it covers, regardless of
current protection or Heliosphere status. A snapshot is a record of history, and current sort
protection shouldn't be able to stop you from getting back to it. Only two things stop a mod from
being restored: it's no longer installed (reported as skipped), or Penumbra's own `SetModPath`
rejects the move (reported as failed, with Penumbra's error code). Confirming a Restore also
captures its own pre-restore snapshot first, so a restore is itself undoable.

Delete removes a snapshot from the list; restoring to any other snapshot still works normally
afterward. History persists across game restarts.

### Search

A read-only reverse lookup: find every installed mod, enabled or not, by the game items it changes.
It's independent of the Sort tab. It builds its own index and never moves, protects, or otherwise
modifies anything.

Build/Refresh Index scans your installed mods and their changed-items sets into a searchable index.
This is separate from the Scan tab's own scan, so click it once here before searching, and again
after installing or removing mods. A summary line reports what was indexed (mod count, total changed
items, and a gear-slot breakdown); the index's build time is shown beneath it.

Like the Scan tab's scan, the index build runs in the background with a progress line and a Cancel
button, and is discarded if your mod list changes while it's running.

Two text boxes narrow the results: "Mod name contains" matches against the mod's name, and "Item
contains" matches against the names of the items it changes. The Categories row filters by mod
category (with an Unknown toggle for mods that didn't classify). When Gear is among the selected
categories, a Slots row appears to filter Gear mods by equipment slot, plus an Unresolved toggle
for Gear mods whose slot couldn't be determined.

Results show in two panes. The left pane lists the matching mods as `Name (Author)`; click one to
select it. The right pane lists that mod's changed items. If a mod matched only because of the "Mod
name contains" box and not because of any item, a red "Matched by mod name, not by item" note
appears above its item list, so you can tell a name hit from an item hit.

## Typical workflows

First-time sort: Scan, pick a Sort strategy, review the proposed paths on Review Changes, then
Apply.

New mods installed since the last sort: Scan again (this picks up the new mods; previously-set
protection carries over), re-run a Sort strategy, review, then Apply.

Hand-tuning through Excel: on Review Changes, Export Workbook, edit destinations in Excel, then on
the Sort tab Import Workbook, review again, then Apply.

Undoing an Apply, or going back further: open the History tab and Restore the snapshot from just
before the Apply (or any earlier one). Protection doesn't block this, so it works even for mods
you've since protected.

After moving mods around inside Penumbra directly: Scan, check the Orphaned Folders section on
Review Changes, Clean Up Selected Folders, click Rediscover Mods in Penumbra, then Scan again to
confirm.

Finding which mod changes a given item: open the Search tab, Build/Refresh Index, type part of the
item name into "Item contains", then click a matching mod to see its full changed-items list.

## Where files live

Everything the plugin writes lives under Dalamud's per-plugin config directory:
`organizer-history.json` for rollback snapshots (Apply, Restore, and manual backups all share this
one multi-snapshot history), `organizer-folder-backup.json` for Folder Cleanup's own separate
rollback data, `organizer-export.txt` for the plain-text Review export, `organizer-diagnostics.txt`
for the diagnostic dump, `organizer-workbook.xlsx` for the last exported workbook, and
`npc-name-list.json` for the NPC/enemy/boss name list, which is seeded on first run and updated in
place by Refresh. Your protected mods and protected folders live in the plugin's own Dalamud
settings file (reachable via Show Config File on the Review Changes tab), not in this directory.
The plugin never writes inside Penumbra's own mod storage, with one exception covered in
[TECHNICAL_SPEC.md](TECHNICAL_SPEC.md): Folder Cleanup's edits to `organization.json`.

## Known caveats

See [ROADMAP.md](ROADMAP.md) for the current status. The child-race-variant NPC classification fix
is implemented but not yet confirmed against a real, in-game mod library. The NPC wiki-refresh path
has no automated test coverage. Folder Cleanup detection can appear stale immediately after a large
Sort or Restore. See the Review Changes section above for why, and for how to force a re-check.
