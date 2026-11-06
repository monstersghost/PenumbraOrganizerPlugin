# Body parts folders and body suggestion from the active collection

Status: design, awaiting author review (2026-11-06). Two independent features, one spec each section.
Planning and building can be done as two separate plans.

## Review of the existing body specs (done first)

`2026-11-05-body-priority-from-mod-content` and `2026-11-05-sort-tab-ux-pass` are both implemented:
tiers 1 to 2.5, `BodyPriorityResolver`, saved priority, two split checkboxes (`SplitGearBody`,
`SplitBodyMods`), the layout helper and the First-choice body dropdown exist in the code. No new
design is needed for the Sort tab layout. Still unverified in game: First-choice body after arrow
moves and restart, and keyboard activation of the (?) popup. Those go on the checklist, not in this spec.

## Feature 1: split Body mods by part

### Goal
Today `Body/<body>/<mod>` holds the body replacer and every add-on for that body. This adds a part
folder: `Body/<body>/<part>/<mod>`.

### Decisions (author, 2026-11-06)
1. Plain-named subfolders by part, not numbered.
2. A mod is assigned a part from its name keywords AND its file paths.
3. Anything not clearly a part stays in `Replacer`.

### Parts
`Replacer`, `Skeleton`, `Physics`, `Hands and Feet`. `Replacer` is the default and the fallback.

### Detection (new pure classifier, `BodyPartClassifier`)
Inputs: mod name, and the mod's game paths (the keys of Penumbra's `Files`, the same walk that
already feeds body detection).

| Part | File evidence | Name keywords (word-boundary, case-insensitive) |
|---|---|---|
| Skeleton | any `.sklb` or `.skp` game path | skeleton, skel |
| Physics | any `.phyb` game path | physics, jiggle, bounce |
| Hands and Feet | body models under `/obj/body/` whose slot suffix is only `_glv` (hands) and/or `_sho` (feet) | hands, feet, foot, nails, toenails, claws |

Precedence when several match: file evidence beats name; among files, Skeleton, then Physics, then
Hands and Feet. A mod whose files hit two parts (a full body package) is a Replacer: only
single-part mods are split out.
**Body-model rule.** Any `.mdl` under `/obj/body/` decides the part before skeleton or physics files
are considered, because a replacer often ships a skeleton beside its models. Models for hands and feet
only make Hands and Feet; any torso (`_top`), legs (`_dwn`) or unknown slot makes a Replacer. This rule
is a hypothesis about how the author's library is laid out, so it is verified in game first; if it
misfiles real mods it falls back to name keywords only for Hands and Feet.

**Package, not selection.** The unit classified is the whole mod package: files from every option are
read together, never the options currently enabled. A package that offers several parts through its
options reads as mixed and stays a Replacer.

**Accepted limits (review of 2026-11-06).** File evidence beats name evidence. Skeleton and physics
files count wherever they sit in a Body mod. An unreadable config falls back to the name.

### Where it applies
A new third checkbox is NOT added. The part folder appears when `Split body mods by body type` is on,
because parts only make sense inside a body. With it off, `Body/<mod>` is unchanged. Gear is untouched.
Template export and the template fallback gain no new flag: parts follow `SplitBodyMods`.
A Body mod with no resolved body keeps today's flat placement.

### Components
- `BodyPartClassifier` (new, pure): `Classify(name, gamePaths) -> BodyPart`.
- `OrganizerModRow`: a stored `BodyPart` set at scan time next to the other evidence.
- `SortFolderSelectors.Select`: the Body branch appends the part folder when `SplitBodyMods` is on.
- Help text and `docs/HOW_SORTING_WORKS.md`: one topic line.

### Testing
Unit: each part from file evidence alone, from name alone, conflicting evidence, two-part file sets
falling back to Replacer, no evidence, word boundaries ("Handsome" is not Hands), and the folder shape
with the flag on and off and with no body. In game: Rue+ add-ons file under the right part folders on
the author's library; the Review tab shows every move before Apply.

## Feature 2: suggest the body from the active collection

### Goal
Tell the user which body their active Penumbra collection actually uses, and offer to make it
First-choice. The user confirms; nothing changes silently (author decision).

### Behavior
When the user presses "Check my collection" in the Body priority section, the plugin reads the enabled
mods of the collection affecting the local player (falling back to Penumbra's Current collection),
resolves each enabled Replacer-part Body mod's body with the existing resolver, and counts distinct bodies.

**Claim.** This is metadata, not effective-body detection: it counts enabled body replacer mods. It does
not prove which body's files win in game, since mod priority and selected options still decide that.
The wording says so: `<Player|Selected> collection "<name>" has one enabled body replacer: Rue+.` The
source and collection name always appear, so a fallback to the selected collection is visible.

If exactly one body is found, a "Make first choice" button follows, using the existing
`BodyPriority.MoveToTop`. If that body is already first, the line stays as information with no button.
With none or several, a line says no single body replacer was found. Enabled body mods whose body could
not be resolved are ignored (they cannot be named, and the result is only a suggestion the user
confirms). The result is a snapshot of the moment of the check; pressing the button again refreshes it.

### IPC used
`GetCollectionForObject(0)` (the local player), else `GetCollection(ApiCollectionType.Current)`, then
`GetAllModSettings(collectionId)`, keeping entries whose Enabled flag is true. Keys are mod directory
names (the Penumbra API docs say so), the same identifier the scan already stores as
`OrganizerModRow.Identifier` and uses to look up changed items. Inherited and temporary settings count
as enabled. Verified in game, not assumed: which collection each call returns when they differ.

### Components
- `ActiveCollectionBodyDetector` (pure): enabled mod directories + rows -> single body or null.
- An IPC adapter method for the collection's enabled mods, behind the existing Penumbra interface.
- `SortPanel`: the suggestion line inside the Body priority section.

### Testing
Unit: one body, several, none, a Body mod with no resolved body, already first. In game: change the
collection and rescan; the suggestion follows.

## Out of scope
Numbering the part folders; per-part priority; changing gear layout; templates storing parts.
