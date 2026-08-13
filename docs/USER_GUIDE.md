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

## The seven tabs

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
why: Apply, Restore, Create Backup, Folder Cleanup, the Sort controls, and Import Workbook.

If you add, remove, or move a mod in Penumbra while a scan is running, its results no longer match
your library, so they're discarded rather than shown. You'll be told the mod list changed and asked
to run it again.

If the mod list changes while the plugin is taking its initial snapshot, the scan stops straight away
and asks you to run it again, rather than spending time on a snapshot that may already be out of
date.

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
list. A protected mod is excluded from every grouping and from Apply: its proposed path
always stays equal to its current path, and if anything does touch a protected mod, Review Changes
flags it as a "protected mod changed" error. A mod protected by something other than its own
checkbox (a folder, or Heliosphere) shows a note next to it explaining why.

Toggle protect all is a true toggle: it protects everything if anything is currently unprotected,
and unprotects everything otherwise. Toggle Heliosphere protection does the same, scoped to
[Heliosphere](https://heliosphere.app/)-managed mods (detected by the `hs-` directory prefix, by
Heliosphere's `[HS] ` name prefix, by a `heliosphere.json` file in the mod folder, or by having been
recognised as Heliosphere's on any earlier scan; see [TECHNICAL_SPEC.md](TECHNICAL_SPEC.md)). That
last one matters during a Heliosphere update, which briefly removes the mod's folder and writes a new
one — without it, a scan landing in that moment would see an ordinary mod and offer to move it.
Heliosphere mods are re-protected on every scan no matter how that toggle was last left, because
Heliosphere owns their location and the plugin never proposes moving them during Sort or Apply.
Manually unprotecting a non-Heliosphere mod does persist across scans; that choice, along with your
protected folders, is saved to the plugin's own config.

Protection governs Sort and Apply only. Restoring a snapshot from the History tab reproduces its
recorded paths regardless of current protection. See History below.

If any protected mods are sitting at the very top level of your library, outside every folder, the
tab says so. That combination is a dead end worth knowing about: protection means the plugin never
proposes a path for a mod, so sorting will never file those away and they stay at the top level
indefinitely. Heliosphere installs new mods there and they are protected by the same scan that first
notices them, so this can happen without you doing anything. Untick one and it sorts like any other
mod.

### Sort

Choose a grouping, tick whichever splits you want, and press Sort. That computes a proposed folder
path for every unprotected mod without moving anything yet. Only Apply, on the Review Changes tab,
actually calls Penumbra.

**Group by** picks the shape of the path:

- Creator: `{Creator}/{ModName}`
- Mod type: `{Category}[/{SubCategory}]/{ModName}`
- Type then creator: `{Category}[/{SubCategory}]/{Creator}/{ModName}`
- Creator then type: `{Creator}/{Category}[/{SubCategory}]/{ModName}`

**Split gear by equipment slot** puts gear mods in `Gear/Feet`, `Gear/Head` and so on, wherever the
slot can be determined from a single, unambiguous match. Anything ambiguous or unidentifiable falls
back to the plain `Gear` folder.

**Split NPC mods by kind** puts NPC mods in `NPC/NPCs`, `NPC/Bosses` or `NPC/Enemies`. Turning it
**off puts every NPC mod straight into `NPC`** with no subfolder. That combination did not exist in
earlier versions, where NPC mods were always subdivided — if you preferred the old behaviour, leave
this ticked, as it is by default.

Both checkboxes are greyed out when Group by is set to Creator, because grouping by creator alone
never looks at a mod's type.

A mod with no resolvable creator, category, or both, falls back to `Review/{ModName}`, so it's
easy to find and sort by hand instead of landing somewhere unexpected.

If you change the grouping or either checkbox after sorting, a line appears reminding you that the
selection no longer matches what was sorted. Press Sort again to bring the proposals up to date.

Import Workbook opens a file picker for an `.xlsx` file, exported either from this plugin or the
standalone app, and applies its per-mod destination folders as proposed paths. Use this instead of
the built-in groupings when you want to hand-edit an assignment list in Excel first. A summary
line, plus any errors or warnings, appears below the button once the import finishes.

**Also use the NPC list scraped from the wiki** is turned off and unavailable in this version. NPC
name recognition works from a curated list that ships with the plugin, which needs no setup and no
network access. The opt-in exists for a much larger list scraped from the FFXIV wiki; it recognises
far more character names at the cost of matching some very common words, and it stays disabled
until that has been verified in-game.

Refresh NPC list from wiki is likewise turned off in this version. When enabled, it rewrites the
opt-in scraped list — not the bundled one — with whatever the wiki returns on that run, so names
the wiki no longer lists are dropped rather than accumulating forever. Results are shown per
category as a plain name total, or a failure reason if a category's scrape didn't complete; a
category whose scrape fails keeps whatever it already held rather than being emptied.

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

Export Workbook writes an `.xlsx` file (`organizer-workbook.xlsx`). It's interoperable with the
standalone app's own workbook feature: edit the file in Excel, then use Import Workbook on the Sort
tab to bring your edits back in.

The Workbook destinations dropdown decides what goes in the workbook's Destination column. The four
sorting choices fill it with suggested folders. Keep current folders (as-is) fills it with each
mod's folder as it stands right now, which is what you want when you intend to write the layout
yourself in Excel rather than start from a suggestion. Protected mods and mods sitting at the top
level of your library get a blank Destination, which means "leave this one alone" on import.

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

### Templates

A template is an organization layout someone else shared as a `.json` file. Importing one
proposes where your mods would go: mods you and the template's author both have land in the
folder they chose, and everything else is placed by the fallback sort strategy they picked, using
their folder names.

1. Get a template file from whoever shared it and click **Import template file...**, or drop the
   file into the templates folder yourself (**Open templates folder** shows you where) and click
   **Refresh list**.
2. Select it in the list to see who made it and what it is for.
3. Click **Preview against my library**. You get a count of how many of your mods the template
   matched, how many its fallback strategy placed, and a browsable tree of the resulting folders
   with the number of mods in each.
4. If it looks right, click **Apply this template to my proposals**, then open **Review Changes**
   to check the result and apply it like any other sort.

Nothing is written to Penumbra until you apply from Review Changes, exactly as when you sort from
the Sort tab.

A few things worth knowing:

- **A template never sees your mod list.** It matches on mod names, so it only affects mods you
  already have. Mods its author never had are placed by the fallback strategy, not left behind.
- **Matching is on the mod's name**, ignoring case, install suffixes like `_1_1_0`, and bracketed
  tags. A mod you renamed will not match, and will be placed by the fallback strategy instead.
- **Protected mods are never moved**, the same as with any sort.
- If you rescan your library after previewing, the preview is discarded — preview again before
  applying.

#### Sharing your own layout

**Export my layout as a template...** builds a template from your library.

Read this part before you use it: **a template contains a list of your mod names, and anyone you
send it to can read that list.** That is the whole point of the format — it is how an importer knows
which of their mods you have an opinion about — but it means exporting is publishing. If any of your
mods are things you would not want a stranger, or a friend, reading off a list, take them out first.

That is what the review screen is for, and there is no way to export without going through it:

1. Click **Export my layout as a template...**. The screen opens with **everything included**, so
   what you see is your actual library.
2. Give it a name, and optionally your name and a description.
3. Pick the **fallback grouping** — where an importer's mods go when your template says nothing
   about them. These are the same choices the Sort tab offers.
4. Go through the folder and mod lists and untick anything you would rather not share. Unticking a
   folder also unticks the mods in it. The search box narrows what you are looking at; **Include
   all** and **Exclude all** always apply to every mod, not only the ones showing.
5. Then either **Save to templates folder**, which writes a `.json` you can send to anyone, or
   **Copy share code**, which puts the whole template on your clipboard as one line of text.

Some things worth knowing:

- **Export uses where your mods are now**, not any proposals sitting on Review Changes. If you have
  sorted but not applied, the template describes your *old* layout, because that is the one your
  library actually has. The screen says so.
- **Share codes only work for small templates.** A chat message holds about 2000 characters, which
  is roughly a hundred mods. Past that the button is disabled and the screen tells you the length —
  send the file instead.
- **Mods sitting loose at the top level are left out.** A template only carries folders, so a mod
  that is not in one has nothing to record. The screen tells you how many.
- If two of your mods have names that match once suffixes and tags are stripped, and they are in
  *different* folders, that name is left out and the screen says so — a template cannot say two
  things about one name. Exclude one of them to include the other.

### Help

Every explanation the plugin holds, at reading length rather than tooltip length. It is grouped into
collapsible sections: what the plugin does, what is and isn't safe, one section per tab, what to do
if an operation was interrupted, and where your files live.

Each tab's section lists that tab's controls with the same one-line explanation you get by hovering
them, so the Help tab doubles as a reference for anything you saw a tooltip on and want to find
again.

"Join the Discord for support" opens the project's Discord in your browser. That is the fastest
place to get help, and where testing builds are announced.

"Open the full guide in your browser" opens this document, pinned to the release you're running
rather than to the latest one, so it always describes the version you actually have.

"Show the walkthrough" reopens the guided first run, the five-step tour shown the first time you
open the plugin. It can be reopened as often as you like.

If you upgraded to 0.6.0 from an earlier version, that walkthrough runs once for you too. That is
deliberate rather than a mistake: 0.6.0 replaced the whole Sort control, changed how NPC names are
matched, and added this tab, so there is as much new for an existing user as for a new one. Dismiss
it and it will not return.

Nothing on this tab changes anything - it reads no mods and writes no files.

## Typical workflows

First-time sort: Scan, choose a grouping on the Sort tab and press Sort, review the proposed paths
on Review Changes, then Apply.

New mods installed since the last sort: Scan again (this picks up the new mods; previously-set
protection carries over), press Sort again, review, then Apply.

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
