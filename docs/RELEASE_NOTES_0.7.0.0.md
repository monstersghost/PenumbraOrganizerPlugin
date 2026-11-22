# 0.7.0.0 (testing) - race folders, a reworked workbook, and keyword rules

This is a testing build. Nothing in it has been tried in game or in Excel yet, so please report anything odd.

## New in 0.7.0.0

- **Race split** (Sort tab, new Race section). A master switch plus a row per kind of mod: Hair, Face, Eyes, Skin, Tails/Ears/Horns and Racial scaling. A ticked row files its mods under a race folder.
  - A mod that touches several races goes to `Multi-race`, or, if you untick "Send multi-race mods to Multi-race", to the race highest in your race priority list.
  - Racial scaling packs (height and body-size mods) file under `Body/03. Racial Scaling/<Race>` when that row and "Split body mods" are on.
  - The Review export now says why a mod got its race folder.
  - Your old "Split hair by race" setting carries over as the Hair row. If you downgrade and upgrade again, only the hair setting survives; the other rows, the priority and the toggle reset.
- **Reworked workbook** (Review tab). The plugin now owns the Excel export and import.
  - **Export mode:** "As is" exports your library as it stands. "As suggested by Sort" exports what the last full Sort proposed. It is available only while the staged paths still come from one Sort with the current settings, and the Review tab tells you why when it is not.
  - **Category Mapping sheet:** the 16 built-in categories plus your own, numbered 17 to 9999 (up to 200). The sheet wins over saved settings.
  - **Tokens in destinations:** `{type}`, `{creator}` and `{race}`. A token that comes out empty drops its folder level, and an unknown token is reported as an error.
  - **Unchanged cells:** a destination you did not edit is read back exactly as written, so folders named like categories, numbers or tokens survive a round trip.
  - **Import report:** counts what changed, what was skipped and what could not be staged. Version 2 workbooks still import under the old rules.
  - **Standalone app:** it cannot read the new workbook format.

## Keyword rules (Sort tab, Rules section)

A rule says "if a mod matches this condition, file it in this folder":

`name contains 'hrothgar' and type = 'Body' -> Body/Hrothgar/{creator}`

The part before `->` is the condition. The part after it is the folder. The mod's own name is always added at the end.

**How rules are applied**

- Rules are checked from the top of the list. The first enabled rule that matches files the mod, so **order is priority**. Use Up and Down to change it.
- A rule's folder replaces the whole path. Normal sorting (by type, creator, race folders, numbered folders) does not apply to a mod a rule has filed.
- Mods no rule matches sort as before. Protected mods are never touched.
- Untick a rule to turn it off without deleting it. You can have up to 100 rules, each up to 300 characters.

**The condition language: SQL-like, but not SQL**

It follows the `WHERE` part of SQL, so SQL experience carries over. You cannot paste a SQL query in. It is deliberately small.

*Fields, all text:*

| Field | What it holds |
|---|---|
| `name` | the mod's name |
| `author` | the creator, as the Sort tab names it |
| `folder` | the folder the mod is in now (empty if it is loose in the root) |
| `type` | the category folder, for example `Gear`, `Hair`, `Body`, `Animation and VFX` |
| `subtype` | the slot or kind inside a category, for example `Feet` |
| `race` | what `{race}` gives, including `Multi-race` |
| `body` | the body type, for example `Bibo+` |

*Operators:* `=`, `!=`, `contains`, `starts with`, `ends with`, `in ('a', 'b')`, and `not` before an operator (`name not contains 'x'`, `type not in ('Gear')`). Combine them with `and`, `or`, `not` and brackets.

*Same as SQL:*

- Text goes in single quotes, and `''` means a quote inside the text (`author = 'O''Brien'`).
- `not` binds tightest, then `and`, then `or`.
- Keywords and field names are not case sensitive.
- Text comparison ignores case, so `'hrothgar'` matches `Hrothgar`.

*Different from SQL:*

- It uses `contains`, `starts with` and `ends with` instead of `LIKE '%x%'`. There are no `%` or `_` wildcards.
- There is no NULL. A missing author or race is empty text, so write `race = ''`, not `IS NULL`.
- There is no `<>`, `BETWEEN`, `LIKE`, numbers, functions, `ORDER BY` or subqueries. Write `!=` for "not equal".
- `contains ''`, `starts with ''` and `ends with ''` are rejected because they would match every mod. `= ''` and `in ('')` are fine.
- The folder after `->` may use `{type}`, `{creator}` and `{race}`. A token that comes out empty drops that folder level, so `{race}/{creator}` on a mod with no race gives just the creator. If the whole folder ends up empty, the mod stays where it is and the Sort summary says why.

**Examples**

- `author = 'Ann' -> Ann's mods`: everything by Ann, in one folder.
- `type = 'Gear' and subtype in ('Top', 'Legs') -> Gear/Outfits`: tops and legs together.
- `race = 'Viera' and type = 'Hair' -> Hair/Viera/{creator}`: Viera hair by creator.
- `name contains 'recolor' or name contains 'retexture' -> Recolors/{type}`: either word.
- `not type in ('Gear', 'Hair') and name starts with 'a' -> A/{type}`: other kinds of mod starting with "a".

**Live help in the Rules section**

- A rule with a mistake shows a red message with the position, for example `Unknown field 'nmae'. Did you mean 'name'? (at 0)`.
- A good rule shows how many mods it would win, for example "Wins for 12 mods". The count leaves out protected mods and mods an earlier rule already wins, so the numbers add up.
- An amber warning appears if a rule's folder would no longer match its own `folder` condition (so the next Sort may file the mod somewhere else), or if the folder is inside a protected folder.

**Rules and the workbook:** the Review tab's "As suggested by Sort" export includes rule results. Editing, reordering or switching a rule greys that export until you Sort again.

The same explanation is in the plugin: Help tab, "Sort rules".

## A small interface test: the pinned Sort row (not final)

The Sort tab is long, so this build tries one change. The Sort button, its summary line, the `?` button, Group by and What to sort are pinned at the top of the tab. They stay visible while everything below (the splits, Race, Rules, Body priority, Advanced and Manual assignment) scrolls underneath.

This is a small test, and **the interface is not final**. It may be moved, resized or changed back before the next stable release, and other tabs may get similar treatment or none. If it helps, or gets in the way, say so.

## What moves

- Nothing moves until you Sort, review and Apply.
- A rule applies to every Sort, including the automatic new-mod sort, but not to a template used as a fallback.
- If a rule's folder cannot be built for a mod (for example `{creator}` on a mod with no author), the mod stays where it is and the Sort summary says so.

## Please test

- Race folders on real libraries, especially Highlander, Roegadyn, Lalafell, Hrothgar and Viera, and ear and horn mods.
- A real Excel round trip: export, edit, import. Excel's repair prompt, if it appears, matters.
- Typing a bad rule, the live count, Sort, and re-exporting after editing a rule.
- The pinned Sort row: scroll the Sort tab and check the Sort button stays in view.

## Not in this release

- Not verified in game or in Excel yet.
- There is no per-mod tooltip for the winning rule; it appears only in the diagnostics export (`StagedRule:` and `RulesUnplaced:` lines).
- The Sort tab layout is a test and is not final. See the pinned Sort row above.
