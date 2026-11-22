# Sort keyword rules: manual checklist (2026-11-23)

Spec: docs/superpowers/specs/2026-11-23-sort-keyword-rules-design.md
Plan: docs/superpowers/plans/2026-11-23-sort-keyword-rules.md

Help topic id: `sort.rules` (the Rules header on the Sort tab). The Review tab has no per-row staged-rule tooltip;
the staged rule shows in the diagnostics export under `StagedRule:` and unplaced rule mods under `RulesUnplaced:`.

## Editing

- [ ] Type a bad rule (for example `name contains -> X`): a red message and the character position appear.
- [ ] Fix it: after a short pause the line reads "Wins for N mods".
- [ ] The win count updates after a rescan.
- [ ] Toggle a rule Off and back on; an Off rule wins for nothing.
- [ ] Up/Dn reorder changes which rule wins when two rules match the same mod.
- [ ] A folder rule whose own folder no longer satisfies its folder condition shows the amber warning.
- [ ] A rule whose literal folder sits under a protected folder shows the protection warning.
- [ ] Restart the plugin: the rules are still there, in order, with their Off state.

## Sorting

- [ ] Sort files a matching mod at the rule's folder and leaves protected mods alone.
- [ ] The Sort summary lists a skipped (broken) rule.
- [ ] A rule using `{race}` still works with the race master switch off.
- [ ] Sorting twice in a row leaves the ruled mods where they are.
- [ ] The new-mod popup sort applies a rule to a freshly installed mod.
- [ ] A root-only sort applies rules to the root mods only.

## Review tab and export

- [ ] The diagnostics export shows `StagedRule:` for a ruled mod and `RulesUnplaced:` when a rule could not place one.
- [ ] "As suggested by Sort" turns unavailable after editing a rule, and available again after Sorting.

## Headless results (2026-11-23, real library, read-only)

Same harness as the race-split checklist (`PENUMBRA_HARNESS_LIBS`), now with a rules pass of five example rules.
Run on `C:\Mods` (287 mods) only. Headless rows carry no author, subtype or race, so only the name-based rule can fire
here; the other four rules parse and run but win nothing.

- All five example rules parse; no exception; every ruled path is non-empty with no `..`.
- An empty rule set gives the same proposals as no rules (287 mods).
- Rule 1 (`name contains 'hair'`) wins 5 mods; rules 2 to 5 win 0. Whole pass: 8 ms.
- Not run: the 2,557-mod timing bound (under 2 seconds) needs the second library on the other PC.
- Still needs the game: everything above.
