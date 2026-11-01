# Sort Tab Layout - Design

**Status:** Implemented in 0.6.1.1 (stable).
**Related:** `2026-11-01-race-priority-hair-design.md` (adds two controls to the Hair group),
`2026-11-01-sort-help-update-design.md` (the Help tab text for the same controls).

## Problem

The Sort tab is a flat column. Every control is one line, and the split checkboxes, the race
dropdown, the fallback checkbox, the scraped-list option, the Sort button and the manual assignment
section all sit at the same level. With race priority added there would be two more dropdowns and
checkboxes, and the user cannot tell which of them belong to which mod type.

The user has noticed this and asked for it to be grouped by category, separated with lines and
spacing: gear, hair, NPC and so on.

## Goals

1. Group the Sort tab's controls by the mod category they affect.
2. Separate groups visually, with a rule line and spacing between them.
3. Keep every control, its behaviour and its saved or session state exactly as it is today, except
   for the two race-priority controls that the other spec adds.
4. Keep the Sort button and the Group by control at the top, where a user looks first.

## Non-goals

- No change to what any control does, to Sort's output, or to any saved setting.
- No new controls beyond the race-priority pair.
- No tabs-within-tabs. Groups are headings and rules, not sub-tabs.

## Layout

Top to bottom:

1. **Grouping** (not a category heading; it applies to all categories)
   - Group by dropdown.
   - Sort button, with its gate tooltip, followed by a `?` button that opens the Sort help
     section (see the help-update spec).
2. rule
3. **Gear**
   - Split gear by equipment slot.
4. rule
5. **NPC**
   - Split NPC mods by kind.
   - Also use the NPC list scraped from the wiki (existing gate and tooltip unchanged).
6. rule
7. **Hair**
   - Split hair by hair number.
   - Priority race dropdown (race-priority spec).
   - Midlander fallback checkbox (race-priority spec).
8. rule
9. Existing sections below the split groups, unchanged: manual assignment, workbook import.

Each category heading is a bold or coloured label, using the existing `PluginTheme` colours. Between
groups: one horizontal rule and a standard item spacing. The Sort button stays where it is today,
above the split groups, so it does not move as the user scrolls past them.

## Behaviour rules

- Each category's controls disable together when their splits do not apply, using the same
  `splitsApply` gate that disables them now. Disabled groups keep their heading visible and grey out
  their contents, so the user can see why a group is inactive.
- The Hair group's priority dropdown and fallback checkbox are disabled while the Split hair checkbox
  is off. The Hair group's Help tooltips remain on each control.
- Group order is fixed: Gear, NPC, Hair. Adding a category later means adding a group, not reordering.

## Implementation notes

- `SortPanel.Draw` is one method. Split it into one private method per group, each taking the
  `splitsApply` value it needs, so the group order is visible in one place.
- The ImGui positional rule still applies: each control keeps its own disabled scope and its own
  `Help.Tooltip` call, as `SortPanel` does now. The group methods must not change this.
- Rules and spacing use `ImGui.Separator()` and `ImGui.Spacing()`, not a new widget.
- No change to `SortSelection` or to `OrganizerState.Sort`.

## Testing

- Visual check in game against the layout above. This is a manual gate; there is no unit test for
  ImGui layout.
- Unit tests stay on what can be tested: `SortSelection` value equality and `SplitsApply` remain
  unchanged, and existing tests must pass without edits.
- Confirm no control has moved state: each split checkbox and dropdown keeps its value across the
  layout change.

## Open questions

1. Should the Grouping section keep its current position above Gear, or sit in its own heading?
   Proposed: no heading, to keep the top of the tab as it is now.
2. Should Hair's two race-priority controls be collapsed by default, since most users will not touch
   them? Proposed: no; a collapsed control is harder to discover, and there are only two.
