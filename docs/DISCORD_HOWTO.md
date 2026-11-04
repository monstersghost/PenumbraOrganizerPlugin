# Discord how-to posts

Ready-to-paste copy for the server's how-to channel, current as of **0.6.2.1**. Kept in the repo so
it is updated with a release instead of rewritten from memory.

Each block between the `=== MESSAGE ===` markers is one Discord message, written in Discord's own
markdown and sized to fit the 2000-character limit. `python docs/check-discord-posts.py` re-checks
the lengths after an edit.

Attach the named image from `docs/images/` to that message before sending. Discord shows an
attached image under the message, so the text reads first and the picture illustrates it.

Suggested order: post 1 pinned at the top of the channel, then 2 to 5 in sequence. Post 6 is the
update note for whatever channel you announce releases in.

---

=== MESSAGE === 1. What it is and how to install
*attach: none*

**Penumbra Organizer — what it is**

A Dalamud plugin that sorts your installed Penumbra mods into folders for you: by creator, by mod
type, or both. You can also split gear by equipment slot, NPC mods by kind, and hair by the hair
number it replaces.

It never touches your mod files. It asks Penumbra to change a mod's *folder path* — the same thing
you do by dragging a mod in Penumbra's own window. Your `.pmp` files, textures and physical folders
stay exactly where they are.

**Nothing moves until you say so.** Sorting only ever *proposes* a layout. You look at the whole
list of current-folder-to-proposed-folder first, and only **Apply** asks Penumbra to move anything.

**Installing**

1. `/xlsettings` → **Experimental** tab
2. Paste this into Custom Plugin Repositories and click **+**:
```
https://raw.githubusercontent.com/monstersghost/PenumbraOrganizerPlugin/main/repo.json
```
3. **Save and Close**, open the Plugin Installer, find **Penumbra Organizer**, install.

Do **not** install it by unzipping a release into `devPlugins` — it will not update, and it can
conflict with the repo version. If you did that before, delete that copy first.

Needs Penumbra installed and enabled. Every tab talks to Penumbra, so if Penumbra is not running
you will get a connection error instead of a mod list.

Full guide, with a screenshot of every tab:
<https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/main/docs/USER_GUIDE.md>

=== MESSAGE === 2. Your first sort
*attach: sort.png*

**Your first sort, start to finish**

**1. Scan tab → Refresh mod list.**
Reads every installed mod from Penumbra and works out what each one is. Do this first, and again
any time you install or remove mods — nothing in the plugin updates by itself.

**2. Sort tab → pick a grouping → Sort.**
- **Creator** → `Nyx/Aetheric Bob`
- **Mod type** → `Hair/Aetheric Bob`
- **Type then creator** → `Hair/Nyx/Aetheric Bob`
- **Creator then type** → `Nyx/Hair/Aetheric Bob`

Then the optional splits: gear into `Gear/Feet`, `Gear/Head` and so on; NPC mods into NPCs, Bosses
and Enemies; hair into `Hair/115`, the vanilla number it replaces.

Pressing **Sort** moves nothing. It fills in a proposed folder for every unprotected mod.

**3. Review Changes tab.**
Current path beside proposed path, for every mod. Read it. If two mods would land on the same path,
or something touched a mod you protected, it says so in red and **Apply** stays disabled until you
deal with it — there is a one-click **Protect & Skip All Blocking Mods** for that.

**4. Apply.**
Confirms how many mods will move, then asks Penumbra to move them. It takes a snapshot of your
library first, so this is undoable.

A mod the plugin cannot classify goes to `Review/<name>` instead of somewhere surprising, so you
can find it and file it by hand.

=== MESSAGE === 3. Protecting things you do not want moved
*attach: protect-tree.png*

**Keeping things where they are**

Three things can protect a mod, and the Protect tab tells you which one applies:

- **its own checkbox** in the Mods list
- **a folder rule** — the note reads `(via folder: Gear)`
- **Heliosphere manages it** — shown as `(Heliosphere)`, and re-applied on every scan, because
  Heliosphere owns where its mods live

**Folders are a tree.** Each folder has one box that cycles through three states as you click it:

`[ ]` no rule — follows whatever the folder above it says
`[x]` protected — nothing in here or below it moves
`[-]` excluded — this one may be sorted, **even if a parent folder is protected**

**The nearest rule wins.** That is the bit that is new in 0.6.2.1: protect `haloween`, exclude
`haloween/animation`, and that subfolder sorts while everything else in `haloween` stays put. Before
this you had to choose between protecting the parent and sorting the subfolder.

A **dim** box means the state came from a folder above — hover it to see which. A **solid** box
means that folder sets its own rule. So going from an inherited `[x]` to `[-]` takes two clicks: the
first gives that folder its own rule, the second flips it to excluded.

**What an exclude does not free:** a mod you ticked yourself, or a Heliosphere mod. Those are your
decisions, not the folder's. Use the **Unprotect N inside** button on that folder to clear those
too — it says how many it can free and asks first.

=== MESSAGE === 4. Undo, and why this is safe to try
*attach: history.png*

**If you do not like the result**

**Every Apply snapshots your library first.** So does every Restore. Open the **History** tab, pick
the snapshot from just before, click **Restore**, and you are back.

Restore shows you exactly what it will do before you confirm: how many mods move, how many are
already in place, how many installed since the snapshot get moved to the Penumbra root, and how
many from the snapshot are gone and will be skipped.

**Restore ignores protection on purpose.** A snapshot is a record of where things were, and the
protection you set up afterwards should not be able to trap you. If any protected or Heliosphere
mods are about to move, it warns you with a count first.

You can also hit **Create Backup** any time, with a label, before trying something you are unsure
about. History survives restarts.

So the honest summary: the worst case is that you sort your library into a layout you dislike, and
getting back is two clicks. Nothing here deletes a mod or edits a mod file.

=== MESSAGE === 5. Two things people miss
*attach: search.png*

**Two things worth knowing**

**1. Search — find the mod that changes an item.**
The **Search** tab is a reverse lookup over every installed mod, enabled or not. Click
**Build/Refresh Index** once (it is separate from the Scan tab's scan), then type part of an item
name into **Item contains**. Click a mod on the left to see everything it changes on the right.

Useful for "which of my 1700 mods is changing *this* glove", and it never moves or protects
anything.

**2. Templates — share a layout, or use someone else's.**
Import a `.json` someone shared, **Preview against my library** to see what it would do to your
mods, then apply it like any other sort. Mods the template's author never had get placed by their
fallback grouping instead of being left behind.

If you export your own layout to share: **a template contains a list of your mod names, and whoever
you send it to can read that list.** That is how matching works, but it means exporting is
publishing. The export screen starts with everything included and lets you untick anything you would
rather not share — go through it rather than past it.

=== MESSAGE === 6. Update note for 0.6.2.1
*attach: protect-tree.png*

**Penumbra Organizer 0.6.2.1 is out** — update through the normal plugin list.

**You can now protect a folder and still exclude one subfolder inside it.** Each folder in the
Protect tab has a box that cycles protected → excluded → inherit, and the nearest rule on a path
wins. So `haloween` protected plus `haloween/animation` excluded means that one subfolder sorts and
the rest stays put. That was not possible before — protection only ever flowed downward.

**The folder list is a tree now.** Collapsible, with per-folder counts of how many mods are
protected out of how many it holds, it remembers what you left open, and the search box opens
matching branches for you. If you sorted hair into per-number subfolders you will notice the
difference immediately.

Also in this one: the per-folder **Unprotect N inside** button from the 0.6.1.2 testing build is now
stable.

**Your protected folders carry over** — nothing to re-tick. And if you roll back to 0.6.1.1 they
survive the trip; only folders you marked *excluded* are lost, since the older build has no way to
express one.

Testers: this is on both channels, so the testing build updates to the same 0.6.2.1.

Full notes:
<https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/0.6.2.1/docs/RELEASE_NOTES_0.6.2.1.md>

=== END ===
