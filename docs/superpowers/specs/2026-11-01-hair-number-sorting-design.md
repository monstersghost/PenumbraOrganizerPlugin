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

   "4 digits" means `[0-9]{4}`, explicitly, **not** `\d{4}`. .NET's `\d` matches every Unicode
   decimal digit, so a path carrying fullwidth or Devanagari digits would match and then throw
   `FormatException` in the parse - an exception the walker does not catch, so it would escape
   into the scan. This repo has been bitten four times by a non-ASCII codepoint reaching code as
   something other than what it looked like; this is that hazard in regex form.

   **Correction, measured during implementation (2026-11-01):** the above is the argument for
   `[0-9]` as originally written, and it holds only against `int.Parse`. The reader parses with
   `int.TryParse` under `NumberStyles.None` and `InvariantCulture`, which rejects non-ASCII digits
   without throwing. So with TryParse in place, `[0-9]` and `\d` are behaviourally identical here:
   mutating the pattern back to `\d` leaves all 26 of the reader's tests green. `[0-9]` stays,
   because it documents intent and because it keeps the hole shut if someone ever changes TryParse
   back to Parse - but it is defence in depth, not a load-bearing guard, and no test can pin it
   while TryParse is there. Do not claim test coverage for it.

2. **`Est` manipulations** whose `Slot` is `Hair`, whose `SetId` is the hair number.

   `Est` is explicitly excluded by `ModEquipmentFileReader`, whose comment reads: "Est also has a
   Slot, but it names a customization slot (Hair/Face), not equipment - deliberately excluded by
   only recognizing the three equipment-relevant types." That exclusion is correct for gear and
   exactly backwards here. Update that comment to point at this reader, so the next person does not
   read it as "Est is never useful."

   The shape is corroborated by a fixture already in this repo.
   `ModEquipmentFileReaderTests.ReadEquipmentSlots_EstManipulation_ContributesNothing` uses:

       {"Type":"Est","Manipulation":{"Entry":161,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}

   `SetId` (157) is the hair number; `Entry` (161) is the EST skeleton row the hair maps to, which
   is why the two differ. Read `SetId`, never `Entry`.

   **Still verify before shipping**, from Penumbra's own EST manipulation model and the code that
   applies it - then corroborate against real mods.

   Agreement between `SetId` and the `Files` path number confirms the reading. Disagreement, on
   its own, confirms nothing: a mod may legitimately carry an EST entry for one hair and
   replacement paths for another, in which case two numbers is the correct reading and the mod
   files flat, which is what this design already does. Dropping source 2 is a conservative
   fallback for the case where `SetId` turns out not to name a hair number at all - not the
   automatic consequence of two numbers differing. Path matching is the load-bearing source
   either way.

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

## `ModTypeFolders.GetFolder` must learn about hair

This is not optional plumbing, it is a crash otherwise. `GetFolder` enumerates valid
`(category, subCategory)` pairs explicitly and its final arm **throws**
`ArgumentOutOfRangeException`, with the comment: "that would silently accept a nonsense combination
a classifier bug could produce (e.g. Gear + 'Bosses') instead of failing fast during development."

So `GetFolder(Hair, "115")` throws today, inside the sort. A new arm is required.

Hair numbers are an open set of several hundred values, so they cannot be enumerated the way the
nine gear slots and three NPC kinds are. Validate the **shape** instead, preserving the fail-fast
intent for a genuinely nonsense pairing:

    (ModCategory.Hair, _) when IsHairNumber(subCategory) => $"{ModCategory.Hair}/{subCategory}",

where `IsHairNumber` accepts one to four ASCII digits with no leading zero. `Hair` + `"Bosses"`
still throws, which is the property the existing comment is protecting.

### The supported range, and who owns it

`IsHairNumber` implies a domain of 1..9999. **`ModHairFileReader` enforces it, and nothing else
does.**

Formatting an `int` invariantly guarantees a well-formed decimal string, not a value in range -
`"0"` and `"10000"` are both well-formed and both rejected by the guard, which means both would
throw inside the sort. Two real inputs produce them: a path containing `h0000`, which parses
cleanly to `0`, and an `Est` `SetId`, which can be any `int`. So the reader filters its numbers to
1..9999 before anyone sees them, and everything downstream may assume the range holds.

Out-of-range evidence is **discarded, not promoted to a read failure**. A stray `h0000` beside a
real `h0115` still files the mod under 115; treating it as untrustworthy would throw away a good
answer. That is a deliberate policy, not a side effect.

`EnrichHairSubCategory` is the only producer of these subcategories. It does not re-check the
range - one owner for the domain, and the reader is it. The guard in `GetFolder` is what turns a
future producer that skips the filter into a loud failure rather than a `Hair/0115` folder sitting
beside an existing `Hair/115` one.

## Plumbing

| File | Change |
|---|---|
| `Classification/ModTypeClassifier.cs` | the `ModTypeFolders.GetFolder` arm and `IsHairNumber`, above |
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
  uppercase paths still resolve; malformed JSON returns `null`; a missing directory returns empty,
  not `null`; **a good file followed by a malformed one returns `null` and discards what it had
  already collected**, since the callbacks write into the result as the walk proceeds.
- Range boundaries, which are where the crashes would be: `h0000` yields nothing, `h0001` and
  `h9999` resolve, an `Est` `SetId` of 0, -1, 10000 or `int.MaxValue` yields nothing, and
  out-of-range evidence beside valid evidence keeps the valid number rather than failing the read.
- A path whose digits are non-ASCII is rejected without throwing - written as `\uXXXX` escapes in
  the test, never a pasted glyph.
- Paths and `Est` naming the same number yield one; naming different numbers yield two.
- `EnrichHairSubCategory`: assigns `"115"` on exactly one number; returns the input untouched for a
  non-Hair category, for `null`, for empty, and for two or more numbers.
- `FlattenHairSubCategory`: clears the subcategory for Hair only, leaving Gear and NPC alone.
- `SortSelection`: `SplitHair` defaults false; `SplitsApply` is still false only for `CreatorOnly`.
- `ModTypeFolders.GetFolder`: `(Hair, "115")` returns `Hair/115`; `(Hair, null)` still returns
  `Hair`; `(Hair, "Bosses")`, `(Hair, "0115")` and `(Hair, "")` all throw
  `ArgumentOutOfRangeException`.
- Hair sorting through `OrganizerState`: a hair mod with a number lands in `Hair/115` with the
  split on and `Hair` with it off, across all three category-consulting strategies.

  **This goes in its own fixture, not into the existing `OrganizerStateSortTests` theory.** That
  theory is 16 `[InlineData]` rows over `(strategy, splitGear, splitNpc)` plus three expected
  paths, and its comment records that the expectations were *captured* from the seven original
  `SortBy*` methods rather than hand-written, "rather than getting the order wrong and then
  'fixing' the expectations until green, which destroys the guarantee." Adding a hair row to its
  shared fixture changes all 16 rows' expected output, and adding a `splitHair` axis doubles it to
  32 hand-written rows - precisely the failure mode that comment warns about. Instead: pass
  `splitHair: false` at that theory's single `Sort` call site so its captured expectations stay
  byte-identical, and put hair coverage in a new `OrganizerStateSortHairTests` with its own
  two-row fixture.
- `ModEquipmentFileReaderTests`: unchanged, and passing, after the walker refactor.

What the tests deliberately do **not** prove, so nobody mistakes a green run for more than it is:

- That the `Category == Hair` disk-I/O gate exists. A Gear mod with hair paths keeps no hair
  subcategory whether or not the gate is there, because `EnrichHairSubCategory` independently
  refuses a non-Hair classification. The gate is a performance property with no seam to assert
  through; it is checked by reading the code in review.
- That the checkbox itself defaults off. `SortPanel`'s fields are private with no seam, so a
  record-level test would only prove the record stores its constructor argument. The fresh-panel
  default is on the in-game checklist instead.
- The existing help-topic coverage test should fail until `SortSplitHair` is registered everywhere.

Manual, in game, on a real library: the checkbox appears and is disabled under Creator-only; a
library with hair mods produces plausible numbers.

Two things that DO change for a user who never touches the checkbox, neither affecting their
proposed layout (corrected 2026-11-01; an earlier draft of this line claimed the diagnostic export
was unchanged, which is false):

- **The diagnostic export differs.** `OrganizerExportFormatter` prints `SubCategory:` for every
  mod, and enrichment happens at scan time regardless of the checkbox, so a hair mod whose number
  resolves now exports `SubCategory: 115` where it previously exported nothing. The checklist uses
  this deliberately - it is how the tester spot-checks the numbers.
- **Scanning a hair mod costs one extra config-file walk.** The `Category == Hair` gate skips every
  other category, but it does not consult the checkbox, so a hair-heavy library pays that cost on
  every scan even with the split permanently off. Filing is what the checkbox controls, not the
  read. Acceptable - the walk is the same one the gear path already does per gear mod - but "the
  checkbox is off so nothing changed" is not true at scan time.

## Non-ASCII note

`115` and the paths above are ASCII. If any later change introduces a non-ASCII character into a
folder name or a test assertion, specify it as an escape sequence rather than pasting the glyph -
this repo has been bitten three times by a pasted codepoint reaching code wrongly while the test
still passed.
