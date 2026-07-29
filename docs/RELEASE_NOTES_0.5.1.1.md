# Penumbra Organizer Plugin v0.5.1.1

## Changes since v0.5.1.0

This is a maintenance release. There are no new features and nothing should look different day to
day. It replaces the way the plugin tracks whether an operation is running, which had grown into
three separate mechanisms that could disagree with each other.

### Fixed: a failed Apply could leave the plugin refusing everything until you reloaded

Apply does a lot of work before it actually starts moving anything: it validates your proposed
paths, checks for folder collisions, reads your live mod list, and captures a rollback snapshot. If
any of that failed, the plugin could be left believing an operation was still running. Every other
action, Restore, Create Backup, Folder Cleanup, and Apply itself, would then refuse to start, and
the only way out was reloading the plugin.

The most likely way to hit this was a proposed path colliding with a leftover folder entry in
Penumbra's own `organization.json`, which is exactly the case the plugin already warns you about.
You would get the warning, and then find yourself unable to do anything about it.

That can no longer happen. A failed preparation now always releases cleanly, and Apply is
immediately usable again.

### Fixed: the History tab could show a stale list

Applying or restoring saves a snapshot before it begins, so the History tab should show a new entry
afterwards. In a few situations it kept showing the old list until something else happened to
refresh it. The most reliable way to see it was resolving an interrupted operation with Keep Current
State, where the snapshot had definitely been written but the list never caught up.

History now refreshes whenever a snapshot is written, not only when an operation finishes normally.

### Changed: finishing an operation no longer waits for you to look at the right tab

When an Apply or Restore finished, the plugin only noticed once you were looking at the tab that
started it. If you kicked off an Apply and switched to the Protect tab to do something else, the
mod list would stay stale until you wandered back.

Completion is now handled the moment it happens, whatever you are looking at. The Rediscover Mods
reminder still appears on the Review Changes tab, since that is where it is relevant, and it still
appears exactly once.

### Changed: Delete in the History tab is disabled during recovery

If an interrupted operation is waiting to be resolved, the Delete button on a history snapshot is
now greyed out, matching Create Backup and Restore, which already behaved this way. Previously it
stayed clickable and simply failed with an error message.

### Removed: leftover code from before the current engine

Two obsolete internal paths for Apply and Restore, superseded when those moved onto the crash-safe
engine in v0.5.0.0 and v0.5.1.0, have been deleted along with a Restore results panel that could
never actually appear. None of this was reachable in normal use.

## Verification

The automated test suite covers 820 cases, all passing, including a new set written specifically to
pin the previous behaviour so this rewrite could be checked against it rather than assumed correct.
Two real defects were caught and fixed during development by that suite and by review, both in the
recovery paths.

In-game testing for this release covered scanning, applying, restoring, and the normal operation
flow. Unlike v0.5.1.0, the interrupted-operation recovery flow has not been re-verified against a
forced crash for this build. Its behaviour is unchanged by design and is covered by automated tests,
but if you do hit an interrupted operation, that is the area to watch.

## Known issues

Scanning a large mod library still blocks the game while it runs, and can look like a freeze on a
big library or one stored on a network or cloud-synced drive. This is unchanged in this release and
is the subject of the next one.

Full technical detail is in this repo's commit history and [docs/ROADMAP.md](ROADMAP.md).
