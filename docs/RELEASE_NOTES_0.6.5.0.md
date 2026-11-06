# 0.6.5.0 - numbered folders, body folders and two more split options

0.6.4.0 was a quiet testing build and was never announced, so this release covers both. Full detail
for 0.6.4.0 is in [its notes](RELEASE_NOTES_0.6.4.0.md).

## New in 0.6.5.0

Both options are off by default, so nothing moves unless you tick them.

- **Hands and feet in a shared folder** (Sort tab, needs the gear slot split and the gear body split): gloves, gauntlets, boots and nails go to `Gear/03. Hands` and `Gear/05. Feet` instead of under each body. Tops, legs and sets stay under their body, and a body that only had hands or feet mods loses its folder and number.
- **Split hair by race**: hair made for one race goes to `Hair/<race>` (`Hair/Viera`), with the number below it when the hair number split is on. Hair for several races, or with no race information, goes to `Hair/Multi-race`.
- Heliosphere and Proteus mods set to sort inside their folder now get their own numbering, so they no longer leave gaps in the numbers of your plain folders.

## From 0.6.4.0 (testing build)

- **Numbered gear folders:** with both gear splits on, `Gear/` is `00. Sets`, `01. Head` to `09. Rings`, then numbered body folders from `10.` in Body priority order, each holding the same numbered slot folders.
- **Numbered body folders:** with **Split body mods by body type** on, `Body/` is `00. Skeleton`, `01. Physics`, `02. Tails, Ears and Horns`, then bodies from `10.`, each holding `00. Replacer`, `01. SFW`, `02. Hands and Feet`.
- **Hair folders are three digits** (`Hair/002`).
- **Check my collection** (Sort tab, Body priority) suggests a first-choice body from the mods enabled in your collection. It only suggests; you confirm.
- Fixed: the **Unprotect N inside** button on the Protect tab now receives clicks.

## What moves

- The first sort after updating moves gear, body and hair folders once. History restores the old layout.
- Ticking either new option moves the affected mods once on the next sort. Unticking and sorting again restores the previous layout.
- Template `folderLabels` keyed to the old subfolders (`Gear/Legs`, `Gear/Bibo+`, `Hair/2`) stop matching, and folder protection rules naming old gear folders no longer match. Earlier workbooks use the old names.
- Templates and the workbook export do not follow the two new options yet.
