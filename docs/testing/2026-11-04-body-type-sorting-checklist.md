# Manual check: split gear and body mods by body type

Build, load in game, and run a scan over a real library before sorting anything. Nothing here is
covered by the unit suite — every item is something only the game or a real library can answer.

## The `+` question

This is the one correctness question no unit test can answer, and it affects most of the list:
**12 of the 30 body labels contain a `+`**, and three carry other punctuation (`Tight&Firm`,
`TBSE-X` is folded away but `Re[de]fined Lalafell` has brackets).

`PenumbraPathSemantics.FixName` mirrors Penumbra's own rule as trim, `/` to `\`, empty to `<None>`,
and a multi-level type folder is deliberately never passed through it. On that reading `+`, `&` and
`[` `]` all survive untouched. That has been read off the mirror, NOT watched round-trip.

- [ ] Sort with the body split on so at least one `Gear/Bibo+/...` path is proposed. Apply it.
- [ ] Confirm in Penumbra that the folder is literally `Bibo+`, not `Bibo` or `Bibo_`.
- [ ] Rediscover Mods, or restart the game, and confirm the folder is still `Bibo+` — a name that
      survives the write but not the reload is the failure mode that matters.
- [ ] Repeat for `Tight&Firm` and `Re[de]fined Lalafell`, whose `&` and brackets are the other
      characters at risk.
- [ ] If any of them is rewritten, the fix is in `ModTypeClassifier.IsBodyTypeLabel` and the label
      set, not in the sort — report which character and what it became.

## Plausibility on a real library

- [ ] Scan a library with a decent number of gear mods. Export the diagnostic dump.
- [ ] Count how many gear mods got a body versus stayed flat. Essentially none resolving is the
      signal that the alias list is wrong for how your library is named; report the counts either
      way.
- [ ] Spot-check ten mods that DID match. Each should be gear actually made for that body, not a
      mod that merely has the word in its title.
- [ ] Spot-check ten that did NOT match and whose titles name a body. Each is either a body spelled
      a way the list does not know — worth adding — or correctly excluded.

## The exclusions, which are the point of the gate

- [ ] A **weapon** mod whose name contains a body name is NOT filed under a body. "Artemis Bow" is
      the worked example; any weapon named after a body will do.
- [ ] A **hat or helmet** is not filed under a body.
- [ ] An **earring, necklace, bracelet or ring** is not filed under a body.
- [ ] A top, dress, trousers, gloves or shoes mod named after a body IS filed under it.

## Known false positives, by design

The list keeps loose aliases on purpose: a missed body is invisible, a wrong one is a visible row.

- [ ] Confirm the Review tab shows every proposed move before anything is applied, and that a
      wrongly filed mod can be corrected there.
- [ ] `artemis` is the one bare common word knowingly kept, because it is the only name that body
      has. A dress named after the goddess will be filed under the body. Confirm that is tolerable
      in practice; if not, remove the alias and the body becomes undetectable — that is the trade.

## The checkbox

- [ ] "Split gear and body mods by body type" appears under its own "Body type" heading on the Sort
      tab, below the hair controls.
- [ ] It is unchecked on a fresh plugin load.
- [ ] Selecting "Creator" under Group by disables it, with the tooltip explaining that grouping by
      creator never uses the mod's type.
- [ ] Hovering it shows its own help text.
- [ ] It appears in the Help tab's Sort section with its description shown.
- [ ] Ticking it and pressing Sort changes the proposed paths; unticking and sorting again restores
      exactly the paths you had before, with no body folders anywhere.

## The user list

- [ ] With no `body-type-list.json` present, the plugin works and creates no such file.
- [ ] Add one with a new body and confirm a mod named after it is filed under the new label.
- [ ] Add one whose `Label` matches a shipped body with a shorter alias list, and confirm the
      shipped aliases are REPLACED rather than merged — removing a bad alias is the reason this
      behaves that way.
- [ ] Corrupt the file and confirm the scan still completes, uses the bundled list, and says so in
      the log.

## Scale

Measured off-game over 8,000 synthetic mods, the body matching added about 90 ms to the scan, within
a stage dominated by reading mod configs from disk. That measurement was on Linux with no antivirus.

- [ ] On a large real library, confirm the scan does not take noticeably longer with the body list
      in play than it did before. Windows real-time antivirus scanning many small config reads is
      the most likely source of a difference the off-game measurement would not show.

## Content detection and priority

Body is now read from the mod's options and files (tier 1), then its name, then its library folder.

- [ ] Rescan and compare the per-body counts with the previous build. Expect some mods that filed
      by name before to move; confirm each move is explained by the mod's options.
- [ ] Sort `[TPM] Succubus Lifestyle (YAB - Rue)` and `Heartbeat for Bibo & YAB+` under two
      different Body priority orders, re-sorting WITHOUT a rescan. Each should follow the order.
- [ ] Reorder the priority, restart the game, and confirm the order persisted (also the hair
      priority race and Midlander fallback).
- [ ] Find a mod with no body in its name and confirm it is filed from its options or files.
- [ ] Confirm the Review tab shows the moves before anything is applied.
- [ ] To see why a mod landed where it did, copy the diagnostic dump and read its `BodyEvidence:`
      line (candidates by tier); after a sort also `StagedBody:` and the "Body priority used for
      the last sort" header. A Body mod whose config could not be read shows `ContentReadFailed`
      and falls back to its name.
