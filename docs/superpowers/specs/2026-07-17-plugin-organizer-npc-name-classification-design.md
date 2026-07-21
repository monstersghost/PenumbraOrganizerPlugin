# Design: NPC/enemy/boss name-based classification

**Status:** design approved by user in conversation, awaiting written-spec review.
**Depends on:** `docs/HANDOFF_NPC_CLASSIFICATION.md` (all research/evidence for this design lives
there — this doc is the design, not a restatement of the investigation).

## Context

### The confirmed problem

`ModTypeClassifier` (`PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`)
classifies purely from Penumbra's `GetChangedItems` output — never the mod's own display name. That
works for one NPC shape already: a `(NPC, id, slot)` key suffix (`ChangedItemKeyShape.Npc`, parsed by
`ChangedItemKeyParser`) resolves to `Category: NPC` today, unchanged by this design.

It does **not** work for mods that replace one specific named character's own unique face/skin
sculpt. Real, confirmed `GetChangedItems` data for two such mods (captured 2026-07-16 through this
plugin's own scan, not the old standalone app):

- A Y'shtola-specific face/skin overhaul → Changed Items: `Miqo'te Female Face 201`, `Miqo'te Female
  Face 204`, `Miqo'te Female Skin Textures`. Classifier result: `Category: Face`.
- A Thancred-specific face overhaul → Changed Items: `Midlander Male Face (Iris) 219`, `Midlander
  Male Face 219`, `Midlander Male Skin Textures`. Classifier result: `Category: Face`.

Both report only generic race/gender-keyed customization slots — identical in shape to an ordinary
player face replacer for that race/gender. The character identity ("Y'shtola", "Thancred") exists
nowhere except the mod's own display name. **No structural signal exists to catch these; the only
remaining signal is the mod's name.**

Also confirmed in the same batch, as a control case: `Slightly Better Alphinaud` / `Slightly Better
Alisaie` structurally affect a real shared Gear item (`Didact's Coat`, `Augmented Classical Medicus's
Wrist Torque`) that any player or NPC wearing that item would have replaced. These classify as
`Category: Gear` today and that is correct per the user's own confirmation — not a bug. This case is
the reason a name-based signal is risky in general (a mod named after an NPC can still be a
legitimate shared-item mod) — see "Accepted trade-off" below for how this design resolves the
tension anyway.

### Constraints carried over from prior decisions

- `ModTypeClassifier`'s own doc comment: "never guesses: anything unrecognized is
  `ClassificationResult.Unknown`." A name-based heuristic is a deliberate, explicit exception to that
  philosophy, scoped as narrowly as this design allows (see Non-goals).
- The user does not want specific NSFW mod titles used as reference material in docs/specs — see
  memory `npc-content-reference-preference`. This spec and its plan must cite bare character/NPC
  names only, never the specific mod titles from the original research corpus.
- Existing on-disk storage convention: the plugin already writes its own data files to
  `PluginInterface.ConfigDirectory.FullName` (`organizer-workbook.xlsx`, `organizer-export.txt`,
  `organizer-backup.json`, `organizer-folder-backup.json`) — this design follows the same pattern
  rather than inventing a new location.
- The plugin's actions are synchronous today (`RunScan()`, `ApplyChanges()`, `ExportWorkbook()`,
  `ImportWorkbook()` all run to completion on the calling thread, errors surfaced through a
  `_lastError` field that `MainWindow.cs` renders in red — there is no existing "success status"
  display anywhere in the plugin, only silence-on-success or a red error line). The scan-time name
  check stays synchronous (it's pure in-memory string matching). The new network refresh action is a
  deliberate, narrow exception to the synchronous convention — see Architecture §5.
- Existing atomic-write precedent: `Plugin.cs` already writes both the exported workbook
  (`ExportWorkbook`, temp file + `File.Move(overwrite: true)`) and the folder-cleanup backup
  (`BackupFilePath`, same pattern) via a temp-file-then-replace sequence, never a direct in-place
  write. This design reuses that exact pattern for the name-list file.

## Goal

Flag mods whose display name matches a known NPC, enemy, or boss name for **manual review** — routed
into a distinct folder location, never silently auto-organized as if fully classified — using a
curated, periodically-refreshed name list as the signal, since no structural signal exists for this
class of mod.

## Non-goals

- **Not a confidence/certainty system.** A name match is binary: either the mod's display name
  contains a known name as a whole word, or it doesn't. No partial-match scoring, no fuzzy/Levenshtein
  matching.
- **Not a complete or authoritative NPC database.** The list will never cover every NPC/enemy/boss in
  the game, and isn't trying to. It's explicitly a best-effort trail, refreshed occasionally, not
  continuously maintained.
- **Not automatic classification.** A match never claims certainty the way `Category: Gear` from a
  real Gear key does — it exists specifically to route a mod to a folder a human will open and check,
  not to make a final organizational decision unattended.
- **No live network access during scanning.** `RunScan()` stays fully offline; only the explicit
  "Refresh NPC list from wiki" button touches the network.
- **No solving the `(Child)` race-variant classifier gap.** That's a separate, already-tracked,
  unrelated bug (memory `child-race-variant-classification-gap`) — out of scope here.

## Accepted trade-off: name match overrides everything, including Gear

This was explicitly discussed and confirmed with the user: the name check sits **above** every
existing rule in `Classify`, including Rule 0 (Smallclothes/Emperor's New Clothes placeholders,
previously documented as "an unconditional override... ahead of every other rule") and Rule 1
(Gear/Mount/Minion/NPC-suffix). Concretely, this means a mod like `Slightly Better Alphinaud` —
already confirmed to be correctly and legitimately classified as `Gear` today — would flip to
`Category: NPC` under this design, purely because "Alphinaud" appears in its name, even though
structurally nothing changed about what the mod actually affects.

This is a deliberate, informed choice, not an oversight: the user was shown this exact consequence
and confirmed they want the name check to win regardless. The reasoning is that routing to a review
folder is a much lower-cost outcome than it looks — a false positive here just means a human opens
the `NPC/*` folder and moves the mod back out, not a wrong permanent decision. Optimizing for "never
miss a genuinely NPC-bound mod" was judged more valuable than "never re-flag an already-correct
Gear mod."

## Architecture

### 1. Classifier priority change

`ModTypeClassifier.Classify` stays a static method on a static class (consistent with
`ModTypeFolders`/`ChangedItemKeyParser`), but now takes the matcher as an explicit parameter rather
than reaching for global/cached state implicitly:

```csharp
public static ClassificationResult Classify(
    string modName, IEnumerable<string> changedItemKeys, NpcNameMatcher npcNameMatcher)
```

`ClassificationResult` gains a `ClassificationSource` so callers (UI, logging, exports) can tell a
structurally-confirmed result apart from a name-heuristic one without inferring it from whether
`SubCategory` happens to be null:

```csharp
public enum ClassificationSource { Structural, NameHeuristic, Unknown }

public sealed record ClassificationResult(
    ModCategory? Category, string? SubCategory, ClassificationSource Source)
{
    public static readonly ClassificationResult Unknown = new(null, null, ClassificationSource.Unknown);
}
```

New rule order (name check first, everything else unchanged below it):

```
0. Name match (NEW) — modName contains a known name, whole-word, case-insensitive
   -> Category: NPC, SubCategory: "NPCs" | "Enemies" | "Bosses", Source: NameHeuristic
1. Known equipment placeholders (Smallclothes / Emperor's New Clothes) -> Body, Source: Structural
2. Gear / Mount / Minion / NPC-suffix (existing Rule 1) -> Source: Structural
3. Action / Emote / Animation / VFX -> Source: Structural
4. Housing -> Furniture, Sound -> Source: Structural
5. Customization fallback -> Face / Hair / Body / Skin -> Source: Structural
6. Unknown
```

The one call site, `Plugin.cs:101` (`RunScan()`), already has `mod.Name` available. The matcher is
built once per scan (see §2) and threaded through:

```csharp
var npcNameMatcher = NpcNameMatcher.Load(NpcNameListPath); // once per RunScan(), not per mod
var rows = modList.Select(mod =>
{
    var changedItemKeys = allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
        ? changedItems.Keys
        : Enumerable.Empty<string>(); // name check doesn't depend on changed-items data at all
    var classification = ModTypeClassifier.Classify(mod.Name, changedItemKeys, npcNameMatcher);
    ...
});
```

### 2. Name matching (`NpcNameMatcher`, new)

**Matching mechanism — combined regex per category, not per name.** A separate compiled `Regex`
object per known name doesn't scale: a full wiki scrape across three categories could realistically
reach the low thousands of names, and constructing/compiling thousands of individual `Regex` objects
is real, measurable overhead (independent of how cheap any single match is). Instead, each category
builds exactly **one** regex — an alternation of every name in that category, each escaped via
`Regex.Escape`, sorted longest-first so a longer name is preferred over a shorter one it contains:

```csharp
var alternatives = names.OrderByDescending(n => n.Length).Select(Regex.Escape);
var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", alternatives)})(?![\p{{L}}\p{{N}}])";
var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

Three regex objects total per matcher instance, built once per `RunScan()` and reused for every mod —
compiling three (potentially large-alternation) patterns once per scan is negligible; it's compiling
thousands of *separate* pattern objects that would not be.

**Boundary definition.** `\b` is a `\w`/`\W` transition, not a linguistic word boundary — it treats
underscore as a word character, which doesn't match intent (`"_Zenos_"` should not match `Zenos`).
Boundaries are defined explicitly instead as "not adjacent to a Unicode letter or digit":
`(?<![\p{L}\p{N}])NAME(?![\p{L}\p{N}])`, shown above. This also correctly handles multi-word names
("Feo Ul") and internal punctuation ("Y'shtola", "Kan-E-Senna") without any custom tokenizer.

**Normalization, applied to both the list on load and the mod name at match time:**
- Case-insensitive (`RegexOptions.IgnoreCase`).
- Unicode NFC normalization (`string.Normalize(NormalizationForm.FormC)`).
- Curly apostrophes (`’`, U+2019) normalized to straight (`'`, U+0027) before matching, so a wiki
  title and a mod title using different apostrophe glyphs for the same name still match. This is
  character normalization, not fuzzy/approximate matching — doesn't conflict with the Non-goals.

**Multi-list priority.** If a mod's name matches entries in more than one category, priority is
**NPCs > Bosses > Enemies** — deterministic, not user-configurable. The matcher returns which specific
name and category matched, not just the winning category, so the result stays debuggable:

```csharp
public enum NpcNameKind { Npc, Enemy, Boss }
public sealed record NpcNameMatch(string Name, NpcNameKind Kind);
```

(`NpcNameKind` is the typed internal representation; it's mapped to the existing `string? SubCategory`
field — `"NPCs"`/`"Enemies"`/`"Bosses"` — only at the point `ClassificationResult` is constructed, so
nothing else that already reads `SubCategory` as a string needs to change.)

### 3. `ModTypeFolders.GetFolder` generalization

Current implementation hardcodes any non-null `SubCategory` to nest under a literal `"Animation and
VFX"` parent folder, regardless of the actual `Category` — that was only ever correct for the
Animation/VFX pairing. Rather than a fully open-ended `$"{category}/{subCategory}"` fallback (which
would silently accept nonsense combinations a classifier bug could produce, e.g. `Gear` +
`"Bosses"`), the valid combinations are enumerated explicitly and anything else fails fast:

```csharp
public static string GetFolder(ModCategory category, string? subCategory) => (category, subCategory) switch
{
    (_, null) => category.ToString(),
    (ModCategory.Animation or ModCategory.VFX, _) => $"{AnimationVfxParent}/{subCategory}",
    (ModCategory.NPC, "NPCs" or "Enemies" or "Bosses") => $"{ModCategory.NPC}/{subCategory}",
    _ => throw new ArgumentOutOfRangeException(
        nameof(subCategory), $"Unsupported subcategory '{subCategory}' for {category}."),
};
```

This is what makes `Category: NPC, SubCategory: "Bosses"` produce the folder `NPC/Bosses` instead of
incorrectly routing into `Animation and VFX/Bosses`, while catching any future classifier bug that
produces an unexpected category/subcategory pairing during development instead of silently emitting a
wrong folder path.

Existing structural NPC-suffix matches (`ChangedItemKeyShape.Npc`) keep `SubCategory: null` —
unchanged — so they land at the folder root `NPC/`, naturally distinguishing "Penumbra itself
confirmed this targets an NPC" (root) from "name-heuristic flagged this for review" (`NPC/NPCs`,
`NPC/Enemies`, `NPC/Bosses`).

### 4. List storage & format

File: `Path.Combine(PluginInterface.ConfigDirectory.FullName, "npc-name-list.json")`.

```json
{
  "Version": 1,
  "NPCs": ["Y'shtola", "Thancred", "Alphinaud", "Feo Ul"],
  "Enemies": ["Titania", "Garuda"],
  "Bosses": ["Zenos", "Shinryu"],
  "Excluded": []
}
```

- **`Version`** — present from the start so a future format change can detect and migrate old files
  instead of guessing from shape. This design writes `Version: 1` and rejects (falls back to seed,
  logs a warning) any file with an unrecognized version rather than trying to interpret it.
- **`Excluded`** — names that must never be re-added by a refresh. The "curated list" goal and the
  "additively harvest everything from three wiki categories" mechanism are in real tension: a wiki
  category can contain disambiguation pages, redirects, subcategory entries, or generic short titles
  that produce a bad entry. Since refresh never removes anything, a bad import would otherwise be
  effectively permanent (deleted by hand, then silently re-added on the next refresh). Adding a name
  to `Excluded` (a manual edit of the file, not a UI feature in this phase) is what actually removes it
  for good; refresh must check `Excluded` before merging any name into a category array.
- Duplicate names across category arrays are allowed if the wiki genuinely lists a name under more
  than one category (e.g. a primal appearing in both Enemies and Bosses) — the arrays are not
  deduplicated against each other, and the NPCs > Bosses > Enemies priority resolves it at match time,
  not at storage time. Within a single array, exact duplicates (post-normalization) are not stored
  twice.
- Persisted names are trimmed, rejected if blank, and capped at a sane maximum length (128 chars) —
  guards against a malformed scrape producing garbage entries. Arrays are written in a deterministic
  sort order so repeated writes without actual content changes produce no diff.
- File encoding: UTF-8 without a BOM, consistently.

Seed content ships as an embedded resource bundled with the plugin. On first run, if the on-disk file
doesn't exist, the plugin writes the seed content out to that path. After that, the on-disk copy is
authoritative for all future scans; the plugin never wholesale-overwrites it again (only the additive
refresh below ever modifies it).

Building the actual seed list content (curating names from the three wiki categories) is
implementation-phase work, not part of this design.

### 5. Refresh flow (new, manual-only, asynchronous)

New button, **Sort tab**, near the existing Sort-by-Type controls: **"Refresh NPC list from wiki"**.

**Why this one action breaks from the plugin's synchronous convention:** every other action
(`RunScan`, `ApplyChanges`, workbook export/import) is bounded, local file/IPC work. This action makes
multiple paginated HTTP requests to an external site, with unbounded and unpredictable latency. Dalamud
plugins render through `UiBuilder.Draw`, which runs on the game's render thread — blocking it on
network I/O would visibly freeze the game, not just the plugin window. This is a deliberate, narrow
exception, not a general shift to an async architecture:

```csharp
private async Task RefreshNpcNamesAsync(CancellationToken cancellationToken)
```

The button disables itself (and guards against duplicate clicks) while the operation is in flight, and
the result is written to a field `Draw()` already polls on the next frame — the same "background work,
UI reads a field" shape already used elsewhere in Dalamud plugins, just not yet in this one.

**Fetch/parse/merge sequence**, each category handled independently so one failing category doesn't
block the others:

1. Fetch `consolegameswiki.com`'s `Category:NPCs`, `Category:Enemies`, `Category:Bosses` pages, each
   with a short per-request timeout and an overall operation timeout, a real `User-Agent`, and only
   following `https://` redirects that stay on the same host (a redirect to a different host aborts
   that category as failed).
2. Follow "next page" pagination links per category with defensive termination: a visited-URL set (a
   repeated URL stops pagination for that category), a hard ceiling (100 pages), and treating a
   missing/ambiguous next-page link, a non-HTML response, or a missing category-member container as
   "stop, this category is done or failed" rather than looping.
3. Parse each fetched page with a real HTML parser (see below), extracting category-member page
   titles as candidate names.
4. Filter candidates against that category's `Excluded` list before considering them for merge.
5. Union the remaining new names into an **in-memory copy** of the currently-loaded document —
   **additive only, nothing already present is ever removed**, even if a name doesn't reappear on a
   re-fetch (guards against a parsing hiccup or a temporarily-failed page silently shrinking the list).
6. Serialize the in-memory copy to a temp file in the same directory, then `File.Move(tempPath,
   npcNameListPath, overwrite: true)` — the same temp-file-then-replace pattern `Plugin.cs` already
   uses for the workbook export and the folder-cleanup backup. If serialization or the move fails, the
   original on-disk file is untouched.
7. Report a result summary (see status reporting below), e.g. "Added 42 new names (NPCs: 12, Enemies:
   25, Bosses: 5)", or for partial failure, "Added 17 new names (NPCs: 12, Bosses: 5). Enemies failed:
   request timed out."

**Corrupted file at refresh time.** Scan-time corruption falls back to the seed in memory only, for
that session (see Error handling). Refresh is different because it's about to *write*: if the existing
file can't be parsed, refresh preserves it as `npc-name-list.corrupt-<timestamp>.json` (so nothing is
silently lost), starts the in-memory document from the bundled seed, merges whatever the fetch/parse
step successfully found, writes a new valid file, and reports that recovery happened. Without this
explicit rule, a naive implementation could deserialize a corrupt file to an empty object and then
"successfully" overwrite the user's real list with almost nothing.

**Status reporting.** `_lastError` is reserved for actual errors everywhere else in the plugin
(`MainWindow.cs` renders it in `DalamudRed`; every other action shows nothing at all on success — there
is no existing success/status display to "reuse"). Rather than introduce a plugin-wide status-kind
system with no other precedent, this feature gets one scoped field, `_npcRefreshStatus` (nullable
string, rendered in a neutral color, cleared when the button is clicked again), used only for this
button's result — including the partial-success case, which is a real outcome and not an error.
Anything that's a genuine unexpected failure (not merely "one of three categories timed out") still
goes through `_lastError`.

**Parsing approach:** add a small HTML parser library dependency (e.g. AngleSharp) to parse each page
as a real DOM and reliably extract category-member links and the pagination link, rather than
hand-rolled regex/string parsing against raw HTML. The project already added focused dependencies
(ClosedXML, Microsoft.Extensions.Logging.Abstractions) for the workbook feature, so this isn't a new
kind of decision, just a new specific package.

## Data flow summary

```
Scan time (unchanged trigger, offline, synchronous):
  RunScan() -> NpcNameMatcher.Load(npc-name-list.json) (once, cached for the scan)
            -> per mod: ModTypeClassifier.Classify(mod.Name, changedItemKeys, npcNameMatcher)
            -> name match found? Category=NPC, SubCategory=matched list, Source=NameHeuristic
               (structural NPC-suffix matches keep SubCategory=null, Source=Structural)

Refresh time (new, manual button, only network access in the plugin, asynchronous):
  Click "Refresh NPC list from wiki" -> button disables, guards duplicate clicks
    -> fetch + paginate + parse each of the 3 category pages independently (bounded: timeouts,
       page ceiling, same-host-only redirects)
    -> filter against Excluded, union new names into an in-memory copy of the document (additive,
       never removes)
    -> write via temp-file + atomic replace (existing Plugin.cs pattern)
    -> report result via _npcRefreshStatus (success, partial success, or corrupted-file recovery)
    -> next scan automatically picks up the updated list
```

## Error handling

| Situation | Behavior |
|---|---|
| `npc-name-list.json` missing (first run) | Seed from bundled embedded resource, write it out, proceed normally. |
| `npc-name-list.json` present but corrupted/unreadable at scan time | Fall back to the bundled seed list in memory for that session only (never crashes the scan, never touches disk); log a warning via `IPluginLog`. |
| `npc-name-list.json` has an unrecognized `Version` at scan time | Same as corrupted: fall back to the bundled seed in memory for that session, log a warning. |
| Refresh: network unreachable / a category page fails to fetch / times out | That category's merge is skipped; other categories that succeeded still merge and get written. Reported via `_npcRefreshStatus` as a partial result (e.g. "Enemies failed: request timed out"). On-disk file is only ever replaced by the atomic write in step 6 — a failed category simply contributes nothing to that in-memory copy. |
| Refresh: page fetched but markup/parse fails, or pagination hits a defensive limit (repeated URL, cross-host redirect, page ceiling) | Treated the same as a fetch failure for that category — logged, reported via `_npcRefreshStatus`, that category's merge is skipped, others proceed. |
| Refresh: existing file is corrupted, or has an unrecognized `Version`, when refresh runs | Both treated identically: preserved as `npc-name-list.corrupt-<timestamp>.json`, in-memory document restarts from the bundled seed, successfully-fetched names still merge, new valid file is written, `_npcRefreshStatus` reports that recovery occurred. |
| Refresh: serialization or atomic file replace fails | Original on-disk file is untouched (temp file is written first; replace is the last step). Reported via `_lastError` as a genuine failure. |

## Testing

- **`NpcNameMatcher` matching/normalization** — pure unit tests, no I/O: case-insensitivity, Unicode
  NFC equivalence, straight-vs-curly-apostrophe equivalence, the explicit Unicode-boundary regex
  (including cases `\b` would get wrong — `"_Zenos_"`, `"Zenos2"`, `"NotZenos"`, `"Zenos-themed"`),
  regex metacharacters inside an imported name (must be escaped, never break the pattern), multi-word
  names ("Feo Ul"), overlapping names ("Zenos" vs. a longer name containing it — longest-first
  alternation must prefer the longer one), and multi-list priority (NPCs > Bosses > Enemies) returning
  the correct `NpcNameMatch`.
- **`ModTypeClassifier` priority reordering** — extend existing tests: a name-matched mod resolves to
  `NPC`/`NameHeuristic` even when its changed-items keys would otherwise resolve to Gear, a known
  equipment placeholder, Body/Face/etc., or a structural NPC-suffix; a mod with zero changed-items keys
  still gets checked; a non-matching mod's classification is completely unaffected (every existing
  test still passes unchanged).
- **`ModTypeFolders.GetFolder` generalization** — unit tests for `NPC` + each valid subcategory
  alongside the existing Animation/VFX cases, and a test confirming an unsupported category/subcategory
  pairing throws rather than silently producing a folder path.
- **List persistence** — missing file seeds exactly once; a valid on-disk file is never overwritten
  wholesale; a corrupted file at scan time falls back in-memory without touching disk; a corrupted file
  at refresh time is preserved as a timestamped backup and recovered from seed; an atomic-write failure
  leaves the original file intact; `Excluded` entries are never re-added by a refresh; serialization is
  deterministic (repeated writes with no new data produce byte-identical output); an unrecognized
  `Version` is rejected safely rather than mis-parsed.
- **Wiki scraper/pagination parsing** — fixture-based tests against saved HTML snapshots of real
  category pages (same pattern as the workbook feature's fixture-based interop tests in
  `WorkbookInteropTests.cs`): multiple pages, a repeated next-page URL, a next-page link pointing off
  the configured host, a missing category-member container, a missing next-link on the final page,
  malformed HTML, a non-200 response, a successfully-empty category vs. a parse failure, duplicate
  members appearing across pages, and the page-ceiling guard. No live network calls in the automated
  test suite.
- **Perf sanity check** — one test asserting matcher construction + a full pass over a realistic mod
  count against a synthetic large name list (low thousands of entries) completes within a generous
  time bound, as a regression guard for the compiled-regex-per-name mistake this design avoided — not
  a formal benchmarking process.

## Open risks (carried forward, not blocking)

- The list will always be incomplete — this is accepted, not a defect to fix later.
- Wiki markup could change in a way that breaks parsing silently (mitigated by per-category
  independent failure handling and the additive-merge safety net, but not eliminated).
- The Rule-0-and-Gear-override trade-off means some legitimately-fine mods (like the two "Slightly
  Better ___" cases) will now show up in an `NPC/*` review folder every scan. This is accepted as
  the cost of not missing real cases, per the user's explicit confirmation.
- `Excluded` is a manual JSON edit in this phase, not a UI feature — acceptable for a low-frequency
  maintenance action, but worth revisiting if false positives turn out to be frequent enough to want a
  "exclude this" button directly in the review folder.
