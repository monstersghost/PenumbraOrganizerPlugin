# Manual check: body parts and Check my collection

Load the build, scan a real library, then check each item.

- [ ] Split body mods on: the layout is `Body/00. Skeleton`, `01. Physics`, `02. Tails, Ears and Horns`, then `Body/10. Rue+/00. Replacer`, `01. SFW`, `02. Hands and Feet` (Rue+ first if it is your first-choice body; a reordered priority renumbers the bodies) (check that an Au Ra horn mod and a Viera ear mod both land there: the `/obj/zear/` file rule is a guess). Count the mods per folder on the real library and see that they look right.
- [ ] A replacer that ships a `.sklb` stays in Replacer.
- [ ] Split body mods off: body mods stay as `Body/<mod>`.
- [ ] Check my collection with exactly one body replacer enabled shows that body; Make first choice moves it to rank 1, and it survives a restart.
- [ ] With two bodies enabled, Check my collection says no single body was found and offers nothing.
- [ ] Which collection it reads (your player's collection, or Penumbra's Current when they differ) matches what the message says.
- [ ] Review Changes shows every move before Apply.
- [ ] Carried over, not yet verified: the First-choice dropdown after arrow moves and a restart, and keyboard activation of the (?) popup.
- [ ] Tick Hands and feet in a shared folder (with the slot split) and sort: `Gear/03. Hands` and `Gear/05. Feet` gain the hands and feet mods, and no body folder holds a hands or feet slot.
- [ ] A body that only had hands or feet mods vanishes from the Gear folders.
- [ ] Tick Split hair by race and sort: a one-race hair mod files under its race, and a known multi-race hair mod under `Hair/Multi-race`.
- [ ] Untick both and sort: the old layout returns.
- [ ] Tick Split emotes by emote and sort: emote mods land in `Animation and VFX/Emotes/<name>`, `Multiple`, `Expressions` or `Undetermined`, and nothing is left flat in `Emotes`.
- [ ] Untick it and sort: today's layout returns.
- [ ] After a scan, `[emote-diag]` lines appear in `dalamud.log` with counts per outcome and per emote name.
- [ ] Add `.Character specific mods` (or any folder) under Sort inside folders and sort: the folder stays on top and its mods are sorted beneath it.
- [ ] A protected mod inside that folder stays where it is.
- [ ] Remove the folder and sort: its mods return to the normal folders.
