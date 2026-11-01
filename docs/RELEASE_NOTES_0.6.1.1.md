# 0.6.1.1 - Hair by race, title-bar links, community templates

This is a stable release. It includes everything from the 0.6.1.0 testing build, plus the work below.

## Hair sorting by race

Split hair by hair number now has two more controls on the Sort tab:

- **Priority race.** Pick a race and gender. A hair mod that uses exactly one hair number for that race
  is filed under that number, whatever the other races use. Example: a mod that uses 169 for most races
  and 3 for Viera goes to `Hair/3` with Viera female chosen.
- **Midlander fallback.** When a mod uses different hair numbers across races, this uses the Midlander
  number if male and female Midlanders agree.

Both controls are off by default, and both do nothing unless Split hair is on. A mod that does not
resolve to one number stays in the plain `Hair` folder, as before. A race with two hair numbers in
the same mod never picks one.

**Not yet confirmed:** the hair number is read from the mod's files first. A second source, the EST
entries in a mod's metadata, is also read for the flat number. We have not confirmed in game that
its value is the hair number, so a mod that relies only on EST entries could be filed under the
wrong number. Turn Split hair off to avoid this. Please report any hair mod filed under a number
that is not the hair it replaces, with the mod name and both numbers.

## Sort tab

- Gear, NPC and Hair now sit in separate groups with a rule between them.
- A `?` button next to Sort opens the Help tab at its Sort section.
- Help text now explains the groups, the example folder paths, and when a hair mod leaves the `Hair` folder.

## Title-bar links

Three buttons in the window's title bar open the GitHub repository, the Discord server, and the Ko-fi
page in your browser. They are placed to the left of the Dalamud pin and gear icons.

## Community templates

Templates from the 0.6.0.2 testing build are included: you can export a sorting layout as a template,
share it, and preview an import against your library before applying it.

In a template preview, an entry whose only matching mods are protected is now reported as "skipped,
protected in your library" rather than "you do not have". Protected mods are still left alone by Sort
and Apply.

Templates do not store a hair setting, so applying a template keeps hair flat.

## Upgrading and going back

Install or update through the normal plugin list. To go back to 0.6.0.0, reinstall it from the plugin
list. This build does not change any saved data.

## Release body (short form)

> Penumbra Organizer 0.6.1.1: hair sorting by race with a Midlander fallback, a grouped Sort tab with a
> help button, title-bar links to GitHub, Discord and Ko-fi, and community templates. Hair splitting is
> off by default. Full notes: https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/0.6.1.1/docs/RELEASE_NOTES_0.6.1.1.md
