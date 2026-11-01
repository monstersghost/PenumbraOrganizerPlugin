# Manual check: race-priority hair sorting

Build and load the plugin. Use the real mods named below, from the test set.

## Devilry Hair With Bow (Viera female = 3, others = 108)
- [ ] Split hair on, priority None: the mod sorts to flat Hair.
- [ ] Split hair on, priority Midlander female: Hair/108.
- [ ] Split hair on, priority Viera female: Hair/3.

## Imeris - Solar (hair 115, Viera female = 3)
- [ ] Priority None: flat Hair.
- [ ] Priority Miqo'te female: Hair/115.
- [ ] Priority Viera female: Hair/3.

## Winter Berry for Lalafell (Lalafell male and Midlander = 135, Highlander male = 5)
- [ ] Midlander fallback on, priority None: Hair/135.
- [ ] Midlander fallback off, priority None: flat Hair.

## Lumi's Hairstyle V2 (male Midlander 111, female 110)
- [ ] Midlander fallback on, priority None: flat Hair (the two genders disagree).

## Button and Help
- [ ] The Priority race and Midlander fallback controls are disabled when Split hair is off.
- [ ] Changed Items names: for each mod above, the number in Penumbra's changed-item name matches the number the plugin files under. Record any mismatch.
