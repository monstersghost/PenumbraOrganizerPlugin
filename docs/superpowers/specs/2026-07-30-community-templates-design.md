# Community organization templates — Design

**Status:** approved 2026-07-30, not yet implemented. Revised 2026-07-30 after an external design
review — see "Revision notes" at the end for what changed and why.

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
artifact, not an extension of the workbook, and specifically not an "ignore identity" workbook mode,
which would weaken an existing safety guarantee and still fail to match rows. (Separately, the workbook
still lacks an "export as is" button — a real gap for a user's own library, tracked independently of
this spec, and *not* a partial solution to template sharing.)

## Goal

A user can export their organization scheme as a small, portable document, share it by any means they
already use (Discord message, file attachment, blog post), and another user can import it and get
proposed paths for their own library: mods they have in common land where the template author put them,
and everything else is placed by a fallback strategy the author chose, using the author's folder names.

## Non-goals

- **Any change to the workbook format, or to `installationIdentity`/`scanIdentity` semantics.**
- **A hosted gallery, curated index, or remote fetch.** Rejected: a gallery makes the author an ongoing
  curator and adds a hosted dependency to a plugin not yet distributed through a public plugin
  repository; URL fetch adds network I/O and an arbitrary-remote-content trust surface for no gain over
  pasting a code or saving a file. Distribution stays out-of-band.
- **Fuzzy or edit-distance mod matching.** Rejected: every fuzzy hit is a judgment call, and a silently
  wrong placement is the worst available failure mode for a feature whose output feeds Apply. A rename
  simply becomes an unmatched mod handled by the fallback.
- **Carrying a template author's hand-picked personal groupings as data.** A bucket like
  `Characters/<my OC>` is curation, not a rule. A template can declare the *empty folder* so an importer
  sees the intended shape and fills it in themselves; it never guesses which mods belong there.
- **Learning fallback placement from the matched mods.** Proposed during brainstorming, not adopted; the
  author declares a fallback strategy explicitly. See Decisions and trade-offs.
- **A new sort engine or rule language.** The seven existing strategies plus a folder-label rename map
  cover the expressed need.
- **A background-thread execution model.** Consistent with every prior spec in this repo.

## Template format

One JSON document. Both transports carry the identical bytes.

```json
{
  "formatVersion": 1,
  "name": "Detailed type sort",
  "author": "Akako",
  "description": "Character mods up front, then mod type in detail.",
  "createdWithVersion": "0.5.2.0",
  "createdAtUtc": "2026-07-30T07:00:00Z",
  "fallbackStrategy": "TypeThenCreator",
  "folderLabels": { "Others": "_Unsorted", "NPC/Bosses": "NPC/Raid bosses" },
  "folders": ["Characters", "Gear/Head", "Gear/Top", "_Unsorted"],
  "entries": [ { "n": "bibo+ medieval", "f": "Gear/Top" } ]
}
```

- **`fallbackStrategy`** names one of the seven strategies `OrganizerState` already exposes:
  `Creator`, `ModType`, `ModTypeDetailed`, `TypeThenCreator`, `TypeThenCreatorFlat`,
  `CreatorThenType`, `CreatorThenTypeFlat`. An earlier draft carried a separate `useGearSubCategories`
  boolean; dropped as redundant — the flat/detailed distinction is already the difference between the
  `Flat` and non-`Flat` variants (`FlattenGearSubCategory`).
- **`createdWithVersion`/`createdAtUtc`** are informational provenance only. They are never validated
  beyond being strings and never block import; they exist to make bug reports and future format
  migrations tractable.
- **`folderLabels`** renames canonical folder paths, keyed on the *output* of
  `ModTypeFolders.GetFolder`. See Folder labels below for the exact matching model. The rename happens
  strictly *after* `GetFolder`, so no template value can reach `GetFolder`'s deliberate
  `ArgumentOutOfRangeException` for nonsense (category, subcategory) pairings.
- **`folders`** is the author's folder tree, carried separately from `entries` so it can be browsed
  read-only before importing and so an intentionally empty bucket can be shared. See Export for how
  empty folders are obtained.
- **`entries`** are `{ n: normalized name, f: destination folder }`. Destinations are **folder-only, no
  leaf**, matching the `WorkbookAdapter.SplitPath`/`JoinPath` convention. Short field names because
  these dominate payload size.

## Name normalization

`ModNameNormalizer.Normalize(string)` — pure, no state, no culture sensitivity — is the compatibility
surface of this whole feature and is therefore specified exactly rather than described:

1. Trim; `ToLowerInvariant`.
2. Delete apostrophes (`'`, `’`) with no replacement, so `Emperor's` → `emperors` rather than
   `emperor s`.
3. Delete bracketed groups: `\[[^\]]*\]`, `\{[^}]*\}`, and the literal `(penumbra)`.
4. Strip a trailing install/version suffix, matching **only** the two forms that actually occur:
   `(?:_\d+)+$` (Penumbra's own `_1_1_0` dealt suffix) and `[ _\-.]v\d+(?:[._]\d+)*$` (an author's
   `v2.1`). Deliberately *not* a general "trailing digits" rule — that would destroy `Gear 2000`.
5. Replace every character that is not a Unicode letter, Unicode digit, or `+` with a single space.
   `+` is preserved explicitly because it is semantically load-bearing in this ecosystem (`Bibo+`,
   `YAB+`). Accented and non-Latin characters are Unicode letters and are preserved (`Café` → `café`).
6. Collapse whitespace runs to one space; trim.

| Input | Output |
| --- | --- |
| `Bibo+ Medieval (Penumbra)_1_1_0` | `bibo+ medieval` |
| `Bibo+  Medieval` | `bibo+ medieval` |
| `Bibo+ Medieval Redux` | `bibo+ medieval redux` (must **not** collide with the above) |
| `Emperor's New Fists` | `emperors new fists` |
| `[WIP] Foo-Bar` | `foo bar` |
| `My Mod v2.1` | `my mod` |
| `Gear 2000` | `gear 2000` (trailing digits preserved) |
| `Café Outfit` | `café outfit` |

Normalization is lossy in one direction only: it never invents a match that differing significant words
would rule out. Any future change to it changes which entries match in already-published templates, so
it is a `formatVersion` question, not a free bug fix.

## Matching and cardinality

Matching is exact on normalized name. Cardinality is defined explicitly, because both sides can have
duplicates:

- **One template entry → many local rows.** Every local row whose normalized name equals the entry's
  `n` receives that destination. This is intentional: two installs of the same mod (different versions,
  a duplicate directory) should both land where the author put it. Downstream leaf disambiguation is
  already handled by the existing `FinishProposals` tail.
- **Many template entries → one normalized name** is impossible in a *validated* template; the codec
  resolves in-document duplicates before the template is usable (see Validation).
- Ambiguous local groups (two or more local rows sharing one normalized name) are counted and surfaced
  in the preview, not hidden — they are the most likely source of a surprising result.

For every non-`Protected` row: a hit takes the entry's `f`; a miss takes whatever `fallbackStrategy`
computes for that row, with `folderLabels` applied to the `GetFolder` result. In both cases the leaf is
the **importer's own `row.Name`**, put through `PenumbraPathSemantics.FixName` by the existing
`BuildPath` — inheriting the leaf-is-`Name` rule settled in the workbook spec (never `Identifier`,
never the leaf currently in `CurrentPath`, which can carry a stale Penumbra-dealt `" (N)"` suffix into
a fresh folder) rather than re-deciding it.

Protected rows are excluded for free: `OrganizerState.Sort` already filters `!m.Protected`, and
`AssignManual` returns `false` for a protected row.

## Folder labels

`folderLabels` uses **longest-prefix matching on whole path segments**, not exact-key replacement:

- `{"Gear": "Equipment"}` rewrites `Gear` → `Equipment` *and* `Gear/Head` → `Equipment/Head`.
- Matching is on segment boundaries, so `Gear` never rewrites `Gearbox`.
- When several keys match, the longest (most segments) wins: with `{"Gear": "Equipment", "Gear/Head":
  "Equipment/Headgear"}`, `Gear/Head` → `Equipment/Headgear`.
- No key matches → the path is unchanged.

Exact-key-only matching was considered and rejected: an author renaming `Gear` would get
`Equipment` alongside an unrenamed `Gear/Head`, producing exactly the split tree this feature exists to
avoid. Rewriting is applied **once**, non-recursively — the output of a rename is never re-matched
against the label map, so a map cannot loop or cascade.

## Architecture

New units in `PenumbraOrganizer.Plugin/Organizer/Templates/`:

| Unit | Responsibility |
| --- | --- |
| `OrganizationTemplate`, `TemplateEntry`, `TemplateMetadata` | The document records |
| `ValidatedOrganizationTemplate` | A template that has passed schema + semantic validation. Only this type reaches `OrganizerState` |
| `ModNameNormalizer` | `Normalize(string)`, above |
| `TemplateBuilder` | State + inclusion sets + metadata → `OrganizationTemplate` (export) |
| `TemplateCodec` | Staged decode/encode, below |
| `TemplateStore` | Enumerate/read/write `templates/*.json` |
| `TemplatePlanner` | Validated template + rows → `TemplateApplicationPlan` (pure, non-mutating) |
| `TemplateWarning`, `TemplateWarningCode` | Structured diagnostics |

### One plan, used by both preview and apply

Preview and apply **must not** be two computations of the same answer. A preview that approximates
what apply will do is a bug generator: unmatched rows, fallback folders, label rewrites, ambiguous
groups, and final folder occupancy all affect the result, and none of them are visible from a
matched-rows-only map.

```csharp
// Pure. No mutation. Everything the preview shows and everything the apply writes.
public static TemplateApplicationPlan TemplatePlanner.Plan(
    ValidatedOrganizationTemplate template,
    IReadOnlyCollection<OrganizerModRow> rows,
    Func<string, string> canonicalizeCreator);

public sealed record TemplateApplicationPlan(
    IReadOnlyDictionary<string, string> DestinationFolders,   // identifier -> folder, every eligible row
    IReadOnlyDictionary<string, int> FolderCounts,            // folder -> rows landing there
    TemplateApplyReport Report,
    IReadOnlyList<TemplateWarning> Warnings);

public sealed record TemplateApplyReport(
    int ConsideredRows,
    int ProtectedRows,
    int RowsMatchedByEntry,
    int RowsPlacedByFallback,
    int TemplateEntriesMatched,
    int TemplateEntriesUnmatched,
    int AmbiguousLocalMatchGroups,
    int InvalidEntriesSkipped);
```

Row counts and entry counts are separate fields because they are separate numbers — 214 matched rows
can come from 190 matched entries.

### Applying is an eighth strategy on `OrganizerState`

The pin-and-disambiguate tail (`FinishProposals`, which exists because Penumbra discards `" (N)"`
suffixes on save — see `[[pathrenamefailed-cycle-fix]]`) is private, and skipping it would reintroduce
that bug. So application goes through the same private `Sort(folderSelector)` path every existing
strategy uses:

```csharp
// Consumes a plan the caller already built (and already showed the user). Returns the plan's
// report rather than a bare touched-row count -- the count is one of the report's fields.
public TemplateApplyReport ApplyTemplate(TemplateApplicationPlan plan);
```

The selector is a dictionary lookup into `plan.DestinationFolders`, returning the folder as `Primary`
with `Secondary` null (multi-segment `Primary` is already the norm — see `KnownFolder`'s comment about
`Gear/Feet`). Pinning, disambiguation, `Protected` filtering, and collision handling are inherited
unchanged, and a template apply is indistinguishable downstream from any other sort. Because the plan
was computed from the same rows, preview and result cannot diverge.

One supporting change to existing code, kept minimal: `TypeFolder` and the seven `SortBy*` methods take
an **optional** folder-label resolver (`Func<string, string>?`, default `null` → identity). Every
existing call site keeps its behavior and needs no edit.

## Validation

`TemplateCodec` decodes in five distinct stages, and nothing unvalidated ever reaches `OrganizerState`:

1. **Transport decode** — share-code prefix, base64, inflate (with the caps below).
2. **JSON deserialize** — well-formedness only.
3. **Schema validation** — `formatVersion`, required fields, `fallbackStrategy` is one of the seven.
4. **Semantic normalization** — re-normalize every entry's `n` (see below), resolve in-document
   duplicates, validate every path.
5. **`ValidatedOrganizationTemplate` construction.**

### Entry keys are untrusted input

A template's `n` values are claimed to be normalized, but they arrive from outside. The codec
**re-normalizes every `n`** at stage 4 rather than trusting or merely checking it. Re-normalization can
itself create in-document collisions, so it is followed immediately by the duplicate policy below.

### Duplicate and collision policy — one rule, both directions

The same rule governs in-document duplicates (import) and normalized-name collisions among the
author's own mods (export), because they are the same problem:

- **Colliding entries agree on destination folder** → emit/keep one, report
  `DuplicateEntry`. Deterministic and harmless.
- **Colliding entries disagree** → keep **none** of them, report `ConflictingDuplicateEntry` naming the
  group. On export the author resolves or excludes it; on import the affected mods fall through to the
  fallback strategy.

"Last entry wins" is explicitly rejected: JSON array ordering must never silently change meaning. An
earlier draft's "keep the first in Ordinal order" is also rejected — it publishes an arbitrary choice
between genuinely different intents.

### Every externally supplied path is validated

Not just `entries[].f`. The same segment validator (no empty segment, no leading/trailing separator, no
control characters, segment length within limit) is applied to every `folders` value, every
`folderLabels` **key and replacement value**, and the slug derived from `name` for a filename.

### Hard limits

Enforced during decode, since inflation succeeding does not make a document structurally sane:

| Limit | Value |
| --- | --- |
| Compressed input | 1 MB |
| Decompressed size (enforced *during* inflation) | 8 MB |
| Entries | 20,000 |
| Folders | 5,000 |
| `folderLabels` keys | 500 |
| Any single string | 512 chars |
| Path depth | 16 segments |
| Segment length | 128 chars |

Exceeding any limit rejects the document with the specific limit named.

## Export

`TemplateBuilder` takes explicit inclusion sets rather than deriving them:

```csharp
public static OrganizationTemplate Build(
    OrganizerState state,
    IReadOnlySet<string> includedIdentifiers,
    IReadOnlyCollection<string> includedFolders,
    TemplateMetadata metadata);   // name, author, description, fallbackStrategy, folderLabels
```

**`includedFolders` is required because empty folders are not discoverable from rows.**
`OrganizerState.KnownFolders` is built purely from mod `CurrentPath` parents
(`OrganizerState.cs:64-71`), so a folder holding no mods is invisible to it. The export screen seeds
the folder list from two sources: `KnownFolders`, plus the `Folders` dictionary in Penumbra's
`organization.json` — which does list every folder Penumbra knows, empty ones included, and which this
plugin already parses via `OrganizationJsonCodec` for orphaned-folder cleanup. If `organization.json`
is missing or unparseable, export degrades to `KnownFolders` alone and says so; it does not fail.

**Export reflects the *current applied* organization, not pending proposals** —
`SplitPath(row.CurrentPath).Folder`. This is deliberate ("here's how my library is organized"), and it
is a real trap otherwise: a user could sort, review a new structure, export, and unknowingly share the
old layout. The export screen therefore states it in the UI, and only this mode ships. Exporting a
proposed-but-unapplied organization is not supported in v1.

### Review-and-trim, before anything is emitted

Exporting a name→folder map publishes the author's entire mod list, which for this plugin's user base
routinely includes content they would not choose to broadcast. Export opens a review screen first:
every mod name that would be included, a search box, per-mod checkboxes, per-folder include/exclude,
live in/out counts, and any `ConflictingDuplicateEntry` groups awaiting resolution. Nothing is written
or copied until the author confirms.

An excluded mod is simply absent from `entries` — importers treat it as unmatched and the fallback
places it. Exclusion cannot produce a malformed or half-valid template, which is what makes this screen
safe rather than a source of new failure modes. This screen is a safety mechanism, not polish: if it is
shipped incomplete, or bypassed by a "quick export" affordance added later, the feature publishes
users' full mod lists.

## Transports and template identity

1. **`.json` file.** Written to `templates/<slug>.json` under the plugin's config directory (the same
   directory the workbook and Apply backup files use), via the atomic write-to-`.tmp`-then-replace
   pattern `Plugin.cs`'s `WriteBackup` already uses. The **document's internal `name` is authoritative
   for display**; the filename is only storage. `TemplateStore` generates a safe unique filename
   (slugified `name`, suffixed `-2`, `-3`… on collision) and never overwrites an existing file
   implicitly, so two templates sharing a `name` coexist and an import can never clobber a template
   already on disk.
2. **Share code on the clipboard.** `POT1:` + base64(deflate(utf8 json)). Export copies it; import
   reads the clipboard.

**A stated limitation, surfaced in the UI rather than hidden:** a Discord message caps at 2000
characters, roughly 100 entries after compression. A real 900-mod library will not fit. Export displays
the encoded length and, past the threshold, tells the author plainly to share the `.json` file instead
of emitting a code that will be truncated on paste. The code path stays useful for small, hand-curated
templates.

## UI

A new **Templates** tab:

- **Available templates** — everything in `templates/`, each with `name`, `author`, `description`, and
  entry count. Selecting one shows a preview built from `TemplatePlanner.Plan`: the folder tree with
  live per-folder counts, the full report, and any warnings — before anything is applied.
- **Import from file** — reuses the existing `FileDialogManager.OpenFileDialog` already wired in
  `MainWindow` for workbook import (`MainWindow.cs:57`, `:781`), filtered to `.json`. The validated
  document is copied into `templates/` under a generated filename. An **Open Templates Folder** button
  sits alongside it for users who prefer to manage files directly. "Save the file to this path
  yourself" is not the primary flow — Discord downloads do not land there and most users do not know
  the plugin config path.
- **Import from clipboard** — decode, validate, show the same preview, apply on confirm.
- **Export** — the review-and-trim screen (metadata, fallback strategy, folder-label editing, mod
  search, per-mod and per-folder inclusion, live counts, collision resolution, encoded-length
  guidance), then writes the file and/or copies the code.

Applying a template produces ordinary proposals, reviewed on the existing Review Changes tab and
applied through the existing Apply pipeline. No new write path, no new gating rule, no new Penumbra IPC.

## Error handling

Import refuses rather than partially applies, and no `OrganizerState` mutation happens until the whole
document has validated. `TemplateWarning` carries a structured `TemplateWarningCode`
(`UnknownFolderLabelKey`, `InvalidEntryPath`, `DuplicateEntry`, `ConflictingDuplicateEntry`,
`ExportNameCollision`, `UnmatchedTemplateEntry`, `AmbiguousLocalMatch`) plus its subject, so the UI
formats consistently and tests assert on codes rather than prose.

**Fatal (refuse the document):** unknown `formatVersion`, naming the version found and those supported;
missing `POT1:` prefix, invalid base64, invalid deflate, malformed JSON, each naming the stage that
failed; `fallbackStrategy` not one of the seven; any hard limit exceeded.

**Non-fatal (warn, continue):** unknown `folderLabels` key (most likely a future category vocabulary) —
ignore that mapping; an `entries` destination with invalid path segments — skip the entry, count it;
duplicate/conflicting entry groups per the policy above. `TemplateStore` ignores an unreadable or
invalid file in `templates/` rather than failing enumeration, surfacing its name as a warning.

Every import reports the full `TemplateApplyReport`, so "matched 214 of 900 rows, 686 by fallback, 12
template entries matched nothing" is visible before the user goes near Apply.

## Data flow

`OrganizerState` remains the single in-memory model. `TemplateBuilder` is a one-way read out of it.
`TemplatePlanner` is pure and reads rows without mutating. `OrganizerState.ApplyTemplate` writes
through the same private `Sort`/`FinishProposals` path every existing strategy uses.
`TemplateCodec`/`TemplateStore` never see `OrganizerState`, Penumbra IPC, or a mod path — only the
document. Nothing here touches `mod_data.db`, `organization.json` writes, or `SetModPath`.

## Implementation phasing

Three phases, so the privacy-sensitive export surface is never rushed to complete an initial release:

- **T1 — format and application core.** Models, normalizer, validation, codec, planner,
  `OrganizerState` integration, unit tests, fixtures from two synthetic libraries. No UI.
- **T2 — file import and preview.** `TemplateStore`, JSON file import via the existing dialog manager,
  available-templates list, preview tree, reports and warnings, proposal generation. Delivers the core
  user value — importing someone's template — without exposing anyone's mod list.
- **T3 — export and clipboard sharing.** Review-and-trim UI, row and folder inclusion, collision
  resolution, atomic export, share-code encode/decode, encoded-length guidance.

## Testing

Per this repo's convention (pure logic unit-tested, IPC/file glue verified in-game):

- **`ModNameNormalizer`** — the table above verbatim, including the non-colliding pair and `Gear 2000`.
- **`TemplateBuilder`** — exclusions honored; ancestor folders collected; an explicitly included empty
  folder present in `folders`; a folder whose every mod was excluded still present; collision policy
  (agreeing → one entry + `DuplicateEntry`; disagreeing → none + `ConflictingDuplicateEntry`).
- **`TemplateCodec`** — round-trip equality; rejection for bad prefix, bad base64, bad deflate,
  malformed JSON, unknown `formatVersion`, unknown `fallbackStrategy`, and each hard limit; a document
  whose `n` values are *not* normalized is re-normalized rather than trusted; re-normalization that
  creates a conflicting collision drops the group.
- **Path validation** — invalid segments rejected in `entries[].f`, `folders`, `folderLabels` keys and
  values, and the filename slug.
- **Folder labels** — prefix rewrite (`Gear` rewrites `Gear/Head`); segment boundary respected (`Gear`
  does not rewrite `Gearbox`); longest key wins; rewrite output is not re-matched.
- **`TemplatePlanner`** — hit, miss, one entry matching several local rows, an ambiguous local group
  counted, an entry matching nothing counted, every report field distinct and correct.
- **`OrganizerState.ApplyTemplate`** — result equals the plan that was previewed; matched rows land in
  the entry's folder; unmatched land per the named strategy; labels apply to both halves; `Protected`
  untouched; leaf is the local `row.Name`; two rows resolving to the same folder and leaf are
  disambiguated by the existing tail.
- **`TemplateStore`** — enumerates valid files, ignores an invalid one while returning the rest,
  generates a unique filename on slug collision, never overwrites implicitly.

**In-game verification requires two libraries** — export from one install, import into another — so
unlike prior items in this repo it cannot be self-verified. A Discord tester should be lined up before
the work starts rather than at the end.

## Decisions and trade-offs

- **Fallback strategy is declared by the author, not learned from the matched mods.** Learning (take the
  most common folder matched mods of each category landed in) was rejected in favor of explicit
  declaration. Known cost: a strategy's generated folder names need not coincide with the template's
  own, producing two competing structures in one tree. Mitigated — not eliminated — by running the
  fallback through the template's `folderLabels`, so both halves draw on one vocabulary. Residual risk:
  a template whose `entries` use names no strategy would generate (e.g. `Characters`) still shows
  unmatched mods elsewhere. Visible in the preview and the report before Apply.
- **Matching is on display `Name`, not `Identifier`.** Directory identifiers carry install-time version
  suffixes and are renamed freely, so an identifier-first scheme degrades to the name path in practice
  while adding a second mechanism to explain and maintain.
- **Both transports ship.** The clipboard code is the low-friction path people want for Discord; the
  file is the only path that works at real library size.
- **Export reflects applied, not proposed, organization** — see Export.

## Open risks

1. **Match rate is unknown until real templates exist.** If typical overlap between two libraries is
   low, most of an importer's tree comes from the fallback and the template contributes less than the
   feature promises. The report counts make this honest rather than hidden, but the value proposition
   is unvalidated until a real template is exchanged between two testers.
2. **Normalization is a compatibility surface** — see Name normalization.
3. **Scope is medium, not small.** The UI alone spans a template list, preview tree, match analysis,
   two import paths, and an export screen with metadata editing, label editing, search, dual inclusion
   sets, live counts, collision resolution, and code-length guidance. The T1/T2/T3 phasing exists
   because treating this as "six units and a tab" is how the export screen ends up rushed.

## Revision notes

The first draft went through an external design review before implementation. Adopted, folded into the
sections above:

- **The normalizer contradicted its own example.** "Collapse punctuation" would have turned
  `Bibo+ Medieval` into `bibo medieval`, not `bibo+ medieval`. Replaced with an exact six-step
  algorithm, an explicit `+` carve-out, stated apostrophe/accent/bracket handling, and a worked table.
  Also tightened version-suffix stripping to the two real forms, since a general trailing-digit rule
  would have destroyed names like `Gear 2000`.
- **Empty folders were unobtainable from the stated builder inputs.** `KnownFolders` derives purely
  from mod paths, so a folder with no mods is invisible. `TemplateBuilder` now takes an explicit
  `includedFolders` set, seeded by the export screen from `KnownFolders` plus `organization.json`'s
  `Folders` dictionary — real data the plugin already parses for cleanup, rather than the manual
  folder entry the review proposed as one option.
- **Preview and apply would have been two different computations.** Introduced the pure
  `TemplatePlanner.Plan` → `TemplateApplicationPlan`, consumed by both, so an approximate preview is
  structurally impossible. `ApplyTemplate` now takes the plan rather than recomputing.
- **Match cardinality was undefined** and the report conflated matched *rows* with matched *entries*.
  Defined one-entry-to-many-rows explicitly, and split the report into eight named fields.
- **The collision policy contradicted itself** ("keeping neither silently" / "keeps the first"). The
  review's stronger point was adopted too: colliding names that disagree on destination now drop the
  whole group rather than publishing an arbitrary pick.
- **`folderLabels` matching was unspecified.** Settled on longest-prefix, segment-boundary matching,
  applied once and non-recursively, over exact-key-only.
- **Path validation covered only `entries`.** Extended to `folders`, `folderLabels` keys and values,
  and the filename slug, plus a concrete table of hard limits enforced during decode.
- **Entry keys were treated as trustworthy.** They are external input; the codec now re-normalizes
  every `n` and applies the duplicate policy to the result.
- **File import was "save it to this path yourself."** Corrected to reuse Dalamud's
  `FileDialogManager`, already wired in `MainWindow` for workbook import, plus an Open Templates Folder
  button. (The review framed this as adding a picker; the plugin already has one — `.csproj:14` records
  that WinForms was abandoned for exactly this.)
- Adopted as-is: provenance metadata, staged decode with a `ValidatedOrganizationTemplate` type,
  structured `TemplateWarningCode`s, explicit template identity/filename semantics, explicit
  applied-vs-proposed export semantics, and the T1/T2/T3 phasing.

Considered and not adopted:

- **An open-ended list of additional structural limits.** Replaced with a concrete, testable table —
  an unbounded "define limits for…" list invites limits nobody can assert on.
- **Rejecting a template whose `n` values are not already normalized.** Re-normalizing is more
  tolerant of hand-written templates, and the duplicate policy already handles the collisions
  re-normalization can create, so rejection adds a failure mode without adding safety.
