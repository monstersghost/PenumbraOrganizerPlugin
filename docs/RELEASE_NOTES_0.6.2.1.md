# 0.6.2.1 - Protect a folder, except the one subfolder you want sorted

This is a stable release. It includes the **Unprotect N inside** button from the 0.6.1.2 testing
build, plus the work below.

## Protect a folder, exclude a subfolder inside it

Reported on Discord: with `haloween` protected, there was no way to let `haloween/animation` sort
while the rest of `haloween` stayed protected. Protection only ever inherited downward, so the
choice was to protect the parent or to sort the subfolder, never both.

Every folder in the **Protect** tab now carries one box with three states, and clicking it cycles
through them:

- **Protected.** Nothing in this folder or below it is moved by Sort or Apply.
- **Excluded.** This folder and everything below it may be sorted, *even if a parent folder is
  protected.*
- **Inheriting.** No rule of its own; it follows the nearest folder above it that has one.

The nearest rule wins, so you can keep nesting: protect `haloween`, exclude `haloween/animation`,
and protect `haloween/animation/keep` inside that if you want. A folder with no rule of its own
shows a **dim** mark for the state it inherited, and hovering it names the folder the rule came
from; a folder that sets its own state shows a **solid** mark. One click on a dim folder gives it
its own rule first, so reaching "excluded" from an inherited protect takes two clicks.

### What an exclude does not do

An exclude frees only the mods that the *folder* was protecting. Inside an excluded folder:

- a mod you ticked yourself stays protected;
- a Heliosphere-managed mod stays protected, because Heliosphere owns where it lives.

That is deliberate - those are per-mod choices, and a folder rule has no business reversing them.
To clear those too, use **Unprotect N inside** on that folder, which asks first and tells you how
many mods it can free. The count never includes mods it cannot free, so if the button is absent
there is nothing left for it to do.

## The Folders list is now a tree

With mods sorted into per-number subfolders - hair especially - the old list was hundreds of rows
of full paths with nothing to collapse. It is now a tree, like Penumbra's own:

- Click the arrow to open a folder; subfolders sit inside it, one short name per row.
- Each row says how many of its mods are protected out of how many it holds, at any depth, and how
  many subfolders are under it.
- Folders you open stay open the next time you open the window.
- Typing in the search box shows only the branches that match and opens them for you. Clearing the
  search puts the tree back the way you had it. While a search is running, folders stay open.

A folder that still has a rule but no longer holds any mods is still listed, marked
**(currently empty)**, so a rule can never become invisible.

## Unprotect N inside, now stable

The per-folder **Unprotect N inside** button from the 0.6.1.2 testing build is in this stable
release. It clears protection from every mod in that folder at any depth, whatever protected them -
their own checkbox, Toggle protect all, the folder itself, or Heliosphere - and asks first.

So "protect everything except this one folder" is still: **Toggle protect all**, then **Unprotect N
inside** on the folder you want sorted.

One change since the testing build: on a folder you have **excluded**, the button now leaves the
exclude in place and clears only the per-mod protection inside. Previously it withdrew the folder's
own rule, which on an excluded folder would have handed the subfolder straight back to its
protected parent and re-protected the very mods you were freeing.

As before, unprotecting Heliosphere mods this way lasts the session only: the next Scan
re-protects them.

## Upgrading and going back

Install or update through the normal plugin list. Testers do not need to do anything different:
this build is offered on both channels, so **Receive plugin testing versions** updates to the same
0.6.2.1 and nobody is left behind on 0.6.1.2. **Your protected folders carry over - there is
nothing to re-tick.** Every folder you had protected becomes a protected folder rule, which means
exactly what it did before, so a library you do not touch sorts exactly as it did on 0.6.1.1.

To go back to 0.6.1.1, reinstall it from the plugin list. **Your protected folders survive the way
back**, because this build keeps writing them in the format 0.6.1.1 reads. Folders you marked
*excluded* are the one thing that does not survive: 0.6.1.1 has no way to express an exclude, so
those folders simply return to inheriting from their parent. If you then update again, everything
you protected on either version is still protected.

## Also in this build

- Which folders you have open in the Protect tab is saved between sessions.
- The note beside a protected mod now names the folder whose rule is actually protecting it, which
  with nested rules is not always the nearest protected folder above it.

1480 automated tests pass, up from 1442 on the 0.6.1.2 testing build.

## Release body (short form)

> Penumbra Organizer 0.6.2.1: you can now protect a folder and still exclude one subfolder inside it
> so it sorts, and the Protect tab's folder list is a collapsible tree with per-folder counts and
> search. Includes the Unprotect N inside button from the 0.6.1.2 testing build. Your protected
> folders carry over, and survive going back to 0.6.1.1.
> Full notes: https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/0.6.2.1/docs/RELEASE_NOTES_0.6.2.1.md
