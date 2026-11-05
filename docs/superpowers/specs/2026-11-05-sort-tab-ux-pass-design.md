# Sort and Review tab UX pass, with a two-checkbox body split

Status: design, awaiting author review (2026-11-05). Inputs: the author's in-game screen recording
and screenshots of 2026-11-05, and a design critique of the Sort tab and a glance critique of the
other tabs. Builds on body detection from mod content (merged as a55ca2a / 522d168).

## Goals

The Sort tab has grown crowded, and the Review Changes tab is a wall of equal-weight buttons. This
pass makes the primary action obvious, hides what is not relevant until it is, and splits the body
checkbox in two. Nothing here changes how mods are classified or where they are filed, except the
body split flag becoming per category.

## Decisions (all from the author)

1. **Three sections on the Sort tab.** Sort actions are always visible. Body priority and Manual
   assignment are collapsible and collapsed by default.
2. **Hair stays in the Sort actions section.** It is not moved into an "Advanced" container.
3. **Dependent controls are hidden until their checkbox is ticked**, not greyed. The exception is
   Group by Creator, which greys the split checkboxes (hiding them would empty the section).
4. **The Sort button is the primary action**, in the plugin's purple accent, the same style the
   Scan tab's Refresh, Search's Build/Refresh Index and Help's Join Discord already use.
5. **The disabled NPC-wiki feature is hidden**: the "Refresh NPC list from wiki" button, its long
   yellow note, and the "Also use the NPC list scraped from the wiki" checkbox do not render while
   `Configuration.ScrapedNpcListFeatureEnabled` is false.
6. **Workbook import and export live together on Review Changes.** Import moves off the Sort tab.
7. **One body checkbox becomes two**, with a visible `(?)` explanation on the body-mods one.
   Nothing is released, so there is no migration concern.
8. **A "My main body" quick pick** above the full priority list, for the common one-body case.

## Sort tab layout

```
Group by                      [Type then creator v]
Gear     [ ] Split gear by equipment slot
NPC      [ ] Split NPC mods by kind
Hair     [ ] Split hair by hair number
            Priority race [ v ]        <- only while the hair box is ticked
            [ ] Midlander fallback     <- only while the hair box is ticked
Body     [ ] Split gear by body type
type     [ ] Split body mods by body type (?)
[ Sort ]  [?]        Selection changed since the last sort.
> Body priority                        <- section appears only while a body box is ticked
> Manual assignment                    <- collapsed by default
```

- **Group by** gets a small heading like the other sections. With Group by Creator, the split
  checkboxes are greyed with the existing tooltip; their dependent controls stay hidden if their own
  checkbox is unticked.
- **Body priority** (collapsed): a "My main body" dropdown that moves the chosen body to rank 1 and
  leaves the rest in order (saved at once, like every other change to the priority); the "Highest
  first. Used when a mod supports more than one body." hint; the full ranked list with up/down
  arrows and Reset to default inside a nested, collapsed "Full priority order" header, with a little
  spacing above Reset. The section appears only while at least one body checkbox is ticked.
- **Manual assignment** (collapsed): today's search box, destination folder, Assign button and mod
  list, unchanged.
- **Expansion state** of the two collapsible sections is per session: they start collapsed each time.
- **Sort button**: purple accent via the theme's existing accent colour, followed by the `?`. The
  "Selection changed since the last sort" hint stays beneath it.
- Removed from this tab: Import Workbook (moves to Review), the wiki-refresh button and its note.

### The two body checkboxes

- **Split gear by body type**: Gear mods file at `Gear/<body>/<slot>/...`.
- **Split body mods by body type (?)**: Body-category mods file at `Body/<body>/...`. The `(?)` text:
  "Body mods are the body replacers themselves (Rue+, YAB, Bibo+...) and SFW variants that file
  under Body. Splitting groups them under Body/<body>/. Leave it off if you only keep one mod per
  body." Its hover shows the same text as a tooltip; the `(?)` makes it visible without hovering.
- Each acts only on its own category. Both are off by default and per-session. The saved body
  priority applies to both.

## Review Changes tab

- **Apply** becomes the primary button (purple accent) and is visually separated from the rest.
- **Workbook** group: the combo is relabelled "Group workbook by" (it currently reads
  "Workbook destinations" beside "By Type Then Creator"); Export Workbook; **Import Workbook**
  (moved from Sort, with its activity gate and the re-check inside the file-dialog callback kept
  exactly as they are); Open Workbook after an export.
- **Diagnostics**: Export, Show Config File, Create Diagnostic Dump and Show Dump File move into a
  collapsed "Diagnostics" section, with their path lines.
- The stacked "Exported to..." lines collapse to the most recent result.

## Code changes

- `SortSelection`: `SplitBody` is replaced by `SplitGearBody` and `SplitBodyMods`; the constructor
  parameter count goes from 8 to 9 and `TemplateFallbackConversionTests` is updated on purpose
  (templates still store neither flag).
- `SortFolderSelectors.Select` and `OrganizerState.Sort`: `splitBody` becomes
  `splitGearBody` and `splitBodyMods`. A row's body segment is used only when its own category's
  flag is on: Gear uses `splitGearBody`, Body uses `splitBodyMods`. `StagedBodyResolution` is
  recorded only for rows whose flag is on; `StagedBodyPriority` while either flag is on.
- `BodyPriority.MoveToTop(label)`: returns a new snapshot with that label first and the rest in
  order; a label not in the list returns the same instance.
- A pure visibility helper (in the Sort panel's pure-helper style, public static, testable):
  given the strategy, the split flags and the feature gate, it reports which controls show and which
  are greyed. This is where the "hidden until ticked" rules live, so they are tested.
- `SortPanel` is split into the three section draw methods; the Import Workbook button and its
  dialog code move from `MainWindow.SortTab.cs` to the Review tab's draw code; the help topic
  `sort.import-workbook` is listed under Review instead of Sort. Topic ids are unchanged.
- Help copy and guide text: updated for the new layout; the first-run tutorial and the Help tab
  rewrite are a separate, later piece.

## Out of scope (separate items)

- Rewriting the first-run tutorial with on-screen highlights, and the Help tab refresh.
- Docs, PDF and handoff updates (done last, once the UI has settled).
- Smaller findings from the critique, to be taken or left: History's Delete next to Restore (add a
  confirm), Protect's two "Toggle" buttons (label the direction), Scan's empty "Live events" box
  (shrink), and whether the Search tab still earns its place.

## Testing

Unit tests: the visibility helper for every combination (hair box, each body box, Group by
Creator, feature gate on and off); `SortSelection` equality with the new flags; category-aware
`Select` and `Sort` (gear flag on only, body flag on only, both, neither) including the staged
resolution and priority; `BodyPriority.MoveToTop` (moves, unknown label, already first, immutability);
the updated tripwire count.

In game (the author):
1. Hair and body dependent controls appear and disappear with their checkboxes; Group by Creator
   greys the checkboxes; the Body priority section appears only while a body box is ticked.
2. "My main body" changes rank 1 and survives a restart; the nested full list still reorders.
3. Gear flag alone files gear under a body and leaves Body mods flat, and the reverse.
4. Sort is purple; the wiki-refresh button and note are gone.
5. Import Workbook works from Review (including while a scan runs: it must stay gated).
6. Export, import, Review and Apply flow end to end; Diagnostics still produce the dump.
