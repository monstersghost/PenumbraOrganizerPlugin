# 0.6.3.0 - Body types from the mod itself, a calmer Sort tab, and Heliosphere and Proteus modes

**This is a big one.** This release finds each mod's body type from what is inside the mod, remembers your Sort settings,
lets you decide what happens to Heliosphere and Proteus mods, and tells you which mods an Apply
could not move.

## Body type is read from the mod, not just its name

Many mods do not put the body in the title; it is in an option group, a dropdown choice, or the
first folder of their files. The plugin now looks **inside the mod first** (option group names,
option names, and the first folder of its file paths), then at its name, then at the library folder
it sits in.

When a mod supports several bodies, your **Body priority** picks the winner. It is a ranked list
with a **First-choice body** dropdown that moves one body straight to the top; the full order sits
under it with arrows and **Reset to default**. The order is saved, and it is applied when you press
Sort, so reordering and sorting again re-files mods without a rescan.

- **Split gear by body type** and **Split body mods by body type** are now two checkboxes, so you
  can split one and not the other.
- A **body replacer's own name now beats its option groups.** "Yet Another Body+" ships a Bibo+
  compatibility option and is still a YAB mod; it now files under YAB. "Yet Another Body+" is
  recognised as YAB.
- Mods whose body cannot be found are left alone, as before. Review Changes still shows every move
  before anything happens.

## A calmer Sort tab

The Sort tab was getting crowded, so it is now three sections: **Group by** and the **Sort** button
are always visible; **Gear, NPC and Hair** and **Body type** sit below. The small **(?)** next to a
checkbox explains it. Controls that do not apply (for example Priority race when hair splitting is
off) are hidden, and everything greys out when Group by is Creator, because grouping by creator
never looks at a mod's type.

- **Your Sort settings are remembered** between sessions: Group by, every checkbox, the hair race,
  the Midlander fallback and the body priority. They used to reset every time.
- **Manual assignment** is collapsed by default.
- The NPC wiki controls, which were switched off in this version anyway, are gone.

## Review Changes

- A **Workbook** group holds Group workbook by, Export Workbook, Import Workbook and Open Workbook.
  Import Workbook moved here from the Sort tab and now works any time a scan is loaded.
- **Apply** is the purple button, set apart from the rest.
- **Diagnostics** is collapsed by default.
- **Show failed mods.** When an Apply could not move some mods, a button next to the result opens a
  small window naming each mod, the reason Penumbra gave (for example `PathRenameFailed`), and the
  path it was being moved to. It is also under Diagnostics.

## Heliosphere and Proteus mods

[Heliosphere](https://heliosphere.app/) and [Proteus](https://github.com/solona-m/proteus) both
find their mods by directory rather than by where they sit in Penumbra's tree, so moving them is
safe. You choose what the plugin does, per tool, on the **Protect** tab:

- **Protected** (the default): left exactly where they are.
- **Sort inside their folder**: sorted like any other mod, but kept beneath the top-level folder they
  already sit in, for example `Proteus Upscales/Gear/Top/...`.
- **Treat as normal**: sorted like everything else.

The dropdown for a tool appears only when your library has that tool's mods, and the choice is
saved. Proteus's own managed mod, named `Proteus`, can be placed at the top of your library with
**Place the Proteus managed mod at the root**. Mods you protected yourself, or that sit in a
protected folder, stay protected whatever the mode says. The old "Protect/Unprotect Heliosphere
mods" buttons are replaced by this, and **Protect all, then Unprotect Heliosphere** no longer leaves
the mods protected.

## Templates

- A template's fallback can now include the **hair** and **body type** splits. They are optional in
  the file, so templates made earlier work exactly as before. Which hair race and which body order
  apply is always the importer's own setting.
- The template creator **remembers its fallback choices**, and **saving closes it**.
- Importing a template now respects the Heliosphere and Proteus modes above.

## A first-run walkthrough that points at things

The walkthrough has eight steps now, includes History, and uses current wording. While it runs, the
plugin window behind it switches to the right tab and outlines what to press.

## Fixes

- **A mod sitting where its own folder should go.** When the sort wanted a folder named like a mod
  that already occupied that path (the `Bibo+` mod and a `Body/Bibo+` folder), Penumbra refused the
  move and the mod, plus others heading into that folder, failed with `PathRenameFailed`. The mod is
  now moved aside first.
- Protect all, then Unprotect Heliosphere mods did nothing; fixed (see above).
- Several smaller robustness fixes found in review: a damaged operation record can no longer break
  the Failed mods window, and the History delete confirmation re-checks that it is allowed.

## Known limits

- Restoring a snapshot that moves such a mod back onto a folder path that still exists can fail the
  same way, because Penumbra keeps empty folders. Run Folder Cleanup on the Review Changes tab, then
  restore again.
- The Proteus controls have been tried against test mods, not a library made by Proteus itself. If
  something there looks wrong, tell us on [Discord](https://discord.gg/cAeUjMvvXz).
- The Help tab's **Open the guide as a PDF** button points at this release's tag, so it only works
  once the release exists.

The user guide has been rewritten with screenshots from the real plugin.
