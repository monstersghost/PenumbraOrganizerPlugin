# XIV Mod Archive post

The live listing: <https://www.xivmodarchive.com/modid/164842>

## Updating the images (the usual job)

The listing's own description is fine and is **not** rewritten on every release. What goes stale is
the gallery, because it shows the old app and the pre-0.6.2.1 UI.

Upload the files in `docs/xma-images/` in filename order. They are numbered for exactly that, since
XMA shows the gallery in upload order and supports no captions:

| File | Shows |
| --- | --- |
| `01-preview-protect-tree.jpg` | The Protect tab's folder tree. Use this as the **preview image** — it is the thing the listing is selling. |
| `02-sort.jpg` | The Sort tab: grouping and the three splits. |
| `03-review-changes.jpg` | Current beside proposed paths. This is the "nothing moves without you" proof. |
| `04-protect-search.jpg` | Searching the tree. |
| `05-unprotect-confirm.jpg` | The confirmation before a bulk unprotect. |
| `06-history.jpg` | Snapshots and Restore. |
| `07-search.jpg` | Reverse item lookup. |
| `08-templates.jpg` | Templates and the export warning. |
| `09-scan.jpg` | The Scan tab. |

Regenerate them after retaking any screenshot:

```bash
python docs/build-xma-images.py
```

They are JPEG at 2x the source size, which is what keeps the small UI text readable in XMA's
gallery viewer.

### Two things to check while you are in there

- The listing links **two** Discord invites (`MhQzVJ65c` and `NHssF8ux6B`) and the text calls the
  second one the new server. If the first is dead, remove it — a broken invite on the front page
  costs you support traffic.
- The description still describes the standalone app's language in places ("bulk moves, undo/redo,
  one-click strategies"). It is not wrong about the plugin, but if you ever want it to match the
  plugin's own vocabulary, the rewrite below is ready to paste.

## A full rewrite, if you ever want one

Not needed for a normal release. Everything below is an alternative description, written in the
plugin's own terms and current as of 0.6.2.1.

XMA's description box takes plain text with basic formatting, so the headings below are plain lines
rather than Markdown `#`.

---

## Title

Penumbra Organizer — sort your mod library into folders, safely

## Short summary

A Dalamud plugin that files your installed Penumbra mods into folders by creator, type, or both.
It proposes every move first, you review the whole list, and nothing happens until you press Apply.
Anything you protect is never touched, and every Apply can be undone.

## Description

WHAT IT DOES

Penumbra Organizer reads your installed mods from Penumbra, works out what each one is, and
proposes a folder for it. You can group by creator, by mod type, or by type and then creator, and
split gear into equipment slots, NPC mods by kind, and hair by the hair number it replaces.

Nothing is moved while it works that out. The Review Changes tab shows every mod's current folder
beside its proposed folder, flags anything that would collide, and only then lets you press Apply.

NOTHING HAPPENS WITHOUT YOU

- Sort only proposes. Apply is the only thing that asks Penumbra to move anything.
- Every Apply takes a snapshot of your library first, and the History tab restores any snapshot, so
  a sort you dislike is one click away from being undone. You can also take a labelled backup
  whenever you like.
- Protected mods are never moved, never proposed, and flagged as an error if anything touches them.
- The plugin never writes inside your mod folders. It asks Penumbra to change a mod's folder path,
  which is the same operation as dragging it in Penumbra's own window.

PROTECTION, INCLUDING THE PART THAT IS NEW IN 0.6.2.1

Protection is per mod, per folder, or automatic for Heliosphere-managed mods.

Folders are a tree, like Penumbra's own, with one box per folder that cycles through three states:
protected, excluded, or inheriting from the folder above it. The nearest rule wins, so you can
protect a folder and still exclude one subfolder inside it — protect "haloween", exclude
"haloween/animation", and that subfolder sorts while everything else in "haloween" stays put. Before
0.6.2.1 that was not possible: protection only ever flowed downward, so you had to choose between
protecting the parent and sorting the subfolder.

The tree also fixes the other thing people complained about: if you have sorted hair into
per-number subfolders you had hundreds of rows of full paths with nothing to collapse. Now every
folder collapses, shows how many of its mods are protected out of how many it holds, remembers what
you left open, and the search box opens matching branches for you.

An exclude frees only what the folder was protecting. A mod you ticked yourself, or one Heliosphere
manages, stays protected — those are your decisions, not the folder's. There is a per-folder
"Unprotect N inside" button when you do want to clear those too, and it tells you how many mods it
can free before you confirm.

WHAT ELSE IS IN IT

- A reverse lookup: find which mod changes a given game item, enabled or not.
- Excel export and import, if you would rather write the layout yourself in a spreadsheet.
- Templates: share a folder layout as a file or a share code, and preview someone else's against
  your library before applying it. Export always goes through a review screen first, because a
  template contains a list of your mod names and sending one means publishing that list.
- Orphaned folder cleanup for folder entries Penumbra keeps after a mod moves out of them.
- A Help tab with the full explanation of every control, and a guided first run.

REQUIREMENTS

XIVLauncher and Dalamud, with Penumbra installed and enabled. Every tab depends on Penumbra being
available.

INSTALLING

This is a custom plugin repository, not a plugin you unzip by hand. In Dalamud settings, open
Experimental, add this to Custom Plugin Repositories, save, then install Penumbra Organizer from
the plugin list:

https://raw.githubusercontent.com/monstersghost/PenumbraOrganizerPlugin/main/repo.json

Do not install it by extracting a release archive into your devPlugins folder — it will not update
and it may not load.

LINKS

- Full user guide:
  https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/main/docs/USER_GUIDE.md
- Source, releases and issues:
  https://github.com/monstersghost/PenumbraOrganizerPlugin
- Discord, for help and release announcements: linked from the plugin's own title bar and Help tab.

A NOTE ON HAIR NUMBERS

Hair numbers are read from each mod's own files, and also from the EST entries in its metadata. The
EST reading has not been confirmed in game to be the hair number. If a hair mod is filed under a
number that is not the hair it replaces, turn "Split hair by hair number" off and report the mod
name and both numbers on Discord. The option is off by default, and a hair mod that resolves to two
numbers stays in the plain Hair folder rather than guessing.
