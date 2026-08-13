# Penumbra Organizer Plugin v0.6.0.2

**This is a testing build.** You are seeing it because you opted in to plugin testing builds.
Stable users stay on 0.6.0.0.

Follow-up to 0.6.0.1, from two tester reports. Templates are unchanged apart from one fix.

## Heliosphere protection could briefly stop recognising a mod

Heliosphere mods are protected automatically, and that was decided partly by looking for a
`heliosphere.json` file in the mod's folder. When Heliosphere updates a mod it removes the old folder
and writes a new one, so for a moment that file is not there. A scan in that window saw an ordinary
mod, and a later Apply would have offered to move it out of where Heliosphere had put it.

Detection now also uses Heliosphere's `[HS] ` name prefix, and remembers every mod it has ever
recognised as Heliosphere's, so a missing file cannot quietly drop protection. It only ever errs
towards protecting: if something is protected that you would rather organise yourself, untick it on
the Protect tab.

**This did not cause the report that led to me finding it.** That library's mods were all correctly
protected and none had been moved by the plugin. It is a real gap that was found while checking, not
a confirmed cause of anything.

## Protected mods sitting outside every folder are now pointed out

The Protect tab now tells you when protected mods are at the very top level of your library. That
combination goes nowhere on its own: protection means the plugin never proposes a path for a mod, so
sorting will never file those away, and they stay at the top level indefinitely.

Heliosphere installs new mods there, and they are protected by the same scan that first notices them,
so this happens without you doing anything wrong. Untick one and it sorts like anything else.

## Apply now leaves a trail in the log

Apply wrote nothing to `dalamud.log` at all, which made a report about mods moving very hard to look
into — the one operation that moves files left no record. It now logs what it is about to do, how
many mods it is skipping as protected, and how it finished.

The diagnostic dump's **Last Apply result** was also broken outright: it always said no Apply had run
this session, even right after one. It reports properly now.

## Templates: exporting before a scan

**Export my layout as a template...** is disabled until you have pressed Refresh mod list. Before,
you could export without scanning and get a template containing your folder structure and no mods —
one was shared, and it looked like a normal template. If you deliberately want to share only a folder
skeleton that still works, and the screen now says plainly that is what you will get.

## What would help most

Still the two-library run: export from one install, import into another. That is the part of Templates
I cannot test alone.

If the game closes or something looks wrong, `%APPDATA%\XIVLauncher\dalamud.log` is the file worth
sending — grab it before restarting, since a new session can roll it over. Apply is actually in there
now.

## Known limitations

- Restore still has no entry in the diagnostic dump's own summary. Its record needs a breakdown the
  plugin does not currently keep, and filling it with zeros would be worse than leaving it blank.
- The remembered Heliosphere list starts filling from your next scan. It does not reach back and
  protect anything retroactively.
- Exported templates still do not set folder renames; the format carries them, the screen has no
  editor for them yet.
