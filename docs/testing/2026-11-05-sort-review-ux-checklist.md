# Manual check: Sort and Review tab UX pass

Build, load in game (dev plugin path: the MAIN checkout's bin\Debug, not an old worktree), scan a
real library, then check each item. Nothing here is covered by the unit suite except the layout rules.

## Sort tab
- [ ] Group by has a small heading; Gear, NPC, Hair and Body type sections follow; Sort is purple.
- [ ] Untick "Split hair by hair number": Priority race and Midlander fallback disappear. Tick it: they return.
- [ ] Pick Group by "Creator": every split checkbox greys with the tooltip; a TICKED hair box keeps its
      dependents visible but greyed, and switching Group by back restores their values untouched.
- [ ] The wiki-refresh button, its yellow note and the scraped-NPC checkbox are gone.
- [ ] "Split gear by body type" and "Split body mods by body type" are separate. The "(?)" opens a popup
      by click, closes on click-away and on Escape, shows the same text on hover, and takes keyboard
      focus (Tab) and activates with Enter or Space. If it cannot be focused, note that ImGui keyboard
      navigation is off in Dalamud and the click still works.
- [ ] Gear flag only: gear files under a body, Body mods stay `Body/<mod>`. Body flag only: the reverse.
      Gear flag with the slot split off gives `Gear/<body>/<mod>`; with it on, `Gear/<body>/<slot>/<mod>`.
- [ ] Body priority is always there, collapsed, and hovering its header shows its help. Expanded:
      "First-choice body" shows rank 1. Pick another body: it moves to rank 1 and the others keep their
      order. Reorder with the arrows in the nested "Full priority order": the dropdown follows. Reset to
      default: the dropdown follows. Restart the game: the order persists.
- [ ] "Manual assignment" is collapsed by default and works when opened.
- [ ] "Selection changed since the last sort." still appears after changing a checkbox or the priority.

## Review Changes
- [ ] A Workbook group holds "Group workbook by", Export Workbook and Import Workbook.
- [ ] Import Workbook works with a scan loaded and nothing staged, and stays greyed while a scan runs.
- [ ] Create a diagnostic dump after exporting a workbook: the workbook path and Open Workbook are unchanged.
- [ ] Apply is purple and set apart. Diagnostics is collapsed and still produces the dump.
- [ ] Export, import, review and Apply work end to end.

## Other tabs
- [ ] History: Delete asks first, Cancel keeps the backup, "Yes, delete" removes it.
- [ ] Scan: "Live events (N)" is collapsed and keeps its open state as N changes.
- [ ] Protect: the buttons read "Protect all"/"Unprotect all" and "Protect Heliosphere mods"/"Unprotect
      Heliosphere mods" and each does what it says.
