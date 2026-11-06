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
| Hands and Feet | models/textures under `chara/human/*/obj/body/*` only for hand or foot slots, i.e. `glv`/`sho` equipment paths used as a body part | hands, feet, nails, toenails, claws |

Precedence when several match: file evidence beats name; among files, Skeleton, then Physics, then
Hands and Feet. A mod whose files hit two parts (a full body package) is a Replacer: only
single-part mods are split out.
The Hands and Feet file rule is the weakest. If it cannot be made reliable against the author's library
it ships name-keyword only, and the spec table is amended.

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
After a scan, the plugin reads the enabled mods of the current collection, resolves each Body-category
mod's body with the existing resolver, and counts them. If exactly one body is enabled, the Body priority
section shows "Your active collection uses Rue+." with a button "Make first choice". Pressing it uses the
existing `BodyPriority.MoveToTop`. Several bodies enabled, or none: show nothing. The line disappears when
that body is already first.

### Open point to verify before planning
Which Penumbra IPC calls give the active collection and its enabled mods, and whether a "current
collection" or "the local player's collection" is the right source. If neither is stable, the fallback
is the Default collection. This needs a short spike against the installed Penumbra before the plan.

### Components
- `ActiveCollectionBodyDetector` (pure): enabled mod directories + rows -> single body or null.
- An IPC adapter method for the collection's enabled mods, behind the existing Penumbra interface.
- `SortPanel`: the suggestion line inside the Body priority section.

### Testing
Unit: one body, several, none, a Body mod with no resolved body, already first. In game: change the
collection and rescan; the suggestion follows.

## Out of scope
Numbering the part folders; per-part priority; changing gear layout; templates storing parts.
