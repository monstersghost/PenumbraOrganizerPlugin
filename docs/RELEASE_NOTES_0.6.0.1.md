# Penumbra Organizer Plugin v0.6.0.1

**This is a testing build, for trying the new Templates feature.** You are seeing it because you
opted in to plugin testing builds. Stable users stay on 0.6.0.0.

## What to try

The **Templates** tab. A template is a folder layout someone shared, as a `.json` file or a share
code. You can now both import one and build your own.

- **Importing:** add a template, preview it against your library, then apply it. You get a folder
  tree with counts and a summary of how many of your mods it matched before anything is staged.
  Nothing is written to Penumbra until you apply from **Review Changes**, as with any sort.
- **Exporting:** **Export my layout as a template...** builds one from your own library.

## Before you export, please read this

**A template contains a list of your mod names, and anyone you send it to can read that list.** That
is how the format works — it is how someone importing it knows which of their mods you have an
opinion about — but it means exporting is publishing.

The export screen opens with everything included and shows you every name that would go out, with a
search box and per-mod and per-folder tickboxes. Go through it before you save or copy anything. If
you would rather a name not be on a list you hand to someone, untick it there.

Share codes are capped by what a chat message holds, so a large library will not fit into one — save
the `.json` and send that instead. The screen tells you the length and disables the button rather
than handing you a code that gets cut off when pasted.

## What would help most

Templates are the first feature here that only works properly between **two** libraries, and that is
the part I cannot test alone. If you and someone else both install this, export from one and import
into the other — that is the run worth reporting on.

Also worth reporting: anything that looks wrong in a preview, a mod placed somewhere you did not
expect, or a template that refuses to import.

## If something goes wrong

Nothing here writes to your library without going through Review Changes and Apply, and Apply still
takes a backup snapshot first, so the usual recovery applies.

If the game closes or the plugin errors, `%APPDATA%\XIVLauncher\dalamud.log` is the file worth
sending — grab it before restarting, since a new session can roll it over.

## Known limitations

- Export describes where your mods are **now**, not any proposals waiting on Review Changes. If you
  have sorted without applying, the template records your current layout. The screen says so.
- Mods sitting loose at the top level of your library are left out, because a template only carries
  folders. The screen tells you how many.
- Folder-label renaming is in the format but has no editor yet, so exported templates do not set it.
