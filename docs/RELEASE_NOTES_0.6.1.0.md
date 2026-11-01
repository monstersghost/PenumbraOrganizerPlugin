# 0.6.1.0 - Split hair by hair number (testing build)

**This is a testing build.** It is offered to testers only; stable stays on 0.6.0.0. There is one
specific thing we need checked before this becomes stable - see "What we need from testers".

## What's new

A third checkbox on the Sort tab: **Split hair by hair number**.

With it on, a hair mod that clearly replaces one vanilla hair goes into a numbered subfolder, so
`Hair/115` instead of everything piled into one `Hair` folder. A mod that covers several hair
numbers, or whose files could not be read, stays directly in `Hair` as before.

The numbers come from the mod's own files, not from its name, so a mod called "Cute Bob v3" still
lands under the hair it actually replaces. One hair released for every clan counts as one number,
not eight.

The checkbox is **off by default**. If you never touch it, your library sorts exactly as it did on
0.6.0.0 - this was traced end to end and is not a "should be fine".

### One thing that will look odd

Penumbra sorts folders as text, so `Hair/9` appears *after* `Hair/115` in the tree. That is
expected, not a bug. Numbers are unpadded on purpose; padding them to `Hair/0009` would sort
correctly but reads worse everywhere else.

## What we need from testers

**Does the hair number match the hair?** One of the two places we read numbers from is a piece of
mod metadata whose meaning we have corroborated but not confirmed against Penumbra's own source.
It is the only part of this feature that could put a mod in a *wrong* folder rather than simply
leaving it where it was.

If you see a hair mod filed under a number that is not the hair it replaces, please say so on
Discord with the mod name and both numbers. That single report is worth more than any amount of
further testing on our side.

Everything else about the feature is conservative by design: when it cannot work out a number it
leaves the mod alone, and nothing is moved until you press Apply on a proposal you have reviewed.

## Also in this build

- Nothing else user-facing. The rest of the change is internal: the code that reads Penumbra's
  config files is now shared between the gear and hair paths instead of existing twice, so the
  next time Penumbra changes that format there is one place to fix.
- Scanning now costs one extra file read per **hair** mod, even with the checkbox off. Not
  noticeable on a normal library; worth knowing if you have thousands of hair mods and time your
  scans.
- The diagnostic export now shows a hair mod's resolved number. That is how you can check the
  numbers in bulk without installing anything.

## Upgrading and going back

Opt in through **Receive plugin testing versions** in `/xlplugins`. To go back, turn that off and
reinstall; 0.6.0.0 reads everything this build writes, and this build changes no saved data.

## For the curious

Full design and implementation detail:

- `docs/superpowers/specs/2026-11-01-hair-number-sorting-design.md`
- `docs/superpowers/plans/2026-11-01-hair-number-sorting.md`
- `docs/testing/2026-11-01-hair-number-sorting-checklist.md` - the checklist this build exists to
  get through

1111 automated tests pass, up from 1052 on 0.6.0.0.
