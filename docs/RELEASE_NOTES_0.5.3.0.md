# Penumbra Organizer Plugin v0.5.3.0

## Changes since v0.5.2.0

### Fixed: the plugin was asking Penumbra for your mod list from the wrong place

When you pressed Refresh mod list, the plugin requested Penumbra's live mod data from the game's
drawing work rather than from the update work its own design specifies. Those reads now happen where
they were always meant to, alongside the rest of the plugin's Penumbra work. This release covers two
paths: Refresh mod list (Scan) and building the Search index.

This may address reports of the game closing instantly, with no error, on pressing Refresh mod list.
It is only "may". That crash could not be reproduced here and no crash dump was available for it, so
its cause remains unconfirmed. The behaviour was wrong regardless, and is now correct. Other actions
that talk to Penumbra - Restore, its preview popup, Create Backup, Apply, Folder Cleanup, and workbook
Export/Import - still read Penumbra from the drawing work the same way. That is a known follow-up, not
covered by this release; if you hit the same crash from one of those, it is not yet fixed.

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
actual guarantee. An injected predicate also asserts, at the top of `Update`, that it is running on
the framework thread; that is a secondary check against a genuine background-thread caller, not a
guarantee against the draw callback specifically, since the draw callback likely runs on the same
thread and the predicate cannot distinguish the two. The staleness epoch is captured immediately
before the snapshot rather than after, so a mutation during materialization invalidates the run
instead of becoming its baseline.

Materialization is still unbounded and now holds the framework thread instead of the render thread.
That is a different stall in a different place, not the absence of one. Making it incremental is a
separate concern.

908 tests pass on this release.
