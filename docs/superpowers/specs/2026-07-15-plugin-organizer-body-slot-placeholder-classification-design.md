# Plugin organizer, body-slot placeholder classification — Design

**Status:** approved, not yet implemented. Reached via brainstorming with the user on 2026-07-15,
after in-game verification of Folder Cleanup surfaced a real-library classification review: a fresh
239-mod `organizer-export.txt` export showed body-mesh mods (`Bibo+`, `Bibo+ Body Hugging
(Penumbra)`, `[HS] Bibo+ (Bibo+ Base Install)`, `Yet Another Body Hugging`, `Yet Another Body+`, and
others) landing under `Category: Gear` instead of `Body`.

## Context

`ModTypeClassifier` (`PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`,
Phase 1c, `docs/superpowers/specs/2026-07-13-plugin-organizer-phase1c-classification-design.md`)
reduces a mod's full set of `GetChangedItems` keys to one classification using a strictly
first-match-wins priority order. Its Rule 1 is "Gear wins unconditionally" — deliberately, so that a
compilation pack bundling a body-slider tweak alongside real equipment still classifies as Gear.

`ChangedItemKeyParser` recognizes several structural key shapes (`Customization: ...`,
`Emote: ...`, `Action: ...`, category literals like `Housing`/`Sound`, mount/minion/NPC suffixes).
Anything that doesn't match one of those falls into a catch-all `Gear` shape, carrying the raw item
name unparsed.

The root cause: body-mesh replacer mods (Bibo+, Yet Another Body+, and similar) carry their model
via FFXIV's "bare body" equipment slot — the literal item `Smallclothes`, or (for Emperor's New
Clothes-based mods) one of five per-slot items (`The Emperor's New Hat`/`Robe`/`Gloves`/`Breeches`/
`Boots`). These are real, named `GetChangedItems` entries, so they fall into the Gear catch-all and
trigger Rule 1 — even though semantically these mods are body replacements, not equipment.

This design also gathered evidence on a second, related-but-distinct problem — NPC-targeted mods
(e.g. a `Feo Ul (Gen3)`-style body replacer) landing in inconsistent categories (`Bodies`/`Clothing`/`Minions`) in the
support Discord. The user explicitly decided NPCs need a different approach and are **out of scope
here** — see "Non-goals" and the standalone memory `npc-classification-deferred-issue.md` for the
research gathered.

## Goal

`Smallclothes` and the five Emperor's New Clothes body-slot items always classify as `Body`,
regardless of anything else present in the same mod's changed-item set — including real named Gear,
Customization signals (Face/Hair/Skin), Mount, Minion, or NPC-suffixed keys. This is an intentional,
user-confirmed absolute override, not a soft priority merge: the user was explicit that Smallclothes
"should always go to body no matter what... even over real gear."

Generalize the mechanism (not just hardcode two categories) so that a future entry — for a Skin
case, or another body-slot placeholder discovered later — is a one-line table addition, not a new
rule.

## Non-goals

- **NPC classification.** Explicitly deferred by the user ("we need to approach npc's differently,
  hence the change"). No NPC-suffix behavior changes here. See
  `npc-classification-deferred-issue.md` for the research already gathered (raw game-path
  substring-match hypothesis from the old standalone app, inconsistent Penumbra
  Bodies/Clothing/Minions bucketing for real NPC mods, and the concrete next step: capture a real
  `GetChangedItems` dump for one of the affected mods through this plugin before designing a fix).
- **Emperor's New Clothes accessories and weapons.** `The Emperor's New Shield`, `The Emperor's New
  Fists - Main Hand`/`- Off Hand`, `The Emperor's New Earrings`, `The Emperor's New Necklace`, `The
  Emperor's New Bracelet`, `The Emperor's New Ring - Left`/`- Right` are real accessory/weapon slots,
  not body-mesh placeholders. They are deliberately **not** added to the table and keep their
  existing Gear classification.
- **A new `SubCategory` for body-mesh mods.** The user chose the existing `Body` category over
  introducing a distinct one; these mods will sit alongside Customization-derived Body mods with no
  further distinction.
- **`docs/ROADMAP.md` entry.** This is a same-phase classification refinement within Phase 1c's
  existing design, not a scope boundary crossing (unlike Phase 2/Folder Cleanup's write-access
  decisions) — no roadmap update needed.

## Architecture

### `KnownEquipmentPlaceholders` table

A new `private static readonly Dictionary<string, ModCategory>` in `ModTypeClassifier.cs`,
`StringComparer.Ordinal` keys, six entries — all mapping to `ModCategory.Body` today:

```csharp
private static readonly Dictionary<string, ModCategory> KnownEquipmentPlaceholders =
    new(StringComparer.Ordinal)
    {
        ["Smallclothes"] = ModCategory.Body,
        ["The Emperor's New Hat"] = ModCategory.Body,
        ["The Emperor's New Robe"] = ModCategory.Body,
        ["The Emperor's New Gloves"] = ModCategory.Body,
        ["The Emperor's New Breeches"] = ModCategory.Body,
        ["The Emperor's New Boots"] = ModCategory.Body,
    };
```

Kept as a `Dictionary<string, ModCategory>` rather than a `HashSet<string>` deliberately: every
entry maps to `Body` today, but the whole point of this design is that a future placeholder item
implying a different category (Skin, most likely) is a one-line addition to this table, not a new
rule or a new code path.

### Rule 0: unconditional placeholder override

`ModTypeClassifier.Classify` gains a new first check, ahead of the existing Rule 1
("Gear wins unconditionally"):

```csharp
if (keys.Any(k => k.Shape == ChangedItemKeyShape.Gear
                   && KnownEquipmentPlaceholders.TryGetValue(k.ItemName!, out var category)))
    return new(category, null);
```

This is checked **before** every existing rule (Gear, Mount, Minion, NPC, Action/Emote/Animation/
VFX, Housing, Sound, Customization body-part priority) — matching the "no matter what" requirement.
A mod whose changed items include `Smallclothes` classifies as Body even if the same mod also
touches a real named Gear item, a Face/Hair Customization key, a Mount, a Minion, or an
NPC-suffixed key.

Everything after Rule 0 is unchanged: mods that don't touch any of the six literals go through the
exact same priority chain as before.

### Explicit trade-off: NPC co-occurrence

Because Rule 0 is checked before the existing NPC rule, a hypothetical mod touching both a bare
`Smallclothes` key and an NPC-suffixed key (e.g. `Smallclothes (NPC, 9903-1, Legs)`) now resolves to
Body, not NPC. This is the opposite of a mechanism briefly considered during brainstorming (letting
Smallclothes lose its Gear-shape status "fix" NPC-rule preemption as a side effect) — the user
explicitly chose the absolute-override version instead, accepting this trade-off, since NPC
classification is being deferred to its own future design pass regardless.

## Data flow

No change to how classification is invoked — `ModTypeClassifier.Classify` is still called once per
mod during `RunScan`, over that mod's full `GetChangedItems` key set, same as Phase 1c. Existing
`SortByModType`, `SortByTypeThenCreator`, and `SortByCreatorThenType` all consume the classification
result unchanged; `ModTypeFolders.GetFolder` needs no changes since `Body` already maps directly to
folder `"Body"` with no `SubCategory`.

## Testing

New cases in `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`,
following the existing file's style:

- Each of the six placeholder literals alone → `(Body, null)`.
- `Smallclothes` + a real named Gear item in the same key set → `(Body, null)` (confirms the
  override beats real Gear, not just the compilation-pack Gear-wins case).
- `Smallclothes` + a `Customization: ... Face` key → `(Body, null)` (confirms the override beats
  the existing Face > Hair > Body > Skin priority, not just merges into it).
- `Smallclothes` + a Mount-shaped key (`... (Mount)`) → `(Body, null)`.
- `Smallclothes` + an NPC-suffixed key (`Smallclothes (NPC, 9903-1, Legs)`) → `(Body, null)` —
  regression-locks the accepted NPC trade-off as an explicit test case, not just prose in this spec.
- `The Emperor's New Earrings` alone (an excluded accessory literal, deliberately not in the table)
  → still classifies as ordinary Gear — confirms the exclusion list actually has no effect, not just
  that it was left out.
- A baseline real-Gear-only mod (no placeholder literals at all) → unchanged Gear result, confirming
  no regression for the 195-mod majority case already classified as Gear in the reference export.

No executor/file-I/O tests needed — this is pure in-memory classification logic, same testing shape
as the rest of `ModTypeClassifier`.

## Error handling

None beyond what already exists: `ChangedItemKeyParser.Parse` never throws (same guarantee as
today), and an unrecognized item name simply doesn't match the dictionary lookup, falling through to
the existing Rule 1 exactly as before. No new failure modes are introduced.

## Implementation notes

- The six literal strings were confirmed directly against Penumbra's own item-association ("By
  Category") browser and Changed Items tab during brainstorming — not guessed, and not sourced from
  FFXIV TexTools' internal game-database naming (which uses a different convention, e.g. `SmallClothes
  Body`/`SmallClothes Hands` per-slot, that does not match what Penumbra's `GetChangedItems` actually
  emits).
- `Smallclothes` is intentionally a single literal covering all of Body/Hands/Legs/Feet, unlike
  Emperor's New Clothes' five separate per-slot literals — this follows the existing NPC-suffixed
  example already documented in this codebase's own design docs (`Smallclothes (NPC, 9903-1, Legs)`:
  one base name, slot only in the parenthetical), and the structural fact that Smallclothes is
  FFXIV's single default/bare-body item rather than five separately-ownable equipment pieces.

## Open risks

- The six literals were confirmed via Penumbra's own UI but not yet cross-checked against a live
  `GetChangedItems` IPC call through this plugin for a real installed Bibo+/YAB-style mod. Matching
  Folder Cleanup's precedent (BOM verification deferred to in-game testing), this should be a
  specific in-game verification step before this is considered fully done: scan a real library
  containing one of these mods and confirm it now classifies as Body, not Gear.
