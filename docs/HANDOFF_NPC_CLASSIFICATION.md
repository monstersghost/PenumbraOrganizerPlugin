# Handoff: NPC-targeted mod classification (deferred, not started)

**Status: not designed, not planned, not implemented.** No branch, no worktree, no spec. This is
pure research + requirements gathered during the 2026-07-15 session that did the
`docs/HANDOFF_BODY_SLOT_CLASSIFICATION.md` work, kept separate on the user's explicit instruction —
NPCs "need to be approached differently" and deserve their own design session, not a rider on that
change. This doc is everything a fresh conversation needs to start that session.

## The problem, in one sentence

`ModTypeClassifier` (`PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`)
already has working NPC detection for one specific shape — a `GetChangedItems` key with an
`(NPC, id, slot)` suffix (e.g. `Smallclothes (NPC, 9903-1, Legs)`, parsed by
`ChangedItemKeyParser.NpcSuffix`) — but real-world mods that target one *specific named* NPC (not
"any NPC using the shared bare-body slot") most likely don't produce that suffix at all, and there is
currently no other signal the classifier can use to catch them.

## Evidence this is a real, current problem

### From the support Discord (2026-07-14/15), Hayley's real library

Penumbra's own item-association picker sorts a mix of NPC-targeted body-replacer mods
inconsistently across three different top-level categories, no obvious pattern:

- **"Bodies"** (arguably the "right" bucket, but not an NPC-specific one): body-replacer mods named
  after specific story/Trust NPCs (`<prefix> <Name> (Gen3)`-shaped titles) — Alphinaud, Asura,
  Athena, Chirurgeon General, False Idol, Feo Ul, Gaia, Kan-E-Senna, Pari of Plenty, Raya-O-Senna,
  Red Girl, Scathach, Spectral Statice, Vamp Fatale.
- **"Accessories/Clothing"**: some NPC mods land here too — "it put my AB body in clothing for some
  reason."
- **"Minions"**: body-replacer titles with the NPC name concatenated with no space after a prefix
  word (`<Prefix>Azeyma`-shaped) — Azeyma, Garuda, Lakshmi, Scathach, Shiva, Sophia, Titania,
  Tsukuyomi — plus spaced `<prefix> <Name> (TF3 …)`-shaped titles for Beatrice, Compound 2P,
  Hegemone, and Hydaelyn, and one for faeries. Several names (Garuda, Lakshmi,
  Shiva, Titania, Sophia, Tsukuyomi) are actual FFXIV primal/raid-boss NPCs, not minion pets — this
  bucket is a genuine misclassification, not a legitimate minion mod.

The user's own live hypothesis (unverified, about the OLD standalone app's mechanism, not
necessarily this plugin's): "the tool sees the path contains body and doesn't know the difference
between a normal body replacement and an npc body... the one's in minions... have a game path of
monster instead of npc body." This describes a raw game-file-path substring match — a different
mechanism than this plugin's `GetChangedItems`-based approach, which already handles the
`(NPC, id, slot)` case correctly. **Whether this plugin has the same blind spot as the old app is
unconfirmed** — see "What to do first" below.

This plugin exists partly because of this exact conversation — direct quote from the user, same
thread: "I'm working on a better version of this as a plugin instead... I'll add a filter for
smallcloth/emperor but it's not in the app version." (That specific filter shipped in
`docs/HANDOFF_BODY_SLOT_CLASSIFICATION.md`; the NPC part of the same conversation is what this doc
covers.)

### From web research (xivmodarchive.com, Heliosphere), 2026-07-15

- **Neither site has a structural "NPC" category.** xivmodarchive.com's real category list:
  `Gear, Face, Minion, Furniture, Racial Scaling, Animation, Body, Hair, Mount, Skin, VFX, Sound,
  Pose, Other, Reshade Preset`. "npc" only ever appears as a free-text tag authors add voluntarily
  (confirmed on [Everyone Bites](https://www.xivmodarchive.com/modid/56413): filed under `Face`,
  tagged `npc` among a dozen unrelated tags). This rules out ever using website metadata as a
  signal — Penumbra/this plugin only ever sees the mod's own files, never the hosting site.
- **Two structurally different kinds of "NPC mod" likely exist, with different `GetChangedItems`
  behavior:**
  1. **Shared-slot NPC variants** — body replacers (YAB, TBSE) that ship an optional
     "Smallclothes (NPC)" install option, applying the same mesh to any NPC via the generic bare-body
     slot. These probably DO produce the `(NPC, id, slot)` suffix already handled correctly.
  2. **Single-named-NPC mods** — standalone mods bound to one specific NPC's own unique model/item
     ID, not a shared slot. Strongest evidence: an author shipping **two separate mods for the same
     face sculpt**, one per audience —
     [the sower's scales - an npc edit](https://heliosphere.app/mod/c3nx0n2we94r18v0qkdkre5j1g) vs.
     [the sower's scales - for players (face 2)](https://heliosphere.app/mod/33q8tv2w290fq0v4dj5nrfcv20)
     (description: "a companion piece... for a specific NPC while maintaining texture continuity").
     Because it's a face/skin sculpt, not the generic bare-body slot, it almost certainly binds to
     that NPC's own unique model — **the single best candidate to capture a real `GetChangedItems`
     dump from**, to confirm or kill this theory.
- **Counter-evidence that name-based heuristics alone would be unsafe:**
  [Slightly Better Alisaie](https://heliosphere.app/mod/ace91168917zf4navyypy59gvr) /
  [Slightly Better Alphinaud](https://heliosphere.app/mod/xbj56jnp7179fcjfz25s6e7z38) reuse a shared
  outfit-slot item (the Didact's Coat) — the author's own description warns "this will affect...
  other NPCs and players wearing that coat" and recommends a Penumbra collection to scope it. A mod
  named after one NPC that structurally affects everyone sharing that gear item — proof that the
  mod's *display name* alone is not a trustworthy signal.
- Other concrete candidates, ranked by likelihood of lacking a structural NPC signal:
  [reconstructed thancred waters v2](https://heliosphere.app/mod/yzsppa06fs6zk13mb9sz5g8v48)
  (Thancred-specific facial resculpt/retexture), [Archon Tattoo - Y'shtola](https://xivmodarchive.com/modid/63280)
  (skin/tattoo bound to one NPC), [Dark-skinned Twins](https://xivmodarchive.com/modid/31060)
  (targets Alisaie + Alphinaud together, description mentions "NPC hands" — possibly ALREADY has a
  working NPC tag; good cheap disconfirming test case), [Leveilleur Twins Face Fixes](https://www.xivmodarchive.com/modid/12743).

## What to do first — before writing any spec

Nobody has captured real `GetChangedItems` output for any of these mods through **this plugin**
(not the old standalone app, not the website). That capture is the prerequisite for any design work,
because it determines which of two completely different problems this actually is:

1. **If the raw keys DO carry some usable NPC-identifying signal** (even an inconsistent or partial
   one) — the fix is a parser/classifier extension, similar in shape to
   `docs/HANDOFF_BODY_SLOT_CLASSIFICATION.md`'s work.
2. **If the raw keys carry NO signal at all** (most likely, per the research above) — any fix
   necessarily involves name-based heuristics (regex/known-NPC-name matching against the mod's
   *display name*), which is fundamentally fuzzier and higher-false-positive-risk than anything else
   this classifier does today (`ModTypeClassifier`'s own doc comment: "never guesses"). That's a
   much bigger scope conversation with the user before any code gets written — probably its own
   brainstorming session just to decide if it's worth the false-positive risk at all.

**Concrete next step:** get the user to check Penumbra's Changed Items tab (or capture a
`GetChangedItems` dump) through this plugin for:
- [the sower's scales - an npc edit](https://heliosphere.app/mod/c3nx0n2we94r18v0qkdkre5j1g) — best
  candidate for confirming the true-gap case (single-named-NPC, custom sculpt).
- [Dark-skinned Twins](https://xivmodarchive.com/modid/31060) — best candidate for the
  already-working case (mentions "NPC hands" explicitly).

These two bracket the actual problem space. Once that data exists, start a proper
`superpowers:brainstorming` session — don't skip straight to a plan.

## Update, 2026-07-16 — real `GetChangedItems` data captured, hypothesis confirmed

The user downloaded and installed 5 real candidate mods and checked Penumbra's own Changed Items tab
for each through this plugin (not the old app, not a website) — this is the exact capture the
"Concrete next step" above called for, using different mods than the two originally suggested, but
landing on the same answer.

**Confirmed: single-named-NPC face/skin resculpts carry no structural signal at all.**
- `Rhul of Cool: A Y'shtola Overhaul` (Miqo'te Female, RimaHadley) — Changed Items: `Miqo'te Female
  Face 201`, `Miqo'te Female Face 204`, `Miqo'te Female Skin Textures`. This plugin's classifier:
  `Category: Face`.
- `[HS] reconstructed thancred waters v2` (Midlander Male, elspie) — Changed Items: `Midlander Male
  Face (Iris) 219`, `Midlander Male Face 219`, `Midlander Male Skin Textures`. This plugin's
  classifier: `Category: Face`.

Both report *only* generic race/gender-keyed customization slots — identical in shape to what an
ordinary player face/skin replacer for that race and gender would report. "Y'shtola" and "Thancred"
exist nowhere except the mod's own display name. This settles scenario 2 from the "Concrete next
step" section above: **no signal exists to key off; any real fix requires a name-based heuristic
against the mod's display name.**

**Confirmed as a non-issue: shared-Gear-slot NPC mods are already classified correctly.**
- `Slightly Better Alphinaud` — Changed Items includes `Didact's Coat (696-1)`, a real, ordinary,
  named equipment item. Classifier: `Category: Gear` — correct, since the mod genuinely affects any
  player/NPC wearing that coat (matches the author's own description warning about this).
- `[HS] Slightly Better Alisaie (Default)` — Changed Items includes `Augmented Classical Medicus's
  Wrist Torque (511-2)`, same pattern. Classifier: `Category: Gear` — also correct.

This confirms the "Slightly Better Alisaie" prediction from the original research (the one flagged
as "a good concrete example of why name-based heuristics alone would be unsafe") — these two are
*not* misclassifications and don't need fixing.

**A new, separate, unrelated bug surfaced in the same test batch:** `Leveilleur Lip Fix` (soullesshusk)
reports only `Elezen Female (Child) Face 201` / `Elezen Male (Child) Face 201` — child-race-variant
customization keys — and classified as `Category: (none)`. This has nothing to do with NPC targeting;
`ChangedItemKeyParser`/`ModTypeClassifier` appears not to recognize the `"(Child)"` race-variant
naming pattern as a Face customization key at all. This is a structural key-shape gap, the same
*category* of fix as the Smallclothes/Emperor's New Clothes work
(`docs/HANDOFF_BODY_SLOT_CLASSIFICATION.md`) — much more tractable than the NPC problem, and
independent of it. Not yet designed or fixed; flagged here so it isn't lost.

**Where this leaves the NPC problem:** the hypothesis is no longer a hypothesis. Whenever this gets
picked up, skip straight to `superpowers:brainstorming` for the name-heuristic design — the
scenario-1-vs-2 fork above is resolved (it's scenario 2) and doesn't need re-litigating. The two
originally-suggested candidates (`the sower's scales`, `Dark-skinned Twins`) are still worth checking
too if more example diversity is wanted before designing, but are no longer strictly required to
proceed.

## Explicitly out of scope until this is designed

Per `docs/HANDOFF_BODY_SLOT_CLASSIFICATION.md`: that change's Rule 0 is an unconditional override
that now beats even NPC-suffixed keys (a mod with both a bare `Smallclothes` key and an NPC-suffixed
key resolves to Body, not NPC) — a deliberate, user-confirmed trade-off, not something to "fix" as
part of this work without revisiting that decision explicitly first.

## Update, 2026-07-17 — name-heuristic classification implemented

Implemented per `docs/superpowers/specs/2026-07-17-plugin-organizer-npc-name-classification-design.md`
and `docs/superpowers/plans/2026-07-17-plugin-organizer-npc-name-classification.md`. Summary:

- `NpcNameMatcher` (whole-word, Unicode-boundary, combined-regex-per-category matching) now
  outranks every structural rule in `ModTypeClassifier.Classify`, including the Smallclothes/
  Emperor's New Clothes placeholder override — a deliberate, user-confirmed trade-off.
- The name list persists at `<plugin config dir>/npc-name-list.json`, seeded from a small curated
  embedded resource on first run, and is additively grown via a manual "Refresh NPC list from
  wiki" button on the Sort tab that scrapes `consolegameswiki.com`'s NPCs/Enemies/Bosses
  categories (the only network-touching code path in the plugin).
- The child-race-variant classifier gap (memory `child-race-variant-classification-gap`) remains
  separate, unrelated, and not yet fixed.
- Full in-game verification is still outstanding — see Task 9's manual checklist in the
  implementation plan linked above.
