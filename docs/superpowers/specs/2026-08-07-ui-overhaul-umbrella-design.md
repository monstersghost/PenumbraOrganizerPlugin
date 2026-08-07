# UI overhaul: umbrella

Date: 2026-08-07
Branch: `design/ui-overhaul` (not merged; main is being tested separately)

This is the parent for six pieces of work. Each has its own spec and can be **implemented and
reviewed independently**; they **ship as one release**, with the cross-piece dependencies defined
below. This document exists for the three things that only make sense across all of them: **the
shared content model**, **the dependency graph**, and **the order they are built in**.

An earlier draft of this document said "each ships independently". That was wrong: pieces 3, 4 and 5
form a content chain, piece 5 has no entry point without piece 4, and piece 2 is a worse release
without piece 1. Independence is a property of the *work*, not of the release.

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
- **`id` and `title` are required. `short`, `body` and `step` are each optional, and a topic must
  carry at least one of them.** An earlier draft made `short` mandatory. That was wrong: the Help tab
  has sections with no control at all ("The safety rules", "When something goes wrong", "Where your
  files are"), and the tutorial has steps that are reassurance rather than instruction. Requiring
  `short` on those would force a meaningless tooltip string to be invented purely to satisfy a test,
  and it would ship unread.
- **Validation is by consumer, not by field:**
  - every tooltip reference must resolve to a topic with a non-empty `short`
  - every Help-section reference must resolve to a topic with a non-empty `body`
  - every walkthrough-step reference must resolve to a topic with a non-empty `step`
  - and, in the other direction, every topic carrying `body` must appear in some section list, and
    every topic carrying `step` must appear in the tutorial's step list
- **Topics are of two scopes and the schema says which.** Control topics (`sort.gear-detail`) back a
  widget and carry `short`. Section topics (`help.safety-rules`) back a Help section and carry
  `body`. A section is composed of an ordered list of topics, so a Help section may render its own
  `body` followed by the `body` of the control topics it covers. Without this a flat schema cannot
  express "the Sort section is these five controls in this order", which is what the Help tab
  actually needs.
- **A referenced id that does not exist fails a test, not the build.** An earlier draft claimed
  build-time enforcement; nothing here delivers that. A test enumerates ids and asserts each
  resolves. To make it meaningful, **call sites take a typed topic constant, not a `string`**, so a
  mistyped literal cannot compile in the first place. Without that, `Help.Tooltip("sort.gear-detial")`
  compiles, ships, and passes every test in all three specs.
- The resource is embedded, matching how `npc-name-list-seed.json` already ships, so there is no
  new file to lose or corrupt at runtime.
- **Text is static.** No substitution or state-dependent wording. Controls whose meaning changes with
  state (Toggle protect all is a true toggle in both directions) need one explanation covering both.
- **Tooltips do not wrap on their own.** `ImGui.SetTooltip` lays out one line as wide as it needs, so
  a long `short` produces a tooltip wider than the viewport. Tooltip rendering pushes a fixed wrap
  position; so does the tutorial window.

Piece 3 defines the schema and loader because it is the first consumer. Pieces 4 and 5 add both new
topics and new fields on existing topics; an earlier draft said they only add fields, which is not
true of the cross-cutting Help sections or the tutorial's reassurance steps.

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

Piece numbers identify the dependency sequence for the UI work. **Piece 1 is deliberately parallel:
it may land any time after piece 0, but must be merged before piece 2 is considered release-ready.**
Build order is therefore not a total order over piece numbers, and an earlier draft's claim that
"build order and piece number are the same thing and cannot drift" was false in the document
asserting it.

```
0 ──┬── 1 ──────────────┐
    └── 2 ── 3 ── 4 ── 5 ┴── release
```

### The dependency graph, stated once

| Piece | Hard-depends on | Why |
|---|---|---|
| 0 | nothing | |
| 1 | 0 (soft) | only to avoid editing a file mid-split |
| 2 | 0, **3** | its disabled-checkbox tooltip needs the tooltip mechanism |
| 3 | 0 | defines the schema and loader |
| 4 | 3 | renders `body` from the shared resource |
| 5 | 3, **4** | its only re-entry point is a button on the Help tab |
| release | 1 **and** 2 together | see below |

**Piece 2 depends on piece 3, which contradicts building 2 before 3.** Piece 2 disables both split
checkboxes when Group by is Creator and shows a tooltip explaining why; that tooltip is the
mechanism piece 3 builds. Resolution: **piece 3's schema and loader move to the front of piece 2**,
so piece 2 is the first consumer rather than piece 3. Piece 3 then becomes the sweep across the
remaining controls. The alternative — an inline literal in `SortPanel.cs` to be replaced later — is
the exact thing piece 3 exists to eliminate, with no test that would catch it being left behind.

### Where piece 1 sits

Piece 1 has **two gates, not one**:

- **The static list and the new matcher are ungated.** They remove the oversized compiled-regex path
  that correlates with the reported crashes, restore classification quality, and fix the silently
  broken Detailed sort. **This must not be described as a proven crash fix**: piece 1 establishes
  that resetting the large list stops the observed crash and that the mechanism is unknown, and the
  new matcher has not been shown to remove the fatal path. The justification for shipping it is
  correctness and classification quality, which stand on their own.
- **Only the opt-in toggle for the scraped list is gated**, on reproducing the crash and verifying
  the new matcher in-game against a full 20,115-name list. That toggle ships **disabled**, which
  costs users nothing because the static list is the better default anyway.

**Piece 1 also adds one control to the Sort tab** — the "Also use the NPC list scraped from the wiki"
checkbox, default off. It lands in `SortPanel.cs` (piece 2's file), it is covered by piece 3's
tooltip sweep, and its topic id is `sort.scraped-npc-list`. Piece 1 additionally leaves the wiki
refresh button disabled, which piece 2's out-of-scope list must not contradict.

One real interaction forces pieces 1 and 2 into the same release: **piece 2 adds "split NPC mods by
kind", and piece 1 decides which mods are NPCs at all.** With the 20,000-name list in play nearly
everything classifies as NPC, so that new checkbox would appear broken.

### What is not part of this overhaul

`2026-08-07-library-work-breadcrumbs-design.md` sits on the same branch and is **not** one of the six
pieces. It is a diagnostic for the same crash piece 1 correlates with, and **the crash cause remains
open**: nothing in this overhaul closes that investigation.

Its implementation is **postponed until after piece 1 ships and we see whether crashes persist**. The
reason is specific: the suspect matcher is built in `ScanProcessor.Prepare`, which
`LibraryWorkCoordinator.RunBatch` calls **before** the per-item loop. If death occurs there, per-item
breadcrumbs yield a header and no item at all. The breadcrumb spec handles that case correctly and
refuses to blame item 1, but its diagnostic value against *this* crash is much lower than when it was
written. If crashes stop after piece 1, we avoid adding a per-item flushed write nobody needs; if
they continue, breadcrumbs are then instrumenting a residual crash rather than a path already
replaced.

## What this overhaul does not change

- Nothing about Apply, Restore, the operation state machine, or recovery.
- No classification behaviour beyond what piece 1 and piece 2 each state explicitly.
- No new Penumbra IPC.
- The tab set grows by exactly one (Help). No tabs are removed or reordered.

## Risk worth naming once

**Three** of these pieces modify `MainWindow`'s UI surface — piece 2 (the sort block), piece 3
(tooltips at ~30 call sites) and piece 4 (a tab entry plus dispatch). Piece 5 does **not**: it
registers `FirstRunWindow` as a second Dalamud `Window` and integrates through a button on the Help
tab. An earlier draft said "pieces 2 to 5 all add UI to it", which is not true, and three consumers
is ample justification without inflating it.

`MainWindow.cs` is 2,080 lines and the largest file in the project. Adding tooltip plumbing, a tab
and a rebuilt sort panel without restructuring would make it materially worse, which is what piece 0
addresses.

Each spec puts its own UI in its own file (`SortPanel.cs`, `HelpTab.cs`, `FirstRunWindow.cs`).
`MainWindow` keeps tab dispatch and shared state; the panels own their drawing. **Tab dispatch is the
one place all of them converge**, so it stays in `MainWindow.cs` after piece 0's split and is the
expected growth point.
