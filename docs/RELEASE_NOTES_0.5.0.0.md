# Penumbra Organizer Plugin v0.5.0.0

## Changes since v0.4.0.3

### New: Apply runs on a crash-safer execution engine

Apply no longer runs as one long synchronous call that could freeze the game while it worked
through a large batch of mods. It now runs as a frame-budgeted background operation: the plugin
processes a small slice of the move plan each game frame instead of all at once, so the game stays
responsive even on a large Apply.

Every Apply now also checkpoints its progress to disk (`journal.json`/`plan.json`/`snapshot.json`
per operation, under the plugin's config folder) as it goes. If the game crashes or the plugin is
unloaded mid-Apply, the record of exactly what was attempted and what succeeded survives. That's
the foundation an upcoming release will build an actual "resume/undo an interrupted Apply" flow on
top of (see Known Issues below for where that stands today).

Restore is **not** on this new engine yet: it still runs on the same synchronous path as before.
That's deliberate, not an oversight. Restore gets its turn on the new engine in a follow-up release.

### Fixed: Folder Cleanup missing newly-emptied folders after Apply

After an Apply moved mods out of a folder, Folder Cleanup could keep reporting that folder as still
occupied. Two separate, now-fixed issues caused this:

- The plugin's own record of each mod's current location wasn't refreshing after the new async
  Apply finished, so Folder Cleanup was checking against stale data.
- Separately, Penumbra itself doesn't always write its folder-tree file to disk immediately after a
  move. A new reminder now appears after every successful Apply pointing you to Penumbra's own
  **Rediscover Mods** button, which is what actually flushes that change.

### New: flat variants for the combined sort strategies

**By Type Then Creator** and **By Creator Then Type** always split Gear into its detailed slot
subfolders (Gear/Feet, Gear/Head, etc.) with no way to turn that off, which was inconsistent with
the plain **By Mod Type** sort, which already had both a flat and a detailed button. Both combined
sorts now have a **(Detailed)** variant alongside the default flat one, matching that existing
convention.

### Quality-of-life fixes

- The Protect tab now shows how many of your Heliosphere-managed mods are currently protected, and
  sorts them to the top of the mod list.
- The Search tab now notes that Penumbra 1.7+ has its own native search syntax (`c:`, `t:`, `a:`,
  etc.) covering similar ground.
- Long button rows and hint text on the Sort and Search tabs now wrap properly instead of spilling
  past the window edge on a narrower window.
- The window title now shows the plugin's full version number. A display bug meant every patch
  release since v0.4.0.1 showed identically as "v0.4.0" regardless of which one was actually running.

## Known issues

- **An Apply that gets interrupted by a crash or a mid-operation Penumbra IPC outage has no
  automatic recovery flow yet.** This is rare (normal Apply usage won't hit it) and it isn't
  destructive (nothing is lost; your pre-Apply snapshot is already sitting in the History tab).
  Right now you'd need to reload the plugin and, if anything looks off, manually Restore from
  History. A real recovery dialog (resume, restore, or keep as-is) is planned for a future release.

Full technical detail on everything above is in this repo's commit history and
[docs/ROADMAP.md](ROADMAP.md).
