# Weapons by job (Weapon: Job split) - in-game checklist

Build: `feat/weapon-split`. Keep a copy of `pluginConfigs/PenumbraOrganizer.Plugin.json` before starting. Nothing here has been seen in game yet: every item below is unverified until ticked. The unit tests use a fake item lookup; the real one (the game's Item sheet through Dalamud's IDataManager) runs only in game.

Nothing changes with it off
- [ ] With Weapon left on Recommended (Job off), Refresh mod list and Sort: the weapon-only mods ("[SAM]-...-ONIKAGE SHURA" and the two w2201/w2251 packs) still go to `Gear/00. Sets`, exactly as before. Advanced > Weapon shows the Job toggle off and no "Scan again" line.
- [ ] Export (Review Changes > Diagnostics) has no `WeaponJob:` lines with it off.

Weapon-only mods leave Gear (needs a rescan)
- [ ] Advanced > Weapon: turn Job on. The line "Scan again so weapon-only mods move between Gear and Weapon." appears under the editor; the Job hint says to scan again; the example path reads `Weapon/GNB/...`.
- [ ] Refresh mod list: the "Scan again" line goes away. Sort: ONIKAGE SHURA and the two w2201/w2251 packs leave `Gear/00. Sets` for `Weapon/<job>` (or `Weapon` if no job matched). Apply and check Penumbra.
- [ ] Mixed mods stay Gear: the w2024 pack that also changes a bracelet (`chara/accessory/a0132 _wrs`) stays in `Gear/08. Wrists`; Firefly & Sam and Ganyu - Twilight Blossom stay in Gear.
- [ ] Turn Job off: the "Scan again" line appears. Refresh and Sort: the weapons go back to `Gear/00. Sets`.
- [ ] Select all splits turns Job on (the popup lists "weapons by job").

The item-name lookup (your client language)
- [ ] With Job on, the export shows `WeaponJob: <job>` for each Weapon row. Check the job is right for ONIKAGE SHURA (SAM expected for w2201) and the two packs. Your client language matters: the lookup matches Penumbra's changed-item names against the Item sheet in the client language; if your client is not English, check at least one weapon gets a job (not `(none)`).
- [ ] If every weapon shows `WeaponJob: (none)`, look in the Dalamud log for "Weapon job lookup failed" (the sheet could not be read) - with no warning, the names did not match (for example a suffix Penumbra adds to off-hand items); note one weapon's changed-item names from Penumbra's Changed Items tab.
- [ ] The first scan with Job on builds the item index on the scan thread: note whether that scan is noticeably slower or the game hitches.

Classes, Arcanist, crafters and gatherers
- [ ] A class weapon (a Gladiator sword, a Conjurer cane) files under its job (`PLD`, `WHM`), not the class.
- [ ] An Arcanist/Summoner/Scholar book goes to `Weapon/Multiclass` (Arcanist folds to both SMN and SCH).
- [ ] A crafter or gatherer tool mod (a saw, a pickaxe): check whether it is classified Weapon (tools are under `chara/weapon/`) and which folder it gets (one class such as `CRP`, or `Multiclass` for an item several can use). Note the result; there is no special rule for tools.
- [ ] A mod changing two weapons of different jobs goes to `Weapon/Multiclass`.

Downgrade
- [ ] With Job on, the config's `CategoryLayouts` has a Weapon entry with `"Splits": ["Job"]` and the legacy split fields are unchanged. (An older build drops the split: weapons return to Gear after its next scan.)
