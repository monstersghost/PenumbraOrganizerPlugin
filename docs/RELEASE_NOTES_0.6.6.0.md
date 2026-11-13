# 0.6.6.0 - sort only the new mods, and a popup when they arrive

## New in 0.6.6.0

- **What to sort** (Sort tab): choose "Only loose mods in the root" to sort just the mods sitting in the library root and leave everything already filed alone. A line beside the Sort button says how many mods will be sorted, and the same line shows in the Apply confirmation. It is per session and is never saved as protect rules.
- **New-mod popup**: when mods are installed, a small floating window offers to sort them. **Sort now** rescans and shows a Confirm sort window listing each mod and the folder it would go to, with Apply, Review first and Cancel. Nothing is applied until you press Apply. The popup can be turned off with "Don't show this popup" or in Sort > Advanced.
- **Automatic mode** (Sort > Advanced, off by default): sorts new root mods without asking, up to 20 at a time. Larger batches fall back to the popup.
- **Unused mods** (Scan tab, collapsed section): lists mods that are not enabled in any of your collections, with their size on disk, and can stage a move into an `Unused` folder. The move goes through Review and Apply like any other. "Protect the Unused folder" is ticked by default so a later full sort does not pull them back out.
- A rescan after Apply or Restore now also happens when the window is closed.

## Switched off for now

The `/porganizer sortnew` and `/porganizer sortroot` chat commands did not behave in the first in-game test, so they currently answer with a one-line notice. Use the popup or the Sort tab instead. They will come back once they have been checked in game.

## What moves

- Nothing moves unless you sort. Root-only sorting only proposes moves for mods that are loose in the root and not protected. Heliosphere and Proteus mods are protected by default, so they are not counted as new mods to sort.
- The popup counts only mods that would actually move.
- Sorting from the popup rescans your library first. An unapplied sort you had staged is discarded, and the popup says so.

## Not in this release

Sorting into one folder per collection is built but not exposed. The Help tab and the first-run walkthrough do not describe these features yet.
