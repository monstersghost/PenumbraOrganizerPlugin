# Workbook rework: export follows Sort, custom category codes, tokens (design)

Status: draft for the author's review, 2026-11-22, revised after one external review (see Review log).
Nothing here is built. Second of two specs; it depends on `2026-11-22-race-split-all-categories-design.md`
for the `{race}` token.

## Problem

A user (a QA tester, in the community Discord) added a row `17 | Test` to the workbook's Category Mapping
sheet, assigned a mod to `17/...`, and import refused it: "Category code 17 is not defined in the workbook
mapping sheet." The message is misleading, because the sheet does define 17. The truth is that the mapping
sheet is only a printed cheat sheet; the sixteen codes are hardcoded and the import never reads the sheet.

The same investigation found the workbook is badly out of date with the Sort tab (author's words: "kinda too
outdated now"):

- The export does not use the Sort tab at all. `WorkbookAdapter.ToProposals` sets every proposed folder to
  the current folder; the destinations come from the standalone app's own `BuildSuggestedDestination`, which
  can only write `<code>`, `<creator>`, `<code>/<creator>` or `<creator>/<code>`. None of the Sort tab's
  splits, numbered gear and body folders, emote split, hair split or race split reach the workbook.
- The "Group workbook by" choice on Review Changes re-implements a cruder Sort tab.
- The mod-type column is validated but never used: `ApplyImportResult` applies only the protected flag and
  the destination. A bad value in a column that does nothing can still block a row.

## Goal

Make the workbook the way to hand-edit the Sort tab's result in Excel, and let users define their own
categories, without writing full paths for every mod.

## Decisions already made (author, 2026-11-22)

- The "Group workbook by" dropdown is removed. Export has two modes: **As is** (each mod's current folder)
  and **As suggested by Sort** (what the Sort tab currently stages).
- Mapping sheet codes 1-16 stay built in and **locked**: a bright warning says not to edit them; an edit is
  accepted but has no effect, with a warning at import.
- Users add their own rows from **17** up. The mapping sheet is read at import, and the sheet wins.
- Hybrid storage: the sheet is law at import; the plugin config only remembers the custom rows to seed the
  next export.
- Destination cells accept three tokens, `{type}`, `{creator}`, `{race}`, anywhere in the path, for example
  `Mine/{type}/{creator}/test`. A token with no value **collapses**: its folder level disappears, so
  `17/{creator}/test` becomes `<folder of 17>/test` for a mod with no author.
- Keyword rules are out of scope (later, separately).

## 1. Approach: the plugin owns the workbook code

Today the plugin compiles the standalone app's `WorkbookWorkflowService.cs` straight from that repo (a
file link). The author confirmed the standalone app is obsolete and that a plugin-owned copy is acceptable.
This spec takes it: bring the service and the category catalog into the plugin, drop the link, keep the
workbook format readable.

Why not leave the link and wrap it: the shared service validates destinations itself, so a custom code would
be rejected before the plugin could expand it; and it writes the destination column itself, so Sort-tab paths
would have to be written by opening and rewriting the file afterwards. Both mean editing the xlsx around a
black box. Fallback if the author prefers to keep the link: a plugin-side step that rewrites a temporary copy
of the file before the shared service reads it, and overwrites its destination column on export. Same
behavior, more moving parts.

The copy is not taken as one block. It is split into three units with one job each:

- **Workbook file layer:** reads and writes the xlsx (sheets, columns, protection, metadata, package checks).
- **Destination resolver:** pure functions: code lookup, tokens, collapse, validation. No file access.
- **Import and export orchestration:** ties the two to the plugin's state.

There is one category catalog (the built-in table), shared with the mapping sheet writer and the resolver.
Before the link is removed, every build-time and runtime dependency on it is listed and checked, and the
existing interoperability tests move across first, passing, before any new behavior is switched on.

## 2. Export

Two modes replace "Group workbook by".

- **As is:** destination is each mod's current folder (the existing "keep current folders" behavior).
- **As suggested by Sort:** destination is the folder the Sort tab has staged for the mod, taken from the
  staged proposals in the plugin's own state, so every Sort setting (splits, numbered folders, race, emotes)
  is reflected exactly, root-only moves included. The mode is available only while the staged paths really came
  from the last sort. The plugin has no such notion today (the sort panel's "last sorted" is local to the
  panel, and manual assignment, template apply, "move under" and a workbook import all overwrite staged paths
  without invalidating anything), so this is new work: the plugin state keeps a sort fingerprint (the Sort
  settings plus the scan) that any staging not made by Sort, and any new scan, clears. While the fingerprint is
  missing the mode is disabled with the reason ("Press Sort first", or "Staged paths changed since the last
  sort, press Sort again"). The workbook metadata records the mode, the scan identity and the fingerprint.
  Export never silently recalculates and never labels manual or template paths as Sort output.
- Blank destinations: a protected mod is always blank ("leave alone"). In As is, a mod at the top level of the
  library is blank too, as today. In As suggested by Sort, the staged path is exported for every unprotected
  mod, top-level ones included, so a root-only sort's moves are not lost. A user may type a destination on any
  unprotected row; a protected mod given a different destination stays an import error, as today.
- The destination column holds a folder path. A sorted mod's leaf can differ from its name (a duplicate
  install is staged as `Name (2)`), and import re-appends the row's name, so duplicates would collide again.
  Version 3 therefore adds a hidden `_leaf` column holding the staged leaf; import uses it when present and
  the mod's name when not (version 2 files, as-is mode).

The workbook keeps its three sheets (Edit Destinations, Category Mapping, hidden metadata) and its guards
(sheet protection on the data columns, no macros or external links, installation and scan identity, stale
rows skipped when the current folder changed). The format version moves from 2 to 3 for the new mapping
sheet. Import accepts 2 and 3. A version 2 file is read under version 2 rules (below), not as a version 3
file with an empty custom table.

File writing keeps the current safe pattern: write a temporary file next to the target, then replace the
target, so a failed export leaves the previous workbook intact. A failure to replace is reported neutrally
("The workbook could not be replaced; another program may be using it"), because a sync client or antivirus
can hold a file just as Excel can.

## 3. The mapping sheet

Regenerated on every export; columns code, name, description, folder, example.

- **Rows 1-16:** the built-in categories, as today, under a bright red banner row: "Rows 1-16 are built in.
  Do not edit them: edits are ignored." Their folder column is the folder the **Sort tab** files that category
  under, read from the same table the sorter uses (one source, no second catalog). The two differ today: the
  workbook calls codes 13, 15 and 16 `VFX`, `Animation` and `Others`, while Sort files them under
  `Animation and VFX/...` and `Review`. Version 3 follows Sort, so a sheet that mixes codes and `{type}` builds
  one tree and not two; version 2 files keep the version 2 meaning of their codes. The author approved
  this change to codes 13, 15 and 16 (see "Resolved by the author").
- **Rows 17 and up:** the user's own. Only **code** and **folder** route anything; name, description and
  example are free text for the user's own notes and are not saved. A row is a whole-number code (17 to 9999)
  and a folder path, e.g. `17 | Face/Sculpts`. Blank rows are ignored. At most 200 custom rows, and a folder
  path of at most 260 characters, so an export never produces a truncated or invalid sheet.
- Export seeds these rows from the config (section 5), so a re-export does not wipe them.

Import first parses the whole mapping sheet into a table, before it reads any mod row. A formula in any cell
the import reads is an error, as on the data sheet.

- Rows 1-16 compared with the built-in table: any difference is a warning that shows the original and the
  edited value ("Row 5 'Body' was edited to 'Bodies'; the built-in value was used"), and the built-in wins.
- A custom code is usable only when exactly one valid row defines it. A duplicated code is an error on every
  row that defines it, so row order never decides a winner, and the code is unusable. A code with a blank or
  invalid folder is an error on that cell. Errors name the cell: `Category Mapping!A19: code 17 is used
  twice`, `Category Mapping!D19: folder cannot contain ".."`. A problem with a custom row blocks only the
  mods whose destination uses that code, not the whole file.
- Codes are read tolerantly but strictly: the text `17`, `0017`, the number 17 and 17.0 are the same code;
  17.5, a negative number, a value over 9999 or below 17, and a boolean are errors. Surrounding spaces are
  ignored. Excel stores numbers differently depending on how they were typed.
- A custom folder may equal a built-in category name or sit under one; it is just a folder.

## 4. Destinations and tokens

A destination cell is a relative folder path. How its first segment is read:

1. A positive whole number: a **code reference**. (Zero and negative numbers stay literal folder names, as
   today, so a folder like `-16` still works.) Look it up (built-ins, then the validated custom rows) and replace
   the segment with that row's folder path. A code that exists nowhere is an error naming the cell, in plain
   words ("code 17 is not in the Category Mapping sheet"), replacing today's misleading message. Only the
   first segment is ever read as a code; a number anywhere else is an ordinary folder name.
2. A built-in category name (case-insensitive): that category's folder, as today.
3. Anything else, like `Mine`: a literal folder, kept as written.

Then, for version 3 files:

4. Expand tokens one segment at a time (token values never expand again). A segment is removed only when its
   whole expanded text is empty. A partly empty segment keeps its literal text: with no author,
   `By-{creator}` becomes `By-`. A token value never contains a path separator.
5. If every segment is removed the destination is blank and the mod is left alone.
6. Validate the final path with the existing destination rules (relative, no empty segment, no `.` or `..`,
   no drive letters or control characters) and the same length caps. These are Penumbra virtual folder
   paths applied through Penumbra, not file-system moves, so nothing here is checked against the disk; name
   collisions at the destination are handled by the existing Apply, unchanged.

Version 2 files keep their established rules exactly (steps 1-3 and 6 only; braces are ordinary text), tested
against real version 2 workbooks as fixtures.

Tokens (case-insensitive; an unknown `{name}` is an error naming the cell):

- `{creator}`: the canonical creator folder name (`CreatorCanonicalizer`), as the Sort tab uses it. Empty for
  a mod with no author.
- `{type}`: the mod's category folder as the Sort tab names it (`Gear`, `Hair`, `Face`, `Animation and VFX`,
  `Review` for an unclassified mod, ...), and only the category, never a sub-folder or numbered tail. It is the mod's own category, not the category chosen by a
  code in the same cell: `17/{type}` is "folder 17, then the mod's category".
- `{race}`: the race folder for the mod from the race-split spec's folder rule, called as one shared function
  (the workbook has no race detector of its own): the race, `Multi-race` for several races under the toggle,
  the priority pick, or `Multi-race` for no or unreadable evidence. It is empty, and so collapses, only for a
  mod that is not race-applicable under the race spec's definition (Gear, Weapon, a Body replacer, and so on);
  that definition belongs to the mod and does not depend on which rows are ticked. It works whether or not the
  race split is switched on, because the user wrote it on purpose.

No segment is removed for being a repeat: `Gear/Gear` is the user's path and stays.

Example: `Mine/{type}/{creator}/test` for a Hair mod by Thor becomes `Mine/Hair/Thor/test`; with no author,
`Mine/Hair/test`. Code 1 already means `Gear`, so `1/{type}` repeats the word; `{type}` earns its place after
a custom root.

Import then appends the mod's name to the folder and stages it as a manual assignment, exactly as now. The
Review tab shows every resulting path before Apply, and nothing moves until Apply.

## 5. Storage and the import report

- Config: `WorkbookCustomCodes`, a list of (code, folder) pairs, additive, no legacy mirror needed.
- When an import is accepted (the workbook is for this library and its mapping sheet has no errors), the
  sheet's valid custom rows replace the saved list, and the result line says what changed ("Saved 3 custom
  categories: 1 added, 1 changed, 1 removed"). A blocked import changes nothing, and neither does an import
  whose mapping sheet has any error (the saved list is replaced only by a clean table). Mod rows that fail or
  are skipped do not stop a clean mapping from being saved. Meanwhile an error in a custom row still blocks
  only the mod rows that use that code, so the rest of the file can be reviewed.
- The sheet always wins: the saved list is only a starting point for the next export, never consulted at
  import.
- A workbook belongs to one library: the existing installation-identity check still blocks a workbook from a
  different Penumbra library, so custom rows are not shared between people this way. (An earlier draft said a
  friend's workbook would work; it does not, and is out of scope here.)
- The import report shows separate counts: moves ready for review, rows unchanged or blank, rows skipped as
  stale, rows blocked by errors, warnings, and the custom categories saved; plus, for each custom code in
  use, what it expands to and how many mods use it ("17 -> Face/Sculpts: 43 mods"), so an edit to a mapping
  row is visible as the bulk change it is. The overall line never says "success" when any row was blocked.

## 6. Import limits and quirks

- Beyond the existing package checks (no macros, ActiveX, external links or connections), import refuses a
  file larger than a fixed size cap or with more rows than the library has mods plus a small margin, and any
  parse failure becomes the plain message "This file could not be read as a Penumbra Organizer workbook",
  never a stack trace. The sheet protection is an editing convenience, not a security boundary, and the
  import trusts nothing in the file: metadata, codes and destinations are all validated.
- A workbook open in another program is locked: see the neutral message in section 2.
- The mod-type column stays (format compatibility) but is documented as having no effect. An unrecognized
  value there becomes a warning, not an error, so it cannot block a row. The protected column keeps working.

## 7. Out of scope

Keyword rules, sharing custom categories between people, a `{subcategory}` token (a resolved sub-folder such
as `Eyes` would be its own token if users ask; `{type}` never changes meaning), merging the old four group
choices into the new two (they simply go away), changing the sheet protection scheme, the standalone app, and
any release or announcement. Templates and the community template export are unchanged.

## 8. Testing and docs

- Built-ins locked: editing rows 1-16 warns with both values and uses the built-in value. Codes 17+ resolve;
  unknown codes fail with the new message and cell reference; a duplicated code fails on both rows and is
  unusable; blank or malformed custom rows error on their own cells and block only their users.
- Code parsing: text, number, float, leading zeros, spaces; fractional, negative, out-of-range and boolean
  rejected.
- Grammar: a numeric first segment is a code; a built-in name resolves; `Mine` is literal; a number in a later
  segment is literal.
- Tokens: each token, mixed with literals, `By-{creator}` with no author, case-insensitive, unknown token, no
  race evidence, a mod that is not race-applicable, a token in the mapping folder and in the destination, no
  re-expansion, all-collapsed destination, version 2 file with braces left literal.
- Export modes: as-is and as-suggested produce the staged paths, including numbered gear folders, hair and
  race splits and the emote split; as-suggested is disabled with no staged sort and after a settings or scan
  change; protected and top-level mods stay blank; the metadata records mode, scan identity and fingerprint.
- Seed: an accepted import saves custom rows and reports the diff; deleting a row removes it; a blocked import
  or one with mapping errors saves nothing; a different-library workbook is blocked; a re-export round-trips
  the rows.
- Compatibility: real version 2 workbooks still import under version 2 rules; the existing interop tests pass
  in their new home before behavior changes; stale-row skipping, identity checks and the formula ban are
  unchanged; oversized and corrupt files give the plain message.
- Sort staging: a manual assignment, a template apply, "move under" or a workbook import clears the sort
  fingerprint and disables As suggested by Sort; a new scan clears it too; duplicate installs round-trip
  through `_leaf` without colliding; root-only sorts export their staged moves.
- Docs: the user guide's Workbook section (it currently says top-level mods get a blank destination in the
  suggested export, which is wrong for the new mode, and promises interop with the standalone app, which
  cannot read version 3), the help text for the three workbook buttons, the in-game checklist, and a short
  worked example of adding code 17.
- Not verified until run: behavior against a real library and a real Excel round trip, and whether staged
  proposals carry every Sort transformation (checked by comparing staged paths with Sort output).

## Review log

One external review (ChatGPT, 2026-11-22). Accepted: the destination grammar (code, built-in name, literal);
segment-level token expansion; the shared race function and `{race}` rules aligned with the race spec;
staleness checks and a settings fingerprint on suggested export; whole-table parsing, duplicate codes and
strict code parsing; showing what each custom code expands to; version 2 rules kept separate and tested with
real fixtures; neutral file-replacement messaging; input size limits and plain parse errors; a Review-style
accounting of counts; the three-unit split of the copied code. Corrected by the review's prompting: the claim
that a friend's workbook would import (the identity check blocks it). Declined: file-system confinement,
junction, reserved-device-name and overwrite rules, because destinations are Penumbra virtual folder paths
applied through Penumbra (collisions are the existing Apply's job); a `{subcategory}` token (deferred); and
making import itself transactional with Apply (the saved list changes only on an accepted import and is
described in the result line).

Opus, checked against the code. Accepted and changed: top-level mods are not blank today outside keep-current,
so Sort mode exports what is staged; there is no staleness notion to reuse, so the state-level sort
fingerprint is new work, and manual or template staging must not be exported as Sort output; the `_leaf`
column for collision suffixes; built-in code folders follow Sort (codes 13, 15, 16 differ today); zero and
negative first segments stay literal; the mapping-error and seed rules reconciled; the `{race}` definition
tied to the mod's applicability; the docs that now say the wrong thing.

## Open questions

None open.

## Resolved by the author (2026-11-22)

1. **Own the code.** The plugin-owned copy (section 1), on the conditions the review set: one catalog and the
   version 2 tests moved across first. The wrapper stays only as a fallback if that proves wrong.
2. **Codes 13, 15 and 16 follow the Sort tab.** Version 3 files built-in codes under `Animation and VFX/...`
   and `Review`, where the old sheet said `VFX`, `Animation` and `Others`. Version 2 files keep their old
   meaning.
