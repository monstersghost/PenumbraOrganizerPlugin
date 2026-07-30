# Penumbra Organizer Plugin v0.5.2.0

## Changes since v0.5.1.1

This release is mostly about one thing: the plugin no longer freezes the game while it works on your
mod library. There is also one small addition to workbook export.

### Added: export the workbook exactly as your library stands

Workbook export always filled the Destination column with a suggested folder, based on whichever
sorting strategy you picked. There was no way to get a workbook that simply described your library
as it is, so anyone wanting to write the layout by hand had to pick a strategy they did not want and
then clear every cell it produced.

The dropdown now offers Keep current folders (as-is). Every mod's Destination is the folder it is
in right now, and importing that workbook back without editing it changes nothing.

### Fixed: Rediscover Mods froze the game on large libraries

Scanning used to run entirely inside the game's drawing code. Every mod had to be read, classified,
and sorted before the game was allowed to draw another frame. On a small library you would not
notice. On a large one the game stopped responding for as long as the scan took, and Windows would
eventually mark it as not responding. At least one player reported it as a crash.

Scanning now happens in the background. The game keeps drawing, you get a progress readout, and
there is a Cancel button if you change your mind.

The search index build had the same problem and got the same fix.

### Fixed: results could no longer be applied to a stale library

Because a scan now takes place over several frames, your mod list can change underneath it. If you
add, remove, or move a mod in Penumbra while a scan is running, the results would have been built
against a library that no longer exists.

The plugin now watches for that and throws the results away rather than showing you a plan that
does not match reality. You will be told the mod list changed and asked to run it again.

Results are also swapped in all at once when they are ready, so there is no window where the plugin
is holding half of an old scan and half of a new one.

### Changed: things that would conflict with a running scan are now disabled

While a scan or index build is running, the buttons that would collide with it are greyed out and
tell you why. That covers Apply, Restore, Create Backup, Folder Cleanup, the sort buttons, and
Import Workbook.

The sort buttons and Import Workbook are worth calling out. Previously they stayed live during a
scan, and a scan finishing would silently replace the results underneath a sort you had just
staged. Your work would disappear with no explanation.

### Fixed: the event log could corrupt itself

The live event log on the Scan tab was being written from Penumbra's callbacks and read by the UI
at the same time, with nothing keeping the two apart. This was rare and hard to trigger, but it was
a genuine hazard and is now safe.

### Changed: the plugin now notices when Penumbra unloads

The plugin subscribes to a set of Penumbra events. It was not listening for Penumbra's own shutdown
notification, so unloading Penumbra while the organizer was still running left it holding
subscriptions to something that had gone away. It now listens and cleans up.

## Known issues

- Applying a large plan moves every mod one at a time, and Penumbra announces each move on its own
  schedule. The automatic rescan that follows an Apply can therefore see those announcements
  arriving late and decide its own results are stale. If that happens you will be told the mod list
  changed immediately after an operation you just watched succeed. Nothing is wrong with the
  result, and running the scan again will work, but the message is misleading.
- Mod names are drawn through a formatting function that treats certain characters specially. A mod
  with an unusual name could in principle cause problems. This has not been observed and is on the
  list to fix.

## For developers

Background work goes through a three-phase pattern: read the game's state on the framework thread,
compute on a background thread, publish the result back on the framework thread. The computing half
lives in a namespace with an enforced test that no Dalamud or Penumbra type can be referenced from
it, so it stays testable without a running game.

883 tests pass on this release.
