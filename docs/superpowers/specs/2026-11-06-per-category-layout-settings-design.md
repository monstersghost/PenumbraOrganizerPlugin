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

## Release scope (decided 2026-11-06)

One release carries everything below; there is no refactor-only release.

1. The per-category settings records, a one-time migration from the old flags, and Recommended / Flat / Custom
   presets. Defaults reproduce 0.6.4.0 output.
2. The two new options above (Gear hands and feet; Hair split by race).
3. The universal options on every category: sort this category (off leaves its mods where they are), folder
   name, group by creator inside the category, numbered folders on or off.
4. The remaining category options from the earlier proposal, each defaulting to today's behavior:
   - Gear: a `Sets` folder for multi-slot mods on or off; which slots get body folders (all, body-fitted only,
     Sets only); body-free gear in the root slots or in a `General` folder.
   - Body: each part's folder (Replacer, SFW, Hands and Feet, Skeleton, Physics, Tails/Ears/Horns) on or off;
     shared parts on top or under each body.
   - NPC: split by NPCs, Enemies, Bosses (as today).
   - Animation and VFX: split by kind; one shared parent or separate folders (as today).
   - Unknown: move to `Review` or leave in place.
5. The Advanced section of the Sort tab, one collapsible section per category, each with a live sample path.
6. The template planner and Check my collection read the records. Docs, help text and the in-game checklist
   cover every option.

Items 3 and 4 beyond the two named options are carried over from the proposal; the author said "the rest is
good" for categories not discussed, so they stay at today's behavior unless changed.
