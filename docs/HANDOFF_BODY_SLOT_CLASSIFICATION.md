# Handoff: Body-slot placeholder classification

Merged to `main` (`37721ad`). This note is for whoever picks up the in-game verification or the
next classification-strategy change.

## What's on `main` now

Fixes a real misclassification found while reviewing a user's real 239-mod library export:
body-mesh/body-texture mods (Bibo+, Yet Another Body+, `[HS] Bibo+ (Bibo+ Base Install)`, etc.) were
landing under `Category: Gear` instead of `Body`, because they carry their mesh via the bare-body
equipment slot (`Smallclothes`, or the five Emperor's New Clothes body-slot pieces), and
`ModTypeClassifier`'s Gear catch-all doesn't distinguish those literal item names from real named
equipment.

- `Organizer/Classification/ModTypeClassifier.cs` — new `KnownEquipmentPlaceholders` table
  (`Dictionary<string, ModCategory>`, `StringComparer.Ordinal`, 6 entries today, all → `Body`):
  `Smallclothes`, `The Emperor's New Hat`, `The Emperor's New Robe`, `The Emperor's New Gloves`,
  `The Emperor's New Breeches`, `The Emperor's New Boots`. New **Rule 0**, checked before every
  existing rule (Gear/Mount/Minion/NPC/Animation/VFX/Housing/Sound/Customization): any of these 6
  literals **unconditionally** classifies a mod as Body — beats real named Gear, Face/Hair/Skin
  Customization, Mount, and NPC-suffixed keys alike. This is a deliberate, user-confirmed absolute
  override ("should always go to body no matter what... even over real gear"), not a soft priority
  merge into the existing Face > Hair > Body > Skin system.
- **Explicitly excluded, not in the table** (stay ordinary Gear): `The Emperor's New Shield`,
  `The Emperor's New Fists - Main Hand`/`- Off Hand`, `The Emperor's New Earrings`,
  `The Emperor's New Necklace`, `The Emperor's New Bracelet`, `The Emperor's New Ring - Left`/`-
  Right` — these are real jewelry/weapon items, not body-mesh placeholders. Regression-tested (one
  representative: `The Emperor's New Earrings` alone → still `Gear`).

Design: `docs/superpowers/specs/2026-07-15-plugin-organizer-body-slot-placeholder-classification-design.md`.
Plan: `docs/superpowers/plans/2026-07-15-plugin-organizer-body-slot-placeholder-classification.md`.

182 tests pass, build clean.

## How the exact literal strings were confirmed — don't re-guess these

Every literal in the table came from the user directly reading Penumbra's own item-association
picker / Changed Items tab on their real install — **not guessed, not inferred from FFXIV's raw game
database naming** (which differs — e.g. FFXIV TexTools' internal database calls the equivalent items
`SmallClothes Body`/`SmallClothes Hands`/etc., per-slot; Penumbra collapses the player-side ones into
a single `Smallclothes` key). If a future Penumbra version changes this presentation, re-verify
against the live Changed Items tab, not TexTools or any other tool.

## Explicit, accepted trade-off — not a bug

A mod combining a bare `Smallclothes` key with an NPC-suffixed key (e.g.
`Smallclothes (NPC, 9903-1, Legs)`) now resolves to **Body, not NPC**. This was raised explicitly
mid-design as a real consequence of making the override unconditional (it has to sit ahead of the
NPC rule too, not just ahead of Gear/Customization), and the user confirmed they want it this way —
NPC classification is being deliberately deferred to its own future design pass, not folded into
this change. See `docs/HANDOFF_NPC_CLASSIFICATION.md` for that deferred work.

## What's NOT done yet

**Not yet in-game verified.** The 6 literals were confirmed via Penumbra's UI, but nobody has yet
scanned a real library containing a Bibo+/YAB-style mod through this plugin and confirmed it now
reports `Category: Body` instead of `Gear`. That's the one open risk the spec itself names as
deferred to in-game verification (not a code-review blocker — the final whole-branch review passed
clean otherwise).

## Process note

Executed via subagent-driven-development, single task + final whole-branch review, both clean. One
minor process note: the implementer's commit initially had the wrong `Co-Authored-By` attribution
(`Claude Sonnet 5` instead of the repo's `Claude Fable 5` convention) — caught and amended by the
controller after the task-level review flagged it as unverifiable-from-diff, before the final
whole-branch review ran. Worth a passing mention if you're auditing commit trailers across this repo.
