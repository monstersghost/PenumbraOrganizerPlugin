# Penumbra Organizer Plugin v0.5.3.0

## Changes since v0.5.2.0

**This is a testing build.** You are seeing it because you opted in to plugin testing builds.

### Fixed: the plugin was asking Penumbra for your mod list from the wrong place

When you pressed Refresh mod list, the plugin requested Penumbra's live mod data from the game's
drawing work rather than from the update work its own design specifies. Those reads now happen where
they were always meant to, alongside the rest of the plugin's Penumbra work. This release covers two
paths: Refresh mod list (Scan) and building the Search index.

**This is unlikely to fix the reports of the game closing instantly, and the honest answer is that
the cause is still unknown.** An earlier version of these notes said it "may" fix them. That was
based on the idea that Penumbra could be changing its mod list at the same moment the plugin read
it. That idea has since been measured and does not hold: the game's drawing work and its update
work run on the same thread, so they take turns rather than overlapping, and the plugin's read
cannot be disturbed part-way through by Penumbra's own update. The old placement was still wrong by
the plugin's own design, and is now correct, but it should not be sold as a crash fix.

Other actions that talk to Penumbra - Restore, its preview popup, Create Backup, Apply, Folder
Cleanup, and workbook Export/Import - still read Penumbra from the drawing work. Those are unchanged
here, and given the above there is no longer reason to think that placement is what closes the game.

If the game still closes on you, the logging below is what will actually help.

### Added: the plugin leaves a trail in the log

A scan used to write nothing to the Dalamud log, so a crash report could not show whether a scan had
even started. Each scan and index build now records what it is doing as it goes.

If the plugin is involved in a future crash, `dalamud.log` will show how far it got. If you hit
something like this, that is the file worth sending.

### Correction to the 0.5.2.0 notes

Version 0.5.2.0 listed a known issue claiming that a mod with an unusual name could cause problems
when drawn. That was investigated and is wrong: the text drawing involved does not interpret mod
names in the way that entry assumed. There is no such hazard, and there never was. Apologies for the
noise.

## Known issues

- Applying a large plan moves every mod one at a time, and Penumbra announces each move on its own
  schedule. The automatic rescan that follows an Apply can therefore see those announcements
  arriving late and decide its own results are stale. If that happens you will be told the mod list
  changed immediately after an operation you just watched succeed. Nothing is wrong with the result,
  and running the scan again will work, but the message is misleading.

## For developers

Materialization moved from `Start` (called in the ImGui draw callback) to the first coordinator
`Update` (the framework-update callback). `Start` itself no longer materializes anything, so the draw
callback structurally cannot do this Penumbra read regardless of what calls it later - that is the
actual guarantee. The staleness epoch is captured immediately before the snapshot rather than after,
so a mutation during materialization invalidates the run instead of becoming its baseline.

The previous draft of this section guessed that the draw callback "likely runs on the same thread"
as the framework update. That has now been measured, and it does. A probe logging
`Environment.CurrentManagedThreadId` and `IFramework.IsInFrameworkUpdateThread` from both callbacks
reports the same managed thread id from each, with `IsInFrameworkUpdateThread` **true in both**.

Two consequences worth writing down:

- The injected predicate asserted at the top of `Update` cannot distinguish a draw-callback caller
  from a framework-update caller, because it is true in both. It remains a check against a genuine
  background-thread caller and nothing more. Do not read it as enforcing the draw/update boundary.
- Materialization did not move from one thread to another - it moved to a different point on the
  same thread. It is still unbounded, so this is a stall relocated within a frame, not a stall
  removed, and not a thread hop. Making it incremental remains a separate concern.

This build carries that probe. It logs one line per callback, once per session, at Information
level. It exists to confirm the same result on other machines and is intended to be removed
afterwards.

908 tests pass on this release.
