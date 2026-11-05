# Sort and Review tab UX pass, with a two-checkbox body split

Status: design, revised after review (2026-11-05), approved by the author to plan. Inputs: the author's
in-game screen recording and screenshots of 2026-11-05, a design critique of the Sort tab and a
glance critique of the other tabs. Builds on body detection from mod content (a55ca2a / 522d168).

## Goals

The Sort tab has grown crowded and the Review Changes tab is a wall of equal-weight buttons. This
pass makes the primary action obvious, hides what is not relevant until it is, and splits the body
checkbox in two. Nothing here changes how mods are classified; the only filing change is that the
body split flag becomes per category.

## Where each decision came from

**Decided by the author**
1. The Sort tab is three sections: Sort actions, Body priority, Manual assignment. Sort actions are
   never collapsible. The other two are collapsible and **collapsed by default**.
2. Hair stays inside the Sort actions section (it is not moved into an "Advanced" container).
3. Controls that depend on a checkbox stay **hidden until it is ticked**.
4. The Sort button is the primary action, in the plugin's purple accent.
5. The "Refresh NPC list from wiki" button is hidden while its feature is off.
6. Workbook import and export live together on **Review Changes**.
7. One body checkbox becomes two, and the body-mods one gets a `(?)` explanation.
8. A quick pick above the body priority list ("My main body" in the author's words).
9. A little spacing around Reset to default.
10. The Body priority section is **always present, collapsed**, rather than hidden until a body box
    is ticked (confirmed by the author on 2026-11-05).

**Proposed by the agent, not yet confirmed (each is a one-line change)**
- Expansion state of the two collapsible sections is per session (they start collapsed each time).
- The quick-pick label is "**First-choice body**", because it sets a sorting preference, not which
  body the character uses.
- The "Also use the NPC list scraped from the wiki" checkbox and the yellow note are hidden along
  with the refresh button while the feature is off.
- Under Group by Creator, dependent controls are disabled, not hidden (see below).

## Sort tab layout

```
Group by                      [Type then creator v]
Gear     [ ] Split gear by equipment slot
NPC      [ ] Split NPC mods by kind
Hair     [ ] Split hair by hair number
            Priority race [ v ]        <- only while the hair box is ticked
            [ ] Midlander fallback     <- only while the hair box is ticked
Body     [ ] Split gear by body type
type     [ ] Split body mods by body type [(?)]
[ Sort ]  [?]        Selection changed since the last sort.
> Body priority                        <- collapsed by default
> Manual assignment                    <- collapsed by default
```

- **Sort actions** means never collapsible. It is not pinned to the window while the tab scrolls.
- **Group by** gets a small heading like the other sections.
- **Body priority** (collapsed): a "First-choice body" dropdown, then the hint "Highest first. Used
  when a mod supports more than one body.", then the full ranked list with up/down arrows and Reset
  to default inside a nested, collapsed "Full priority order" header, with a little spacing above
  Reset. When neither body box is ticked the section stays available, and its hint adds "It applies
  when a body split above is ticked."
- **Manual assignment** (collapsed): today's search box, destination folder, Assign button and mod
  list, unchanged.
- **Sort button**: the theme's existing accent colour (the style Scan's Refresh, Search's
  Build/Refresh Index and Help's Join Discord already use), followed by the `?`. The "Selection
  changed since the last sort" hint stays beneath it.
- Removed from this tab: Import Workbook (moves to Review), the wiki-refresh button and its note.

### The First-choice body dropdown

It is always the current rank-1 body, **derived from the priority list each frame, never stored
separately**. After an arrow reorder, Reset to default, or a body-list refresh it shows whatever is
now first, so the dropdown and the list cannot disagree. Choosing a body moves it to rank 1 and
leaves the rest in order, saved at once like every other priority change.

### Visibility and enabled states

| Control | Visible when | Enabled when |
|---|---|---|
| Gear, NPC, Hair, body checkboxes | always | the strategy is not Creator, and the activity gate is open |
| Priority race, Midlander fallback | the hair box is ticked | same as above |
| First-choice body, full list | its section is expanded | the activity gate is open |
| Scraped NPC checkbox, wiki refresh | `ScrapedNpcListFeatureEnabled` is true | the activity gate is open |

Under **Group by Creator** nothing is hidden or cleared: every checkbox, and any dependent controls
whose box is ticked, stay visible but greyed with the existing tooltip, their values preserved, and
they become usable again when the strategy changes back. Dependents of an unticked box stay hidden.

### The two body checkboxes

They are independent of each other and of the slot split. Each acts only on its own category:

| Body split | Slot split | Gear mod lands at |
|---|---|---|
| gear on | off | `Gear/<body>/<mod>` |
| gear on | on | `Gear/<body>/<slot>/<mod>` |
| off | on | `Gear/<slot>/<mod>` |

Body-category mods land at `Body/<body>/<mod>` with the body-mods box on, and at `Body/<mod>` with
it off. Grouping by creator or by type then creator can add a creator folder level to any of these.

The `(?)` beside **Split body mods by body type** is a small **clickable, keyboard-focusable
button**: click, or Enter or Space when focused, opens a popup with the explanation, and clicking
away or Escape closes it. Hovering it also shows the same text as a tooltip, as an additional
convenience. The text: "Body mods are the body replacers themselves (Rue+, YAB, Bibo+...) and SFW
variants that file under Body. Splitting groups them under Body/<body>/. Leave it off if you only
keep one mod per body."

Both boxes are off by default and per session. The saved body priority applies to both.

## Review Changes tab

- **Apply** is the primary button (purple accent), visually separated from the rest.
- **Workbook** group: the combo is relabelled "Group workbook by" (it reads "Workbook
  destinations" today); Export Workbook; **Import Workbook**, moved from Sort with its activity
  gate and the re-check inside the file-dialog callback kept exactly as they are; Open Workbook.
- **Empty state:** the tab renders with nothing staged today and keeps doing so. Import stays
  available whenever the activity gate allows it, including with a scan loaded and no staged
  proposals, so a user can import a workbook without sorting first.
- **Results stay in their own groups.** The workbook export path and Open Workbook belong to the
  last workbook export; the import summary, errors and warnings (which live on the Sort tab today)
  move with Import into the Workbook group and stay until the next import. A diagnostic export or
  dump never replaces either, and Open Workbook always opens the last workbook export.
- **Diagnostics** (collapsed): Export, Show Config File, Create Diagnostic Dump and Show Dump File,
  each with its own result line.

## Code changes

- `SortSelection`: `SplitBody` is replaced by `SplitGearBody` and `SplitBodyMods`; the constructor
  parameter count goes 8 to 9 and `TemplateFallbackConversionTests` is updated on purpose (templates
  still store neither flag).
- `SortFolderSelectors.Select` and `OrganizerState.Sort`: `splitBody` becomes `splitGearBody` and
  `splitBodyMods`. A row's body segment is used only when its own category's flag is on (Gear uses
  `splitGearBody`, Body uses `splitBodyMods`). `StagedBodyResolution` is recorded only for rows
  whose flag is on, and `StagedBodyPriority` while either flag is on.
- `BodyPriority.MoveToTop(label)`: a new snapshot with that label first and the rest in order; a
  label not in the list, or already first, returns the same instance.
- A pure layout helper (public static, in the Sort panel's pure-helper style): given the strategy,
  the split flags, the feature gate and the activity gate, it reports visible and enabled per
  control. The rules in the table above live there and are tested.
- `SortPanel` is split into one draw method per section. The Import Workbook button, its dialog code
  and its result lines move from `MainWindow.SortTab.cs` to `MainWindow.ReviewTab.cs`; the
  `sort.import-workbook` help topic is listed under Review in the Help tab instead of under Sort
  (topic ids unchanged).
- **Help copy updated now:** tooltips and `help-content.json` entries for the controls this pass
  adds, renames or moves (the two body checkboxes, the `(?)`, First-choice body, Import Workbook's
  new home, Group workbook by, Diagnostics). The first-run tutorial, the broader Help tab, the user
  guide, the PDF and handoff notes are deferred.

## Out of scope

- The first-run tutorial rewrite with on-screen highlights, and the Help tab refresh.
- User guide, PDF and handoff updates (done last, once the UI has settled).
- Smaller critique findings, to be taken or left later: History's Delete beside Restore (add a
  confirm), Protect's two "Toggle" buttons (label the direction), Scan's empty "Live events" box
  (shrink), and whether the Search tab still earns its place.

## Testing

Unit tests: the layout helper across every combination (hair box, each body box, Group by Creator,
feature gate on and off, activity gate open and closed); `SortSelection` equality with the new
flags; category-aware `Select` and `Sort` (gear flag only, body flag only, both, neither, with and
without the slot split) including the staged resolution and priority; `BodyPriority.MoveToTop`
(moves, unknown label, already first, immutability); that the dropdown's value equals
`Labels[0]` after MoveUp/MoveDown, Reset and a label-list refresh; the updated tripwire count.

In game (the author):
1. Hair and body dependents appear and disappear with their checkboxes; Group by Creator greys them
   instead of hiding any that are ticked, and their values survive switching back.
2. The `(?)` opens by click and by keyboard, closes on click-away and on Escape, and also tooltips.
3. First-choice body changes rank 1, matches the list after arrow moves and Reset, and survives a
   restart.
4. Gear flag alone files gear under a body and leaves Body mods flat, and the reverse; the slot
   split composes as in the table.
5. Sort is purple, and the wiki-refresh button, note and scraped checkbox are gone.
6. Import Workbook works from Review with a scan loaded and nothing staged, stays gated while a
   scan runs, and its result stays visible after a diagnostic export; Open Workbook still opens the
   last workbook export.
7. Export, import, Review and Apply flow end to end; Diagnostics still produce the dump.
