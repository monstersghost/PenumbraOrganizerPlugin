# Penumbra Organizer Plugin v0.6.0.0

## Changes since v0.5.3.1

This is mostly a release about explaining itself. The Sort tab is simpler, every control tells you
what it does, there is a Help tab, and there is a short walkthrough the first time you open it.

**If you are upgrading, the walkthrough runs once for you too.** That is deliberate, not a bug: the
Sort tab has changed enough that an existing user has about as much new to look at as a new one.
Dismiss it and it will not come back.

### Changed: seven sort buttons are now one dropdown and two checkboxes

The Sort tab had seven buttons whose names you had to decode - "By Type Then Creator (Detailed)"
told you very little about where your mods would end up.

It is now **Group by** (Creator, Mod type, Type then creator, Creator then type), two checkboxes for
whether gear and NPC mods get subfolders, and a Sort button.

This is the same sorting you already had, said more plainly - but it also gives you six combinations
the buttons never offered, because **Split NPC mods by kind can now be turned off**. With it off,
NPC mods go into a single `NPC` folder instead of `NPC/NPCs`, `NPC/Bosses` and `NPC/Enemies`.

**It is on by default, so your sorting does not change unless you turn it off.** Nobody upgrading
gets a surprise reshuffle.

If you change the grouping after sorting, a line appears telling you the proposals no longer match
what you picked. Press Sort again.

### Changed: NPC names come from a curated list, and the wiki refresh is still off

0.5.3.1 turned "Refresh NPC list from wiki" off after a full refresh grew the saved list to around
21,000 names. Those notes said 0.6.0 would bring it back as an opt-in once we could verify it was
safe.

**Half of that shipped. The opt-in exists; it is still switched off.**

What did change:

- The plugin now ships a **curated list of 827 characters, bosses and enemies** worth sorting by,
  instead of the small seed list. This is the default and it needs no setup, no network access and
  no refresh.
- The name matching was rebuilt. It no longer builds one giant pattern per category, so a large list
  is no longer expensive to load. This was done for cost and correctness.
- The wiki scrape is now a **separate, optional second list** rather than the list. When it is
  eventually enabled, turning it on adds names to the curated list rather than replacing it, and a
  refresh now **replaces** its contents rather than adding to them, so it cannot grow without bound
  the way it did before.
- Your existing `npc-name-list.json` is renamed to `npc-name-list-scraped.json` automatically on
  first run. Nothing in it is lost, and nothing in it is loaded unless you opt in - which you
  currently cannot.

**Why the toggle is still off, plainly:** we know that resetting an oversized list stops the game
closing, and we still do not know *why* a large list did that. The new matching is cheaper and we
are confident in it, but "cheaper" is not the same as "we found the cause". Enabling a 20,000-name
list before we can explain the original failure would be guessing with your game. It stays off until
it has been tested in-game against a full list.

The curated list is unaffected by any of this and works from the first scan.

One small matching change you might notice: names now match across different punctuation, so a mod
called `Y-shtola Hair` or `Y shtola Hair` is recognised the same as `Y'shtola Hair`.

### Added: every control explains itself on hover

Hover anything on any tab and you get a sentence saying what it does to your library. Disabled
controls explain why they are disabled, which is when it matters most - Apply now distinguishes
"fix the errors listed above first" from "another operation is in progress", and Clean Up Selected
Folders says when you simply have not ticked anything yet.

### Added: a Help tab

Everything the tooltips say, at reading length, plus what the plugin does, what is and is not safe
to press, what to do if an operation was interrupted, and where your files live. It also links to
the full guide for the version you are actually running.

Nothing on that tab changes anything.

### Added: a guided first run

Five short steps covering the scan, protect, sort, review, apply loop. It appears the first time you
open the plugin window - not when the game starts - and you can reopen it any time from the Help tab.

If Penumbra is not loaded when it would appear, it says so and offers itself again next time,
instead of walking you through steps whose results you would not be able to see.

### Fixed: protecting a folder no longer re-protects every Heliosphere mod

If you used "Toggle Heliosphere protection" to unprotect your Heliosphere mods, then ticked or
unticked any folder on the Protect tab, all of them silently became protected again.

Your choice now survives. It still resets on the next scan, which is unchanged and deliberate -
Heliosphere owns where those mods live. Protecting a folder that contains a Heliosphere mod still
protects that one, since that is a more specific instruction.

This was not new in 0.6.0; it has been there since the first public release.

## Known issues

- Applying a large plan moves every mod one at a time, and Penumbra announces each move on its own
  schedule. The automatic rescan that follows can therefore see those announcements arriving late
  and decide its own results are stale. If that happens you will be told the mod list changed
  immediately after an operation you just watched succeed. Nothing is wrong with the result, and
  running the scan again will work, but the message is misleading. *(Unchanged from 0.5.3.1.)*
- "Refresh NPC list from wiki" remains disabled, as above.

## Coming next

Enabling the scraped NPC list, once it has been verified in-game against a full list. That is the
only thing this release deliberately left unfinished.

## For developers

- `NpcNameMatcher` no longer uses `Regex` at all. Names are normalised, tokenised and bucketed by
  first token; a full scrape's 20,115 names go from a 205KB compiled alternation per category to one
  dictionary lookup plus a median of one comparison. Category order remains the outer loop of the
  match, which is what keeps `"Titan Slaying Y'shtola"` an NPC rather than a Boss.
- Three matching behaviours changed with it, each pinned by a test: separators between tokens are
  interchangeable, non-BMP letters now count as letters, and case folding moved from culture
  sensitive to ordinal (which changes Turkish dotted/dotless I). `NpcNameMatch.Name` is now the
  list's spelling rather than the mod title's.
- The seven `OrganizerState.SortBy*` methods collapsed into
  `Sort(strategy, splitGear, splitNpc, canonicalizeCreator)`. The legacy mapping was pinned against
  the old methods as an oracle before they were deleted.
- Refresh writes a snapshot rather than merging additively, which is what removes the unbounded
  growth. A category whose scrape fails keeps what was already on disk, because a failed scrape
  returns partial results and replacing from those would delete everything past the failure point.
- The scraped list is gated at compile time as well as in the UI, so a `true` left in config by hand
  cannot load it.
- Protection now has a single rule. It previously had two that disagreed depending on which control
  you last touched, which is what caused the Heliosphere bug above.

1045 tests pass on this release.
