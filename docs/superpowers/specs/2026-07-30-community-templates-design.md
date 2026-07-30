# Community organization templates — Design

**Status:** approved 2026-07-30, not yet implemented.

## Context

Tester feedback (Discord, 2026-07-29/30) asked for shareable organization presets: *"is there a way to
possibly have like community templates to import like oh heres how i organize my mods and im sharing my
template… ive literally been trying to figure it out for like 4 days now and came to the server to see if
anyone posted their examples as like a base to work off of."* The reported problem is not a missing sort
algorithm — it is decision paralysis about what the folder tree should look like in the first place.

A second tester independently cited the Namingway plugin's preset packs (JSON files posted on a personal
blog, downloaded and dropped in) as a distribution model they already understand. That was offered as an
illustration, not a specification to follow.

### The workbook cannot serve as the sharing artifact

The obvious candidate — the existing `.xlsx` workbook (`WorkbookAdapter.cs`, and
`docs/superpowers/specs/2026-07-16-plugin-organizer-workbook-import-export-design.md`) — is
structurally unable to travel between users, for two independent reasons confirmed in that spec and the
linked `WorkbookWorkflowService`:

1. **`installationIdentity`** = SHA256(Penumbra config directory + mod root). `ImportAsync` treats a
   mismatch as a *hard* error ("this workbook belongs to a different Penumbra library"), so another
   user's workbook is rejected before any row is read.
2. **Row identity is `StableScanId`** — the Penumbra mod *directory* name, local to one install. Even
   with the identity check bypassed, every row from another library would resolve as an unknown-id skip.

The workbook is a round-trip editing document for exactly one library. This feature is therefore a new
artifact, not an extension of the workbook. (Separately, the workbook still lacks an "export as is"
button — a real gap for a user's own library, tracked independently of this spec, and *not* a partial
solution to template sharing.)

## Goal

A user can export their organization scheme as a small, portable document, share it by any means they
already use (Discord message, file attachment, blog post), and another user can import it and get
proposed paths for their own library: mods they have in common land where the template author put them,
and everything else is placed by a fallback strategy the author chose, using the author's folder names.

## Non-goals

- **Any change to the workbook format, or to `installationIdentity`/`scanIdentity` semantics.** Templates
  are a separate artifact with no identity binding by design.
- **A hosted gallery, curated index, or remote fetch.** Considered and rejected: a gallery makes the
  author an ongoing curator and adds a hosted dependency to a plugin that is not yet distributed through
  a public plugin repository; URL fetch adds network I/O and an arbitrary-remote-content trust surface
  for no gain over pasting a code or saving a file. Distribution stays entirely out-of-band.
- **Fuzzy or edit-distance mod matching.** Rejected: every fuzzy hit is a judgment call, and a silently
  wrong placement is the worst available failure mode for a feature whose output feeds Apply. Matching is
  exact-after-normalization; a rename simply becomes an unmatched mod handled by the fallback strategy.
- **Carrying a template author's hand-picked personal groupings as data.** A bucket like
  `Characters/<my OC>` is curation, not a rule, and the specific mods in it are the author's own choices.
  A template can declare the *empty folder* so an importer sees the intended shape and fills it in
  themselves; it does not attempt to guess which of the importer's mods belong there.
- **Learning fallback placement from the matched mods** (i.e. "most matched gear-head mods went to
  `Gear/Head`, so send the unmatched ones there too"). Proposed during brainstorming and not adopted:
  the author declares a fallback strategy explicitly instead. See Decisions and trade-offs.
- **A new sort engine or rule language.** The seven existing strategies plus a folder-label rename map
  cover the expressed need; an ordered match-rule DSL would be a new evaluation surface and a format
  commitment to version indefinitely.
- **A background-thread execution model.** Consistent with every prior spec in this repo: template work
  is a bounded in-memory pass over a few thousand rows, triggered by a button, on the ImGui thread.

## Template format

One JSON document. Both transports (Transports, below) carry the identical bytes.

```json
{
  "formatVersion": 1,
  "name": "Detailed type sort",
  "author": "Akako",
  "description": "Character mods up front, then mod type in detail.",
  "fallbackStrategy": "TypeThenCreator",
  "folderLabels": { "Others": "_Unsorted", "NPC/Bosses": "NPC/Raid bosses" },
  "folders": ["Characters", "Gear/Head", "Gear/Top", "_Unsorted"],
  "entries": [ { "n": "bibo+ medieval", "f": "Gear/Top" } ]
}
```

- **`fallbackStrategy`** names one of the seven strategies `OrganizerState` already exposes:
  `Creator`, `ModType`, `ModTypeDetailed`, `TypeThenCreator`, `TypeThenCreatorFlat`,
  `CreatorThenType`, `CreatorThenTypeFlat`. An earlier draft carried a separate
  `useGearSubCategories` boolean; it is dropped as redundant — the flat/detailed distinction is
  already the difference between the `Flat` and non-`Flat` variants (`FlattenGearSubCategory`).
- **`folderLabels`** renames canonical folder paths, keyed on the *output* of
  `ModTypeFolders.GetFolder` (`"Gear"`, `"Gear/Head"`, `"NPC/Bosses"`, `"Animation and VFX/…"`,
  `"Others"`, …), not on `ModCategory`. Keying on the output means labels apply uniformly to flat and
  nested folders, and — critically — the rename happens strictly *after* `GetFolder`, so no template
  value can reach `GetFolder`'s deliberate `ArgumentOutOfRangeException` for nonsense
  (category, subcategory) pairings. Absent keys map to themselves.
- **`folders`** is the author's folder tree, carried separately from `entries` so it can be browsed
  read-only before importing and so an intentionally empty bucket survives export.
- **`entries`** are `{ n: normalized name, f: destination folder }`. Destinations are **folder-only,
  no leaf**, matching the convention `WorkbookAdapter.SplitPath`/`JoinPath` already established for
  the workbook. Short field names because these dominate the payload size (Transports).

## Matching

`ModNameNormalizer.Normalize(string)` — pure, no state — is applied identically to the author's mod
names at export and the importer's mod names at import:

1. Trim; lowercase with `ToLowerInvariant`.
2. Strip a trailing install/version suffix (`_1_1_0`, `_2_0_3`, …): trailing runs of `_<digits>`.
3. Strip `(penumbra)` and bracketed tag groups (`[...]`, `{...}`).
4. Collapse remaining punctuation and whitespace runs to a single space; trim again.

So `Bibo+ Medieval (Penumbra)_1_1_0` and `bibo+  medieval` both normalize to `bibo+ medieval`.
Normalization is deliberately lossy in one direction only — it never invents a match that differing
significant words would rule out.

Import then, for every non-`Protected` row:

- **Hit** in `entries` → destination folder is the entry's `f`.
- **Miss** → destination folder is whatever `fallbackStrategy` computes for that row, with
  `folderLabels` applied to the `GetFolder` result.

In both cases the leaf is the **importer's own `row.Name`**, put through
`PenumbraPathSemantics.FixName` by the existing `BuildPath`. This inherits the leaf-is-`Name` rule
settled in the workbook spec (never `Identifier`, never the leaf currently sitting in `CurrentPath`,
which can carry a stale Penumbra-dealt `" (N)"` suffix into a fresh folder) rather than re-deciding it.

Protected rows are excluded for free: `OrganizerState.Sort` already filters `!m.Protected`, and
`AssignManual` returns `false` for a protected row.

## Architecture

Six new pure/near-pure units plus one UI tab, in `PenumbraOrganizer.Plugin/Organizer/Templates/`:

| Unit | Responsibility | Depends on |
| --- | --- | --- |
| `OrganizationTemplate` (+ `TemplateEntry`) | The record types above | nothing |
| `ModNameNormalizer` | `Normalize(string)`, above | nothing |
| `TemplateBuilder` | `OrganizerState` + an inclusion set → `OrganizationTemplate` | state, normalizer |
| `TemplateCodec` | JSON serialize/deserialize; share-code encode/decode | models |
| `TemplateStore` | enumerate/read/write `templates/*.json` | codec, file system |
| `TemplateResolver` | template + rows → `identifier → folder` map for matched rows only | models, normalizer |

**Applying a template is an eighth strategy on `OrganizerState`, not an external mutation.** The
pin-and-disambiguate tail (`FinishProposals`, which exists because Penumbra discards `" (N)"` suffixes
on save — see `[[pathrenamefailed-cycle-fix]]`) is private, and skipping it would reintroduce that
bug. So:

```csharp
// OrganizerState.cs — same shape as the seven existing SortBy* methods.
// Returns the report rather than a touched-row count plus an out parameter: the count is
// already one of the report's fields, and the seven existing SortBy* methods' bare int return
// has no caller that needs matching here.
public TemplateApplyReport ApplyTemplate(
    OrganizationTemplate template,
    Func<string, string> canonicalizeCreator);
```

It builds `TemplateResolver`'s matched map once, then calls the existing private
`Sort(folderSelector)` with a selector that returns the matched folder as `Primary` (with `Secondary`
null) when the row is a hit, and otherwise delegates to the same expression the named fallback
strategy uses. Result: pinning, disambiguation, `Protected` filtering, and collision handling are
inherited unchanged, and a template apply is indistinguishable downstream from any other sort.

One supporting change to existing code, kept minimal: `TypeFolder` and the seven `SortBy*` methods
take an **optional** folder-label resolver (`Func<string, string>?`, default `null` → identity), so a
template's `folderLabels` can be applied to `GetFolder` output without duplicating any strategy.
Every existing call site keeps its current behavior and needs no edit.

`TemplateApplyReport` carries the counts the UI reports verbatim: total rows considered, matched by
entry, placed by fallback, skipped as protected, and entries in the template that matched nothing.

## Export

`TemplateBuilder` reads `OrganizerState` and an explicit inclusion set of identifiers. For each
included, non-excluded row it emits `{ n: Normalize(row.Name), f: SplitPath(row.CurrentPath).Folder }`,
and collects the distinct folders (plus their ancestor chain) into `folders`.

Two normalized names that collide are **deduped at export**, keeping neither silently: the builder
reports the colliding display names so the author can see it, and keeps the first in `Ordinal` order
for determinism. `folderLabels` and `fallbackStrategy` are author inputs from the export screen, not
inferred from state — the plugin has no persisted "current strategy" concept (the same finding the
workbook spec had to handle).

### Review-and-trim, before anything is emitted

Exporting a name→folder map publishes the author's entire mod list, which for this plugin's user base
routinely includes content they would not choose to broadcast. Export therefore opens a review screen
first: every mod name that would be included, a search box, per-mod checkboxes, per-folder
include/exclude, and live in/out counts. Nothing is written or copied until the author confirms.

An excluded mod is simply absent from `entries` — importers treat it as unmatched and the fallback
strategy places it. Exclusion cannot produce a malformed or half-valid template, which is what makes
this screen safe rather than a source of new failure modes.

## Transports

Same document, two ways out, both chosen deliberately:

1. **`.json` file.** Written to `templates/<slug>.json` under the plugin's config directory (the same
   directory the existing workbook and Apply backup files use), via the atomic
   write-to-`.tmp`-then-replace pattern `Plugin.cs`'s `WriteBackup` already uses. Anything in that
   folder is enumerated into the Templates list, so importing someone else's template is "save the
   file here". Shareable as a Discord attachment, or from any host.
2. **Share code on the clipboard.** `POT1:` + base64(deflate(utf8 json)). Export copies it; import
   reads the clipboard. No file, no picker, no download.

**A stated limitation, surfaced in the UI rather than hidden:** a Discord message caps at 2000
characters, which is roughly 100 entries after compression. A real 900-mod library will not fit. Export
therefore displays the encoded length and, past the threshold, tells the author plainly to share the
`.json` file instead of emitting a code that will be truncated on paste. The code path stays genuinely
useful for small, hand-curated templates.

## Error handling

Import refuses rather than partially applies, in every case below, and no `OrganizerState` mutation
happens until the whole document has parsed and validated:

- **Unknown `formatVersion`** → refuse, naming the version found and the versions supported. A future
  template must never be half-read by an older plugin.
- **Missing `POT1:` prefix, invalid base64, invalid deflate stream, malformed JSON** → refuse with the
  specific stage that failed.
- **Oversize payload** → a decompressed-size cap enforced *during* inflation, so a hostile or
  accidental paste cannot balloon memory before validation gets a chance to reject it.
- **`fallbackStrategy` not one of the seven names** → refuse (the whole result depends on it).
- **Unknown key in `folderLabels`** → warn, ignore that one mapping, continue. Non-fatal: an unknown
  key is most likely a template written against a future category vocabulary.
- **An `entries` destination with invalid path segments** (empty segment, leading/trailing separator)
  → skip that entry, count it, and report the count. Non-fatal for the same reason a workbook row
  failure is non-fatal.
- **`TemplateStore`** ignores an unreadable or invalid file in `templates/` rather than failing
  enumeration, and surfaces its name as a warning in the list.

Every import reports the `TemplateApplyReport` counts, so "matched 214 of 900, 686 by fallback" is
visible before the user goes anywhere near Apply.

## UI

A new **Templates** tab:

- **Available templates** — everything in `templates/`, each with `name`, `author`, `description`, and
  entry count. Selecting one shows its `folders` as a read-only tree with per-folder match counts
  against the current library, so the user can see what the result will look like *before* importing.
  This is the "someone posted their example as a base to work off of" half of the request, and it costs
  nothing extra because `folders` is already in the document.
- **Import from clipboard** — decode, validate, show the same preview, then apply on confirm.
- **Export** — opens the review-and-trim screen (author name, description, fallback strategy, folder
  label overrides, inclusion checkboxes), then writes the file and/or copies the code.

Applying a template produces ordinary proposals. The user reviews them on the existing Review Changes
tab and applies through the existing Apply pipeline. No new write path, no new gating rule, no new
Penumbra IPC.

## Data flow

`OrganizerState` remains the single in-memory model. `TemplateBuilder` is a one-way read out of it.
`OrganizerState.ApplyTemplate` writes into it through the same private `Sort`/`FinishProposals` path
every existing strategy uses. `TemplateCodec`/`TemplateStore` never see `OrganizerState`, Penumbra IPC,
or a mod path — only the template document. Nothing in this feature touches `mod_data.db`,
`organization.json`, or `SetModPath`.

## Testing

Per this repo's convention (pure logic unit-tested, IPC/file glue verified in-game):

- **`ModNameNormalizer`** — a table of real name shapes: version suffix, `(Penumbra)`, bracketed tags,
  doubled whitespace, mixed case, punctuation runs, and a pair that must *not* collide.
- **`TemplateBuilder`** — exclusions honored (an excluded mod is absent, not empty-valued); ancestor
  folders collected; normalized-name collision deduped deterministically and reported; an empty folder
  with no included mods still appears in `folders`.
- **`TemplateCodec`** — round-trip equality for a representative template; and a rejection case each
  for bad prefix, bad base64, bad deflate, malformed JSON, oversize inflation, unknown
  `formatVersion`, and unknown `fallbackStrategy`.
- **`TemplateResolver`** — hit, miss, and a template entry matching nothing (counted, not silent).
- **`OrganizerState.ApplyTemplate`** — matched rows land in the entry's folder; unmatched rows land
  where the named fallback strategy puts them; `folderLabels` renames apply to both halves;
  `Protected` rows are untouched; the leaf is the local `row.Name` (not `Identifier`, not the
  template's name key); two mods resolving to the same folder and leaf are disambiguated by the
  existing tail rather than colliding.
- **`TemplateStore`** — enumerates valid files, ignores an invalid one while still returning the rest.

**In-game verification requires two libraries** — export from one install, import into another — so
unlike prior items in this repo it cannot be self-verified. This makes it dependent on a Discord
tester, which should be lined up before the work starts rather than discovered at the end.

## Decisions and trade-offs

- **Fallback strategy is declared by the author, not learned from the matched mods.** Learning was
  proposed (take the most common folder the matched mods of each category landed in) and rejected in
  favor of an explicit declaration. The known cost is that a strategy's generated folder names need not
  coincide with the template's own, which would produce two competing structures in one tree; this is
  mitigated — not eliminated — by running the fallback strategy through the template's `folderLabels`,
  so both halves draw on one folder vocabulary. Residual risk: a template whose `entries` use folder
  names that no strategy would ever generate (e.g. `Characters`) will still show unmatched mods
  elsewhere. That is visible in the preview and in the report counts before Apply.
- **Matching is on display `Name`, not `Identifier`.** Directory identifiers carry install-time version
  suffixes and are renamed freely, so an identifier-first scheme degrades to the name path in practice
  while adding a second matching mechanism to explain and maintain.
- **Both transports ship, rather than picking one.** The clipboard code is the low-friction path people
  actually want for Discord; the file is the only path that works at real library size. Shipping one
  would have meant either a feature that breaks on large libraries or one that ignores how the
  community already shares things.

## Open risks

1. **Match rate is unknown until real templates exist.** If typical overlap between two libraries is
   low, most of an importer's tree comes from the fallback strategy and the template contributes less
   than the feature promises. The report counts make this honest rather than hidden, but the value
   proposition is unvalidated until a real template is exchanged between two testers.
2. **Normalization is a compatibility surface.** Changing `ModNameNormalizer` later changes which
   entries match in already-published templates. Any future change to it is a `formatVersion`
   question, not a free bug fix.
3. **The review-and-trim screen is the safety mechanism for a real privacy exposure.** If it is
   shipped incomplete — or bypassed by a "quick export" affordance added later — the feature publishes
   users' full mod lists. It should not be treated as optional polish.
