# In-game testing guide: 0.6.0.0

What to check before releasing 0.6.0.0, and in what order. This covers only what has **not** already
been verified in-game — it is not a full regression pass. `docs/TESTING_GUIDE.md` remains the
broad-coverage guide, but note that its section 2.3 describes the seven sort buttons and is stale for
this release.

**Order matters here more than usual.** Two of these tests are one-shot: the NPC list migration runs
once and then the condition is gone, and the walkthrough runs once and then sets a flag. Both are
step 1 and step 6 respectively for that reason, and both have reset instructions if you need another
go.

Tick as you go. Anything that does not match **Expected** is worth reporting even if it seems minor.

---

## 0. Before you start

**Back up the plugin's own config directory.** Step 1 renames a real file in it.

- Close the game.
- Open `%APPDATA%\XIVLauncher\pluginConfigs\`.
- Copy the whole `PenumbraOrganizer.Plugin` folder somewhere safe, and the
  `PenumbraOrganizer.Plugin.json` file beside it.

That folder is where the NPC name lists live; the `.json` file beside it is the settings, including
the first-run flag. You will restore from these if you want to re-run steps 1 or 6.

You do **not** need to back up your mod files. Nothing in this release touches them. Backing up
Penumbra's own config as well is never a bad idea, and `docs/TESTING_GUIDE.md` section 0 explains
how.

**Note before launching:** whether `npc-name-list.json` exists in that folder, and roughly how large
it is. See step 1 — on the maintainer's machine it has already been migrated, so there is a good
chance yours has too.

**Keep the `.oversized-` backup.** If you have a
`npc-name-list.json.oversized-<timestamp>.json`, that is 0.5.3.1's copy of the ~21,000-name list.
Do not delete it: it is the only real-world corpus available for testing the scraped list when that
feature is eventually enabled, and it cannot be regenerated without pressing the button that
produced it.

---

## 1. NPC name list migration — already done on the maintainer's machine

This is the only code in the release that renames a real user file, and it runs once during plugin
load. It was the highest-risk unverified item.

**As of 2026-08-10 it has already run successfully**, on the maintainer's own install, at some point
while dev-building the overhaul. The evidence in
`%APPDATA%\XIVLauncher\pluginConfigs\PenumbraOrganizer.Plugin\`:

- `npc-name-list.json` is **absent**.
- `npc-name-list-scraped.json` is **present**, holding 19 NPCs / 15 enemies / 11 bosses — byte for
  byte the bundled seed that 0.5.3.1 wrote, carried across intact.
- `npc-name-list.json.oversized-20260807195716.json` still sits beside it with **21,340 names**,
  untouched: 0.5.3.1's backup of the list that correlated with the crash.

That is exactly the four-case table's legacy-only branch, on real data, with nothing lost. Confirm it
still looks like that and move on:

- [ ] Open that folder. **Expected:** as described above — no `npc-name-list.json`, a
      `npc-name-list-scraped.json` present, and the `.oversized-` backup untouched.

**If you want to exercise it from scratch anyway** (worth it on a second machine, or if you want to
watch it happen): close the game, copy `npc-name-list-scraped.json` back to `npc-name-list.json`,
delete `npc-name-list-scraped.json`, relaunch, and re-check.

**On a fresh install with no `npc-name-list.json`**, nothing happens and neither file is created.
That is also correct.

**Also worth one look — the both-present case.** Put a copy of `npc-name-list.json` back *without*
deleting `npc-name-list-scraped.json`, relaunch, and check `dalamud.log` for a line saying both are
present and that the scraped one is in use. **Expected:** both files still on disk, neither modified.
This is the interrupted-migration path and it must never delete anything.

---

## 2. Scan still classifies NPCs

The matcher was rewritten completely. This confirms the bundled list loads.

- [ ] Open the plugin (`/porganizer`), **Scan** tab, click **Refresh mod list**.
- [ ] **Expected:** it completes, and the mod count matches your library.
- [ ] Find a mod whose name contains a well-known character, boss or primal — Y'shtola, Zenos,
      Titan, Shiva, Alphinaud. Check its Category.
- [ ] **Expected:** it classifies as `NPC`. If *every* mod comes back unclassified, the bundled list
      failed to load — report that, it is the failure mode worth catching.

If you have a mod whose title uses a different punctuation style, e.g. `Y-shtola` or `Y shtola`,
those now match too. Worth a look if you have one, not worth hunting for.

---

## 3. Search index build

Never exercised since the rewrite.

- [ ] **Search** tab, click **Build/Refresh Index**.
- [ ] **Expected:** it completes, a summary line reports what was indexed, and a build time appears.
- [ ] Type a few characters into **Mod name contains**. **Expected:** the list narrows.
- [ ] Tick **Gear** in Categories. **Expected:** a **Slots** row appears below.
- [ ] Click a mod in the left pane. **Expected:** its changed items list in the right pane.

---

## 4. The Sort tab's new controls

- [ ] **Sort** tab. **Expected:** a **Group by** dropdown, two checkboxes, and a **Sort** button —
      no row of seven buttons.
- [ ] Set **Group by** to **Creator**. **Expected:** both split checkboxes grey out.
- [ ] Hover a greyed-out split checkbox. **Expected:** a tooltip appears *even though it is
      disabled*, and it says grouping by creator alone never uses the mod's type.
- [ ] Set **Group by** to **Type then creator**. **Expected:** both checkboxes become usable again,
      and hovering now shows only the plain explanation, without the disabled reason.
- [ ] Press **Sort**, then change the dropdown. **Expected:** a line appears reading "Selection
      changed since the last sort."
- [ ] Untick **Split NPC mods by kind** and press **Sort**. Check **Review Changes**. **Expected:**
      NPC mods propose a plain `NPC` folder, not `NPC/NPCs` or `NPC/Bosses`. **This combination has
      never existed before this release** — it is the one most worth a careful look.
- [ ] Re-tick it and Sort again. **Expected:** the subfolders come back.
- [ ] Hover **Also use the NPC list scraped from the wiki**. **Expected:** permanently disabled, and
      the tooltip says it is not available in this version. It should not be clickable.
- [ ] Hover **Refresh NPC list from wiki**. **Expected:** also disabled, with a tooltip.
- [ ] Click **Import Workbook**. **Expected:** the file dialog opens. It moved out of the old button
      row in this release, so it is worth confirming it still works. Cancel out.

---

## 5. Heliosphere protection (the bug you reported)

Only relevant if you have Heliosphere-managed mods. This is the fix from `261db03`.

- [ ] **Protect** tab. Note the counter: "(N/N Heliosphere mods protected)".
- [ ] Click **Toggle Heliosphere protection**. **Expected:** the counter goes to 0/N.
- [ ] Now tick **any folder** in the Folders list. **Expected:** the counter **stays at 0/N**. This
      is the fix — before, all of them snapped back to protected.
- [ ] Untick that folder. **Expected:** still 0/N.
- [ ] If a Heliosphere mod happens to live inside the folder you ticked, that one *should* become
      protected while the others stay unprotected. That is deliberate: protecting a folder is a more
      specific instruction.
- [ ] Now go to **Scan** and click **Refresh mod list**. **Expected:** the counter returns to N/N.
      Heliosphere protection resetting on scan is unchanged and intended.
- [ ] Click **Toggle protect all** off. **Expected:** Heliosphere rows untick too, rather than
      staying visibly ticked.

---

## 6. The first-run walkthrough — the other one-shot

**No reset needed for the first run.** As of 2026-08-10 the maintainer's
`PenumbraOrganizer.Plugin.json` has no `FirstRunTutorialSeen` key at all, because no build containing
piece 5 has been loaded yet. An absent key reads as `false`, so the walkthrough will appear by itself
the first time you open the window on this build — which also exercises the upgrade path, since that
is precisely what every existing user's config looks like.

- [ ] To re-run it afterwards: close the game, open
      `%APPDATA%\XIVLauncher\pluginConfigs\PenumbraOrganizer.Plugin.json` in a text editor, set
      `"FirstRunTutorialSeen": false`, save, relaunch. Leave the `$type` key alone — Dalamud needs
      it.
- [ ] Open the plugin window. **Expected:** a separate small window appears **offset from** the main
      window, not centred on top of it, showing step 1 of 7.
- [ ] **Expected:** **Back** is greyed out on step 1. **Next** and **Skip** are available.
- [ ] Click through with **Next**. **Expected:** the counter advances, Back becomes usable, and on
      step 7 the button reads **Done** and **Skip** disappears.
- [ ] Click **Done**. **Expected:** the window closes.
- [ ] Close and reopen the main plugin window. **Expected:** the walkthrough does **not** return.
- [ ] Go to the **Help** tab and click **Show the walkthrough**. **Expected:** it reopens at step 1.
- [ ] Close it with the **X** this time rather than a button. Reopen the main window. **Expected:**
      still does not return. (Closing counts as dismissing — that is deliberate.)

### 6a. The Penumbra-disabled path — please do try this one

This was broken until the code review caught it, so it has never worked in a build until now.

- [ ] Close the game. Reset `"FirstRunTutorialSeen": false` as above.
- [ ] Relaunch with **Penumbra disabled** (turn it off in Dalamud's plugin installer).
- [ ] Open the plugin window. **Expected:** the walkthrough shows a **single** step saying Penumbra
      is not responding — not the seven-step tour — and the button reads **Done**.
- [ ] Dismiss it, close and reopen the main window. **Expected:** it appears **again**. It must not
      be used up, because you were never shown the actual walkthrough.
- [ ] Now enable Penumbra, relaunch, open the window. **Expected:** the real seven-step walkthrough
      starts from step 1.

---

## 7. Help tab

- [ ] **Help** tab. **Expected:** ten collapsible sections.
- [ ] Expand a few. **Expected:** each has an explanation, and the tab sections then list that tab's
      controls with a line each.
- [ ] **Narrow the plugin window** as far as it goes. **Expected:** text re-wraps to fit; nothing
      runs off the edge or gets clipped.
- [ ] Click **Open the full guide in your browser**. **Expected:** a browser opens.
      **A 404 here is expected right now** — the link points at the `0.6.0.0` tag, which does not
      exist until release. Confirm the browser opens at all; the page will work once tagged.

---

## 8. Hovering, generally

Piece 3 was already verified, so this is a spot-check rather than a sweep.

- [ ] Hover a few controls on each tab. **Expected:** a tooltip on anything non-obvious.
- [ ] Find a disabled control — start a scan and hover **Apply** on Review Changes while it runs.
      **Expected:** a tooltip appears *and* explains why it is disabled.
- [ ] On Review Changes with nothing ticked in Orphaned Folders, hover **Clean Up Selected Folders**.
      **Expected:** it says to choose at least one folder first — not that another operation is in
      progress.

---

## If something fails

Grab `%APPDATA%\XIVLauncher\dalamud.log` — it is the single most useful thing to attach. The plugin
logs migration warnings, scan progress and index progress there.

For anything involving the NPC lists, also say whether
`%APPDATA%\XIVLauncher\pluginConfigs\PenumbraOrganizer.Plugin\` contains `npc-name-list.json`,
`npc-name-list-scraped.json`, or both, and roughly how big each is.

To get back to a clean slate at any point: close the game, restore the `PenumbraOrganizer.Plugin`
folder and `PenumbraOrganizer.Plugin.json` from the backup you made in step 0.

---

## Not bugs — expected in this build

- **The Help tab's guide link 404s.** The `0.6.0.0` tag does not exist until release.
- **"Refresh NPC list from wiki" is disabled**, and so is the scraped-list checkbox beside it. That
  is the release decision, not a fault.
- **Collisions on Review Changes after an Import Workbook.** Import deliberately does not renumber
  clashes; it reports them so you can fix them by hand. A collision produced by a plain **Sort**
  would be a real bug.
- **A stale-mod-list message straight after an Apply.** Known issue carried over from 0.5.3.1; the
  result is fine, the message is misleading.
