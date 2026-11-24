# Sort tab UI rework (spec 2) - in-game checklist

Build: `feat/category-layouts` (spec 1 plus the spec 2 UI Tasks 1-10). Keep a copy of `pluginConfigs/PenumbraOrganizer.Plugin.json` before starting. Nothing here has been seen in game yet: every item below is unverified until ticked.

Screens and the bar
- [ ] Fresh install (config renamed away): the tab opens on Recommended. With a config that has any `CategoryLayouts` entry it opens on Advanced with those categories badged Custom. An existing user on today's defaults (Gear, Hair, Body and Animation overrides from the spec 1 migration) opens on Advanced with those four badged.
- [ ] The pinned bar (Recommended | Advanced, Sort, scope line, ? icon) stays in view while the body scrolls; the ? shows a question-mark glyph and opens Help at Sort.
- [ ] Switch modes back and forth; Group by and What to sort keep their values in both screens.
- [ ] Recommended: the four example paths change when Group by changes (Creator only shows `Ann / Medieval Dress`), and the Hair example gains `Viera` when Split by race is turned on in Advanced.
- [ ] Recommended: "Customise per category" opens Advanced; after customising a category the "N categories customised" line and "Show in Advanced" appear, and the examples show the customised layout.
- [ ] Recommended: the "Everything else" line says "Race split is off." with the master off and "Split by race is on (see Advanced)." with it on.
- [ ] Rules only in Advanced: Recommended shows no Rules card (Sort settings and Manual assignment still show); Advanced shows the Rules card between the Advanced layout card and Sort settings.
- [ ] Recommended with rules: with one enabled, valid rule the first card says "1 rule active, edit it in Advanced"; with two, "2 rules active, edit them in Advanced"; a switched-off rule or one with a red error is not counted; with none the line is gone. "Show rules" opens Advanced; its tooltip is the Rules help.
- [ ] Rules still apply from Recommended: with an active rule, press Sort on Recommended and the matching mod gets the rule's folder on Review Changes.
- [ ] Type into a rule in Advanced and switch to Recommended without leaving the box: the typed text is saved (the count on Recommended reflects it; reload keeps it).

Advanced
- [ ] Category list: hover highlight, accent marker on the selected row, Custom badges; the list scrolls on its own if the window is short; Tab/arrow keys reach the rows and Space or Enter selects; a focus ring is visible.
- [ ] Toggle a split: the Custom badge and the header count ("N of 16 categories customised") update at once; reload the plugin and it is kept; `CategoryLayouts` in the config has the category; toggling back removes it.
- [ ] Toggles: click on the switch or the label flips it; Space flips it with keyboard focus; greyed toggles do not react and their tooltip gives the reason.
- [ ] Depth numbers show 1, 2 for the enabled folder splits only; Hands and feet is a checkbox with no number.
- [ ] Hands and feet greys with "Needs Equipment slot." when Equipment slot is off (tick kept); Body's race rows grey with the Part and body type reason when that split is off.
- [ ] Body shows "Part and body type", "Tails, ears and horns by race", "Racial scaling by race"; Face shows "Race" and "Eyes by race"; Minion shows the no-splits text (Weapon has the Job split since feat/weapon-split).
- [ ] Rename Gear to `Equipment/Gear`: typing `..`, an empty box, `C:/x`, `a//b` and `{x}` each show the error under the field and the badge does not change; a valid name is saved when you leave the box (reload keeps it); Sort proposes `Equipment/Gear/...` for gear only; Apply moves them.
- [ ] Type a folder and click Sort straight away without leaving the box: the plan uses the new folder (B12). Same when switching category or mode instead of clicking Sort.
- [ ] Warnings (saved anyway): Gear folder `Hair` says Hair also uses it; a folder inside a protected folder and one inside a sort-inside folder each say so.
- [ ] Creator folder: under Type then creator, "After type folders" is selected with "Follows Group by" and the category stays Recommended; picking None makes it Custom and gear mods lose their creator folder; picking After again returns to Recommended.
- [ ] Reset to Recommended (one category) and Reset all to Recommended (disabled when nothing is customised) put folders, creator levels and splits back. Reset all acts at once, without a confirmation (see Notes).
- [ ] Creator only: every split and the creator folder grey with "Grouping by creator alone never uses the mod's type."; the folder box stays editable.
- [ ] During a scan or an Apply every editor control greys with the activity reason; the mode switch and ? still work.
- [ ] Race panel appears under Hair, Face, Skin and Body only: Split by race, Send multi-race mods to Multi-race, Race priority (add, move, remove); the rescan hint shows with the master on before a rescan.
- [ ] Hair: Priority race and Midlander fallback appear only while Hair number is on, and are saved (reload keeps them).
- [ ] Body types panel under Gear and Body: First-choice body, Check my collection with Make first choice, the full order with arrows and Reset to default. Its hint says the priority applies only when a body split is on while Gear's Body type and Body's Part and body type are both off.
- [ ] NPC: with the scraped-list feature compiled in, "Also use the NPC list scraped from the wiki" shows under the NPC editor and saves; with it compiled out (today's builds) nothing shows.
- [ ] Labels before controls everywhere on the tab: Group by, What to sort, Priority race, First-choice body, Add a race, When new mods are installed, Search mods and Destination folder each read label first, the label text level with the text inside its box; tooltips still show over the control.
- [ ] Split rows: the depth number sits level with the toggle's label, not a few pixels above it.
- [ ] Right margin: every top-level card (Recommended, What Recommended does, Advanced layout, Rules, Sort settings) and the Manual assignment mod list stop a small gap short of the scrollbar (or the window edge when there is no scrollbar); the example-path strips end inside their card; no horizontal scrollbar appears at any width. The Body types and Race cards inside Advanced fill the editor column.
- [ ] Pinned bar: the ? icon sits at the right end of the bar; a long scope line (several rule notes) wraps before it instead of running under it or off the window, at 900 px and wide.
- [ ] Card spacing: the gap between consecutive cards is the same on both screens (Recommended no longer has a double gap above Sort settings).
- [ ] Manual assignment: with 0 mods ticked, tick one and click Assign in the same motion; the click lands (stable ### id). At 900 px the Assign button fits on the Destination folder line.
- [ ] Narrow window: at the 900 px minimum width the list and editor stay side by side (stacking needs a lower minimum, B14); nothing overlaps or clips badly.

Select all splits (Task 8)
- [ ] The button sits in the Advanced header row, beside Reset all to Recommended.
- [ ] Its tooltip is the Help topic text ("Turns on every split in every category and Split by race, ..."), not the old "Ticks every split and race row" line; greyed (during a scan, or under Creator only) the tooltip gives the reason.
- [ ] The dialog lists the three bullets (every gear, NPC, hair, body and emote split; the race split on every category that has one, and Split by race; hands and feet get their own folders) and the lines "Folder names, creator levels, Group by and priorities stay as they are." and "Nothing moves until you press Sort, review the plan and Apply."
- [ ] Cancel closes it without a change. Escape closes it without a change, and Escape does NOT also close the plugin window behind it.
- [ ] Confirming turns every split on, Hands and feet off, Split by race on; folders, creator levels, Group by, race priority and body priority unchanged; nothing is sorted; the Custom badges and count update; reload keeps it.
- [ ] With a renamed folder (Gear as `Equipment/Gear`) or a category's own creator level, Select all still turns that category's splits on and keeps its folder and creator level (no category is read-only any more).

Old controls gone, settings kept (Task 9)
- [ ] No "Custom (read-only here)" line, no old checkbox sections (Gear, NPC and Hair; Body type; Race with six rows) and no "(?)" button beside a body split anywhere on the tab; the Body "Part and body type" tooltip carries that text instead.
- [ ] Settings persist exactly as before. Change each of these, close and reopen the game (or reload the plugin), and check it is kept: Group by, a split in each of Gear, NPC, Hair, Body, Face, Skin and Animation, the race master, Send multi-race mods to Multi-race, Race priority, Priority race, Midlander fallback, body priority, When new mods are installed, Show a popup.
- [ ] After those changes the legacy mirror in the config file still matches (`SortSplitGear`, `SortSplitNpc`, `SortSplitHair`, `SortSplitGearBody`, `SortSplitBodyMods`, `SortSharedHandsFeet`, `SortSplitEmotes`, `RaceSplitRows`, `SortSplitHairByRace`), so the previous release still reads the same splits (optional: load the saved config with a build from before category layouts and compare a Sort).
- [ ] Turning only the race master on with Hair's race split off leaves `SortSplitHairByRace` false in the config.
- [ ] Rules card (Advanced): a rule using `body` shows the body label the current Gear Body type / Body Part and body type splits give (the rules editor now reads the saved splits).

Cards, help, staleness
- [ ] Cards: the first frame of a card, opening Rules or Body priority inside a card, and adding a rule grow the card on the next frame without visible flicker or clipping; the page scrolls with the mouse wheel over a card.
- [ ] Scroll jump (fixed): on Advanced with Gear selected, open Body priority and Full priority order, then expand Manual assignment and wheel down to the bottom of the mod list and back up. The page scrolls smoothly and never jumps back up; the scrollbar thumb does not change size while scrolling. Repeat on Recommended with a short window.
- [ ] Rules card (Advanced): add a rule; the header label count changes and the header stays open (### id).
- [ ] Sort settings card: When new mods are installed and the popup option still save.
- [ ] "Selection changed since the last sort" appears in the bar after changing a split, a folder, a creator level, Group by, the race master or a rule.
- [ ] Tooltips: every new control shows its help text; the segmented controls show it over the last segment (known limit); the Help tab's Sort section lists Recommended or Advanced, Categories, Folder, Creator folder, Splits, Example, Select all splits and Reset to Recommended, and the renamed split topics.
- [ ] Walkthrough: the Sort step still outlines the Sort button; its text reads true.
- [ ] Theme: badge, toggle, accent marker and path colours are readable on the dark theme.
- [ ] Retake `docs/images/sort.png` (Recommended) and `docs/images/sort-body-priority.png` (Body types panel), then rebuild the PDF. Until then the guide's two Sort pictures show the old tab.

## Notes

Status (2026-11-24): UI Tasks 1-10 of the plan are built on `feat/category-layouts`, on top of spec 1. Unit tests, the engine-agreement test for the example paths, the untouched spec 1 golden and a Release build pass. Nothing has been run in game; the screenshots and the guide PDF are not updated. No release and no version bump.

Decisions that need the author's OK:

- **Group by shown on both screens (B2).** Spec 2 puts Group by and What to sort only on the Recommended card. Advanced categories inherit their creator level from Group by and What to sort applies to both screens, so both screens draw the same row; without it an Advanced user could not change either.
- **Reset all without confirmation (B17).** As in the mock-up, Reset all to Recommended acts at once. It discards every customised folder, creator level and split in one click (nothing moves until Sort and Apply). A confirmation like Select all's would be a small change if wanted.
- **Cards use last frame's height (B7).** Dalamud's ImGui binding has no auto-resizing child, so a card opens at the height its content had last frame. A new card, or one whose content just grew (Rules opened, a rule added), is one frame short. Check the flicker item above.

Wording left as it is:

- **"Sort > Advanced".** The new-mod chat line (`NewModPromptText`, pinned word for word by `NewModPromptTextTests` and `NewModSortPipelineTests`) and the help short of `newmods.popup-dont-show` still say "Turn it back on in Sort > Advanced". The setting now lives in the **Sort settings** card, and "Advanced" is now a screen of the Sort tab, so the line points to the wrong place. Changing it means changing those tests' expected text; it was left for the author to decide (suggested: "Sort > Sort settings").
- The Templates tab's export section still has its own checkboxes named "Split gear by equipment slot" and so on. They belong to template export, not the Sort tab, and were out of scope.

Other things to know:

- Stacking the category list above the editor (B14) is built and tested but cannot happen at the window's 900 px minimum width.
- Select all no longer skips any category: spec 1's read-only categories do not exist in the new editor (B11, B21).
