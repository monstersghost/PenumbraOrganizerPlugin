# Manual check: split hair by hair number

Build, load in game, and run a scan over a real library before sorting anything.

## The Est question

This is the one correctness question no unit test can answer.

The implementation reads `SetId` from an `Est` manipulation with `"Slot":"Hair"` as the hair number.
A fixture already in this repo and the implementation agree with that reading, but it has NOT been
confirmed against Penumbra's own source or a real library. Confirming it is the point of this
section. Establish what `SetId` MEANS from Penumbra's own source - its EST manipulation model and
the code that applies it - not from agreement between numbers. Then corroborate against real mods:

- [ ] Pick three hair mods whose `Files` paths name a hair number AND whose config contains an
      `Est` manipulation with `"Slot":"Hair"`.
- [ ] Record, for each, the path number and the `SetId`.
- [ ] Where they agree, `SetId` is corroborated.
- [ ] Where they **disagree**, do not conclude anything on its own. A mod can legitimately carry
      an EST entry for one hair and replacement paths for another, in which case two numbers is
      the correct reading and the mod should file flat - which is what the code already does.
      Settle the meaning of `SetId` against Penumbra's EST manipulation model and the code that
      applies it, then decide. Disagreement alone is not evidence that `SetId` was misunderstood.
- [ ] Drop `CollectFromManipulation` and its Est tests only if `SetId` turns out not to name a
      hair number at all. That is a conservative fallback for that case, not the automatic outcome
      of two numbers differing. Say so in the report either way - it changes the spec.

## Plausibility

- [ ] Scan a library with a decent number of hair mods. Export the diagnostic dump.
- [ ] Spot-check five hair mods against what they claim to replace. The numbers should match the
      hair they say they are, not be off by a constant.
- [ ] Count how many hair mods got a number versus stayed flat. A library where essentially none
      resolved is the signal that would justify the cut-down `HairNumberDiagnostic` the spec left
      out - report the counts either way.

## The checkbox

- [ ] "Split hair by hair number" appears under "Split NPC mods by kind" on the Sort tab.
- [ ] It is unchecked on a fresh plugin load.
- [ ] Selecting "Creator" under Group by disables it, with the tooltip explaining that grouping by
      creator never uses the mod's type.
- [ ] Hovering it shows its own help text.
- [ ] It appears in the Help tab's Sort section with a body.

## Sorting

- [ ] With it ON, review the proposal: numbered hair mods are under `Hair/<number>/`, and
      multi-number ones are directly in `Hair/`.
- [ ] With it OFF, every hair mod is directly in `Hair/`.
- [ ] Toggling it clears the "already sorted" staleness state, so the Sort button acts on the new
      selection rather than reporting no change.
- [ ] Gear and NPC splitting still behave exactly as before, in every combination.
- [ ] Apply the proposal on a small protected-down test selection, then roll it back from the
      History tab. Both complete without error.

## No crash in GetFolder

- [ ] Nothing in the scan or sort throws `ArgumentOutOfRangeException` with a message like
      "Unsupported subcategory". That exception means a hair subcategory reached `GetFolder` in a
      shape `IsHairNumber` rejects, which is a bug in `EnrichHairSubCategory`, not a bad library.
