# Split hair by hair number

Date: 2026-11-01
Status: approved, not yet implemented

## What this adds

A third split checkbox on the Sort tab: **Split hair by hair number**. When it is on, a mod
classified `ModCategory.Hair` that unambiguously targets one vanilla hair slot is proposed into
`Hair/115/` rather than flat `Hair/`. When it is off, every hair mod stays in `Hair/`, exactly as
today.

This is deliberately the mirror image of the existing **Split gear by equipment slot**. That
feature already has the semantics this one needs, down to the handling of ambiguity, so the design
below reuses its shape rather than inventing a parallel one.

## Semantics

Decided with the maintainer, 2026-11-01:

- **Exactly one hair number** resolved: the mod gets the subfolder.
- **Two or more** resolved: no subfolder. The mod stays directly in `Hair/`.
- **Nothing resolved, or the read failed**: no subfolder. Also stays directly in `Hair/`.

Multi-number and unresolvable mods are therefore indistinguishable in the proposed layout. That was
chosen over a `Hair/Mixed/` bucket and over a lowest-number-wins rule. It matches
`EnrichGearSubCategory`, which assigns a subcategory only on `equipmentSlots.Count == 1` and
returns the result untouched in every other case.

- **Folder name**: `115`, not zero-padded, so the path is `Hair/115/`. The checkbox label carries
  the "hair number" meaning, so the folder does not repeat it. Note the consequence: Penumbra sorts
  its tree as text, so `9` lands after `115`. Accepted.
- **Race is ignored.** One hair shipped for eight clans contributes one number, not eight. Without
  this, the common case would read as ambiguous and get no subfolder at all.
- **Default off**, like `SplitGear`, and session state rather than persisted config, like the rest
  of `SortPanel`'s selection.

## Reading hair numbers

New `PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs`:

    public static IReadOnlySet<int>? ReadHairNumbers(DirectoryInfo modDirectory)

The return contract is `ModEquipmentFileReader.ReadEquipmentSlots`'s contract, unchanged:

- `null` - some file could not be enumerated, read, or parsed. An untrustworthy partial result,
  treated as "no subcategory", never as a confident answer.
- non-null, possibly empty - every file read successfully; empty means none carried recognized hair
  data.

Two evidence sources, mirroring how the equipment reader uses both `Files` and `Manipulations`:

1. **`Files` keys** matching `chara/human/c<4 digits>/obj/hair/h<4 digits>/`, case-insensitively,
   after normalising backslashes to forward slashes. The captured 4-digit group parses to the `int`
   collected. The `c####` race code is matched but discarded.

2. **`Est` manipulations** whose `Slot` is `Hair`, whose `SetId` is the hair number.

   `Est` is explicitly excluded by `ModEquipmentFileReader`, whose comment reads: "Est also has a
   Slot, but it names a customization slot (Hair/Face), not equipment - deliberately excluded by
   only recognizing the three equipment-relevant types." That exclusion is correct for gear and
   exactly backwards here. Update that comment to point at this reader, so the next person does not
   read it as "Est is never useful."

   **Verification required before this source ships.** The repo's existing manipulation-shape
   comments record that the `Eqp`/`Eqdp`/`Imc` field names were confirmed against two independent
   real mod libraries. The `Est` shape used here has not been confirmed the same way.
   Implementation must dump the `Est` entries from a real library and assert the field names
   against what is actually there. If `Est` turns out not to carry a usable hair number, drop
   source 2 and ship source 1 alone - path matching is the load-bearing source either way.

### Shared traversal

Both readers need the same two things, and those two things carry hard-won knowledge:

- **Config file discovery**: `meta.json` if present, else `default_mod.json` plus `group_*.json`.
  This encodes the Penumbra 1.7.0 (2026-07-20) layout change, and the deliberate decision to
  support both layouts so a build spanning the update does not regress for testers.
- **Recursion** through `DefaultData` (object), `Options`, `Containers`, and `Groups` (arrays),
  reaching each nested `Files` / `Manipulations` pair.

Extract both into `ModConfigFileWalker` in the same folder, yielding each `Files` key and each
manipulation `JsonElement`, and have both readers consume it. This is about 60 of
`ModEquipmentFileReader`'s 182 lines. Duplicating it means the next Penumbra layout change must be
found and fixed twice, in code whose comments say the shapes were expensive to establish.

The walker owns the `IOException or UnauthorizedAccessException or JsonException` handling and the
`null`-on-failure signal; both readers keep their existing public contracts unchanged. The refactor
must leave `ModEquipmentFileReaderTests` passing without edits - that is the check that it
preserved behaviour.

## Plumbing

| File | Change |
|---|---|
| `Classification/ModHairFileReader.cs` | new, above |
| `Classification/ModConfigFileWalker.cs` | new, above |
| `Classification/ModEquipmentFileReader.cs` | consume the walker; fix the `Est` comment |
| `Classification/ModTypeClassifier.cs` | add `EnrichHairSubCategory(ClassificationResult, IReadOnlySet<int>?)`, beside `EnrichGearSubCategory` and with the same guard shape: not Hair, null, or `Count != 1` returns `baseResult` untouched |
| `LibraryWork/Pure/ScanProcessor.cs` | call it beside the gear call, gated on `Category == Hair`, so non-hair mods do no extra disk I/O |
| `Organizer/OrganizerState.cs` | `FlattenHairSubCategory`, beside the other two flatteners; a third `if (!splitHair)` line in `Sub`; `splitHair` parameter on `Sort` |
| `Windows/SortPanel.cs` | `SplitHair` on `SortSelection`; `_splitHair` field defaulting false; checkbox under the gear one, in its own `BeginDisabled` scope with its tooltip after `EndDisabled` (ImGui is positional - follow the existing pattern exactly) |
| `Windows/HelpTopics.cs` | `SortSplitHair = new("sort.split-hair")` |
| `Windows/HelpTab.cs` | the new topic in the Sort section, after `SortSplitGear` |
| `Windows/HelpTopicUsage.cs` | register it |
| `docs/HOW_SORTING_WORKS.md` | document the third split |
| `docs/USER_GUIDE.md` | document the checkbox |

`SortSelection` gains a fourth positional parameter. That breaks existing construction sites, which
is wanted: it makes every caller visible rather than silently defaulting.

`SplitsApply` is unchanged - it reads only `Strategy`, and `CreatorOnly` still consults no
category, so the new checkbox is disabled alongside the other two.

The combination count in `Sort`'s doc comment goes from `1 + (3 x 2 x 2) = 13` to
`1 + (3 x 2 x 2 x 2) = 25`. Update that comment; it is load-bearing documentation of why the old
seven buttons were replaced.

## Out of scope

**No `HairNumberDiagnostic`.** `GearSlotDiagnostic` exists because of a specific tester report - 0
of ~1,500 Gear mods got a subcategory, with no way to tell a library-wide read failure from a
library of legitimately ambiguous outfits. A hair counterpart would touch seven files, including
`ChangedItemIndex`, `ChangedItemIndexSummary` and `LibrarySearchFilter`, and a hair-number facet in
Library Search is a feature nobody has asked for.

It is also less needed: both readers share the walker and read the same files, so a library-wide
read problem is already visible as `GearSlotDiagnostic.ReadFailure` across gear mods. The one blind
spot is "every hair mod is flat and every file read fine", which gear's diagnostic cannot speak to.
Decision (maintainer, 2026-11-01): ship without it, run it against a real library once, and add a
cut-down version - enum, `OrganizerModRow` field, `OrganizerExportFormatter` block, nothing in the
search index - only if the counts look wrong.

Also out of scope: filtering or searching by hair number, and `Hair/Mixed/` or any other bucket for
ambiguous mods.

## Testing

Unit tests, in the existing projects, against fixture directories written per test:

- `ModHairFileReader`: one hair number across several race codes collapses to one; two distinct
  numbers return two; `meta.json` and the legacy `default_mod.json` + `group_*.json` layouts both
  work; numbers nested under `DefaultData`, `Options`, `Containers` and `Groups` are all reached;
  malformed JSON returns `null`; a missing directory returns empty, not `null`.
- `EnrichHairSubCategory`: assigns `"115"` on exactly one number; returns the input untouched for a
  non-Hair category, for `null`, for empty, and for two or more numbers.
- `FlattenHairSubCategory`: clears the subcategory for Hair only, leaving Gear and NPC alone.
- `SortSelection`: `SplitHair` defaults false; `SplitsApply` is still false only for `CreatorOnly`.
- `OrganizerStateSortTests`: a hair mod with a number lands in `Hair/115` with the split on and
  `Hair` with it off, across all three category-consulting strategies.
- `ModEquipmentFileReaderTests`: unchanged, and passing, after the walker refactor.
- The existing help-topic coverage test should fail until `SortSplitHair` is registered everywhere.

Manual, in game, on a real library: the checkbox appears and is disabled under Creator-only; a
library with hair mods produces plausible numbers; the diagnostic export is unchanged.

## Non-ASCII note

`115` and the paths above are ASCII. If any later change introduces a non-ASCII character into a
folder name or a test assertion, specify it as an escape sequence rather than pasting the glyph -
this repo has been bitten three times by a pasted codepoint reaching code wrongly while the test
still passed.
