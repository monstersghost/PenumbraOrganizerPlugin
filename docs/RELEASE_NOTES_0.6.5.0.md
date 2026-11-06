# 0.6.5.0 - two more split options

Builds on the 0.6.4.0 testing build (numbered gear and body folders, padded hair, collection check,
Unprotect fix; see [its notes](RELEASE_NOTES_0.6.4.0.md)). Both new options are off by default, so
nothing moves unless you tick them.

- **Hands and feet in a shared folder** (Sort tab, needs the gear slot split and the gear body split): gloves, gauntlets, boots and nails go to `Gear/03. Hands` and `Gear/05. Feet` instead of under each body. Tops, legs and sets stay under their body, and a body that only had hands or feet mods loses its folder and number.
- **Split hair by race**: hair made for one race goes to `Hair/<race>` (`Hair/Viera`), with the number below it when the hair number split is on. Hair for several races, or with no race information, goes to `Hair/Multi-race`.
- Heliosphere and Proteus mods set to sort inside their folder now get their own numbering, so they no longer leave gaps in the numbers of your plain folders.

## What moves

- Ticking either option moves the affected mods once on the next sort. Unticking and sorting again restores the previous layout, and History undoes an applied sort.
- Templates and the workbook export do not follow these two options yet.
