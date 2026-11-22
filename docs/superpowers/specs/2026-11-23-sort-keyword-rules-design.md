# Sort keyword rules (SQL-like conditions): design (2026-11-23)

Status: design approved in conversation, spec awaiting review. No release, no version change.

## 1. Goal and scope

Let the user file mods by a typed rule such as

```
name contains 'hrothgar' and type = 'Body' -> Body/Hrothgar/{creator}
```

Rules live on the Sort tab as one ordered list. The Review tab workbook picks them up through "As suggested by Sort", so there is no workbook change.

In scope: the rule language, the rule list on the Sort tab, Sort wiring, fingerprint, config, help.
Out of scope: regex, functions, rules on the workbook sheets, import/export of rule lists, per-rule priority numbers (order is the priority), rules for protected folders.

## 2. Behaviour

- A rule is one line: `condition -> folder`. Max 100 rules, 300 characters per line.
- Per unprotected mod, the first enabled, valid rule whose condition matches wins. The folder replaces the whole destination path; the leaf is the mod name. Tokens `{type}`, `{creator}`, `{race}` work in the folder; an empty token collapses its segment; an unknown token is an error.
- No match: the mod sorts normally. Protected mods are never touched by rules.
- A matched mod skips the normal layout tails (race and numbered folders). Collision handling and Heliosphere/Proteus placement still run.
- The explanation line for a ruled mod names the winning rule.
- A rule that fails to parse is skipped at sort time and listed in the Sort summary. A rule whose folder expands invalid for one mod skips only that mod.

### Language

Fields: `name`, `author`, `folder` (current folder), `type` (category folder name), `subtype`, `race` (exactly what `{race}` yields, including `Multi-race`), `body`. An empty field is the empty string (`race = ''`).

Operators: `=`, `!=`, `contains`, `starts with`, `ends with`, `in ('a','b')`. Combine with `and`, `or`, `not` and brackets. Precedence: `not`, then `and`, then `or`. Case-insensitive. Strings are single-quoted, `''` is a literal quote. No regex, no functions.

## 3. Units

| Unit | Job |
|---|---|
| `RuleParser` | Text to a condition tree, or an error with character position and a "did you mean" for field names (edit distance 2 or less). Pure. |
| `RuleFields` | Reads a mod row into the seven fields. Pure. |
| `RuleSet` | Ordered compiled rules with value equality (like `RacePriority`) so it sits in `LayoutOptions` and the fingerprint. `Match(row)` returns the first matching rule or nothing. |
| Folder expansion | One new resolver entry point reusing the workbook resolver's token, collapse and structure checks, reading the first segment literally (a rule's `17/x` is not a category code). |
| Sort wiring | `SortFolderSelectors` checks the rule set first. `OrganizerState` records the winning rule for the explanation (like `StagedRace`) and clears it on any other staging. |
| Config and UI | `Configuration.SortRules` (ordered list of text and enabled). Collapsed "Rules" section in `SortPanel`: per-rule checkbox, text field, up/down/delete, Add rule; live red error (position, did-you-mean) or grey "Matches N mods". The panel keeps a compiled copy keyed on text, order and scan generation; it never reads config per frame. Short help topic. |

Rules are part of the Sort selection and its fingerprint: edit, reorder or toggle greys the staged workbook export.

## 4. Order of work

Parser, fields and evaluator, rule set and folder expansion, Sort wiring with fingerprint, config, panel and help. Each task tested before the next.

## 5. Tests

- Parser: valid and invalid tables with exact error positions; quotes and `''`; precedence; brackets; unknown field; unterminated string; the 100-rule and 300-character limits; did-you-mean.
- Evaluator: case-insensitivity, empty fields, `in (...)`, `!=` on empty, mods with no author or race.
- Rule set: first match wins, disabled and errored rules skipped, reorder changes the result.
- Folder expansion: tokens, collapse, invalid result skips one mod, literal first segment.
- Sort integration: rule beats strategy and adds no race or numbered tail; protected untouched; no match sorts as before; explanation names the rule; skipped-rule summary line. Empty rule list is byte-identical to today's output.
- Fingerprint and gate: any rule edit, reorder or toggle greys the staged export. Config round trip.
- UI state: add, remove, move, toggle and live-check text tested without ImGui.
- Harness: a rules pass over the real libraries (287 and 2,557 mods): match counts for example rules, no exceptions, timing at 2,557.
- New in-game checklist: bad rule, live count, Sort, explanation line, re-export.

## 6. Risks

- No config reads on the render thread per frame; the compiled copy is keyed on text.
- Match count keyed on scan generation so a rescan cannot show stale counts.
- Rule folders can collide with real folders; collision handling still runs.

## 7. Review amendments (Opus and GPT-6.2 Astra, 2026-11-23)

Both reviews were read against the code. These rulings override any earlier line they contradict.

### Matching and staging
- The first matching condition consumes the mod. If its folder then expands invalid for that mod, the mod stays at its current path, no later rule is tried, and the Sort summary counts it with the rule number and reason.
- Rules are matched once per row at the top of `OrganizerState.Sort`, before `StagedBodyResolution`/`StagedRace` are set and before the numbered Gear and Body layouts are built. Ruled rows get no staged race or body, are left out of `BuildGearLayout`/`BuildBodyLayout` input (no gaps in `Gear/N. Body`), and the rule folder goes to `Select` as `(folder, null)`.
- New `StagedRule` on the row (ordinal and rule text captured at staging time, so a later reorder cannot change an old explanation). It is cleared in every place `StagedRace` is: `AssignManual`, the scope reset in `Sort`, `ApplyTemplate`, `StageMoveUnder`, scan publish and `LoadScan`.
- Surfaces: a diagnostics export line next to `StagedRace`, a Review row tooltip, and a new `SortSummary` field (skipped rules, invalid-expansion mods) with `SortSummaryText`. There is no per-mod explanation line today; this spec adds those surfaces.
- Rules apply to every Sort run: whole library, chosen set, root-only and the new-mod pipeline (`PluginNewModHost.SortScoped`). `TemplatePlanner` is unchanged: rules do not apply to template fallback, and `renameFolder` never touches a rule folder.
- Placement after a rule: a sort-inside anchor (tool or user folder) is prefixed onto the rule folder (`Anchor/Body/Hrothgar/...`); `ProteusManagedAtRoot` ignores rules. Collisions: ruled rows reserve the current paths of protected rows, so a rule landing on a protected mod's path is renamed by normal collision handling rather than only blocked at `Validate`. A rule folder under a `FolderProtectionRules` entry gets a warning in the rule row, since the mod becomes protected after the next scan.

### Fields
- `name`: the mod name after `FixName`, trimmed. `author`: canonicalized with the same canonicalizer `{creator}` uses. `folder`: the pre-sort parent path, normalized, `/` separators, empty at root (never the staged path, never including the leaf). Because `folder starts with 'Gear'` also matches `Gear2/...` and a rule can move a mod out of its own condition, the panel warns when a rule's literal folder would not match its own `folder` condition, and a test sorts twice and expects the second sort to evaluate the new locations without flip-flopping.
- `type`: the Sort category folder name (VFX and Animation are both `Animation and VFX`, unclassified is `Review`); the help topic lists the valid values. `subtype`: the raw `SubCategory`. `body`: the priority-resolved body while a body split is on, else the default `BodyType`. `race`: as `{race}`.

### Language
- Parentheses group. The tokenizer, not `string.Split`, finds `->` outside strings. Input must be consumed completely; empty condition, empty folder and missing `->` are errors. Keywords and field names are case-insensitive. Matching is ordinal, case-insensitive (`OrdinalIgnoreCase`, so no Turkish-i surprises).
- `not` applies to any comparison, and `not in`, `not contains`, `not starts with`, `not ends with` are accepted. `!=` allows spaces around it.
- Empty strings are allowed for `=`, `!=` and `in`; `contains`, `starts with` and `ends with` reject an empty operand (it would match everything). `in` needs at least one quoted item, rejects a trailing comma, allows duplicates and a single item.
- Double quotes and curly quotes (pasted from Discord) get a targeted error. Error positions are zero-based UTF-16 offsets. Did-you-mean ties break in field-declaration order, and operators get suggestions too (`contians`).

### Folder expansion
- A segment that is a whole token and expands empty is dropped; a mixed segment (`By-{creator}`) substitutes the empty string, and a segment left empty is dropped. An expansion that is empty overall is an error. Token values are single path components (a `/` in a creator name is replaced by the shared resolver's component policy). Each segment is trimmed, because Penumbra trims names. The workbook `NameFolder` lookup and category codes are both disabled for rule folders; a literal `{` that is not a token is an error; `{Creator}` is case-insensitive as in the resolver.

### Fingerprint, caching, config
- `SortSelection.Fingerprint()` builds a fixed string, so the rules are added by hand: a length-prefixed digest of every rule's `(enabled, text)` in order (so a `|` in a rule cannot collide, and edits to disabled or invalid rules still grey the export), appended only when the list is non-empty so an empty list keeps today's exact string. When any rule reads `race` or `{race}`, the full `RaceSplitOptions` value is fingerprinted even when the master switch is off.
- One shared compiled-rule cache, keyed on the ordered `(enabled, text)` list, serves both the panel and `SortSelectionFactory.FromConfig`; `FromConfig` never parses per call. `Plugin.SuggestedWorkbookUnavailableReason` runs every Review-tab frame and must do no parsing.
- The live "Wins for N mods" count (not "Matches"): eligible means unprotected and not claimed by an earlier rule. It is debounced after typing, recomputed when text, order, enabled flags, scan generation, race options, body priority or the creator canonicalizer change, and a result is published only if its inputs are still current. At 2,557 mods and 100 rules (about 255,000 checks) it is not run on every keystroke.
- Config: persisted when a text field is deactivated, not per keystroke or per frame. The 100-rule and 300-unit limits are enforced in the UI and on config load (a hand-edited excess entry is kept and flagged but never executed); 300 is counted in UTF-16 units over the stored line, with a byte-safe `InputText` buffer. A missing legacy `SortRules` property loads as an empty list.

### Added tests
Scoped sort, root-only and new-mod pipeline with rules; numbered layouts omit ruled rows; fingerprint changes with race settings while the master is off; a `|` in a rule; the empty-list fingerprint equals today's string; a rule wins under the CreatorOnly strategy; sort-inside anchor and Proteus-at-root with a rule; protected-path collision; sort twice is stable; `StagedRule` cleared at each site listed above; no parsing per Review-tab frame; 100/101 rules and 300/301 units; an error position after an emoji; invalid expansion consumes the mod while a valid later rule also matches; a stale count finishing after a newer one is discarded; `name = 'a->b' -> X`, `author = 'O''Brien'`, `name = ''`, repeated `not`, and `A or B and not C` with and without parentheses; non-ASCII test names built from code points.
