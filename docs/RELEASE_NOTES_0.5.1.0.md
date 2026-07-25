# Penumbra Organizer Plugin v0.5.1.0

## Changes since v0.5.0.0

v0.5.0.0 shipped Apply's crash-safer execution engine but left interrupted operations with no
recovery flow at all. That was the release's one named Known Issue. This release closes that gap
completely: Restore now runs on the same engine as Apply, and an interrupted Apply or Restore gets
a full recovery dialog instead of requiring a manual workaround.

### New: a real recovery flow for an interrupted Apply or Restore

If the game crashes, Penumbra becomes unavailable, or the plugin is unloaded mid-Apply or
mid-Restore, reopening the plugin now shows a recovery panel with three options instead of silently
blocking every other action with no way out:

- **Keep Current State**: accepts whatever Penumbra currently has as correct and unblocks the
  plugin. Doesn't undo or redo anything; just stops treating the interruption as unresolved.
- **Continue**: finishes the interrupted operation from where it left off.
- **Restore Previous State**: rolls every mod back to exactly how it was before the interrupted
  operation started.

The panel also shows *why* a choice might be unavailable. If the interrupted plan or its
pre-operation snapshot is missing or corrupted, that gets called out directly instead of leaving
Continue or Restore Previous State silently greyed out. An expandable **Details** section lists the
resolved/pending state of every individual mod involved.

In the rare case where more than one interrupted operation is found at once (normally impossible in
ordinary use), each one now gets its own row with its own Keep Current State option, resolved one at
a time, rather than only being able to abandon all of them in a single irreversible bulk action.

### New: Restore runs on the same crash-safe engine as Apply

Restore no longer runs as one long synchronous call. It now runs frame-budgeted in the background,
same as Apply since v0.5.0.0, and checkpoints its own progress to disk as it goes. This is also what
makes it possible for Restore to participate in the recovery flow above.

### New: a real progress bar, and a way to stop mid-operation

Apply and Restore both show an actual progress bar now (tracking mods processed, not raw execution
steps, so a mod involved in a two-step move no longer looks like two mods' worth of progress), plus a
succeeded/failed count and the name of whatever mod just finished. A **Cancel** button appears while
an operation is actively moving mods, stopping it cleanly at the next safe point with no confirmation
needed. It's the one action in this whole flow that's genuinely low-stakes to click by mistake.

### New: diagnostics dump v2 and an operation history view

- The **Create Diagnostic Dump** button now includes the real state of any interrupted operation
  (with its actual timestamp, not when the dump was created), the last 20 completed operations, and
  the slowest recorded Penumbra IPC calls grouped by what was slow, not just the config/session
  summary it captured before.
- The History tab has a new **Recent Operations** section (separate from the existing snapshot
  list) showing what actually ran (Apply, Restore, Continue, Restore Previous State) and how each
  one resolved.
- Old completed-operation records are now automatically pruned to the last 50 (or 30 days,
  whichever keeps more) on startup, instead of accumulating on disk indefinitely.

### Fixed: several buttons could be clicked while another operation was still in progress

Scan, Create Backup, Restore, Clean Up Selected Folders, and Rollback Folder Cleanup are now
properly greyed out (with a tooltip explaining why) whenever another operation is running or the
plugin needs the recovery flow above resolved first, instead of relying on an after-the-fact error
if you clicked them anyway.

### Fixed: several places where long text or too many buttons could overflow the window

- The Search tab's category and slot filter checkboxes now wrap onto additional lines instead of
  running off the edge of a narrower window.
- Several confirmation popups now size themselves to fit their own text properly, instead of
  auto-sizing to an oddly cramped or (for a couple with long explanatory sentences) unusually wide
  shape.
- The folder-cleanup confirmation popup no longer tries to list every selected folder as one
  ever-growing popup. A real library has hit 229 orphaned folders in one run, which used to mean a
  popup taller than the screen.
- File paths, custom backup labels, and mod names/paths in the Scan and Review Changes tables no
  longer get abruptly cut off. Long values now wrap instead of clipping mid-word, and the mod-path
  table gives its path columns proportionally more room than the shorter Name/Author columns.
- The **Rollback Folder Cleanup** button no longer occasionally renders almost entirely outside the
  window, depending on how the paragraph above it happened to wrap.

The recovery flow has been verified end-to-end against a real forced crash (force-closing the game
mid-Apply), confirming the interrupted-operation panel and its resolutions work as intended.

Full technical detail on everything above is in this repo's commit history and
[docs/ROADMAP.md](ROADMAP.md).
