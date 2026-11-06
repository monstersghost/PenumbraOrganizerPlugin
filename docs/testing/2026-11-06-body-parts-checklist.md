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
