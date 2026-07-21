# Penumbra Organizer: Closed Testing Guide

This is a **closed test build**. You're testing a plugin that moves and
deletes real data inside Penumbra's own configuration. It's been through
unit tests and code review, but has **not** been exercised against a wide
range of real, live mod libraries yet. Please read the Precautions section
before doing anything else.

If something breaks, that's exactly what this round is for. Report it (see
"How to report a problem" at the end).

---

## 0. Precautions: read this before you install anything

**1. Manually back up Penumbra's own config folder.**

This plugin has its own rollback/history feature, but that feature lives
*inside* the system being tested. If something goes wrong badly enough,
you want a backup that doesn't depend on the plugin working correctly.
This is a five-minute, one-time safety net independent of everything else
in this guide.

- Close the game (or at least close Penumbra's window; closing the game is safer).
- Navigate to: `%APPDATA%\XIVLauncher\pluginConfigs\Penumbra\`
- Copy that entire `Penumbra` folder somewhere safe (a different drive if
  possible, e.g. `D:\PenumbraConfigBackup-2026-07-20\`).
- **You do NOT need to back up your actual mod files** (the big mod
  storage folder you picked in Penumbra's settings). This plugin never
  touches file contents, only Penumbra's internal virtual-folder paths
  for each mod, which live inside the config folder above.

If anything goes seriously wrong during testing: close the game, restore
this folder over the live one, reopen. You're back to exactly where you
started, independent of anything the plugin did.

**2. Note your current mod count and a few notable mod names/paths.**

Take a screenshot of Penumbra's own mod list before you start, or jot down
the total mod count and 3-5 mod names you'd recognize. This gives you a
quick "does this look right" reference after each test, separate from
whatever the plugin itself reports.

**3. If you can, test on a copy of your setup rather than your only one.**

Not everyone can do this (a full mod library can be huge), but if you have
a spare machine, a spare Windows user account, or a secondary FFXIV
install, that's the lowest-risk way to test. If not, precaution #1 above is
your safety net. Don't skip it.

**4. Don't test on your only/main character during a raid night, a
static run, or anything where a stuck game client would be costly.** This
plugin only touches Penumbra's own data, not game state, but a Dalamud
plugin bug can in rare cases affect client stability, so test somewhere low-stakes.

---

## 1. How to install (Dev Plugin: testing build, not a public release)

This build is **not** on any public plugin repository. You'll load it as a
"Dev Plugin," Dalamud's built-in mechanism for exactly this kind of closed
testing. It shows up in your plugin list with a wrench icon marking it as
a testing/dev plugin, distinct from normal installed plugins.

1. You'll receive a `.zip` file (e.g. `PenumbraOrganizer.Plugin-0.4.0.1.zip`).
   Extract it to a folder you'll keep around for the duration of testing.
   Don't extract it to Downloads and delete it later; Dalamud needs to keep
   reading from that folder. A good spot: `Documents\PenumbraOrganizerTest\`.
2. Confirm the extracted folder directly contains `PenumbraOrganizer.Plugin.dll`
   and `PenumbraOrganizer.Plugin.json` (not nested one level deeper inside
   another folder). If your zip tool extracted a wrapper folder, go one
   level in until you see those two files side by side.
3. In-game, open the Dalamud settings (`/xlsettings` or the wrench icon on
   the Dalamud plugin installer window).
4. Go to the **Experimental** tab.
5. Under **Dev Plugin Locations**, click **Add**. This opens a file
   picker; browse into the folder from step 1 and select
   `PenumbraOrganizer.Plugin.dll` itself (not the folder, and not the
   `.json` manifest).
6. Click **Save and Close**.
7. Open the plugin installer (`/xlplugins`), go to the **Dev Tools** /
   **Installed Plugins** tab. You should see **Penumbra Organizer** listed
   with a wrench/testing indicator. If it's not there, use the installer's
   refresh/reload button.
8. Enable it if it isn't already.
9. Run `/porganizer` to open the plugin window, or use the icon/button
   Dalamud shows for it.

**To update to a newer test build later:** close the game (or at least
disable the plugin), replace the contents of the same folder with the new
zip's contents, reopen/re-enable. Dalamud will pick up the new DLL.

**To fully remove it when testing is done:** disable and remove it from
the plugin installer, then remove the folder from Dev Plugin Locations in
Settings → Experimental, then delete the extracted folder. This does not
touch Penumbra or your mods. See Precaution #1 if you want extra
confidence, but nothing about uninstalling this plugin should require
restoring that backup.

---

## 2. Testing checklist

Work through these roughly in order. Later sections (Apply, History/Restore,
Folder Cleanup) build on state from Scan/Sort, and Restore specifically
needs an Apply to have already happened. Check each box as you go, and note
anything that didn't match "Expected" even if it's minor.

### 2.1 Scan

- [ ] Open the plugin (`/porganizer`), go to the **Scan** tab.
- [ ] Click "Refresh mod list". **Expected:** the mod count shown matches
      what you noted in Precaution #2, and the table populates with
      Name/Author/Current Path for every mod.
- [ ] Scroll through the list. **Expected:** no garbled/mojibake text for
      mods with non-English names (Japanese/Korean/Chinese/German mod
      names should render correctly, not as `?????` or boxes).
- [ ] Install or remove a mod in Penumbra directly (outside the plugin),
      then re-click "Refresh mod list". **Expected:** the change is
      reflected; plugin state isn't stuck on the first scan.

### 2.2 Protect

- [ ] Go to the **Protect** tab.
- [ ] Check the box next to 2-3 individual mods. **Expected:** they show
      checked and (elsewhere in the UI, e.g. Scan/Review tables) their
      name renders in gold/amber text.
- [ ] Click "Toggle protect all". **Expected:** every mod becomes
      protected (all checked). Click it again. **Expected:** every mod
      becomes unprotected (all unchecked): a true toggle, not
      accumulate-only.
- [ ] If you have any Heliosphere-managed mods: click "Toggle Heliosphere
      protection", confirm only Heliosphere mods are affected, and confirm
      manually *unchecking* a Heliosphere mod and then re-scanning
      re-protects it automatically (Heliosphere mods are meant to always
      re-protect on Scan).
- [ ] Close and reopen the plugin window (or restart the game). **Expected:**
      your protection choices persisted.
- [ ] Type a few characters into the "Search mods and folders" box.
      **Expected:** both the Folders list and the Mods list filter down to
      matches as you type (mod name, author, identifier, or current path
      for mods; folder path for folders). Clear the box. **Expected:**
      everything reappears.
- [ ] **Folder-level protection.** Find a folder in the Folders list that
      contains several mods (e.g. `Gear/Feet` if you have mods sorted
      there) and check it. **Expected:** every mod currently in that
      folder *and any subfolder under it* shows as protected, with a
      "(via folder: ...)" note next to each one on the Mods list. Uncheck
      the folder. **Expected:** those mods become unprotected again,
      unless something else (individual protection or Heliosphere) still
      protects them.
- [ ] **Ancestor-folder protection.** If you have mods nested at least two
      folders deep (e.g. `Gear/Feet/Boots`), confirm *both* `Gear` and
      `Gear/Feet` appear as separate, individually checkable rows in the
      Folders list, not just the deepest one. Check the top-level ancestor
      (e.g. `Gear`). **Expected:** every mod anywhere under it, at any
      depth, becomes protected — this is new in this build; a top-level
      folder should now be enough to protect an entire directory tree
      without checking every subfolder individually.

### 2.3 Sort

Run each of these, checking the **Review Changes** tab after each one to
see the proposed new paths before applying anything (do not click Apply
yet in this section).

- [ ] **By Creator**: mods land under `Creator/Modname` folders, grouped
      by author.
- [ ] **By Mod Type**: Gear mods all land as one flat `Gear/` folder, no
      subfolders by slot.
- [ ] **By Mod Type Detailed**: Gear mods split into subfolders by slot
      where the plugin was able to determine one (e.g. `Gear/Feet`,
      `Gear/Head`) with an unresolved remainder in a plain `Gear/`
      catch-all. Every other category (Face, Hair, NPC, Mount, Body,
      etc.) behaves the same as "By Mod Type" for those categories.
- [ ] **By Type Then Creator**: `Type/Creator/Modname`.
- [ ] **By Creator Then Type**: `Creator/Type/Modname`.
- [ ] **Manual assign**: type a few characters into the "Search mods"
      box above the manual-assign list, confirm the list filters down.
      Check the boxes next to 3-4 mods (protected mods should not appear
      in this list at all), type a custom destination folder, click
      "Assign N selected mods". **Expected:** only the checked mods'
      proposed paths change, to `<your folder>/<mod name>` each; a
      summary line reports how many were assigned and how many were
      skipped. Clear the search box and confirm your checked selections
      are still checked (selection should survive a filter-text change).
- [ ] After any sort, confirm protected mods' proposed paths never
      changed (Review Changes tab should show no proposed change for
      protected mods regardless of which sort you ran).

### 2.4 Review Changes / Export / Workbook

- [ ] On the **Review Changes** tab, confirm the table shows both Current
      Path and Proposed Path per mod, and that changed rows show the
      proposed path in green.
- [ ] Click "Export". **Expected:** a text file is written (path shown
      in the UI) with every mod's full details and the current
      validation result. Open it and confirm it's not empty or garbled.
- [ ] Pick a workbook strategy from the dropdown, click "Export Workbook".
      **Expected:** an `.xlsx` file is produced; click "Open Workbook" and
      confirm Excel (or your spreadsheet app) opens it without the game
      freezing or disconnecting.
- [ ] Edit a few destination cells in the exported workbook, save it, then
      use "Import Workbook" (file picker should be Dalamud's own in-game
      dialog, **not** a Windows Explorer window; if you see a native
      Windows file dialog anywhere in this plugin, that's a bug, report
      it). **Expected:** your edited destinations are reflected back in
      the plugin's Review Changes tab.
- [ ] Deliberately create a collision: manually assign two *different*
      mods to the exact same proposed path. **Expected:** the Review tab
      shows a red collision error naming both mods, and the Apply button
      is disabled while the collision exists.
- [ ] With a collision or protected-violation present, click "Protect &
      Skip All Blocking Mods". **Expected:** the offending mods become
      protected and pinned to their current path, the error clears, Apply
      re-enables.
- [ ] Click "Show Config File". **Expected:** a Windows Explorer window
      opens with the plugin's own settings JSON (protected mod list)
      pre-selected, so you can copy or zip it directly.
- [ ] Click "Create Diagnostic Dump", then "Show Dump File". **Expected:**
      Explorer opens with a text file pre-selected. Open it and confirm
      it has a state summary (last error, last Apply/Restore/Folder
      Cleanup results, mod/protection counts, rollback history summary,
      session event log), and that it does not contain any full
      filesystem path with your Windows username in it; if it does,
      that's a bug, report it.
- [ ] **Diagnostics across a reload.** After running an Apply (2.5) or a
      Restore (2.6), fully disable and re-enable the plugin (or restart
      the game), then immediately click "Create Diagnostic Dump" again
      *without* running anything else first. **Expected:** the dump's
      "Last Apply result" / "Last Restore result" sections show your
      prior operation's outcome, phrased as "no ... run this session;
      last known from a prior session: ...", instead of just "(no ...
      run this session)" with the result silently gone.

### 2.5 Apply: the first real write test

**Do this only after Precaution #1's backup is in place.**

- [ ] Run a sort that produces a reasonable number of changes (not
      thousands at once for your first try; a few dozen is a good first
      test). Go to Review Changes, click **Apply**, confirm the popup
      shows the correct mod count, click "Yes, Apply".
- [ ] **Expected:** a result summary appears ("Apply: N succeeded, N
      failed"). If any failed, the failure reason should be a real
      Penumbra error code, not a crash.
- [ ] Go to Penumbra's own UI directly and confirm the mods actually moved
      to the folders the plugin proposed.
- [ ] Go to the **History** tab. **Expected:** a new snapshot appears at
      the top of the list automatically, timestamped just before your
      Apply, with an auto-description like "N mods moved".

### 2.6 History / Restore

Do this after at least one successful Apply from 2.5.

- [ ] On the **History** tab, click "Create Backup", optionally type a
      label, confirm a new snapshot appears in the list with your label.
- [ ] Run another Sort + Apply (a different strategy than before), so you
      now have at least 3 snapshots: the pre-first-Apply one, your manual
      one, and the pre-second-Apply one.
- [ ] Click **Restore** on the *oldest* snapshot. **Expected:** a
      confirmation popup shows exact counts (how many mods will move, how
      many are already at their snapshot path, how many will be relocated
      to root, how many are no longer installed) before you confirm
      anything.
- [ ] Confirm the restore. **Expected:** a result summary appears; go
      check Penumbra's UI directly and confirm mods are back at (or close
      to) their original paths from before your first Apply.
- [ ] Check the History tab again. **Expected:** a *new* snapshot was
      automatically added just before the restore ran (so the restore
      itself is undoable). You should now have one more entry than before.
      This should now appear immediately without needing to click
      anything else first (if you see a *stale* History list here — the
      new snapshot missing until you create a manual backup or restore
      something else — that's a regression, report it).
- [ ] **Edge case: mod installed after a snapshot.** Install a new mod
      in Penumbra (or note one you installed after your first snapshot),
      then Restore to that first snapshot. **Expected:** the new mod gets
      moved to the Penumbra root (no subfolder), not left where it was
      and not causing an error.
- [ ] **Edge case: mod removed since a snapshot.** If you can safely
      uninstall a test mod, do so, then Restore to a snapshot that
      includes it. **Expected:** it's reported in the result summary as
      skipped because it's no longer installed, not as an error, and
      nothing else in the restore is blocked by it.
- [ ] **Edge case: protected or Heliosphere-managed mod (Exact Restore).**
      Protect a mod (individually, via a folder, or confirm you have a
      Heliosphere-managed mod), then Restore to a snapshot where that mod
      had a different historical path. **Expected — this is the opposite
      of older builds:** the confirmation popup shows a yellow warning
      line with a count of currently-protected/Heliosphere-managed mods
      that will move anyway, and after confirming, that mod's path
      *does* move back to its historical path from the snapshot. Restore
      is meant to reproduce the snapshot exactly, ignoring current
      protection — protection only blocks Sort/Apply, not Restore. If a
      protected mod is instead skipped and left in place, that's a bug,
      report it.
- [ ] Click **Delete** on one of the snapshots (not the most recent one).
      **Expected:** it disappears from the list, and restoring to any
      *other* snapshot still works normally afterward.
- [ ] Restart the game entirely, reopen the plugin, go to History.
      **Expected:** every snapshot you created is still there; history
      persists across restarts.

### 2.7 Folder Cleanup

- [ ] After at least one Apply has moved mods out of some folders, go to
      the Review Changes tab's Orphaned Folders section.
      **Expected:** any now-empty folders left behind in Penumbra's own
      `organization.json` are listed, split into a plain-empty section
      and a (harder to accidentally delete) customized-empty section. You
      should also see a "Re-read organization.json" button and a
      "Last read: HH:MM:SS (Ns ago)" line next to it.
- [ ] **If detection looks stale or wrong** (e.g. after sorting/restoring
      a large number of mods, the list shows far fewer orphaned folders
      than you'd expect): click "Re-read organization.json". This
      re-reads the file from disk — it does **not** ask Penumbra for
      fresh data, so if Penumbra itself hasn't written its latest folder
      state to disk yet, the count may not change even after re-reading.
      If that happens: move any one folder manually in Penumbra's own UI
      (or click Penumbra's "Rediscover Mods"), then click "Re-read
      organization.json" again. **Expected:** the count updates to the
      full, correct set once Penumbra has actually written to disk. If
      you can, note whether the count changed on the *first* re-read
      (before touching Penumbra) or only after — this specific detail is
      useful for a report if the timing looks off.
- [ ] Select a few, run the cleanup. **Expected:** a success message with
      counts; check Penumbra's own folder tree UI. It still shows the
      pruned folders until you click Penumbra's own "Rediscover Mods"
      button (the plugin should tell you this; that's expected, not a
      bug).
- [ ] Click Penumbra's "Rediscover Mods". **Expected:** the pruned
      folders are actually gone from Penumbra's UI now.
- [ ] Click "Rollback Folder Cleanup" (available right after a cleanup
      run). **Expected:** the pruned folders come back, including any
      custom color/description a customized folder had.

### 2.8 NPC / Body-slot / Gear-slot classification (spot-check, not exhaustive)

- [ ] If you have any NPC/enemy/boss-named mods, sort "By Mod Type" and
      confirm they land under an NPC-related folder, not miscategorized
      as Gear.
- [ ] If you have any Bibo+/Yet-Another-Body-style mods, confirm they
      classify as Body, not Gear.
- [ ] On the Scan tab, try the NPC name list "refresh" control if visible.
      **Expected:** it either updates successfully or fails with a clear
      message. It should not hang the UI or crash.

### 2.9 General / polish

- [ ] Confirm the plugin window title shows a version number
      (`Penumbra Organizer v0.4.0.1` or similar), not blank or "unknown".
- [ ] Confirm the plugin's icon (not a generic placeholder) shows in
      Dalamud's plugin list.
- [ ] Throughout all of the above, confirm you never saw a native Windows
      file-picker dialog pop up (every file dialog in this plugin should
      be Dalamud's own in-game style). A native dialog pauses the game
      and can cause a disconnect; seeing one would be a real bug to
      report.
- [ ] Leave the plugin window open and tabbed through for a while during
      normal play. **Expected:** no noticeable stutter/FPS drop from the
      plugin being open, even on the History tab with several snapshots.

---

## 3. How to report a problem

For each issue, please include:

1. **Which checklist item** (e.g. "2.6 Restore, edge case: protected mod").
2. **What you expected vs. what happened.**
3. **Your Dalamud log.** `/xllog` in-game, or the log file directly at
   `%APPDATA%\XIVLauncher\dalamud.log`. Copy the relevant section from
   around when the issue happened (timestamps help). This build logs its
   own major operations (Scan, Apply, Restore, Create Backup, Delete
   Snapshot, Folder Cleanup, Folder Cleanup Rollback) to this same file,
   including per-mod failure reasons on Apply/Restore, so search for
   "Penumbra Organizer" (or your timestamp) in the log rather than just
   sending the very last few lines.
4. **The diagnostic dump.** On the Review Changes tab, click "Create
   Diagnostic Dump", then "Show Dump File", and attach it. It's a plain
   text summary of the session (last results, mod/protection counts,
   rollback history, event log), safe to attach alongside the Dalamud log.
5. If it's a File/Apply/Restore issue: whether Precaution #1's backup
   folder is still intact (so we know recovery is possible either way).
6. Roughly how many mods you have installed. Several bugs in this
   plugin's history only showed up at real scale (hundreds+ of mods), not
   in small test libraries.

Do **not** try to fix Penumbra's state yourself beyond restoring
Precaution #1's backup if needed. Report first, restore if you're
blocked from playing, and we'll sort out the root cause from the report.

---

## 4. Known limitations (not bugs, expected for this build)

- This is **not** a public release. It won't auto-update; you'll get new
  zips manually for each test round.
- Rollback/History currently covers **Apply only**. Folder Cleanup still
  has its own separate single-backup mechanism (its own "Rollback Folder
  Cleanup" button), not the multi-snapshot History tab.
- No retention limit on saved snapshots. The history file will grow the
  more you use Create Backup/Apply/Restore over a long test session. This
  is a known, accepted trade-off for this build, not something to report.
- Folder Cleanup's reload banner only clears via a full Scan, not
  Penumbra's own Rediscover Mods alone; expected, not a bug.
