# 0.6.1.2 - Free one folder for sorting while the rest stays protected (testing build)

**This is a testing build, and it has not been tested in-game at all.** Stable stays on 0.6.1.1. It
is out early because the bug it fixes has no workaround; it is offered to testers so someone can
confirm the fix before it becomes stable.

## The bug

Reported on Discord: protect your whole library, then try to let one folder sort anyway. There was
no way to do it.

**Toggle protect all** protects every *mod*; it never ticks a folder. So after using it there is no
ticked folder to untick, and every mod stays protected by its own tick. Sort then tells you "apply
to 0 mods" for the folder you were trying to file away, which is exactly how it was reported.

Unticking a folder was never going to help either: that withdraws only the folder's own protection,
and a mod ticked individually stays protected - by design, so one stray untick cannot quietly
unprotect mods you ticked one at a time.

This has been broken since the first public release, 0.4.0.1. It is not new in 0.6.1.x.

## The fix

Every folder in the **Protect** tab's Folders list that holds protected mods now has an **Unprotect
N inside** button next to it. It clears protection from every mod in that folder, at any depth,
whatever protected them - their own checkbox, Toggle protect all, the folder itself, or Heliosphere.
It asks first.

So "protect everything except this one folder" is now: **Toggle protect all**, then **Unprotect N
inside** on the folder you want sorted.

The count on the button is what it can actually free. If a parent folder is protected, that is a
separate choice the button never overrides, so there is no button on the folder below it - it says
"(covered by protected folder ...)" instead, and you untick the parent.

Folder checkboxes behave exactly as before. Nothing about protection changed except that this one
action now exists.

Unprotecting Heliosphere mods this way lasts the session only, as with Toggle Heliosphere
protection: the next Scan re-protects them, because Heliosphere owns where they live.

## What we need from testers

Everything, honestly - no part of this has been seen running. The most useful checks:

- After **Toggle protect all**, does an "Unprotect N inside" button appear next to your folders, and
  is N the number of mods in each?
- Does using it on one folder leave every other folder protected, and does Sort then propose moves
  for that folder instead of "0 mods"?
- Protect a parent folder such as `Gear`. **Expected:** no button on `Gear/Feet` at all, just a note
  saying the parent covers it.
- The confirm prompt: does Escape cancel it cleanly, and does it survive scrolling the folder list?

Report anything odd on Discord. Nothing here moves a mod on its own - as always, Sort only proposes,
and nothing is applied until you press Apply on a plan you have reviewed.

## Upgrading and going back

Opt in through **Receive plugin testing versions** in `/xlplugins`. To go back, turn that off and
reinstall; 0.6.1.1 reads everything this build writes, and this build changes no saved data.

## Also in this build

Nothing else. No other behaviour changed.

1442 automated tests pass, up from 1429 on 0.6.1.1.
