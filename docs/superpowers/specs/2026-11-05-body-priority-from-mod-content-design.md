# Body detection from mod content, with a saved body priority

Status: design, awaiting review. Builds on the body-type sort (`c3a011b`, verified in game on
2026-11-05: `Bibo+`, `Citrus+`, `Rox+` and `Rue+` survive as Penumbra folder names).

## Goal

Today a mod's body comes from its NAME only. Many mods say nothing in the name and carry the body in
their option groups ("Installation: Bibo / YAB / Rue"). Some support several bodies at once. This adds
detection from the mod's content and a saved, user-ordered body priority that decides which body a
multi-body mod files under.

## Decisions (all made by the plugin author)

1. **One Sort tab, no rename.** A separate "Sort Settings" tab with its own Sort button was considered
   and rejected: it duplicates the selection, the Sort button and the "selection changed since the
   last sort" state (`SortPanel._lastSorted`), and churns the help content, PDF guide and posts.
   If the tab later feels crowded, split off workbook import/export only.
2. **The priority list is a saved preference.** `SortPanel`'s comment calling all sort choices session
   state no longer holds for it, and is rewritten to say which fields persist and why.
3. **User settings always win.** Among the bodies a tier finds, the highest-ranked on the user's
   priority list is chosen. No source gets special weight inside a tier.
4. **Tiers, in order** (a later tier is consulted only when every earlier one found no body):

   | Tier | Source |
   |---|---|
   | 1 | Inside the mod: group names, option names, file paths listed in its config |
   | 2 | The mod's name (today's behavior) |
   | 2.5 | The library folder path - least reliable, Heliosphere folders are named after creators or import batches |

   No body in any tier means the mod stays flat, as now.

## Behavior change to expect

The name is no longer first. A mod titled "Bibo+ Corset Gown" whose options offer only Rue+ and TBSE
now files under whichever of those two ranks higher. Some mods already filed by name will move after a
rescan. The Review tab shows every move before anything is applied.

## Components

- **`ModConfigFileWalker`** gains a callback reporting each group's and option's `Name` for both config
  layouts (`default_mod.json` + `group_*.json`, and Penumbra 1.7.0's `meta.json` with `Groups`).
  Today it reports only file keys and manipulations. No extra disk reads: the files are already read
  for the hair pass.
- **`ModBodyEvidenceReader`** (new, pure beside the existing readers): returns the group names, option
  names and normalized file keys for a mod. Gated like the gear pass, so only Gear and Body rows read.
- **`BodyTypeMatcher`** gains a method returning ALL bodies matched in a text, not only the best one.
  Same alias list, same boundary rule, same removed-alias protections. A file path counts only when an
  alias is a standalone segment match, so a stray `Bibo/` inside a shared folder is not a Tier 1 hit.
- **`BodyPriorityResolver`** (new, pure, modeled on `HairPriorityResolver`): given the candidates of
  each tier and the priority list, returns the body and which tier and source decided it.
- **`ScanProcessor`** runs the tiers in order and writes the result and its source onto the row. The
  existing slot gate (`BodyTypeApplies`) is unchanged and still decides WHETHER a body applies.
- **`OrganizerModRow`** keeps `BodyType` and adds `BodyTypeSource` (tier plus what matched), written
  to the diagnostic dump so "why did this land here" is answerable from an export.
- **`Configuration`** gains `BodyPriority` (ordered labels), plus `HairRaceCode` and
  `MidlanderFallback`, which are also set-once. Group by and the split checkboxes stay per-session.
- **`SortPanel`** gets a collapsed "Body priority" section under the body-type checkbox, with up/down
  buttons per row, a reset-to-default button, saving on every change. It is disabled with the same
  conditions and tooltip as the checkbox. Arrows, not drag-and-drop: simpler in ImGui and accessible.
- **`SortSelection`** carries the priority list so a reorder trips the staleness hint.
- **Help**: one new topic, `HelpTopics.SortBodyPriority`, and a Sort-section line. Wording via the
  `design:ux-copy` skill.

## The saved list never breaks

Default order is the shipped list order. On load: labels no longer in the body list are dropped, and
bodies missing from the saved list (a later release, or the user's own list) are appended at the end.
A null, empty or corrupt value falls back to the default. The list is capped by the existing
`MaxUserBodyCount`.

## Out of scope

- Templates still do not store the body split or the priority (same reasoning as the hair split).
  `TemplateFallbackConversionTests` counts `SortSelection`'s constructor parameters and will fail when
  the field is added; that failure is expected and is resolved deliberately, not by loosening the test.
- Reading mesh or UV data to verify a body. A match remains a stated intent, not a verified fact.
- Per-collection or per-character priorities.

## Testing

Unit tests:
- name extraction from both config layouts, including nested and missing `Name`;
- all-matches matcher method, boundary rule, and the file-path standalone-segment rule;
- resolver: single candidate, several candidates ranked by the list, tier fall-through (1 to 2 to 2.5),
  nothing found;
- priority list normalization: stale labels dropped, new bodies appended, corrupt falls back;
- config round-trip;
- existing body-type tests still pass; any title-first expectation is rewritten to tiers on purpose.

In game (the author):
1. Rescan and compare matched versus flat counts with the previous build.
2. `[TPM] Succubus Lifestyle (YAB - Rue)` and `Heartbeat for Bibo & YAB+` under two different orders.
3. Reorder, restart the game, confirm the order persisted.
4. A mod with no body in its name but one in its options is filed under that body.
5. Confirm the Review tab shows the moves caused by the tier change before applying.
