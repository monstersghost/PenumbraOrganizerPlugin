# 0.6.6.0 - DRAFT, not published

Nothing in this file is released. It is a draft of the notes for the release after 0.6.5.0.

## New in 0.6.6.0

Both options are off by default, so nothing moves unless you use them.

- **Split emotes by emote** (Sort tab, Animation section): emote mods go to `Animation and VFX/Emotes/<emote name>`. Mods with several emotes go to `Emotes/Multiple`, mods with only facial expressions to `Emotes/Expressions`, and mods with no emote name to `Emotes/Undetermined`.
- **Sort inside folders** (Protect tab): pick a top-level folder such as `.Character specific mods` and its mods are sorted beneath it (`.Character specific mods/Gear/...`) instead of being moved out. Protected mods stay put, and Remove returns the mods to the normal folders on the next sort.

## What moves

- Ticking the emote split moves the affected mods once on the next sort. Unticking and sorting again restores the previous layout.
- Adding a folder to Sort inside folders re-sorts everything under it, replacing your own sub-folders there. History restores the old layout.
- Templates do not follow the emote split. Applying a template keeps mods in a chosen sort-inside folder under it (as for Heliosphere and Proteus in Sort inside mode); the template preview does not show that folder prefix yet, and the workbook export ignores both options.
