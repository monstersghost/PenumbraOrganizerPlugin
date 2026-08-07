# UI overhaul: umbrella

Date: 2026-08-07
Branch: `design/ui-overhaul` (not merged; main is being tested separately)

This is the parent for six pieces of work. Each has its own spec, each ships independently, and
this document exists for the two things that only make sense across all of them: **the shared
content model** and **the order they are built in**.

## The six pieces

| # | Piece | Spec |
|---|---|---|
| 0 | Split `MainWindow` before four features land in it | `2026-08-07-mainwindow-split-design.md` |
| 1 | NPC name lists and a matcher that cannot explode | `2026-08-07-two-list-npc-names-design.md` |
| 2 | Sort control consolidation | `2026-08-07-sort-control-consolidation-design.md` |
| 3 | Hover explanations | `2026-08-07-hover-explanations-design.md` |
| 4 | Help tab | `2026-08-07-help-tab-design.md` |
| 5 | Guided first run | `2026-08-07-guided-first-run-design.md` |

Piece 1 was specced before this umbrella and is unchanged by it. It is listed because it is part of
the same release and because it changes behaviour the other four have to describe.

Piece 0 exists because four of the others add UI to `MainWindow.cs`, already 2,080 lines with 16
draw methods. Its spec carries a hard constraint worth repeating here: **stage A is a mechanical
partial-class move and nothing else**, because no test covers the draw path. Real component
extraction is stage B and belongs to whichever feature needs the boundary.

## The shared content model

The tooltip on the Detailed checkbox, the Help tab's section about sorting, and the tutorial step
that explains sorting are **the same explanation at three depths**. Written three times in three
places they will drift, and within two releases the tooltip will describe behaviour the plugin no
longer has.

So they are written once, in one embedded resource, and surfaced at three depths.

```
help-content.json  (embedded resource, like npc-name-list-seed.json today)
   │
   ├─ Short  -> hover tooltips        (one line, no formatting)
   ├─ Body   -> Help tab sections     (several paragraphs)
   └─ Step   -> guided first run      (one instruction, optional)
```

Each topic:

```json
{
  "id": "sort.gear-detail",
  "title": "Split gear by equipment slot",
  "short": "Puts gear in Gear/Head, Gear/Feet and so on instead of one Gear folder.",
  "body": "...",
  "step": "..."
}
```

Rules that make this hold:

- **`id` is referenced from code, never the text.** A control names its topic; it does not carry a
  string literal. Changing wording is a resource edit, not a code edit.
- **`short` is one line and unformatted.** It is a tooltip. If an explanation cannot fit, that is a
  signal the control needs a better label, not a longer tooltip.
- **`body` may be absent** when a control needs a tooltip but not a Help section. **`step` may be
  absent** for anything the tutorial does not walk through. `short` is required.
- **A referenced id that does not exist is a build-time failure**, not a silent empty tooltip. A
  test enumerates every id referenced in code and asserts each resolves. This is the whole reason
  the indirection is worth having.
- The resource is embedded, matching how `npc-name-list-seed.json` already ships, so there is no
  new file to lose or corrupt at runtime.

Piece 3 defines the schema and loader because it is the first consumer. Pieces 4 and 5 add fields to
existing topics rather than inventing their own stores.

## Build order, and why

**0. Split `MainWindow`.** Before anything else, because pieces 2 to 5 all add UI to it. Splitting
afterwards means touching all four again. This was on the cleanup brief sequenced *last*, which was
right while it stood alone and is wrong now: a split is worth most immediately before four features
are added, not after.

**2. Sort control consolidation.** Not because it is smallest. Everything else *documents* the UI.
Writing tooltips, Help sections and tutorial steps for seven sort buttons that are about to become
one dropdown means writing them twice and throwing half away. Consolidate the surface, then describe
it once.

**3. Hover explanations.** Cheapest per unit of value, and it forces a one-line explanation of every
control to exist. That is exactly the raw material pieces 4 and 5 build on, so doing it third means
they start with content rather than a blank page.

**4. Help tab.** Written from `short` entries that already exist, expanded into `body`. Much of the
prose can be adapted from `docs/USER_GUIDE.md`, which is current and accurate.

**5. Guided first run.** Last. It is the only piece needing persistent state and flow control, and
by the time it is built every step's text has been written and reviewed twice.

Headings above use the piece numbers from the table, so build order and piece number are the same
thing and cannot drift.

### Where piece 1 sits

Piece 1 has **two gates, not one**, and conflating them was an error in an earlier draft of this
document:

- **The static list and the new matcher are ungated.** This is the part that fixes users' crashes
  and the silently broken Detailed sort. It ships with the overhaul.
- **Only the opt-in toggle for the scraped list is gated**, on reproducing the crash and verifying
  the new matcher in-game against a full 20,115-name list. That toggle ships **disabled**, which
  costs users nothing because the static list is the better default anyway.

So piece 1 is in the release, not alongside it. It is sequenced loosely against pieces 0 and 2 to 5
only so that an investigation of unknown duration holds up one checkbox rather than five features.

One real interaction argues for shipping them together rather than apart: **piece 2 adds "split NPC
mods by kind", and piece 1 decides which mods are NPCs at all.** With the 20,000-name list in play
nearly everything classifies as NPC, so that new checkbox would appear broken. Piece 2 without piece
1 is a worse release than either alone.

## What this overhaul does not change

- Nothing about Apply, Restore, the operation state machine, or recovery.
- No classification behaviour beyond what piece 1 and piece 2 each state explicitly.
- No new Penumbra IPC.
- The tab set grows by exactly one (Help). No tabs are removed or reordered.

## Risk worth naming once

Four of these five pieces touch `MainWindow.cs`, which is already around 2,000 lines and is the
largest file in the project. Adding a Help tab, a tutorial window, tooltip plumbing and a rebuilt
sort panel to it without restructuring would make it materially worse.

Each spec therefore puts its own UI in its own file (`HelpTab.cs`, `FirstRunWindow.cs`,
`SortPanel.cs`) rather than adding to `MainWindow`. That is not opportunistic refactoring: it is the
only way to add four features to one file without compounding an existing problem. `MainWindow`
keeps tab dispatch and shared state; the panels own their own drawing.
