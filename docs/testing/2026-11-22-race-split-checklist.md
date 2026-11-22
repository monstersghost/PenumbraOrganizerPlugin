# Race split: step 0 evidence gate and manual checklist (2026-11-22)

Spec: docs/superpowers/specs/2026-11-22-race-split-all-categories-design.md

## Step 0 (before any non-hair code is written)

Tick each box and paste what you saw under it.

- [x] A. Import `variant_miqotes_v2.ttmp2` into Penumbra. Open the new mod folder and find its config
      (`default_mod.json`, or `meta.json` under `DefaultData` on Penumbra 1.7+). Confirm it has a
      `Manipulations` array whose entries look like
      `{"Type":"Rsp","Manipulation":{"SubRace":"SeekerOfTheSun","Attribute":"MaleMinSize","Entry":1.0}}`,
      that `SubRace` is a name (not a number), that only `SeekerOfTheSun` and `KeeperOfTheMoon` appear, and
      that there are no `Files` entries.
      Observed (author's real file, 2026-11-22, saved as
      `PenumbraOrganizer.Plugin.Tests/Fixtures/RaceSplit/variant-miqotes-v2.meta.json`): `FileVersion` 4, a
      `DefaultData` object holding only `Manipulations` (28 entries, no `Files` key at all and no `Groups`),
      every entry `{"Type":"Rsp","Manipulation":{"SubRace":"<name>","Attribute":"<name>","Entry":<number>}}`.
      `SubRace` is the enum name, never a number: 14 entries `SeekerOfTheSun` and 14 `KeeperOfTheMoon`.
      `Entry` is sometimes a whole number (`1`) and sometimes a decimal (`0.95`); the reader never reads it.
      So the spec's assumptions hold, and the pack folds to one race, Miqo'te.
- [x] B. In Penumbra's Changed Items tab for the same mod, copy the exact key text of one row. Expect
      `Seeker of the Sun Male Minimum Size` (no `Customization:` prefix).
      Observed (author's screenshot, 2026-11-22): the keys read `<sub-race display name> <attribute display
      name>`, for example `Keeper of the Moon Bust Maximum X-Axis`, `Keeper of the Moon Female Maximum Size`,
      `Keeper of the Moon Female Maximum Tail Length`, `Keeper of the Moon Male Minimum Size`,
      `Seeker of the Sun Bust Maximum X-Axis`. No `Customization:` prefix, and the display names carry spaces
      (`Seeker of the Sun`), which is why the Rsp fold in the code is keyed on the JSON enum names
      (`SeekerOfTheSun`) and never on these. Both Seeker and Keeper appear, nothing else.
- [x] B2. Where the plugin files this mod today. Observed (Review tab screenshot): "Variant Miqotes v2" by Sel
      is proposed to `Gear/00. Sets/Variant Miqotes v2`, so it is classified Gear, as the spec says. With the
      Racial scaling row off it must stay exactly there.
- [ ] C. For one real mod of each of Highlander, Roegadyn, Lalafell, Hrothgar and Viera, copy one exact
      `Customization:` changed-item key (for example `Customization: Viera Female Face 3`). Confirm the race
      word is spelled exactly as in the spec's table. Add each as an `InlineData` row in
      `ChangedItemKeyParserTests.cs` (the existing rows show the format). The Changed Items tab does not
      show the `Customization:` prefix; the race word as shown there is what to copy.
      Library scan (2026-11-22, `C:\Mods`, 287 mods, read-only): single-race single-slot mods exist only for
      Midlander, Elezen and Miqo'te, so none of the five races can be checked from this library.
      Second library (the author's other PC, `F:\XIVMODS`, 2557 mods, scan run on that PC, 2026-11-22):
      single-race single-slot mods exist for Lalafell (face 24, hair 1, body 3), Viera (face 4, hair 2),
      Au Ra (face 5, hair 1, tail 7), Miqo'te and Midlander. Candidates to open in Penumbra's Changed Items
      tab: Viera face `Baoshi Viera` or `Rockstar Viera`; Viera hair `Merikah Male Viera`; Lalafell face
      `Finest Pupil (F Lala)`; Au Ra face `Asym aura Female face`.
      Full scan of the second library over Tailscale (2557 mods, any slots): no mod at all touches only
      Highlander, Elezen, Roegadyn or Hrothgar, so no single-race mod exists for them. Penumbra's own
      `GenderRace` enum confirms the code to race mapping the plugin uses (0301 and 0401 Highlander, 0901
      and 1001 Roegadyn, 1501 and 1601 Hrothgar, 1701 and 1801 Viera), and its race words come from
      the `Race` and `SubRace` name attributes (Elezen, Lalafell, Miqo'te, Roegadyn, Au Ra, Hrothgar, Viera,
      Midlander, Highlander). Real keys have been seen for Midlander, Elezen, Miqo'te and Au Ra (test
      fixtures); still unobserved in a real key: Highlander, Roegadyn, Lalafell, Hrothgar, Viera. One mod
      covers them all: `hs-Universal Ear remover-1.0.0-ctkV` touches the ears slot for all nine races. Open
      it in Penumbra's Changed Items tab and copy one row per race.
- [ ] D. Find one real tail mod and one real ear or horn mod (Miqo'te or Au Ra tail, Viera ears, Au Ra or
      Hrothgar horns). Copy their exact `Customization:` keys. Confirm the body part word is `Tail` or `Ears`.
      If horns use another word, write it here and add it to the part list in Task 5.
      Library scan: four Miqo'te tail mods exist (their names start `Akako_s Tail Jewlery`, `DApoo Ears -
      fluffy tail`, `tail`), so `Tail` can be checked now by opening one in Penumbra's Changed Items tab.
      Second library: 17 Miqo'te tail mods and 7 Au Ra tail mods (for example `hs-Her Dragon Tails`), so
      `Tail` can be checked for two races. Ears slot (`zear`): no single-race ears mod exists, but three
      multi-race mods touch it: `hs-Universal Ear remover-1.0.0-ctkV` (all nine races), `[Startide] Starfox`
      (Au Ra, Hrothgar, Miqo'te, Viera) and `Goo Girls` (Midlander, Highlander, Elezen, Miqo'te, Roegadyn,
      Viera). Open them in the Changed Items tab to read the part word for Viera ears and for the Au Ra and
      Hrothgar rows. Still open: whether Au Ra or Hrothgar horns use `Ears` or another word.
- [x] E. Search your library for any mod folder whose group files contain a `Files` key ending `.rgsp`.
      Write "none found" or list them. If any exist, the racial-scaling reader needs a path rule too: stop
      and update the spec.
      Result (2026-11-22, `C:\Mods`, 287 mods, every config readable): none found. 0 raw `.rgsp` files on
      disk and 0 configs that mention `.rgsp`, so no rgsp path rule is needed for this library. On XMA, the
      Files tabs of seven racial-scaling packs name archives only (four `.ttmp2`, one `.rar`, one `.zip`,
      two Mega links), so the `.rar` and `.zip` contents are unchecked.
      Extra real finding: only two mods in the library have an `Rsp` manipulation. One is Variant Miqotes v2.
      The other is `tiramisu2 beta [Physics]`: a physics mod with 240 file entries whose option group
      "Viera Breast Fix" holds 12 `Rsp` entries inside option "Enabled" (not in `DefaultData`), with
      `SubRace` values `Rava` and `Veena`. So (1) the Viera enum names are confirmed in real data,
      (2) Rsp entries really do sit inside option groups, which the walker visits, and (3) this mod is
      impure (it also has file entries), so it must NOT be flagged racial scaling. It is the real example
      for the impure test.
      Second library (2557 mods): again 0 raw `.rgsp` files. Ten mods have `Rsp` manipulations: eight
      `tiramisu2` variants (ear jiggle, tail and physics bundles, all expected impure), `Body Scale 2.0`
      (AkiraDesign) and `Ultimate Height Mod` (IZUMI).
      Full scan over Tailscale (2557 mods, configs parsed): 0 configs mention `.rgsp`. The ten Rsp mods:
      - `[AkiraDesign] Body Scale 2.0`: 476 Rsp entries, 0 other manipulations, 0 file entries, so PURE.
        Eight races (every race except Lalafell), 14 sub-race names.
      - `[IZUMI] Ultimate Height Mod`: 25,726 Rsp entries, 0 other manipulations, 0 file entries, so PURE.
        All nine races, 16 sub-race names. Very large: it must not slow the scan noticeably.
      - Eight `tiramisu2` variants: 12 Rsp entries each (Rava, Veena, so Viera) plus tens of thousands of
        file entries across their options, so IMPURE and must not be flagged.
      Expected after Task 5: `Body Scale 2.0` and `Ultimate Height Mod` are flagged racial scaling with
      several races (so `Multi-race` with the toggle on, a priority pick with it off); the `tiramisu2`
      variants carry Viera evidence but are not flagged.
- [ ] F. Check a real face mod with an iris option: its keys should include `Face (Iris)` and `Face`.
      Confirm an iris-only mod shows only `Face (Iris)` rows.
      Library scan: four iris-only Miqo'te face mods exist (names start `Akako's Glowy Eyes`, `Akako's red
      Eyes`, `Akako_s Glowy Eyes`, `Akako_s red Eyes`): every `chara/human` path in them is an `_iri` file.
      Open one in Penumbra's Changed Items tab and confirm it shows only `Face (Iris)` rows. Second library:
      8 iris-only face mods (for example `Shiny Eyes`, `Black Sclera - Wet`).
- [x] G. A second real racial-scaling pack, in the older Penumbra folder layout. Source: the author's extract
      of XMA's "Viera Height Diversity" (2026-11-22), saved as
      `PenumbraOrganizer.Plugin.Tests/Fixtures/RaceSplit/viera-height-diversity/` (four JSON files).
      Observed: `meta.json` has `FileVersion` 1 and no `DefaultData`; `default_mod.json` is empty (`Files`,
      `FileSwaps` and `Manipulations` all empty); two group files ("Taller Males", "Shorter Females"), each
      with two options (Veena, Rava) whose `Manipulations` hold the `Rsp` entries (`SubRace` `Veena` or `Rava`,
      attributes like `MaleMinSize`); no file entries anywhere. The extract also had an EMPTY folder
      `chara/xls/charamake/rgsp/` and no `.rgsp` file, so a converted pack leaves no raw rgsp behind (a second
      data point for item E). So the reader must handle both layouts: DefaultData plus Groups (1.7+), and
      `default_mod.json` plus `group_*.json` (older); the walker already does.

## After the feature is built (in game)

- [ ] Master off: Sort proposes exactly the same folders as before the update (compare a saved
      Export before and after).
- [ ] Master on, Hair only ticked (the migrated state for an old hair-race user): same result as the old
      "Split hair by race" checkbox.
- [ ] Tick Face, Skin, Tails/Ears/Horns: run Scan, then Sort. One-race mods file under `Face/<Race>`,
      `Skin/<Race>`, `Body/.../<Race>`. Several-race mods file under `Multi-race`.
- [ ] Untick "Send multi-race mods to Multi-race", rank Viera first, Sort: a Viera plus Miqo'te mod files
      under Viera.
- [ ] Racial scaling: with the row off the pack stays in Gear; with it on (and Split body mods on) it
      files under `Body/03. Racial Scaling/Miqo'te`.
- [ ] Review tab Export shows a `StagedRace:` line for race-governed mods with its reason.
- [ ] Scan again on the library with `[IZUMI] Ultimate Height Mod` (25,726 Rsp entries): the scan does not
      noticeably slow down, and that mod and `[AkiraDesign] Body Scale 2.0` file under
      `Body/03. Racial Scaling/Multi-race` with the row on and the multi-race box ticked.
- [ ] The eight `tiramisu2` variants (Rsp plus file entries) are NOT moved to Racial Scaling.
- [ ] Downgrade then upgrade: the hair setting survives; the other rows, priority and toggle reset.

## Headless results (2026-11-22, real libraries, read-only)

Run by `PenumbraOrganizer.Plugin.Tests/Harness/RealLibraryHarnessTests.cs` (opt-in: set `PENUMBRA_HARNESS_LIBS`).
It reads each mod's real files; it cannot see Penumbra's changed-item keys, so Face/Skin/Tails evidence is NOT covered
and these results do not replace the in-game ticks above. Libraries: `C:\Mods` (287 mods) and the other PC over
Tailscale (2557 mods).

- Master off sorts exactly like the default: 0 differences over 287 and over 2557 mods.
- Master on with only Hair ticked equals the old "Split hair by race" checkbox: 0 differences (11 and 155 mods differ
  from no hair split, so the comparison is not vacuous).
- Racial scaling, big library: `[IZUMI] Ultimate Height Mod` (9 races) and `[AkiraDesign] Body Scale 2.0` (8 races)
  are flagged; row off they stay in `Gear/`; row on with multi-race they file under
  `Body/03. Racial Scaling/Multi-race/`; row on with multi-race off and priority Viera, Miqo'te they file under
  `Body/03. Racial Scaling/Viera/`. `Variant Miqotes v2` (local) files under `Body/03. Racial Scaling/Miqo'te/`.
- All eight `tiramisu2` variants carry Viera evidence and are NOT flagged or moved; `tiramisu2 beta [Physics]` (local)
  likewise.
- Scan speed: the 25,726-entry IZUMI mod reads in 152 ms; 2557 mods in 124 s over the network share, slowest mod 268 ms,
  0 failures. (Time is dominated by the share, not by Rsp parsing.)
- Still needs the game: items C, D, F, the Review tab `StagedRace:` line, Face/Skin/Tails routing, downgrade/upgrade.
