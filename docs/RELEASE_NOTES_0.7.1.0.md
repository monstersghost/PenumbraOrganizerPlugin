# 0.7.1.0 (testing) - a rebuilt Sort tab: Recommended and Advanced

This is a testing build. The Sort tab was rebuilt and none of it has been tried in game yet, so please report anything odd. Your settings carry over: on update, your current ticks become the same layout as before and nothing moves until you Sort, review and Apply.

## New in 0.7.1.0

- **Two modes on the Sort tab**, switched at the top:
  - **Recommended** is one calm card: What to sort, Group by, and live example paths showing where mods will go. It lists what Recommended does for each kind of mod. Most people never need more.
  - **Advanced** has a list of mod types (Gear, Hair, Body, Face, Skin, NPC, Animation and the rest) and an editor for the one you pick.
- **Per-category settings** (Advanced):
  - **Folder:** rename or nest a type's folder, for example `Equipment/Gear`. It is checked as you type, and warns if it clashes with another type's folder or sits inside a protected or sort-inside folder.
  - **Creator folder:** none, before the type folders, or after them. By default a type follows Group by.
  - **Splits:** each split a type can have is a switch with a depth number: equipment slot, body type, hair number, emote, kind, and the race splits. Hands and feet can share one folder.
  - A **Custom** badge marks any type you changed, with **Reset to Recommended** for one type and for all of them.
- **Select all splits** (Advanced header): turns on every split for the most detailed layout. It asks first, and it does not sort. Folder names, creator folders and priorities stay as they are.
- **A pinned top bar:** the Sort button, the scope line, the mode switch and the help button stay in view while the rest of the tab scrolls.
- **Recommended** is a curated starting layout (gear by body type then slot, hair by number, body by body type then part, NPCs by kind, emotes by emote, one folder per type for the rest, race split off). Anyone who has never changed a type follows it, so later improvements reach them; a type you customise keeps your setting.
- **The workbook follows renamed folders.** The Category Mapping sheet's built-in rows, codes 1 to 16 and `{type}` use your configured folders. An untouched exported destination is read back exactly as written. A workbook exported before a rename says "code N now files under X (exported as Y)" when imported, instead of calling the code edited. The rules `type` field still uses the built-in category names.
- **Rules** keep working as before, and the Rules header now stays open when you add a rule.
- Safer folders: a category's own folder counts as a sorter folder, so a renamed root is not mistaken for a "sort inside" folder, and Sort does not move mods again on the next pass.
- The staged-export fingerprint is a short hash, so a library with many rules and renamed folders can no longer overflow a spreadsheet cell.

## What moves

- Nothing moves until you Sort, review and Apply.
- A fresh install, a deleted config, or a config that fails to load starts on Recommended. Your first Sort after that can propose moving most of an existing library (gear into numbered folders, for example). Review it before you Apply.
- Downgrading to 0.7.0.0 keeps your ticks. Renamed folders and creator levels exist only in this build.

## Please test

- Switch between Recommended and Advanced; check the example paths follow Group by and your changes.
- Rename Gear to `Equipment/Gear`, Sort twice, and check the second Sort proposes nothing.
- Select all splits, then Reset all.
- Close and reopen the game: your layout, race settings and hair choices should persist.
- Race folders on real libraries, and a workbook export, edit and import.
- Scroll the Advanced tab; check the pinned bar stays put and nothing is clipped or overlaps.

## Not in this release

- Only the main flows have been tried in game so far; the checklists in the repo (`docs/testing/`) list the rest. The new widgets (cards, switches, badges, icons) may still have rough edges, and a card's height may catch up one frame late.
- Known issue: no popup appears when a Heliosphere mod is added. Heliosphere mods are protected by default (Protect tab), so a new one is not offered for sorting. The next build shows a notice that names the tool and its mode, with a button to the Protect tab, plus a status line on the Sort tab.
- Splits cannot be reordered yet; each type has one fixed depth order. New splits (weapons by job, grouped accessories) are planned.
- The help wording for "Sort > Advanced" in the new-mod popup still points at the old section name, which is now "Sort settings".
- The guide PDF behind the Help tab's button has not been rebuilt, so it is out of date. The guide in the repo (`docs/USER_GUIDE.md`) and the Help tab text are current.
