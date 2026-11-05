# Body detection from mod content, with a saved body priority

Status: design, revised after review (2026-11-05). Awaiting author sign-off before planning.

**Baseline.** Builds on the body-type sort, `c3a011b` on `origin/main`, verified in game on 2026-11-05
(`Bibo+`, `Citrus+`, `Rox+` and `Rue+` survive as Penumbra folder names). Branch
`claude/confident-brown-tvdcmm` is tree-identical to that commit apart from the handoff note, and is
where this work starts. Local `main` does not contain the baseline. It provides `BodyTypeMatcher`,
`BodyTypeApplies` and `BodyTypeListStore.MaxUserBodyCount`, which this spec depends on.

## Goal

Today a mod's body comes from its NAME only. Many mods say nothing in the name and carry the body in
their option groups ("Installation: Bibo / YAB / Rue"). Some support several bodies at once. This adds
detection from the mod's content and a saved, user-ordered body priority that decides which body a
multi-body mod files under.

## Decisions (made by the plugin author)

1. **One Sort tab, no rename.** A separate "Sort Settings" tab with its own Sort button was considered
   and rejected: it duplicates the selection, the Sort button and the "selection changed since the
   last sort" state, and churns the help content, PDF guide and posts. If the tab later feels crowded,
   split off workbook import/export only.
2. **The priority list is a saved preference.** `SortPanel`'s comment calling all sort choices session
   state no longer holds for it, and is rewritten to say which fields persist and why.
3. **User settings always win.** Among the bodies a tier finds, the highest-ranked on the user's
   priority list is chosen.
4. **Tiers, in order** (a later tier is consulted only when every earlier one found no body):

   | Tier | Source |
   |---|---|
   | 1 | Inside the mod: group names, option names, first folder of mod-relative file paths |
   | 2 | The mod's name (today's behavior) |
   | 2.5 | The library folder path - least reliable, Heliosphere folders are named after creators or import batches |

   No body in any tier means the mod stays flat, as now.

## Behavior change to expect

The name is no longer first. A mod titled "Bibo+ Corset Gown" whose options offer only Rue+ and TBSE
now files under whichever of those two ranks higher. Some mods already filed by name will move after a
rescan. The Review tab shows every move before anything is applied.

## Where the priority is applied

**At sort time, not scan time**, exactly as the hair priority is (`HairPriorityResolver.Resolve` runs
inside `SortFolderSelectors.Select` against evidence stored on the row). So reordering the list and
pressing Sort again re-files mods without a rescan.

- The scan stores, per row, the candidate labels found in each tier: `BodyCandidates` with one set for
  Tier 1, Tier 2 and Tier 2.5, plus the source kind that produced each label (group, option, file
  path, name, library path).
- `SortFolderSelectors.Select` gains a `bodyPriority` parameter and calls `BodyPriorityResolver` to
  pick the body: the first non-empty tier, then the highest-ranked label in it.
- `OrganizerModRow.BodyType` remains, holding the resolution under the DEFAULT order, so the existing
  dump line and any consumer that reads it keep working. Sorting uses the live priority, not this field.

## File-path rule

Penumbra's `Files` object maps game paths (keys) to mod-relative replacement paths (values). Keys are
FFXIV game paths and never name a body, so **only values count**.

A value counts only through its **first folder segment**, matched with the same alias and boundary rule
as titles. Authors who ship per-body variants name the top folder (`Prestige YAB+/top.mdl`). Anything
deeper or at root is rejected as too noisy.

| Value | Result |
|---|---|
| `Prestige YAB+/top.mdl` | counts: YAB |
| `Bibo+/chest.tex` | counts: Bibo+ |
| `shared/Bibo/file.tex` | rejected, `Bibo` is not the first segment |
| `common/textures/rue.tex` | rejected |
| `rue_top.mdl` (root file) | rejected, no folder |

This is tuned for what the author's library shows and is checked against it in the in-game list.

## One walk per mod

Today a mod's config is read only for Gear (equipment slots) and for Hair rows (hair evidence), each by
its own reader. Adding a third reader would read a Gear mod's configs twice.

- A single walk per eligible mod (Gear, Hair and Body rows) feeds all three consumers. The walker
  gains one visitor that reports file keys, file values, group names and option names, and the
  existing equipment-slot and hair readers are changed to consume that shared walk rather than each
  calling the walker.
- Body evidence is collected in that walk and discarded when `BodyTypeApplies` is false, so a weapon,
  hat or ring never has its content searched.
- Body rows are not walked today, so they gain one read. They are few, and the cost is the same file
  reads the Gear and Hair passes already make.
- **Names exclude the mod title.** Group names come only from entries of the groups collection, and
  option names only from option entries. The mod-level `Name` in `meta.json` is never read as a group
  name, since that would turn the title into a Tier 1 signal and defeat the tier order.

## Failure and provenance

- The walker discards all evidence after any read or parse failure. For body detection that means
  **Tier 1 is skipped** and Tier 2 runs on the name as usual. The row records `ContentReadFailed`,
  separate from "found nothing", so the dump shows "content unreadable" rather than "no body".
- Per row the dump keeps every candidate label, its tier and its source kind, plus the winner and
  the priority position that decided it, so both the tier precedence and the priority choice are
  explainable from an export.

## Components

- **`ModConfigFileWalker`**: the shared visitor above, for both layouts (`default_mod.json` +
  `group_*.json`, and Penumbra 1.7.0's `meta.json` with `Groups`).
- **`BodyTypeMatcher`**: a method returning ALL bodies matched in a text. Same alias list, boundary
  rule and removed-alias protections.
- **`BodyPriorityResolver`** (new, pure, beside `HairPriorityResolver`).
- **`ScanProcessor`**: one walk, three consumers, writes `BodyCandidates` and the failure flag.
- **`OrganizerModRow`**: `BodyCandidates`, `ContentReadFailed`, and `BodyType` as described.
- **`Configuration`**: `BodyPriority`, `HairRaceCode`, `MidlanderFallback`. Group by and the split
  checkboxes stay per-session.
- **`SortPanel`**: a collapsed "Body priority" section under the body-type checkbox, with up/down
  buttons per row and a reset-to-default button, saving on every change. Disabled under the same
  conditions and tooltip as the checkbox. Arrows, not drag-and-drop.
- **Help**: one new topic, `HelpTopics.SortBodyPriority`, and a Sort-section line. Wording via the
  `design:ux-copy` skill.

## The saved priority

**Type.** An immutable snapshot with content equality (a record wrapping an immutable array, compared
by sequence), added to `SortSelection`. `SortPanel` compares selections with `!=`, and a plain `List`
would compare by reference: mutating one list in place would never trip the staleness hint, and
allocating a new list every frame would trip it permanently. The panel builds the snapshot only when the
list changes.

**Normalization**, run on config load and again whenever a scan loads a changed body list:
- Labels are matched ordinal-ignore-case against the current body list. The canonical label is the
  body list's own spelling.
- Duplicates keep the first occurrence.
- Labels no longer in the body list are dropped.
- Bodies missing from the list are appended at the end, in body-list order.
- Null, empty or an unusable value falls back to the default (the shipped order).
- `MaxUserBodyCount` caps only the bodies a USER file adds to the list. It does not cap the priority
  list, which holds the shipped bodies plus those, so appending never contradicts it.
- A malformed config file that stops the plugin configuration loading is Dalamud's concern: the plugin
  starts on defaults. A config that loads but holds invalid priority entries is normalized as above.

## Out of scope

- Templates still do not store the body split or the priority (same reasoning as the hair split).
  `TemplateFallbackConversionTests` counts `SortSelection`'s constructor parameters and fails when the
  field is added. That failure is expected and is resolved deliberately, not by loosening the test.
- Reading mesh or UV data to verify a body. A match remains a stated intent, not a verified fact.
- Per-collection or per-character priorities.

## Testing

Unit tests:
- group and option name extraction from both config layouts, including nested entries and a missing
  `Name`;
- **the mod-level `meta.json` `Name` is not treated as a group name** (a mod titled for Bibo+ with no
  groups yields no Tier 1 candidate);
- file-path rule: every row of the table above, and that keys are never read;
- the all-matches matcher method and its boundary rule;
- resolver: single candidate, several ranked by the list, tier fall-through 1 to 2 to 2.5, nothing found,
  and a ranking change producing a different winner from the same row;
- **priority changes without rescanning**: sort, reorder, sort again, with rows unchanged;
- **partial config failure**: a mod with one unreadable group file falls to the name tier and records
  `ContentReadFailed`, and a mod with no config files does not;
- **duplicate priorities**, stale labels, appended new bodies, mixed case, empty, null;
- `SortSelection` equality: reorder differs, identical contents are equal, reset restores equality
  with the default;
- one walk per mod: a Gear mod's config files are opened once;
- config round-trip, and existing body-type tests still pass, with any title-first expectation
  rewritten to the tier order on purpose.

In game (the author):
1. Rescan and compare matched versus flat counts with the previous build.
2. `[TPM] Succubus Lifestyle (YAB - Rue)` and `Heartbeat for Bibo & YAB+` under two different orders,
   re-sorting without a rescan.
3. Reorder, restart the game, confirm the order persisted.
4. A mod with no body in its name but one in its options is filed under that body.
5. Confirm the Review tab shows the moves caused by the tier change before applying.
