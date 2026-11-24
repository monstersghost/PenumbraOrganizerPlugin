# Weapons by job: feasibility and design (2026-11-25)

Status: NOT BUILT. Feasibility checked against real data first, as agreed; the evidence below says the split cannot be built reliably from what the plugin has today. No engine, layout, UI or help change was made. This note records why and what would make it possible.

## The request

An optional split for the Weapon category, Weapon/<Job>/<mod> (Weapon/AST, Weapon/DNC, Weapon/Multiclass), matching a tester's hand-made library (Equipment/Weapons/AST, DNC, DRK, GNB, MCH, NIN, PCT, RDM, ~Multiclass). Off in Recommended, on in Select all splits; unknown job means no job folder.

## Finding 1: no mod is ever classified as Weapon

- ModCategory.Weapon exists (it comes from the linked Core enum) and the layout model, workbook table and example paths know the category, but nothing produces it. ModTypeClassifier.Classify has no weapon rule.
- Penumbra's changed-item key for a weapon is the bare item name (no prefix, no suffix), so ChangedItemKeyParser gives it the Gear shape and Rule 1 ("Gear wins") files it as Gear.
- ModEquipmentFileReader only resolves chara/equipment and chara/accessory slot tokens. A weapon mod (files under chara/weapon/wNNNN) gets no slot, so it lands in the Gear sets folder.

Real data:
- The author's export (287 mods): zero rows with Category Weapon. The weapon mods are Gear with GearSlotDiagnostic ZeroEvidence and ProposedPath Gear/00. Sets/... : "[SAM]-...-ONIKAGE SHURA" (24 files under chara/weapon/w2201) and two Chinese-named mods whose names mean "6-star weapon" and "skin weapon" (files under chara/weapon/w2201 and w2251). A third ("... skin weapon" with a bracelet, w2024 plus chara/accessory/a0132 _wrs files) is filed as Gear/08. Wrists.
- The tester's workbook: 30 rows sit in Equipment/Weapons/<job>. Their plugin "mod type" is Gear for most, Others for 3 (for example "MCH Off Hand Be Gone", "[Machinist] Invisible Omnitool") and NPC for 3 ("Red Amazing Manderville Rapier", "Tsukuyomi's Lucky Money", "Phoenix & Bahamut Wings"). None is Weapon. His job folders were made by hand, not by a Sort.

So a split inside the Weapon category would never fire for anyone: the category is always empty. Building it needs a weapon classification first, and that moves every weapon mod from Gear to Weapon for every user, which is a behaviour change on its own and needs the author's decision (and its own golden update, release note and help).

## Finding 2: the job is not in the data the plugin has

- Changed-item keys are item names ("Asphodelos Talekeeper" style). Penumbra's key carries no job or class.
- Neither this repo nor the linked Core has any item-to-job data: no ClassJob, Lumina or IDataManager use, no job table. The only job word in the code is the example path "Gunbreaker Blade".
- The weapon model id (wNNNN) groups by weapon family, which matches one job, but the only family confirmed from real files is w2201 for a mod labelled [SAM]. Writing the full wNNNN-to-job table from memory would be the large unverified table this work was told not to invent.
- Mod names: on the tester's 30 job-filed weapons a conservative match on a job abbreviation or full job name finds 6 ("AST hide 2nd hand weapon accessory", "AST Off Hand Be Gone", "Ludwig - A DRK Weapon", "MCH Off Hand Be Gone", "[Machinist] Invisible Omnitool", "Pictomancer's Mado"), about one in five. "Rune Blade for Redmage" and "Immaculate - Astro Weapon" need spelling variants and nicknames. The rest ("Asphodelos Talekeeper", "Rosethorne", "BJ-2 Combat Knife", "Totentanz - Chakrams", "Garlean Gunblade Replacer") name no job at all; some name a weapon type, which would be another hand-made table with traps ("St. Abigail - Daggers and Sword" is NIN, not Multiclass).

A name-only split would leave about four in five weapons directly under Weapon, on top of Weapon never being assigned. That is not the feature the tester's library shows.

## What would make it buildable

1. Weapon classification (structural, reliable): a mod whose files are under chara/weapon/ and that has no chara/equipment or chara/accessory files is a weapon. ModEquipmentFileReader already walks those files, so this is cheap. Mixed mods (a weapon plus a bracelet, a set plus its weapon) stay Gear. This is the author's call because it moves existing mods; it could ship on its own, or be tied to the split toggle.
2. Job from game data, at scan time, inside the game: Dalamud's IDataManager gives the Item sheet, and each Item row has a ClassJobCategory listing the classes and jobs that can equip it. The changed-item keys are item names in the client language, the same language as the sheet, so a name lookup (or Penumbra's item object, if its id is reachable) gives the job set with no hand-made table. Classes fold into their job (a Gladiator sword is PLD); a row with several jobs (Arcanist books: SMN and SCH; crafter and gatherer tools) is Multiclass; a mod whose items disagree is Multiclass; no match is no job folder. The lookup has to be injected into ScanProcessor (it runs off the framework thread) so it can be faked in tests, and it can only be verified in game.
3. Then the split itself, as originally specified: a new LayoutSplits member for Weapon stored only in CategoryLayouts (no legacy flag, so a downgrade drops it), off in Recommended, on in Select all splits, added to the layout fingerprint only when set so today's fingerprint string is unchanged, a help topic, and an example path.

Steps 1 and 2 are the real work; step 3 is the easy part the request had in mind.
