# Plugin organizer, Phase 3: Workbook import/export — Design

**Status:** approved, not yet implemented. Revised 2026-07-16 after an external design review — see
"Revision notes" at the end for what changed and why.

## Context

`docs/ROADMAP.md`'s "Phase 3 (later, unscoped)" section bundles three unrelated parity features:
workbook import/export, a self-update pipeline, and public Dalamud plugin-repository submission. This
spec covers **workbook import/export only** — the other two are separate, not-yet-brainstormed
sub-projects with no dependency on this one.

The standalone app (`C:\Repo\PenumbraOrganizer`) already has a real, implemented, tested Excel-workbook
export/import feature (`PenumbraOrganizer.Infrastructure/Exports/WorkbookWorkflowService.cs`,
`PenumbraOrganizer.Core/Models/WorkbookWorkflowModels.cs`, covered by
`PenumbraOrganizer.Tests/Exports/WorkbookWorkflowTests.cs`). This is not a new format to invent — the
goal is for the plugin to interoperate with it, not replace or duplicate it.

Investigated and confirmed before writing this spec:

- **Identity.** The standalone app's `ModScanResult.StableScanId` is set to the mod's directory name
  (`PenumbraScanService.cs`) — identical in meaning and value to this plugin's `OrganizerModRow.Identifier`
  (also the Penumbra mod directory name). No reconciliation needed; the same string identifies the same
  mod on both sides.
- **Category vocabulary.** `PenumbraOrganizer.Core.Classification.ModCategory` — the exact 16-value enum
  the workbook's "mod type" column and category-mapping sheet are built from — is already linked into
  this plugin's project (`PenumbraOrganizer.Plugin.csproj`, `<Compile Include>` from the sibling repo),
  and is the same type `OrganizerModRow.Category` already uses. Creator canonicalization
  (`ICreatorCanonicalizer`/`CreatorCanonicalizer.cs`) is linked the same way. Both are already unified
  between the two tools — no new vocabulary-mapping work needed.
- **The one real schema gap: folder-only vs. full-path.** The standalone app's `CurrentVirtualFolder`/
  workbook `destination` fields are **folder-only** — a mod's own leaf/display name is tracked
  separately (`mod.Path.SortName ?? mod.Name`, per `ModFileSystemSaver.CreateDataNodes`) and never
  appears in that field. This plugin's `OrganizerModRow.CurrentPath`/`ProposedPath` are **full paths
  including the leaf name**. Bridging this is the core of the adapter work below.
- **The leaf is `Name`, not `Identifier`.** Every existing sort strategy in `OrganizerState.cs`
  (`SortByCreator`/`SortByModType`/`SortByTypeThenCreator`/`SortByCreatorThenType`) builds a fresh
  `ProposedPath` via `BuildPath(..., row.Name)` — the leaf segment is always the mod's display `Name`,
  never `Identifier`. The one real export sample this spec's first draft was written against
  (`Tsar/Gear/Bibo+ Medieval (Penumbra)_1_1_0`) happened to have `Name == Identifier` for that specific
  mod, which is how the first draft over-generalized "leaf = `Identifier`" from a single coincidence.
  Corrected throughout below.
- **`WorkbookWorkflowService.BuildEditableSheet` never reads a proposal's destination.** Read the method
  in full: its `proposals` parameter (`IReadOnlyList<OrganizerModProposal>`) is used for exactly one
  thing — looking up `Protected` (`proposals.ToDictionary(p => p.StableScanId, p => p.Protected, ...)`).
  The workbook's "destination" column is *always* `BuildSuggestedDestination(mod, category,
  organizationPreferences)`, recomputed from a global `OrganizationStrategy` enum
  (`CreatorOnly`/`TypeOnly`/`TypeThenCreator`/`CreatorThenType`/`PreserveAndClean`/`Custom`) — it never
  reads any proposal's `ProposedVirtualFolder`, even though that field exists on the model. This means
  export does not need to translate each row's already-computed `ProposedPath` into a destination value
  at all; it only needs `(StableScanId, Protected)` pairs plus one `OrganizationPreferences.Strategy`
  value the user picks explicitly at export time (see UI) — this plugin has no persisted "current
  strategy" state to infer one from.
- **Identity hashes are reproducible on both sides, but not via the method the first draft named.**
  `installationIdentity` = SHA256(ConfigDirectory + ModRoot); this plugin already derives an equivalent
  `PenumbraConfigDirectory` (sibling-folder convention, already used for `organization.json` access), and
  `Penumbra.Api`'s `GetModDirectory` IPC call (confirmed present in the 5.15.1 surface) supplies the mod
  root. `scanIdentity` = SHA256 over sorted `(StableScanId, CurrentVirtualFolder)` pairs — reproducible
  once the folder-only split below is in place. **Both hash builders
  (`OrganizerSessionService.BuildScanIdentity`/`BuildInstallationIdentity`) call a `private static`
  `NormalizeForIdentity` method** — the plugin cannot call it as the first draft assumed. Worse: linking
  `WorkbookWorkflowService.cs` at all already requires `OrganizerSessionService.BuildScanIdentity`/
  `BuildInstallationIdentity` to be reachable, since the linked file calls them directly in
  `ValidateMetadata`/`ExportAsync`. `OrganizerSessionService` also implements `IOrganizerSessionService`
  (four more members — `SaveLastSessionAsync`/`TryLoadLastSessionAsync`/`DiscardLastSessionAsync`/two
  directory properties) that the plugin doesn't need. See Architecture for the resolution (extracting
  the three pure hash-building methods into their own file, not linking the whole session service).

## Goal

The plugin can export a `.xlsx` workbook and import one back, **schema- and behavior-compatible** with
the standalone app's existing workbook feature: a workbook produced by either tool can be imported by
the other without format conversion, and equivalent source state produces equivalent workbook semantics,
validation results, and resolved proposals. This is deliberately not "byte-for-byte identical" — `.xlsx`
is a ZIP package, and two exports of identical logical data can differ at the byte level (ZIP entry
timestamps, XML serialization ordering, generated calculation metadata) without that being a compatibility
problem. Byte comparison would make a brittle, misleading test; see Testing.

## Non-goals

- Self-update pipeline, public plugin-repository submission — separate Phase 3 items, not addressed
  here.
- Any change to the standalone app's workbook format, validation rules, or `.xlsx` structure. This spec
  adapts the plugin *to* the existing format; it never proposes changing the format itself.
- A plugin-native custom-sort-name concept (Penumbra's `SortName`, distinct from a mod's own `Name`).
  This plugin's model has no equivalent today — every sort strategy uses a row's `Name` as its path
  leaf. Import will do the same (see Architecture); introducing a first-class "rename this mod's leaf
  without moving it" concept is out of scope.
- Detailed gear-slot sorting. Unrelated and independently blocked (see `docs/ROADMAP.md`) — this spec
  does not touch category granularity, only how already-classified rows round-trip through a workbook.
- **Extracting a shared library/NuGet package/Git submodule between the two repos.** Considered during
  review and explicitly rejected — reopens the deliberate "no shared code between the two repos"
  decision (see `[[dalamud-plugin-decision]]`) that this same brainstorming session already confirmed
  should stand; file-linking (already used for `ModCategory.cs`/`CreatorCanonicalizer.cs`, extended here)
  solves the actual reuse need without it.
- **A background-thread/async execution model for export or import.** This plugin has no async
  orchestration anywhere today — `RunScan`/`ApplyChanges`/`CleanUpFolders` all run synchronously on the
  same call, and the Phase 2 Apply spec explicitly declined to add concurrency-guarding complexity for
  the same reason: ImGui's single-threaded draw loop already rules out the races that would justify it.
  A few hundred rows is a small write for ClosedXML. Revisit only if in-game verification actually shows
  a noticeable UI stall, and only as part of a plugin-wide threading decision, not a one-feature carve-out.
- A CI build pipeline. Neither this repo nor the standalone app has any build/test CI today (only a
  Discord release-notification workflow) — adding one is a separate, larger scope decision than this
  feature, not something this spec should smuggle in.

## Architecture

Link the standalone app's actual `WorkbookWorkflowModels.cs` and `WorkbookWorkflowService.cs` into the
plugin project via `<Compile Include>`, extending the exact pattern already used for `ModCategory.cs` and
`CreatorCanonicalizer.cs`:

```xml
<Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Models\WorkbookWorkflowModels.cs" Link="Linked\WorkbookWorkflowModels.cs" />
<Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Exports\WorkbookWorkflowService.cs" Link="Linked\WorkbookWorkflowService.cs" />
<Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Identity\ScanIdentity.cs" Link="Linked\ScanIdentity.cs" />
```

A new plugin-only class, `Organizer/WorkbookAdapter.cs`, translates between `OrganizerState.Mods` (this
plugin's model) and the standalone app's `ScanInventory`/`ModScanResult`/`OrganizerModProposal`/
`PenumbraInstallation` shapes, then calls the linked service's `ExportAsync`/`ImportAsync` unchanged. The
`.xlsx` reading/writing/validation logic itself is never duplicated — it is the same file, physically
present in both repos via the link, so it cannot drift out of sync with the standalone app's format.

This requires one new plugin dependency, `ClosedXML` (the NuGet package `WorkbookWorkflowService`
depends on), and one small shim: the linked service takes `Microsoft.Extensions.Logging.ILogger<T>`,
which this plugin doesn't otherwise use (it logs via Dalamud's `IPluginLog`). A minimal adapter class
wraps `IPluginLog` behind the `ILogger<WorkbookWorkflowService>` interface, forwarding to
`IPluginLog.Info`/`Warning`/`Error` — logging only, no behavior to get wrong.

### Prerequisite change in the standalone app repo: extract `ScanIdentity`

`WorkbookWorkflowService.cs` calls `OrganizerSessionService.BuildScanIdentity`/`BuildInstallationIdentity`
directly. Linking `WorkbookWorkflowService.cs` unmodified would therefore require also linking
`OrganizerSessionService.cs`, which implements `IOrganizerSessionService` (session save/load file I/O the
plugin has no use for) — a real, confirmed dependency cascade, not a hypothetical one.

Resolution: in the standalone app repo, extract the three pure, already-`public static` hash-building
methods (`BuildScanIdentity`, `BuildInstallationIdentity`, and the currently-`private static`
`NormalizeForIdentity`) out of `OrganizerSessionService` into a new file,
`PenumbraOrganizer.Core/Identity/ScanIdentity.cs`, class `ScanIdentity`. These methods have no I/O and no
dependency beyond `System.Security.Cryptography`/`System.Text`/`PenumbraOrganizer.Core.Models` — a better
architectural home in `Core` than `Infrastructure` regardless of this plugin's needs. `OrganizerSessionService`
is updated to call `ScanIdentity.BuildScanIdentity`/`BuildInstallationIdentity` internally (no behavior
change, same values, existing standalone-app callers and tests unaffected), and `WorkbookWorkflowService.cs`
is updated to call `ScanIdentity` directly instead of `OrganizerSessionService`. The plugin then links only
`ScanIdentity.cs` — no `IOrganizerSessionService`, no session file I/O, no cascade.

This is a small, additive, backward-compatible refactor confined to the standalone app repo, done as the
first task of the implementation plan, before any plugin-side code is written.

### Folder/leaf split (export) and recombine (import)

```csharp
public static class WorkbookAdapter
{
    // Splits "Tsar/Gear/Bibo+ Medieval (Penumbra)_1_1_0" into
    // ("Tsar/Gear", "Bibo+ Medieval (Penumbra)_1_1_0"). Root-level rows (no '/') split to ("", leaf).
    // Contract: accepts only a non-empty, '/'-separated Penumbra virtual path with no leading/trailing
    // separator and no empty segment; violating input is a caller bug (this plugin's own scan/sort
    // output already guarantees it), not a case to silently "fix".
    public static (string Folder, string Leaf) SplitPath(string fullPath);

    // Recombines a workbook-resolved destination folder with a leaf back into a full path.
    // "" + "Foo" -> "Foo"; "Tsar/Gear" + "Foo" -> "Tsar/Gear/Foo".
    // Contract: folder must be "" or a validated folder-only path (no leaf); leaf must be a single
    // segment (rejects a leaf containing '/').
    public static string JoinPath(string folder, string leaf);

    // (StableScanId, Protected) pairs only -- BuildEditableSheet never reads a proposal's destination
    // (see Context), so this is the entire per-row payload export needs beyond ToScanInventory.
    public static IReadOnlyList<OrganizerModProposal> ToProposals(OrganizerState state);

    public static ScanInventory ToScanInventory(OrganizerState state, PenumbraInstallation installation);

    // Maps a user-chosen OrganizationStrategy (see UI: the plugin has no persisted "current
    // strategy" concept -- every sort button is a stateless one-shot action -- so this must be an
    // explicit choice made at export time, not inferred from OrganizerState) to the standalone
    // app's OrganizationPreferences shape, for the export call's organizationPreferences argument.
    public static OrganizationPreferences ToOrganizationPreferences(OrganizationStrategy strategy);

    // Applies a successful WorkbookImportResult's rows back onto OrganizerState. For each row:
    // - if ResolvedDestination is not null, recombine it with the row's own Name (matching how every
    //   existing sort strategy builds a leaf -- NOT whatever leaf happened to be in CurrentPath, which
    //   could carry forward a stale Penumbra-generated duplicate suffix into a fresh target folder) via
    //   JoinPath, then call OrganizerState.AssignManual.
    // - Protected is applied unconditionally via OrganizerState.SetProtected, regardless of whether
    //   ResolvedDestination is null -- a workbook row can validly request a protection-only change with
    //   no destination edit (WorkbookImportRow.Protected is a non-nullable bool, always resolved via
    //   TryParseProtected's fallback-to-current-value logic; it must not be gated on a destination
    //   check).
    public static void ApplyImportResult(OrganizerState state, WorkbookImportResult result);
}
```

`SplitPath`/`JoinPath` are the inverse of each other and are the only new path-manipulation logic this
feature introduces — everything else is either linked-unchanged code or straightforward model mapping.
They deliberately do not reuse `OrganizationCleanupPlanner.GetVirtualParent` as-is: that method returns
`null` for a root-level path (a folder-cleanup-specific convention), while the workbook format wants
`""` for root, matching `WorkbookWorkflowService`'s own
`dbFolders.TryGetValue(directoryName, out var folder) ? folder : string.Empty` convention.

`ToScanInventory` builds a synthetic `ScanInventory` from `OrganizerState.Mods`: `StableScanId` =
`Identifier`; `CurrentVirtualFolder` = `SplitPath(CurrentPath).Folder`; `DetectedCategory` = `Category ??
ModCategory.Others`; `PhysicalDirectory` = `string.Empty`, `PhysicalDirectoryName` = `Identifier` (matching
the standalone app's own precedent for synthesizing `ModScanResult` from a lighter model, e.g.
`ApplyService.cs` setting exactly these two values the same way). Every other `ModScanResult` field the
linked service never reads (`Version`, `Website`, `Tags`, `Favorite`, etc.) gets its type default —
`WorkbookWorkflowService`'s export/import logic only ever touches `Name`, `Author`, `CurrentVirtualFolder`,
`StableScanId`, `Protected`, `DetectedCategory` (confirmed by reading the linked file in full).

`ToProposals` builds one `OrganizerModProposal` per row with only `StableScanId` and `Protected`
populated meaningfully (every other required field gets a placeholder — `ProposedVirtualFolder`, in
particular, is never read by the export path per the Context finding above, so it's set to
`CurrentVirtualFolder` as a harmless, self-consistent placeholder rather than inventing a fake value).

`PenumbraInstallation.ConfigDirectory`/`ModRoot` come from this plugin's existing `PenumbraConfigDirectory`
property and a new `GetModDirectory` IPC call respectively. `installationIdentity`/`scanIdentity` are
computed by calling the linked `ScanIdentity.BuildInstallationIdentity`/`BuildScanIdentity` directly (see
Prerequisite change above) — not reproduced by hand, so there is no normalization-drift risk between the
two tools.

`ApplyImportResult`'s per-row leaf reconstruction uses the row's current `Name` (looked up from
`OrganizerState` by `StableScanId`/`Identifier`), matching `OrganizerState.cs`'s own `BuildPath` convention
for every existing sort strategy — not `Identifier`, and not whatever leaf happens to currently sit in
`CurrentPath` (see Context: the leaf-is-`Name` correction, and the risk of carrying forward a stale
Penumbra-side duplicate-suffixed leaf into a brand-new target folder). Rows the linked service already
skipped (stale, invalid, errored) never reach this method — they're filtered out of
`WorkbookImportResult.Rows` by the linked validation before this plugin ever sees them; this is asserted
by a plugin-side test using a deliberately-stale fixture row, not merely assumed from reading the service
once (see Testing).

## UI

- **Export**: new "Export Workbook" button on the Review Changes tab, next to the existing plain-text
  Export button (which stays unchanged — different artifact: this one is a human-readable snapshot with
  no import path, the workbook is an editable round-trip document). Since this plugin has no persisted
  "current sort strategy" concept (every sort button is a stateless one-shot action — see Architecture),
  the Export button is paired with a small strategy dropdown (the same four options as the Sort tab's
  buttons, defaulting to whichever was last used this session, in-memory only) so the exported workbook's
  suggested destinations are computed from an explicit choice, not an inferred one. Clicking Export calls
  `WorkbookAdapter.ToScanInventory`/`ToProposals`/`ToOrganizationPreferences` then the linked
  `ExportAsync`, writing to a fixed path (`organizer-workbook.xlsx` in the plugin's config directory,
  matching the existing fixed-filename convention for Export/backup files) via the same atomic
  write-to-`.tmp`-then-replace pattern the Apply backup file already uses (`Plugin.cs`'s `WriteBackup`) —
  a failed export must not destroy a previously-good workbook. The UI shows the full resolved path and an
  "Open Containing Folder" affordance rather than a save dialog, since Dalamud has no built-in file-save
  picker and adding one is a separate dependency decision (see Import, below, for the file-*open* side,
  which does need a real picker).
- **Import**: new "Import Workbook" button on the Sort tab, alongside the existing sort-strategy buttons
  (By Creator, By Mod Type, Type/Creator, Creator/Type). Architecturally this *is* a fifth strategy: it
  opens a file picker for a `.xlsx` path — `System.Windows.Forms.OpenFileDialog` (available in-process to
  any .NET Dalamud plugin without a new NuGet dependency; confirm this during planning rather than adding
  an ImGui file-dialog package unless WinForms interop turns out to be unavailable in this plugin's
  runtime), then calls the linked `ImportAsync`, then `WorkbookAdapter.ApplyImportResult` on success.
  Imported rows flow through the exact same downstream `Validate()`/Apply/backup pipeline as every other
  strategy — no new write path, no new gating rule.
- After either action, render the linked service's own `Summary`/`Errors`/`Warnings` in the UI verbatim
  — no new error taxonomy to invent (see Error handling).

## Error handling

All validation is inherited from the linked `WorkbookWorkflowService` unchanged: unsupported format
version, installation mismatch (hard error — "this workbook belongs to a different Penumbra library"),
stale scan mismatch (soft warning — affected rows skipped, not the whole import), macro/ActiveX/
external-link package rejection, and the full set of per-row checks (stale current-folder, duplicate id,
unknown id, invalid protected value, invalid mod type, invalid destination, protected-row destination
conflict). The plugin surfaces the same `Errors`/`Warnings` lists and `Summary` string the standalone app
already produces and already has tests for.

New failure modes specific to the plugin adapter: `GetModDirectory` IPC unreachable (Penumbra not
running) — same try/catch pattern already established for `RunScan`/`ApplyChanges`, surfaced at the same
`MainWindow` call-site convention rather than a new exception type. A malformed/unreadable `.xlsx` file
(not a valid zip, wrong extension) is already handled by the linked service's own checks before this
plugin's code runs.

## Data flow

Same overall shape as Phase 2 Apply: `OrganizerState` remains the single in-memory model.
`WorkbookAdapter.ToScanInventory`/`ToProposals`/`ToOrganizationPreferences` are one-way, read-only
translations *out* of that model for export. `ApplyImportResult` writes *into* that model exactly the way
`AssignManual`/`SetProtected` already do for every other strategy — nothing about `OrganizerState`'s own
public surface changes. The linked `WorkbookWorkflowService` itself never touches `OrganizerState`,
Penumbra IPC, or any plugin file path directly; it only ever sees the synthetic
`ScanInventory`/`OrganizerModProposal` the adapter hands it, and returns data the adapter maps back.
Everything runs synchronously on the same call, same thread, as every other button-triggered action in
this plugin (see Non-goals re: threading).

## Testing

`WorkbookAdapter`'s translation functions are pure and unit-testable, following this repo's existing
convention (`ApplyPlanner`, `CollisionDisambiguator`, `OrganizationCleanupPlanner` are all pure/tested;
only IPC/file-I/O glue in `Plugin.cs` is untested, matching precedent):

- `SplitPath`/`JoinPath`: root-level path (no `/`) splits to `("", leaf)` and rejoins correctly; nested
  path splits/rejoins correctly; round-trip (`JoinPath(SplitPath(p).Folder, SplitPath(p).Leaf) == p`) for
  a representative set of real paths.
- `ToScanInventory`/`ToProposals`/`ToOrganizationPreferences`: confirms `StableScanId`/`Identifier`
  equivalence, confirms `CurrentVirtualFolder` is the folder-only split of `CurrentPath`, confirms a
  `null` `Category` maps to `ModCategory.Others`, confirms each of the plugin's four sort strategies maps
  to its matching `OrganizationStrategy` value.
- `ApplyImportResult`: given a `WorkbookImportResult` with a mix of resolved-destination,
  protection-only, and unresolved rows, confirms only resolved-destination rows call `AssignManual`,
  confirms the resulting `ProposedPath` recombines the resolved folder with the row's current `Name` (not
  `Identifier`, and not the workbook's own `ModName` column), and confirms `Protected` is applied
  unconditionally — including for a row with `ResolvedDestination == null`, directly covering the bug
  the design review caught.

The linked `WorkbookWorkflowService` keeps its existing coverage in the standalone app repo
(`WorkbookWorkflowTests.cs`) — not re-tested here, since it is the same file, not a copy. This plugin adds
a small number of fixture-based contract tests confirming the *interop boundary* specifically (not
re-covering validation branches the linked service's own tests already cover): a workbook fixture
generated by the standalone app for (a) a root-level mod, (b) a nested mod, and (c) a protection-only edit
with no destination change, each imported through the plugin adapter and asserted against the expected
`OrganizerModRow` state. A larger, fully exhaustive fixture matrix (every validation branch, every edge
case) was considered and trimmed — those branches already have coverage in `WorkbookWorkflowTests.cs`
against the same file this plugin links, so re-deriving them here would test the linked code twice without
adding real interop confidence.

In-game verification (deferred until the game is available again, like every other pending item in this
repo): export a workbook from a real library, confirm it opens correctly in Excel; open the same file in
the standalone app and confirm it's recognized as a valid workbook for that install; edit destinations,
import back into the plugin, confirm resolved `ProposedPath` values look correct in the Review Changes
tab and Apply behaves normally; separately, export from the standalone app and import into the plugin to
confirm the reverse direction; confirm `installationIdentity` actually matches between an IPC-derived and
a file-system-discovered path for the same real install (see Open risks).

## Open risks

1. **`ClosedXML` as a new plugin dependency.** Real size/load-time weight added to a Dalamud plugin,
   which currently isn't distributed via a public plugin repository (see `docs/ROADMAP.md`'s Phase 3 —
   distribution is a separate, unstarted item), so there's no current size-budget constraint to violate,
   but this should be revisited if/when public-repository submission is designed.
2. **`GetModDirectory` IPC and `PenumbraConfigDirectory`'s sibling-folder convention must resolve to the
   same string `ScanIdentity.NormalizeForIdentity` would derive from the standalone app's own
   file-system discovery**, or `installationIdentity` hashes won't match for the same real install even
   though both tools are looking at the same Penumbra instance and now call the identical hashing code.
   The hashing algorithm is no longer a drift risk (see Prerequisite change), but the *inputs* to it
   (exact config-directory and mod-root path strings) still come from two different discovery
   mechanisms and need in-game confirmation — case sensitivity, trailing separators, and IPC-vs-filesystem
   path differences are the likely failure points if this doesn't match on the first try.
3. **No plugin-side `SortName` concept (see Non-goals).** A workbook round-trip through the plugin will
   always use a mod's current `Name` as its path leaf, same as every existing sort strategy. If the
   standalone app ever writes a workbook whose destination implies a custom leaf rename (not currently
   part of the format per the linked service's own logic, which only ever edits the `destination`
   folder column), that distinction would be silently dropped on the plugin side. Worth re-checking
   against the linked file if the standalone app's workbook format changes to add leaf-renaming.

## Revision notes

The first draft went through an external design review before implementation started. Adopted, folded
into the sections above:

- "Byte-for-byte interoperable" was the wrong acceptance criterion for a ZIP-based format where identical
  logical data can still differ at the byte level. Replaced with a schema/behavior-compatibility goal
  (Goal).
- The first draft asserted every sort strategy uses a row's `Identifier` verbatim as its path leaf, based
  on over-generalizing from one real export sample where `Name` happened to equal `Identifier`. Verified
  against `OrganizerState.cs`'s actual `BuildPath` calls: the leaf is always `Name`. Corrected throughout
  (Context, Architecture, Non-goals, Open risks). The review's own proposed fix ("preserve whatever leaf
  is currently in `CurrentPath`") was considered and not adopted — that's inconsistent with every existing
  sort strategy (which always rebuild the leaf from `Name`, discarding whatever was there before) and
  risks carrying forward a stale Penumbra-generated duplicate-suffixed leaf into a fresh target folder.
- `ToProposals` was underspecified for how per-row destinations flow into export. Investigating this
  surfaced that the premise was wrong: `WorkbookWorkflowService.BuildEditableSheet` never reads a
  proposal's destination at all — only `Protected`, via a dictionary lookup. The export path always
  recomputes suggested destinations from a global `OrganizationPreferences.Strategy` enum. This simplified
  `ToProposals` (Architecture) rather than requiring the elaborate per-row mapping table the review
  proposed, since that table would have modeled a destination-passthrough mechanism that doesn't exist in
  the linked code.
- `ApplyImportResult`'s pseudocode coupled applying `Protected` to `ResolvedDestination is not null`,
  silently dropping a protection-only edit on a row with no destination change. Fixed: the two mutations
  are applied independently, using the verified real field name (`WorkbookImportRow.Protected`,
  non-nullable, always resolved — not a guessed `ResolvedProtected` nullable field).
- `installationIdentity`/`scanIdentity` normalization was going to be reproduced by hand against a
  `private static` method the plugin can't call. Investigating the fix surfaced a bigger, confirmed
  problem: linking `WorkbookWorkflowService.cs` already requires `OrganizerSessionService` to be
  reachable, which would cascade into linking `IOrganizerSessionService` and unrelated session-file I/O.
  Resolved by extracting the three pure hash-building methods into their own file
  (`PenumbraOrganizer.Core/Identity/ScanIdentity.cs`) in the standalone app repo — a small, additive,
  backward-compatible prerequisite change, not the new from-scratch shared-utility class the review
  proposed (which would have reimplemented rather than reused the algorithm).
- The `PhysicalDirectory`/`PhysicalDirectoryName` placeholder assignment was internally inconsistent — it
  named the correct precedent (`ApplyService.cs`'s `PhysicalDirectory = string.Empty` /
  `PhysicalDirectoryName = proposal.StableScanId`) but then described both plugin-side fields as
  `= Identifier`. Fixed to actually match the cited precedent (Architecture).
- The file-selection workflow was left as "an implementation detail for the plan," which the review
  correctly flagged as unresolved for import specifically (export can reasonably default to a fixed path;
  import needs the user to pick an arbitrary file). Settled: `System.Windows.Forms.OpenFileDialog` for
  import (to be confirmed available during planning), a fixed path with an "Open Containing Folder"
  affordance for export, and an atomic temp-then-replace write for export matching the Apply backup
  file's existing convention (UI).
- `SplitPath`/`JoinPath` now state an explicit input contract (reject malformed input rather than
  silently normalizing it) instead of leaving edge-case behavior unspecified (Architecture).

Considered and explicitly not adopted, with reasoning:

- **Extracting a shared library/NuGet package/Git submodule between the two repos** (the review's top
  recommendation for the sibling-repo build dependency). Directly reopens the "no shared code between
  the two repos" decision this same brainstorming session already made deliberately when approving
  file-linking over a shared-library approach for this exact feature. The narrower fix (extracting just
  `ScanIdentity`, see above) resolves the specific dependency-cascade problem the review found without
  reopening that decision.
- **A CI clean-checkout build requirement.** Neither repo has any build/test CI today (confirmed — only a
  Discord release-notification workflow exists in either repo's `.github/`). Requiring one as part of
  this feature would be a separate, much larger scope decision than workbook import/export, imposed on a
  project that hasn't needed it for any prior phase, including the `ModCategory.cs`/`CreatorCanonicalizer.cs`
  linking this feature extends.
- **A background-thread execution model with scan-generation staleness guards.** This plugin has no
  async orchestration anywhere; every existing IPC/file operation (`RunScan`, `ApplyChanges`,
  `CleanUpFolders`) runs synchronously on the same call, and the Phase 2 Apply spec explicitly declined
  similar concurrency-guarding complexity because ImGui's single-threaded draw loop already rules out the
  races it would guard against. A few hundred workbook rows is not expected to cause a noticeable stall;
  revisit only if in-game verification shows otherwise, and only as a plugin-wide threading decision, not
  a one-feature carve-out.
- **An exhaustive 13-fixture cross-tool contract test matrix.** Trimmed to three fixtures that exercise
  the plugin adapter's own translation logic specifically (root-level leaf, nested leaf, protection-only
  edit) rather than re-deriving coverage the linked service's own existing tests already provide for every
  validation branch (Testing).
