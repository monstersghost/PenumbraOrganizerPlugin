# Penumbra Organizer Plugin v0.5.3.1

## Changes since v0.5.2.0

This release includes everything from 0.5.3.0, which was offered as a testing build only. If you
were on 0.5.2.0, you are getting both.

### Fixed: we found what was closing the game

Some players had the game close instantly to desktop, with no error, when pressing Refresh mod list
or Build/Refresh Index. We now know what sets it off: **pressing "Refresh NPC list from wiki"**.

That button downloads NPC, enemy and boss names from the FFXIV wiki to improve how mods are sorted.
It added to the list rather than replacing it, and a full refresh produces around 21,000 names.
Somewhere past that size, the plugin's name matching becomes fatal to the game rather than merely
slow. Anyone who pressed that button had a plugin that would close the game on the next scan, and
the file survived restarts, so it kept happening until the plugin's data was reset.

Three things change here:

- **If your saved NPC list is oversized, the plugin now ignores it and uses the small built-in list
  instead.** Your old file is kept alongside it with `.oversized-` in the name, and is replaced so
  the problem cannot come back on the next scan. If you were affected, updating is enough; you do
  not need to reset anything by hand.
- **"Refresh NPC list from wiki" is switched off** for now, so nobody else can trigger this. The
  button is still visible with a note explaining why.
- NPC sorting still works. It uses the list that ships with the plugin, which is small and safe.

Honest limitation: we know what triggers this and we can reproduce it, but we do not yet know
*why* a large list kills the game rather than just slowing it down. Measured outside the game, that
list is wasteful but survivable. The fix works by not building the thing that correlates with the
crash, which is a real fix for you and an unfinished answer for us.

### Fixed: your Detailed sort was quietly getting worse

The same oversized list explains a second problem. With 21,000 names loaded, almost any mod title
accidentally matched *something*, so gear mods were being filed as NPC mods. If you ever wondered
why "By Mod Type Detailed" stopped splitting your gear into `Gear/Feet`, `Gear/Head` and so on, that
was why. It sorts correctly again.

### Changed: the plugin reads Penumbra from the right place

*(This was in the 0.5.3.0 testing build.)*

When you pressed Refresh mod list, the plugin asked Penumbra for your mod data during the game's
drawing work rather than during its update work, which is not what its own design specifies. Those
reads now happen where they were meant to, for both Refresh mod list and the Search index.

This was originally thought to be the crash. It is not, and these notes are not selling it as one:
the two callbacks run on the same thread and take turns, so a read cannot be disturbed part-way
through. The old placement was wrong regardless and is now right.

### Added: the plugin leaves a trail in the log

*(Also from the 0.5.3.0 testing build.)*

A scan used to write nothing to the Dalamud log, so a crash report could not even show whether a
scan had started. Each scan and index build now records its progress. This is what identified the
crash above.

If the plugin is ever involved in a crash, `dalamud.log` is the file worth sending.

## Known issues

- Applying a large plan moves every mod one at a time, and Penumbra announces each move on its own
  schedule. The automatic rescan that follows can therefore see those announcements arriving late
  and decide its own results are stale. If that happens you will be told the mod list changed
  immediately after an operation you just watched succeed. Nothing is wrong with the result, and
  running the scan again will work, but the message is misleading.

## Coming next

Version 0.6.0 replaces the name matching entirely with something that cannot grow to that size, adds
a curated list of characters worth sorting by, and brings the wiki refresh back as an opt-in once we
can verify it is safe. It also consolidates the seven sort buttons into a dropdown, adds hover
explanations, an in-game Help tab and a first-run walkthrough.

## For developers

An oversized `npc-name-list.json` is now backed up and replaced with the bundled seed at load time
rather than merely ignored, so the condition cannot re-arm on every later run. The wiki refresh
merged additively, which is why the file grew without bound; 0.6.0 replaces that with snapshot
semantics.

912 tests pass on this release.
