# Sort Help Update - Design

**Status:** Draft for review. Not implemented.
**Related:** `2026-11-01-sort-panel-layout-design.md` (the layout this text describes),
`2026-11-01-race-priority-hair-design.md` (the two new Hair controls).

## Problem

The Help tab's Sort section describes the tab in one short paragraph and has one entry per control.
Once the layout is grouped and the race-priority controls exist, the reader needs to know how the
groups fit together, and in particular what Priority race and Midlander fallback do, and in what
order the rules apply. Today the Sort body text says nothing about the split checkboxes at all; they
are documented only in their own entries.

## Goals

1. Explain the Sort tab by its groups, matching the on-screen layout.
2. Explain the hair resolution order in plain words, so a user can predict where a mod will go.
3. Keep the Help tab's existing structure and length rules. No new mechanism.
4. Keep `docs/HOW_SORTING_WORKS.md` and `docs/USER_GUIDE.md` consistent with the in-app text.

## Non-goals

- No change to the Help tab's section order or to its collapsible structure.
- No new Help tab, and no walkthrough.
- No change to what Sort does.

## Content changes

### Section body: Sort (`sort` section, `body` field)

Replace the current paragraph with a short overview that names the groups in the order they appear:

- Pick a grouping for all mods.
- Gear, NPC and Hair each have a split option. A split puts that category's mods in subfolders.
- Hair can also be steered by a priority race and a Midlander fallback.
- Nothing moves until Review Changes and Apply.

Keep it under the reading-length norm the other section bodies use, and do not repeat the per-control
detail, which lives in the control entries.

### New control entries

- `sort.priority-race`: short (at most 200 characters, no line breaks). Says the dropdown chooses which
  race's hair number a hair mod is filed under when that race has one, and that None turns it off.
- `sort.midlander-fallback`: short (at most 200 characters). Says that when a mod uses different hair
  numbers for different races and no priority race settles it, the Midlander number is used, and
  that male and female Midlanders must agree or the mod stays flat.

Both register in `HelpTopics`, `HelpTab` and `HelpTopicUsage`, as `sort.split-hair` does. Both are
control topics, so their text is the short form, per the existing rule for controls.

### Hair resolution explanation

A dedicated body, placed in the Sort section after the overview, not as a control entry. It gives the
order a hair mod is checked, in plain words, matching the six steps in the race-priority spec:

1. The priority race, if the mod has one hair number for it.
2. Otherwise, the one hair number the mod uses overall.
3. Otherwise, the Midlander number, if the fallback is on and Midlanders agree.
4. Otherwise the mod stays in the plain Hair folder.

This is a new topic id, `sort.hair-resolution`, with a body (not short), since it is reading material
and not a tooltip. It is registered in `HelpTopics`, `HelpTab` and `HelpTopicUsage`, and placed in
`HelpTab.Sections` under Sort, before the control entries.

## Length and format rules

- `short` fields: at most 200 characters, no line breaks, a test enforces this.
- `body` fields: no length test today; keep to the paragraph style of the other sections.
- ASCII in the JSON. Use `\u` escapes for any symbol. The female and male signs, if used, are written
  as escapes.

## Keeping the docs in step

- `docs/HOW_SORTING_WORKS.md` gets the same resolution order, with the worked examples from the race
  priority spec (115 for Miqo'te, 7 for Viera).
- `docs/USER_GUIDE.md` gets a short Hair section that points at the in-app Help tab, not a copy of it.
- The in-app text is the source; the two docs follow it. Where they disagree, the in-app text wins
  and the doc is corrected.

## Testing

- `HelpContentTests` already checks that every topic has a title and that short fields fit. Extend it
  to cover the two new control topics and the new `sort.hair-resolution` body.
- `HelpTopicUsage` is hand-maintained; add a test that every new topic is referenced by a control or
  listed as a section topic, so it cannot drift silently.
- Manual check: open Help, expand Sort, and read the order against the on-screen layout.

## Open questions

1. Should `sort.hair-resolution` be a body or a list of short entries? Proposed: body, because the
   order matters and reads better as a paragraph.
2. Should the Sort overview name the Midlander fallback, or leave it to its own entry? Proposed: name
   it, once, so the overview is accurate on its own.

## Clarifications from in-game review

Reviewed against the 0.6.1.0 screenshots. Changes to the text above:

- **Group by** needs examples, not just a description. Each option gets one example path for a
  gear mod and one for a hair mod, for example "Creator: `Creator/Mod`", "Mod type: `Gear/Mod`",
  "Type then creator: `Gear/Creator/Mod`", "Creator then type: `Creator/Gear/Mod`". The examples are
  generated from the real folder names, not written by hand, so they cannot drift.
- **Sort section overview** still says "decide whether gear and NPC mods get subfolders". It must
  name Hair too, since the hair split now exists.
- **Assign selected mods** is named in the help, but the button reads "Assign N selected mods". The
  help title should match the button's wording, so a reader can find it.
- **Split hair by hair number** keeps its current text. Its example ("Hair/115", "Hair/9 sorts after
  Hair/115") is the one line in the tab that already explains the ordering oddity, and it stays.

## Sort help button

A `?` button sits directly to the right of the Sort button. Clicking it:

1. Switches the main window to the Help tab.
2. Opens the Sort section of the Help tab (its collapsing header is forced open for that frame) and
   scrolls it to the top.

Implementation shape, for the plan to confirm against the code: a single request field on
`MainWindow` (for example a `HelpTarget` enum with a `Sort` value) that the Sort button sets and
the Help tab consumes once, then clears. It is not a persistent setting. The button uses the
existing `Help` helper for its tooltip, "How sorting works", and is not gated by the activity
gates, because it only navigates.

The Help tab already owns its section order, so the `?` button does not need to know the section
index; it names the section topic (`HelpTabSort`) and the Help tab finds it.
