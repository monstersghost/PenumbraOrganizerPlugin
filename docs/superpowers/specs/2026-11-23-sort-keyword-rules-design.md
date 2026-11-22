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
