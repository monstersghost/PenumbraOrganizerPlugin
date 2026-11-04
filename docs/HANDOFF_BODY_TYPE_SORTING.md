# Handoff: sorting gear by body type

Merged to `main` (`c3a011b`). 1671/1671 tests pass on Windows, build clean. This note is for whoever
picks up the in-game verification, or extends the body list.

**Nothing has run in game.** That is the only outstanding work, and the first section says what to
check and why it is not a formality.

## What's on `main` now

A fourth split on the Sort tab, **Split gear and body mods by body type**, off by default. A gear mod
is filed under the body replacer it was made for, with its equipment slot below it, so everything cut
for one body gathers in one place:

```
Gear/Bibo+/Top/Bibo+ Medieval Dress     body + slot split on
Gear/Bibo+/Bibo+ Medieval Dress         body on, slot off
Gear/Top/Plain Robe                     no body in the name
Body/Rue+/Rue+ Body                     a body replacer
```

Detection is two-stage, and the order is the safeguard:

1. **The equipment slots decide WHETHER a body applies.** `ModTypeClassifier.BodyTypeApplies` requires
   a Gear row to occupy `Top`, `Hands`, `Legs` or `Feet` — the four model halves a body replacer
   authors (`_top`, `_glv`, `_dwn`, `_sho`), which is also the set Proteus's own
   `BodySizeCatalog.KnownSlots` carries. A `Body` row always qualifies. Everything else is out.
2. **Only then does the mod's NAME decide WHICH body**, via `BodyTypeMatcher`.

Files:

- `Organizer/BodyTypes/body-type-list-static.json` — 30 bodies, 120 aliases, embedded resource
- `Organizer/BodyTypes/BodyTypeListDocument.cs` / `BodyTypeListCodec.cs` / `BodyTypeListStore.cs`
- `Organizer/Classification/BodyTypeMatcher.cs`
- `Organizer/Classification/ModTypeClassifier.cs` — `BodyTypeApplies`, `BodyShapedSlots`, and the
  three-argument `ModTypeFolders.GetFolder`
- `Organizer/OrganizerModRow.cs` — `BodyType`, its own field and not a `SubCategory` value
- `LibraryWork/Pure/ScanProcessor.cs` — the gate and the match
- `Organizer/SortFolderSelectors.cs`, `Organizer/OrganizerState.cs` — the `splitBody` flag
- `Windows/SortPanel.cs` — the checkbox, under a "Body type" heading

Docs: `USER_GUIDE.md` (Sort tab entry, a "Body type" section, two Known caveats),
`HOW_SORTING_WORKS.md` (a "Body-type sub-classification" section), and the manual checklist at
`docs/testing/2026-11-04-body-type-sorting-checklist.md`.

## What's NOT done yet — the critical next step

**Whether Penumbra stores a folder name containing `+` unchanged, across a write AND a reload.**
Twelve of the thirty body labels contain a `+`. If it does not survive, most of the folder names this
feature produces are wrong, and that is a data problem for anyone who has already applied a sort.

What is known: `PenumbraPathSemantics.FixName` mirrors Penumbra's rule as trim, `/` to `\`, empty to
`<None>`, and a multi-level type folder is deliberately never passed through it. On that reading `+`,
`&` and `[` `]` all survive. **That was read off the mirror, not watched round-trip.** `Tight&Firm`
and `Re[de]fined Lalafell` exercise the other two characters.

If a character is rewritten, the fix is in `BodyTypeListCodec.IsValidLabel` and the label set, not in
the sort. Report which character and what it became.

The rest of the checklist covers plausibility on a real library, the exclusions (a weapon, a hat and
a ring named after a body must not get body folders), the checkbox's own behaviour, and the user list.

The scan now writes a `BodyType:` line per mod into the diagnostic dump, so "how many matched versus
stayed flat" is answerable from an export.

## Key decisions, in case they need revisiting

**Name-based detection is not a shortcut.** A garment made for Bibo+ replaces its own equipment set,
not the body's models, and nothing in its files names the body. Proteus establishes it by mesh and UV
correspondence against the body's own meshes — accurate, and far more than a folder sort should do.
So a match is a STATED intent, not a verified fact. Do not "improve" this by reading files; there is
nothing there to read.

**Loose aliases are deliberate.** A mod that merely contains a body's name as a word can be filed
under it: "Rue de la Paix" reads as a Rue+ garment. Detection is preferred over precision because the
Review step shows every move before anything happens, so a wrong guess is one visible row, whereas an
undetected body is silently missing from the folder the user went looking in. A test documents the
false positive rather than pretending it cannot happen.

**Fourteen bare spellings were removed after a false-positive sweep**, and must not be re-added:
`succulent`, `grande`, `stocky`, `nino`, `cherie`, `hyacinth`, `melusine`, `seraphim`, `annabelle`,
`aphrodite`, `shapely`, and the three-letter acronyms `gao`, `lob`, `omb`. Each matched an ordinary
gear title ("Grande Cafe Apron", "Stocky Boots", "Ma Cherie Dress"). The BODY is still in the list in
every case; only the risky spelling is gone, with a distinctive form kept or added instead
(`seraphim+`, `annabelle+`, `lokesh`, `old man bones`, `shapely hd4`). `KnownNonBodies_AreNotAliases`
fails if one is re-added.

`artemis` is the single knowing exception, because it is the only name that body goes by. Its worst
case, "Artemis Bow", is now excluded structurally — a weapon has no body-shaped slot.

**Variants are folded into their parent, by the plugin author's decision.** TBSE-X is an alias of
TBSE; YAB Buff is an alias of YAB. Proteus models both as bodies of their own, so this deliberately
diverges, and the tests say so to stop a later reader "correcting" it back. HRBody stays separate —
it is a different body, not a TBSE variant.

**Gen2 and Gen3 are NOT bodies.** The Body Mod Compendium devotes a Part to each, which looks like
evidence they are families. They are *texture standards*: Gen2 is vanilla-based mirrored textures,
Gen3 is a different texture set, and a body *uses* Gen3 as its texture rather than being one. No gear
is made for "Gen3". The Compendium's Parts 3 and 4 group bodies by shared texture; Parts 2, 5 and 7
(Bibo+, hrBody, TBSE) are mesh families and their members *are* real sort targets.

**LaRue belongs to LavaBod+, not Rue+.** It is LavaBod in a Rue shape, so a LaRue garment is cut for
LavaBod. Easy to "fix" the wrong way from the name alone; it has a test.

**One owner for label validation.** `BodyTypeListCodec.IsValidLabel` is the single rule, and
`ModTypeFolders.IsBodyTypeLabel` delegates to it. They were once separate and disagreed, which was a
real bug: a user label containing `*`, `:`, an emoji or a pasted non-breaking space parsed cleanly and
then threw out of `GetFolder` mid-sort — `OrganizerState.Sort` mutates `row.ProposedPath` in place, so
some rows were re-pathed and some were not, `FinishProposals` never ran (no collision disambiguation,
no pinning), and the call site in `SortPanel` is unguarded inside the ImGui draw. **Do not reintroduce
a second copy of that rule.**

**The matcher has no index, on purpose.** `NpcNameMatcher` builds a first-token dictionary because it
handles ~20,000 names. This list is 120 aliases, measured at about 2 µs per mod, so an index would be
machinery with nothing to do. If the list ever grows past a few hundred, porting that index is the
fix — `BodyTypeListStore.MaxUserBodyCount = 500` caps how far the user file can push it, which is the
lesson from the NPC name list applied where it can still recur.

**Longest alias wins, and no shipped pair currently exercises it.** Variants being folded into their
parents means the longest match and the shortest now agree on every entry. The ordering matters for
the next variant added as its own body, which is exactly when it would be easy to delete and hard to
notice; `Match_LongestAliasWinsRegardlessOfPosition` pins it on synthetic entries for that reason.

## Known limitations, not fixed here

- **`EXQB` is two things** in the Compendium: "Expansive Body Quartz Boobs [EXQB]" is a Gen3 base
  body, and "EXQB Rue" is a separate Bibo+ edit. The list has one entry.
- **NIRÆ appears nowhere in the Compendium**, which lists only free, publicly linked, non-AI mods — so
  it is likely paid or Discord-only rather than absent. Kept because Proteus carries a whole service
  built around it. The plugin author had not seen it.
- **About 100 Compendium names are not in the list**: roughly 20 rejected as too generic to alias
  safely, the rest personal edits almost no gear targets. The full taxonomy was never committed; it is
  cheap to re-extract from the Compendium's family sections (see Sources) if the list needs widening.
- **No version bump and no release notes.** The csproj warns that `HelpTab.GuideUrl` embeds the
  version string character for character, so bumping it without a matching release tag 404s the Help
  tab's guide link in a shipped build. Both belong to cutting the release.
- **Templates do not store the body split**, the same way they already omit the hair split. A stored
  body split would have a shared template re-foldering a recipient's gear against a body list they may
  hold a different version of. `TemplateFallbackConversionTests` is the tripwire that forces this to be
  a decision rather than an oversight — it counts `SortSelection`'s constructor parameters, so adding a
  field fails it on purpose.

## Sources — don't re-derive these

- **Proteus** (`github.com/solona-m/proteus`, read at `b20e72d`) — a Dalamud plugin with a body-type
  converter. `Services/SizeSet/SupportedBodies.cs` is its curated 9-body table with `NameHints`;
  `Services/BodySizeCatalog.cs` has `IsBodyModel`, the structural signature for "is this mod a body"
  (`^chara/equipment/e0000/model/c\d{4}e0000_(top|dwn|glv|sho)\.mdl$`); `Services/AutoRefit/BodyModIndex.cs`
  shows it detecting bodies by files and never by folder name.
- **The Body Mod Compendium** by @MooncatKeeper, read 2026-11-04, a public Google Doc —
  `1N_EZiNh6nnB-vGcufVnsnIJXv5zmh4p_4EOkzPsIFEE`. The community catalogue, ~134 names grouped by
  family. Authoritative for spellings and for which family a body belongs to. Fetch its plain text with
  `https://docs.google.com/document/d/<id>/export?format=txt`.
- **xivmodarchive.com is unreachable to an agent** — Cloudflare JS challenge on every page. Don't
  spend time on it.

## Process note — environment gotchas

- **The plugin does not build alone.** `PenumbraOrganizer.Plugin.csproj` links 11 source files from a
  sibling checkout at `..\..\PenumbraOrganizer`. In a git worktree under `.claude/worktrees/<name>`,
  `..\..\` resolves to `.claude/worktrees/`, so a directory junction is needed there or the build fails
  with 11 × `CS2001`.
- **The test project is Windows-only to RUN.** It targets `net10.0-windows7.0` and its host needs
  `Microsoft.WindowsDesktop.App`. Retargeting to plain `net10.0` fails restore at ClosedXML's
  `System.IO.Packaging`, which is what the `UseWindowsForms` comment in the csproj warns about. On
  Linux you get compilation only (`-p:EnableWindowsTargeting=true`), plus Dalamud at
  `~/.xlcore/dalamud/Hooks/dev/` and an `APPDATA` shim for the test csproj's
  `$(APPDATA)\XIVLauncher\addon\Hooks\dev\Dalamud.dll` HintPath.
- **The body-type tests can run on Linux** in a throwaway `net10.0` xunit project that links the pure
  source files plus the real test files, with the body list embedded under
  `LogicalName="PenumbraOrganizer.Plugin.Organizer.BodyTypes.body-type-list-static.json"`. 177 of them
  pass that way. `OrganizerState` cascades too far to extract, so the sort and export tests need
  Windows.
- **`docs/build-pdf.py` needs `wkhtmltopdf`**, so the PDF can only be regenerated on Windows. Re-run it
  whenever `USER_GUIDE.md` changes — the guide claims the two cannot describe different versions.
