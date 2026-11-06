# Per-category layout settings, first two options (design)

Status: draft for the author's review, 2026-11-06. Nothing here is built.

## Direction

Replace the loose split checkboxes with one settings record per mod category, plus a "Recommended" preset
and an "Advanced" section (one collapsible section per category, each with a live sample path). Old saved
settings are migrated once; the design only adds options, it removes none. This spec covers the first two
new options. The record refactor itself is the first step of the plan, with no behavior change.

Every category will also get the universal options (sort this category, folder name, group by creator inside
it, numbered folders). Those come after these two options and are out of scope here.

## 1. Gear: hands and feet, shared or under the body

New Gear setting **Hands and feet**: `Under the body` (default, today's behavior) or `Shared`.

- `Under the body`: `Gear/10. Rue+/03. Hands/<mod>`, as today.
- `Shared`: a hands or feet mod never takes a body folder: `Gear/03. Hands/<mod>`, `Gear/05. Feet/<mod>`,
  whatever body it was matched to.
- The other seven slots are unchanged. Head, Ears, Neck, Wrists and Rings never carry a body in practice (0 of
  170 in the author's library); Top, Legs and Sets stay under bodies.
- A mod's body is still resolved and shown in the UI; only the folder changes.
- Body numbering (`10.` upward) is computed from the rows that actually get a body folder, so a body that only
  has hands/feet mods takes no number while Shared is on.

Evidence (author's workbook, 1,845 gear mods): hands 9 of 43 and feet 29 of 117 sit under a body today.

## 2. Hair: single race, multi-race, then numbers

New Hair setting **Split by race** (default off, so existing layouts do not move until chosen).

With it on, and hair split by number on, the layout is:

- `Hair/<race>/<number>/<mod>` for a mod whose hair files cover exactly one race (either gender). Races use
  Penumbra's names from `HairRaceCodes` (Midlander, Highlander, Elezen, Miqo'te, Roegadyn, Lalafell, Au Ra,
  Hrothgar, Viera).
- `Hair/Multi-race/<number>/<mod>` for a mod covering more than one race.
- A mod with no race evidence is multi-race.
- The number a multi-race mod files under still follows the existing Priority race and Midlander fallback.
  A single-race mod files under its own race's number.
- Numbers keep the three-digit padding (`Hair/Viera/115`).
- With hair split by number off, the race split gives `Hair/<race>/<mod>` and `Hair/Multi-race/<mod>`.

Source data: `OrganizerModRow.HairByRaceCode` (race code to hair numbers), already read at scan time.

## 3. Body

No change. The numbered layout (shared Skeleton, Physics, Tails, Ears and Horns on top; bodies from `10.` with
Replacer, SFW, Hands and Feet) stays as built.

## Compatibility and testing

- Defaults reproduce 0.6.4.0 output exactly, which the existing tests assert.
- The template planner and the Check my collection suggestion must read the same settings; both are covered by
  the record refactor.
- New tests: hands/feet Shared vs Under the body (with and without gear body split), body numbering with
  hands-only bodies, hair single race, multi-race, no-evidence, with and without the number split, and the
  priority-race interaction.
- Docs, help text and the in-game checklist are updated with each option.

## Split decisions (2026-11-06, after the debate and judge)

The author decided the new splits are exactly two: **Gear hands and feet shared or under the body**, and **Hair
split by race with a Multi-race folder**. Cut from this release: Gear "which slots get body folders", the Gear
`General` folder, and Body "shared parts under each body". Deferred to a later release: splitting `Sets` by slot
combination (the judge's top-value follow-up), race-specific gear, Face/Skin/Minion/Animation sub-splits.

Hair rules fixed by the judge (they replace the draft in section 2 where they differ):
- A single-race mod files under its own race, `Hair/<race>/<NNN>`, only if that race has exactly one hair
  number; otherwise `Hair/<race>/<mod>`. The priority race and Midlander fallback never apply to it.
- A mod covering several races, or with no race evidence, files under `Hair/Multi-race`, numbered by the existing
  priority-race and Midlander rule. The Multi-race folder is the "multi-race indicator".
- The hair folder code gets a validated race-and-number path; today it accepts a bare number only.
- The one shared "does this row get a body folder" check feeds both layout building and lookup, so Shared
  hands and feet cannot make `GearFolderLayout.Folder` throw.

## Release scope (final, 2026-11-06)

The release carries only the two splits above, on top of 0.6.4.0 behavior:

1. Gear **Hands and feet**: `Under the body` (default) or `Shared`.
2. Hair **Split by race** (default off), with the single-race and Multi-race rules above.

Each is one persisted option in Configuration with the default reproducing 0.6.4.0 output exactly. An older
build ignores the unknown settings and sorts as before, so no rollback mirror is needed. The template planner is
NOT given the options (decided while planning): a template applies today's layout for these two splits, and a
later release adds them to the template and workbook flows.
Docs, help text and the in-game checklist cover both.

Not in this release, and kept as the direction for later ones: the per-category settings records, migration,
Recommended / Flat / Custom presets, the universal options (sort this category, folder name, creator grouping,
numbering), the Advanced section with live sample paths, template format v2, the Sets split by slot combination,
and the judge's amendments for Keep results and folder renames (they only matter once those options exist).

## Tests

- Defaults reproduce 0.6.4.0: the existing suite passes unchanged.
- Hands and feet Shared and Under the body, with the gear body split on and off, with protected rows, with a
  Heliosphere sort-inside tool folder; no body takes a number only for hands or feet mods; never throws.
- Hair: single race with one number, single race with several numbers, multi-race, no evidence, Midlander
  fallback and priority race on multi-race only, number split on and off, three-digit padding.
- Sorting twice changes nothing; the template planner produces the same folders as Sort for both options.
