# 0.7.1.1 - a rebuilt Sort tab, race folders, a reworked workbook and keyword rules

This is the first stable release since 0.6.6.0, on both the stable and the testing channel. It brings together
everything from the two testing builds (0.7.0.0 and 0.7.1.0) and a good deal that was built after them. Where
this page and the older testing notes disagree, this page is right: several things changed again after those
builds (for example the Rules card now lives in Advanced only, and the "Sort settings" card is now called
"Auto sort").

Nothing moves until you press Sort, look at the plan on Review Changes and press Apply. Please read
**Upgrading** below before your first Sort.

## New in 0.7.1.1 at a glance

- A rebuilt Sort tab with two screens, **Recommended** and **Advanced**, and a bar at the top that stays in view.
- Per-category settings: rename or nest any category's folder, choose its creator folder, and switch its splits.
- **Race folders** for hair, faces, eyes, skin, tails, ears and horns, and racial scaling packs.
- **Weapons by job** (opt-in): weapon-only mods leave Gear and file under `Weapon/<job>`.
- **Keyword rules**: file particular mods yourself with one-line rules in a small, SQL-like language.
- A reworked **Excel workbook**: export what Sort suggests, add your own categories, use tokens, and get a clear
  import report.
- A notice when new Heliosphere or Proteus mods arrive and stay where they are, and a status line about them on
  the Sort tab.

## The rebuilt Sort tab

### The bar at the top

- The top of the Sort tab is now a bar that stays in view while everything below it scrolls. It holds the
  **Recommended | Advanced** switch, the **Sort** button, the line saying how many mods the Sort covers, and the
  **?** button (at the right end of the bar), which opens the Help tab at its Sort section.
- "Selection changed since the last sort." appears in the bar after you change a split, a folder, a creator
  folder, Group by, the race switch or a rule, so you know to press Sort again.
- The Heliosphere and Proteus status line also sits in this bar (see **Heliosphere and Proteus** below).
- 0.7.0.0 tried a smaller version of this (the Sort button, Group by and What to sort pinned at the top). This
  bar replaces it.

### Recommended

- One calm card with **What to sort**, **Group by** and four live example paths (Gear, Hair, Body and NPC)
  showing where such a mod would go. The examples follow Group by and any category you customised, so the
  screen never describes a layout the Sort will not use.
- A second card, **What Recommended does**, lists in one line per kind of mod what the layout does, with
  **Customise per category**, which opens Advanced. A line says whether the race split is on.
- If you customised categories, a line says how many, with **Show in Advanced**.
- Rules are edited in Advanced only, but they still apply when you Sort from Recommended. While any rule is
  active, Recommended says so ("2 rules active, edit them in Advanced") with **Show rules**, which opens
  Advanced with the Rules card open and scrolled into view.
- The tab opens on Recommended when nothing is customised, otherwise on Advanced.

### Advanced

- The 16 kinds of mod (Gear, Weapon, Face, Hair, Body, Skin, NPC, Minion, Mount, Pet, Ornament, Furniture,
  Sound, Animation, VFX, Others) on the left, and the selected one's settings on the right. The list works
  with the keyboard (Tab, arrows, Space or Enter) and scrolls on its own in a short window.
- For each category:
  - **Folder**: its top folder, which can be renamed or nested, such as `Equipment/Gear`. See **Folder names**
    below.
  - **Creator folder**: none, before the type folders, or after them. By default a category follows Group by.
  - **Splits**: each split is a switch with its depth number (1, 2, ...) showing which folder level it adds.
    Gear: Body type, Equipment slot, and Hands and feet in one shared folder. NPC: Kind. Hair: Race and Hair
    number. Body: Part and body type, Tails, ears and horns by race, and Racial scaling by race. Face: Race and
    Eyes by race. Skin: Race. Animation: Emote. Weapon: Job. A split that cannot apply right now is greyed with
    the reason and keeps its setting.
  - **Example**: one live path for this category.
- A **Custom** badge marks every category that differs from Recommended, and the header counts them ("N of 16
  categories customised").
- **Reset to Recommended** puts one category back; **Reset all to Recommended** puts every category back at
  once (it does not ask first). Nothing moves until you Sort and Apply.
- **Select all splits** (in the Advanced header) turns on every split in every category, the race split of
  every category that has one, **Split by race**, and weapons by job, and turns the shared hands and feet
  folder off so hands and feet get their own folders. It asks first and lists exactly that. Folder names,
  creator folders, Group by and your priorities stay as they are, and it does not sort. Cancel or Escape
  closes it without a change.
- Settings that belong to a category now sit under it:
  - **Race** panel (Split by race, Send multi-race mods to Multi-race, Race priority) under Hair, Face, Skin
    and Body.
  - **Body types** panel (First-choice body, Check my collection with Make first choice, the full Body
    priority order and Reset to default) under Gear and Body.
  - **Priority race** and **Midlander fallback** under Hair, shown while Hair number is on.
- With Group by set to Creator, every split and the creator folder are greyed ("Grouping by creator alone never
  uses the mod's type."); the folder name stays editable.
- During a scan or an Apply the editor greys with the reason; the mode switch and **?** still work.
- **Group by** and **What to sort** are on both screens and keep their values when you switch.
- The old checkboxes are gone. Your ticks carry over (see **Upgrading**). The old names map like this:

| Old checkbox | Now |
| --- | --- |
| Split gear by equipment slot | Gear: Equipment slot |
| Split gear by body type | Gear: Body type |
| Hands and feet in a shared folder | Gear: Hands and feet in one shared folder |
| Split NPC mods by kind | NPC: Kind |
| Split hair by hair number | Hair: Hair number |
| Split hair by race | Hair: Race (with Split by race on) |
| Split body mods by body type | Body: Part and body type |
| Split emotes by emote | Animation: Emote |

### The cards below

- In Advanced: the **Rules** card (see **Keyword rules**), then **Auto sort**, then Manual assignment. In
  Recommended: Auto sort and Manual assignment.
- **Auto sort** is the card that used to be called **Sort settings**: what happens when new mods are installed,
  and whether the new-mod popup shows. If it was open before you updated, it stays open. The new-mod chat line
  and the help for "Don't show this popup" now point to **Sort > Auto sort** instead of the old "Sort >
  Advanced".

### The Recommended layout

Recommended is a curated starting layout:

- Gear by body type, then by equipment slot, with hands and feet in one shared folder (the numbered `Gear`
  folders from 0.6.5.0).
- Hair by hair number.
- Body mods by body type, then part (the numbered `Body` folders).
- NPC mods by kind.
- Emotes by emote.
- One folder per type for everything else.
- Race split off (the race switch is off; turning it on splits every race row at once).
- Weapons by job off.

A category you never changed follows Recommended, so later improvements to Recommended reach it. A category you
changed keeps your setting. **A new install starts on Recommended**, which turns on many more splits than a new
install of 0.6.6.0 did (that only split NPCs by kind).

## Folder names

- A category's folder is checked as you type. It must be relative (no drive such as `C:/x`), have no empty
  part (`a//b`), no `.` or `..` part, no `{token}`, and be at most 260 characters. Parts are trimmed the way
  Penumbra trims them. An invalid name shows the reason under the box and is never saved.
- A valid name is saved when you leave the box, and also if you click Sort, switch category or switch screen
  straight after typing.
- Warnings (the name is still saved): another category already uses this folder, or the folder is inside a
  protected folder, or inside a "sort inside" folder.
- Renaming moves nothing until you Sort and Apply. Sort then files only that category under the new folder.
- A renamed category folder counts as one of the sorter's own folders. So mods already inside it are not
  treated as being in a "sort inside" folder, Sort does not move them again on the next pass, and the Protect
  tab does not offer it (or its subfolders) as a sort-inside folder; a folder above it, such as `Equipment`,
  still is.
- With Heliosphere or Proteus set to **Sort inside their folder**, a tool mod already under a renamed folder is
  not filed under it twice (no `Kit/Kit/...`), and its body folder takes the same number as the other gear for
  that body.

## Race folders

- **Split by race** is a master switch, off by default and greyed out under Creator grouping. It controls six
  race splits: Race in Hair, Face and Skin, Eyes by race in Face, and Tails, ears and horns by race and Racial
  scaling by race in Body. All six are on in Recommended, so turning the switch on splits all of them; switch
  one off in its category to leave that kind of mod as it is.
- Folders are by race, never by gender: Midlander, Highlander, Elezen, Miqo'te, Roegadyn, Lalafell, Au Ra,
  Hrothgar and Viera, for example `Face/Viera`, `Skin/Miqo'te`, `Body/02. Tails, Ears and Horns/Viera`. With Hair
  number also on, the number goes below the race (`Hair/Viera/115`).
- A mod made for one race files under that race. A mod made for several races goes to `Multi-race` while **Send
  multi-race mods to Multi-race** is ticked. Untick it and the mod goes under the highest-ranked race it covers
  in **Race priority** (add, move and remove races there); a race you do not rank never wins. A mod with no race
  found, or one that could not be read, always goes to `Multi-race`.
- **Eyes**: iris-only face mods file under `Face/Eyes/<race>`. Face paint is never split, because its files carry
  no race.
- **Racial scaling**: a pack made only of racial-scaling (height and body-size) settings used to land in Gear.
  With its split on (and Body's Part and body type on) it files under `Body/03. Racial Scaling/<race>`. Packs
  that also carry other files (for example physics mods with a racial fix inside) are not treated as racial
  scaling and stay where they were. Both the newer and the older Penumbra mod formats are read.
- Face, Eyes, Skin and tails, ears and horns read their race when the library is scanned. A hint, "Scan again to
  use race split for faces, skin, tails and ears.", shows until you do.
- Race priority (which race wins for a multi-race mod) is separate from **Priority race** under Hair, which only
  picks the hair number.
- The Review tab's text export and the diagnostic dump say why a mod got its race folder (a `StagedRace:` line).
- **If you used the old "Split hair by race" box**, the update turns Split by race on with only Hair's race split
  on, so nothing else moves.

## Weapons by job (opt-in)

- Off by default and off in Recommended; it is the Weapon category's **Job** split in Advanced, and Select all
  splits turns it on. There was never a checkbox for it.
- It does two things, so **scan again after switching it** (a line under the editor reminds you until you do):
  - A scan made with it on files a **weapon-only** mod (its files replace a weapon model and no gear or
    accessory) under Weapon instead of Gear. A weapon that comes with a bracelet or a set stays in Gear.
  - Sort puts each weapon in `Weapon/<job>`, such as `Weapon/SAM`.
- The job comes from the game's own item data for the items the mod changes. A class files with its job (a
  Gladiator sword goes to `PLD`). An Arcanist book (used by both Summoner and Scholar), an item several jobs
  share, or items that disagree go to `Weapon/Multiclass`. A weapon whose items match no job stays directly in
  `Weapon`.
- Turn it off and scan again to put weapons back in Gear.
- The Review tab's text export shows a `WeaponJob:` line for each weapon mod.

## Keyword rules

A rule says "if a mod matches this condition, file it in this folder". Open **Rules** on the Sort tab's
**Advanced** screen and press **Add rule**. A rule is one line: a condition, an arrow and a folder.

    name contains 'hrothgar' and type = 'Body' -> Body/Hrothgar/{creator}

The part before `->` is the condition, the part after it is the folder, and the mod's own name is always added
at the end.

### How rules are applied

- Rules are checked from the top. The first switched-on rule that matches files the mod, so **order is
  priority**. Use Up and Dn to reorder, the tick box to switch a rule off without deleting it, and X to delete it.
- A rule's folder replaces the whole path Sort would have chosen: the type folder, creator folder, race folders,
  numbered folders and body splits do not apply to a mod a rule has filed.
- Mods no rule matches sort as before. Protected mods are never touched.
- Rules apply to every Sort: from Recommended or Advanced, a root-only Sort (to the root mods only) and the
  automatic sort of new mods. They do not apply to a template used as a fallback.
- Up to 100 rules, each up to 300 characters. They are saved when you leave the text box, and text typed in
  Advanced is kept if you switch to Recommended without leaving the box.

### The condition language: SQL-like, but not SQL

It follows the `WHERE` part of SQL, so SQL experience carries over, but you cannot paste a SQL query in. It is
deliberately small.

*Fields (all text):*

| Field | What it holds |
| --- | --- |
| `name` | the mod's name |
| `author` | the creator, as the Sort tab names it |
| `folder` | the folder the mod is in now (empty if it is loose in the root) |
| `type` | the category: Gear, Weapon, Face, Hair, Body, Skin, NPC, Minion, Mount, Pet, Ornament, Furniture, Sound, Animation and VFX, or Review |
| `subtype` | the slot or kind inside a category, for example `Feet` |
| `race` | what `{race}` gives, including `Multi-race` |
| `body` | the body type, for example `Bibo+`, as your current Gear and Body splits give it |

`type` always uses the built-in category names, even if you renamed a category's folder.

*Operators:* `=`, `!=`, `contains`, `starts with`, `ends with`, `in ('a', 'b')`, and `not` before an operator
(`name not contains 'x'`, `type not in ('Gear')`). Combine them with `and`, `or`, `not` and brackets.

*Same as SQL:* text goes in single quotes and `''` is a quote inside the text (`author = 'O''Brien'`); `not` binds
tightest, then `and`, then `or`; keywords and field names are not case sensitive; text comparison ignores case,
so `'hrothgar'` matches `Hrothgar`.

*Different from SQL:* `contains`, `starts with` and `ends with` instead of `LIKE` (no `%` or `_` wildcards); no
NULL (a missing author or race is empty text, so write `race = ''`); no `<>` (write `!=`), `BETWEEN`, `LIKE`,
numbers, functions, `ORDER BY` or subqueries; `contains ''`, `starts with ''` and `ends with ''` are rejected
because they would match every mod (`= ''` and `in ('')` are fine).

*The folder* after `->` may use `{type}`, `{creator}` and `{race}`. A token that comes out empty drops that folder
level, so `{race}/{creator}` on a mod with no race gives just the creator. `{race}` works even with the race
switch off. If the whole folder ends up empty, the mod stays where it is and the Sort summary says so.

### Examples

- `author = 'Ann' -> Ann's mods`: everything by Ann in one folder.
- `type = 'Gear' and subtype in ('Top', 'Legs') -> Gear/Outfits`: tops and legs together.
- `race = 'Viera' and type = 'Hair' -> Hair/Viera/{creator}`: Viera hair by creator.
- `name contains 'recolor' or name contains 'retexture' -> Recolors/{type}`: either word.
- `not type in ('Gear', 'Hair') and name starts with 'a' -> A/{type}`: other kinds of mod starting with "a".

### Live help while you type

- A rule with a mistake shows a red message with its position and, for a typo, a suggestion, for example
  `Unknown field 'nmae'. Did you mean 'name'? (at 0)`.
- A good rule shows, a moment after you stop typing, how many mods it would win ("Wins for 12 mods"). The count
  leaves out protected mods and mods an earlier rule already wins, so the numbers add up, and it updates after a
  rescan.
- An amber warning appears if a rule's folder would no longer match its own `folder` condition (so the next Sort
  could move the mod again), or if the folder is inside a protected folder.
- The Sort summary lists any broken rule it skipped. The Review tab's text export shows which rule placed a mod
  (`StagedRule:`) and which ruled mods could not be placed (`RulesUnplaced:`).
- The same explanation is in the plugin: Help tab, "Sort rules".

### Rules, sort-inside folders and the workbook

- A mod in a "sort inside" folder (yours, or a Heliosphere or Proteus folder in Sort inside mode) stays inside it:
  the rule's folder goes below that folder (`.Chars/Body/Hrothgar/...`). The Proteus managed mod, when placed at the
  root, ignores rules.
- A rule never overwrites a protected mod's place: if a rule's folder lands on a protected mod's path, the usual
  collision handling renames the new arrival.
- Rules also work with Group by set to Creator.
- The workbook's "As suggested by Sort" export includes rule results. Editing, reordering or switching a rule
  greys that export until you press Sort again.

## The reworked workbook (Review Changes tab)

- **Export mode** replaces the old "Group workbook by" box:
  - **As is** writes each mod's current folder.
  - **As suggested by Sort** writes the folders the last Sort proposed, including every Sort setting (splits,
    numbered folders, race folders, rules).
- **As suggested by Sort** is only offered while the proposals still come from one full Sort with your current
  settings. Otherwise it is greyed and says why: "Scan first.", "Press Sort first.", "Sort settings changed,
  press Sort again." or "Staged paths changed since the last sort, press Sort again." (after a manual move, a
  template, a protection change or a workbook import). A root-only Sort does not count as a full Sort.
- The check behind this is a short fingerprint of your settings. It is now a fixed-length hash, so a library
  with many rules and renamed folders can no longer overflow a spreadsheet cell.
- Protected mods always get a blank Destination ("leave this one alone"), and so do top-level mods in As is.
- **Category Mapping sheet**: the sixteen built-in codes (1 to 16) under a red banner (edits to them are ignored
  with a warning), and below them **your own categories**, codes 17 to 9999, up to 200 rows, for example `17`
  and `Face/Sculpts`. Type `17/Thor` in a Destination and it becomes `Face/Sculpts/Thor`. After an import with a
  clean mapping table the plugin remembers your rows and writes them into the next export.
- **Tokens** in a Destination or a mapping folder: `{type}` (the category folder), `{creator}` and `{race}`. A
  token with nothing to fill in drops its folder level; text around a token stays. An unknown token is an error.
  `{race}` fills in even with the race switch off.
- **Untouched cells come back exactly as exported**, so folders named like categories, numbers or tokens survive
  a round trip, and an untouched export imports with no moves.
- **The workbook follows renamed folders**: the built-in rows, codes 1 to 16 and `{type}` use your configured
  folders. If you rename a folder after exporting, the import says "code N now files under X (exported as Y)"
  instead of calling the code edited.
- In the new format the built-in folders match the Sort tab: code 13 is `Animation and VFX/VFX`, 15 is
  `Animation and VFX` and 16 is `Review`.
- **Import report**: separate counts for moves ready for review, unchanged rows, rows skipped as stale and rows
  blocked by errors, plus how many mods used each custom category. Errors name the sheet and cell (for example
  a code that is not in the mapping sheet, a code used twice, a `..` in a folder, a formula in a mapping cell),
  and only the mods affected are held back. A mod that is protected by its folder is reported as not staged, and
  its protection is not changed.
- Files that are not workbooks, workbooks from a different Penumbra library, and sheets with stray cells far
  below the data are refused with a clear message instead of being slow.
- Older (version 2) workbooks still import under their old rules (no tokens).
- **The standalone app cannot read the new workbook format.**

## Heliosphere and Proteus

- **New-mod notice.** Heliosphere puts a newly installed mod at the top level of the library. In Protected mode
  (the default) there is nothing to sort, so before this release nothing appeared at all. Now a popup, "New
  tool-managed mods", says for example: "1 new Heliosphere mod added. Heliosphere mods are Protected, so it
  stays where it is. You can change this on the Protect tab." It has **Open Protect tab**, **OK** and **Don't
  show this popup**, and no Sort now, since nothing would move. Proteus mods get the same notice.
- When the same batch also holds ordinary new mods, the usual "Sort it?" popup gets one extra line about the
  protected tool mods, with its own **Open Protect tab** button, instead of a second popup.
- In **Sort inside their folder** or **Treat as normal** mode, new tool mods are offered like any other, and the
  Confirm sort window tags them `(Heliosphere)` or `(Proteus)`.
- Turning the popup off, or choosing Do nothing in Auto sort, also silences the notice.
- **Status line on the Sort tab**, in the bar at the top on both screens: for example "22 Heliosphere mods:
  Protected (not sorted). 13 Proteus mods: Protected (not sorted). Change this on the Protect tab.", with an
  **Open Protect tab** button. Yellow when a tool is Protected and has mods, grey otherwise, hidden when you have
  no tool mods and both modes are Protected. It wraps in a narrow window and the button stays visible.
- No default changed: everything still follows the Protect tab's dropdowns.

## Fixes

- The Rules header stays open when you add a rule.
- The Sort tab no longer jumps back up, or jitters, when you scroll past a card (most visible when scrolling down
  into Manual assignment under a tall Advanced card).
- Labels come before their boxes everywhere on the Sort tab (Group by, What to sort, Priority race, First-choice
  body, Add a race, When new mods are installed, Search mods, Destination folder).
- Cards and the Manual assignment list stop a small gap short of the scrollbar instead of running under it; no
  horizontal scrollbar at any width. The Assign button fits on its line at the minimum window width and catches
  the first click.
- The **?** button sits at the right end of the bar, and a long scope line wraps before it.
- Split depth numbers line up with their labels, and the gap between cards is the same on both screens.
- **Show rules** on Recommended now opens the Rules card and scrolls to it, instead of only switching screen.
- New Heliosphere mods no longer pass silently: the notice above explains why they stay where they are.
- Mods in the older Penumbra format whose `meta.json` holds only the name and author now have their options
  read, so their gear slots, hair numbers and racial scaling are found. Some such mods may get a better folder
  after a rescan.

## Help and guide

- New Help tab topics: Recommended or Advanced, Categories, Folder, Creator folder, Splits, Example, Select all
  splits, Reset to Recommended, Split by race, Race splits, Send multi-race mods to Multi-race, Race priority,
  Sort rules (the full rule language) and Weapon: Job. The split topics are renamed to match (for example "Gear:
  Equipment slot"), and "Group workbook by" became "Export mode".
- The first-run walkthrough's Sort step now says to keep Recommended or switch to Advanced.
- The user guide covers the new Sort tab, race folders, weapons by job, rules, the workbook and the Heliosphere
  and Proteus notices, and "How sorting works" covers the race split. **The guide PDF behind the Help tab's button
  was rebuilt for this release.**

## What moves

- Nothing moves until you press Sort, review the plan on Review Changes and press Apply. History can restore the
  layout from before any Apply.
- Updating keeps your layout (see **Upgrading**), so a library you already sorted should see no new moves from
  the Sort tab rework itself.
- Things that do move, only when you turn them on and Sort: race folders, weapons by job, a renamed category
  folder, a changed creator folder, a rule.
- A fresh install, a deleted config or a config that fails to load starts on Recommended, and the first Sort can
  propose moving most of an existing library (gear into numbered folders, for example).

## Upgrading

**What changes for existing users**

- Your old ticks become the same layout as before: each category whose ticks differ from the new Recommended is
  saved as your own setting. Someone on the old defaults will find Advanced open, with Gear, Hair, Body and
  Animation marked **Custom**. That is expected: it is how your layout is kept. Do not press Reset all to
  Recommended unless you want the new layout, because that would move a lot of mods on the next Sort.
- The old "Split hair by race" box becomes Split by race with only Hair's race split on.
- Your rules, race priority, hair choices, body priority and Auto sort choices are remembered as before.

**What the first Sort will do**

- With your settings unchanged, it should propose nothing new for a library you already sorted with those
  settings. A few older-format mods may get a better folder (see **Fixes**).
- On a fresh install or a wiped config it uses Recommended and may propose moving most mods. Look at the count on
  Review Changes before you press Apply.

**What to check**

- Keep a copy of `pluginConfigs/PenumbraOrganizer.Plugin.json` before updating, just in case.
- Press Refresh mod list (Scan tab) once after updating, so faces, skin, tails and weapons are read the new way.
- Press Sort and read the number of moves on Review Changes before Apply.
- If you use Heliosphere, look at the new status line on the Sort tab and decide on the Protect tab whether its
  mods should stay Protected.
- If you share workbooks with the standalone app: it cannot read workbooks exported by this version.

**Going back to an older version**

- Your split ticks are also written in the old form, so 0.6.6.0 still shows the same ticks.
- Anything that older version does not know about can be lost once it saves its settings: renamed folders,
  creator folders, weapons by job (weapons go back to Gear after its next scan), race splits other than hair, race
  priority, rules and saved workbook categories.

## Known issues

- **Heliosphere mods are protected by default** (Protect tab), so a newly installed one is not offered for sorting.
  This is by design; the new notice and status line now say so and link to the Protect tab.
- **Weapon job lookup and your client language**: the job is found by matching item names against the game's item
  data in your client's language. This has not been checked on every client language. If every weapon shows
  `WeaponJob: (none)` in the export, please report it on Discord.
- The first scan with weapons by job on builds an item index and may be a little slower.
- Crafter and gatherer tools are not handled specially; where they end up has not been checked.
- A card can open one frame short and grow on the next, so you may see a brief flicker when opening Rules or adding
  a rule.
- The help tooltip of a segmented control (such as Recommended | Advanced) only shows over its last segment.
- Reset all to Recommended acts at once, without a confirmation.
- Race words for Highlander, Roegadyn, Lalafell, Hrothgar and Viera have not yet been seen in a real single-race
  face, skin or tail mod, and it is not confirmed which word horns use. Report any race mod that lands in the wrong
  folder.
- How long and complete our testing was: the race split, the workbook, rules and the per-category layouts were
  checked with automated tests and a read-only pass over two real libraries (287 and 2,557 mods). The rebuilt Sort
  tab and these features have only been tried briefly in game, and the workbook has not been through a full
  Excel round trip with every edit case. Weapons by job and the Heliosphere and Proteus notice are the newest parts
  and have had the least in-game use. Please report anything odd.
- The Proteus controls have only been exercised against test mods, not a real Proteus library.

## Not in this release

- Splits cannot be reordered yet; each category has one fixed depth order. Grouped accessories are planned.
- Weapons are filed by job only where the game's item data allows it; anything else stays in `Weapon` or
  `Multiclass`.
- There is no per-mod tooltip for the winning rule; it is only in the text export.
- Templates do not follow renamed folders or rules: a template used as a fallback keeps the default folders and
  ignores rules. The Templates tab's own export checkboxes are unchanged.
- The first-run walkthrough's Sort step mentions Recommended and Advanced in one line, but it does not walk through
  the new screens, rules, race folders or weapons by job.
- The category list cannot stack above the editor at the window's minimum width.

## Under the hood

- A safety net for the update: before the layout rework, every old combination of Sort tab ticks was recorded,
  and a test checks that each one still files every mod in exactly the same place.
- An opt-in, read-only test pass over real mod libraries, used to check the race split, the workbook round trip,
  rules and layouts on 287 and 2,557 real mods.
- The plugin now owns its workbook code instead of sharing it with the standalone app.
- New shared widgets for the Sort tab (cards, switches, badges, segmented controls, icon buttons, path previews).
- Design documents, plans, in-game checklists, a weapon feasibility note, and the 0.7.0.0 and 0.7.1.0 testing
  builds and their channel settings.
