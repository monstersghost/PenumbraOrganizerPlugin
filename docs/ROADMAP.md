# Roadmap: beyond Phase 1c

Living document. Update the status line at the top of each phase as work lands — don't let this
drift out of sync with `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`. Scope guardrails from the
Phase 1 design (read-only MVP, no shared code beyond two linked files) still apply everywhere
below unless a phase explicitly says otherwise.

## Where we are (2026-07-18)

- **Phase 1a/1b — shipped.** Scan/Protect/Sort/Review Changes, live IPC scan, Heliosphere
  auto-protect, manual + By Creator sort, collision/protected-violation validation. Apply disabled.
- **Dark theme — shipped.**
- **Phase 1c (By Mod Type sort) — shipped.** Merged via PR #1 (`worktree-plugin-organizer-phase1c`
  → `main`, commit `ea32d2c`). `ChangedItemKeyParser` + `ModTypeClassifier` classify every mod during
  scan per `docs/superpowers/specs/2026-07-13-plugin-organizer-phase1c-classification-design.md`; the
  Sort tab has a working "By Mod Type" button. The temporary SPIKE dump button is removed. A third
  spike dump (237 mods, fresh library, 2026-07-14) was used to stress-test the design post-merge and
  folded two refinements into the spec — no code changes needed, the classifier already handled both
  cases correctly. 82 tests pass, build clean.
- **Phase 1d (collision disambiguation) — shipped.** `SortByCreator`/`SortByModType` no longer
  produce colliding paths for Penumbra duplicate installs sharing a display name.
- **Phase 1e (combined sort strategies) — shipped.** Adds `SortByTypeThenCreator`/
  `SortByCreatorThenType`; unifies all four sort strategies' unknown-creator/unknown-type fallback
  under one `Review/{Name}` rule.
- **Phase 1f (Review export + table layout) — shipped.** Adds an Export button (full-state text
  snapshot) to the Review Changes tab; fixes the table's column clipping.
- **Phase 2 (Apply, write support) — shipped.** The plugin's first write IPC call. Apply writes
  every unprotected, changed mod's `ProposedPath` via `SetModPath`, gated on `Validate()`, with a
  rolling backup/Rollback and a "Protect & Skip All Blocking Mods" bypass.
- **Folder Cleanup (organization.json orphaned-folder prune) — implemented, pending in-game
  verification.** The plugin's second write target (plain file I/O, not IPC — no IPC exposes
  folder-structure writes). Detects orphaned folder entries, prunes selected ones with a
  byte-fidelity rolling backup, separate from mod-move Apply/Rollback. Requires a manual
  "Rediscover Mods" click in Penumbra after every cleanup/rollback (no reload IPC exists).
- **Workbook import/export — implemented, pending in-game verification.** Exports a `.xlsx`
  workbook and imports one back, interoperable with the standalone app's existing workbook feature,
  by linking that app's actual `WorkbookWorkflowService` rather than reimplementing the format. See
  the Phase 3 section below for design/plan links and `docs/HANDOFF_WORKBOOK_IMPORT_EXPORT.md` for
  the full handoff.
- **NPC/enemy/boss name-based classification — implemented, pending in-game verification.** Name-heuristic matching
  (whole-word, Unicode-boundary, combined-regex-per-category) now outranks every structural rule in
  `ModTypeClassifier`, with a persistent name list seeded from an embedded resource and grown via a
  "Refresh NPC list from wiki" button that scrapes `consolegameswiki.com`. See
  `docs/superpowers/specs/2026-07-17-plugin-organizer-npc-name-classification-design.md` and
  `docs/superpowers/plans/2026-07-17-plugin-organizer-npc-name-classification.md` for design/plan, and
  `docs/HANDOFF_NPC_CLASSIFICATION.md` for the full handoff.
- **Detailed gear-slot classification — implemented, pending in-game verification.** Gear mods now
  resolve a `SubCategory` (Head/Top/Hands/Legs/Feet/Ears/Neck/Wrists/Rings) by reading the mod's own
  files directly from Penumbra's mod library on disk — data `GetChangedItems` never exposes. Reuses
  a small linked `EquipmentSlotMapper` (also fixes a latent suffix-extraction bug in the standalone
  app's own `ModPathClassifier`). See
  `docs/superpowers/specs/2026-07-18-plugin-organizer-gear-slot-classification-design.md` and
  `docs/superpowers/plans/2026-07-18-plugin-organizer-gear-slot-classification.md`.
- **Library Search (reverse changed-item lookup) — implemented, confirmed working on the author's
  in-game setup; wider verification via Discord testers.** New
  read-only "Search" tab: find every installed mod (enabled or not) by the game items it changes,
  independent of the Sort tab's Scan/classification state. Built on a new `LibrarySearch/` namespace
  (mod-centric index + facet/slot filter engine), reusing `ChangedItemKeyParser`/`ModTypeClassifier`/
  `ModEquipmentFileReader` as-is — no new IPC calls. Two-pane UI: a flat, filterable mod list plus the
  selected mod's changed items, category/equipment-slot toggle buttons, and item/mod-name text search.
  Went through two external design-review passes before implementation (see the spec's "Two review
  passes" section for what changed and why) and a whole-branch final review with no Critical/Important
  findings. Two Minor, non-blocking UX polish items noted by the final review: (1) a mod can appear in
  the left pane via one item/category combination while the right pane shows zero items if a narrowed
  category filter and an item-text query each match a *different* item on the same mod — spec-sanctioned
  by the displayed-item algorithm's design, not a bug, but a blank right pane with no explanation could
  use a one-line placeholder; (2) `_librarySearchSelectedModIdentifier` isn't cleared when the selected
  mod filters out of the result set (harmless — the right pane just shows "select a mod" until it
  reappears). 46 new tests (468 total), build clean. See
  `docs/superpowers/specs/2026-07-21-library-search-changed-item-lookup-design.md` and
  `docs/superpowers/plans/2026-07-21-library-search-changed-item-lookup.md`.
- **Community organization templates, Phases T1, T2 and T3 — implemented, not yet verified in-game.**
  A portable, identity-free template document (normalized mod name → folder entries, an
  author-declared fallback, and a longest-prefix folder-label rename map) with staged
  validation and a `POT1:` share-code transport, plus a Templates tab that imports a `.json`
  someone shared, previews the resulting folder tree and match counts against the current
  library, and stages proposals through the existing Review Changes pipeline. Unlike the
  workbook, a template carries no `installationIdentity`, so it travels between users.

  T3 adds export: a review-and-trim screen, `.json` output and a clipboard share code. That screen
  is a privacy mechanism rather than polish — exporting publishes the author's mod names — so it
  opens with everything included, lists every name that would go out, and is the only path in the
  plugin to a written template or a clipboard write. **No "quick export" affordance may be added
  that bypasses it.** Export reads each mod's *current* path, never a pending proposal, so a user
  who has sorted without applying cannot unknowingly share the layout they are about to replace.

  The fallback travels as a `SortStrategy` plus the two split flags, matching the Sort tab's own
  selection. An earlier draft used a seven-member enum that could not express "do not split NPC
  mods" at all; see the reversal note in the design doc before changing this field.

  Verifying the feature end to end needs two libraries and therefore a second tester. Design:
  `docs/superpowers/specs/2026-07-30-community-templates-design.md`. Plans:
  `docs/superpowers/plans/2026-07-30-community-templates-t1-core.md`,
  `docs/superpowers/plans/2026-07-31-community-templates-t2-import-and-preview.md`,
  `docs/superpowers/plans/2026-08-11-community-templates-t3-export.md`.

## Phase 1c — done

No further action needed. In-game verification and the handoff-doc update from the plan's Task 6 both
landed as part of the merged PR.

**Known gap to fold in opportunistically, not blocking:** French-client locale for the classifier
is untested (spec calls it low-priority — sits structurally between the confirmed German and
Japanese clients). Pick up if a French-client report ever surfaces a misclassification.

## Phase 1d — done

Shipped: `CollisionDisambiguator` (`PenumbraOrganizer.Plugin/Organizer/CollisionDisambiguator.cs`)
renumbers `SortByCreator`/`SortByModType` path collisions between Penumbra duplicate installs sharing
a display name. Design: `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1d-collision-disambiguation-design.md`.
Plan: `docs/superpowers/plans/2026-07-14-plugin-organizer-phase1d-collision-disambiguation.md`.
Deliberately doesn't extend to protected/`Unknown`-row collisions — see the handoff doc's Known
limitations.

## Phase 1e — done

Shipped: `SortByTypeThenCreator`/`SortByCreatorThenType`
(`PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`) plus two new Sort-tab buttons. All four sort
strategies now share `OrganizerState.BuildPath`'s fallback rule — unresolvable creator/type goes to
`Review/{Name}` instead of being skipped or dropped bare at root. Design:
`docs/superpowers/specs/2026-07-14-plugin-organizer-phase1e-combined-sort-strategies-design.md`. Plan:
`docs/superpowers/plans/2026-07-14-plugin-organizer-phase1e-combined-sort-strategies.md`.
`PreserveAndClean` and detailed gear-slot sorting remain deferred — see their own sections below.

## Phase 1f — done

Shipped: `OrganizerExportFormatter` (`PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs`)
plus an Export button on the Review Changes tab, writing a full-state snapshot to
`organizer-export.txt` in the plugin config directory. Also fixed `PathTreeView`'s table column
clipping (proportional + resizable sizing) and widened `MainWindow`'s minimum width to 900px. Design:
`docs/superpowers/specs/2026-07-14-plugin-organizer-phase1f-review-export-and-table-layout-design.md`.
Plan: `docs/superpowers/plans/2026-07-14-plugin-organizer-phase1f-review-export-and-table-layout.md`.

## Phase 2 — Apply (write support), backup/rollback included by construction — done

Shipped: `ApplyPlanner` (`PenumbraOrganizer.Plugin/Organizer/ApplyPlanner.cs`, pure/unit-tested —
`BuildBackup`, `BlockingIdentifiers`, `Retain`), plus `Plugin.ApplyChanges()`/`RollbackLastApply()`/
`ProtectAndSkipBlockingMods()` and the Review Changes tab's real Apply/Protect & Skip/Rollback
buttons. This is the plugin's first live write IPC call (`SetModPath`) — everything through Phase 1f
was read-only.

Design: `docs/superpowers/specs/2026-07-15-plugin-organizer-phase2-apply-design.md` (includes an
external design review's findings and what was/wasn't adopted — see its "Revision notes" section).
Plan: `docs/superpowers/plans/2026-07-15-plugin-organizer-phase2-apply.md`.

Key decisions, in case they need revisiting:
- `SetModPath`'s real parameter order was confirmed by reflection against the actual `Penumbra.Api`
  5.15.1 assembly before implementation — it's `(modDirectory, newPath, modName)`, not
  `(modDirectory, modName, newPath)` as the doc comment's prose order would suggest.
- Backup is a single rolling file (`organizer-backup.json`, plugin config directory), not a
  multi-Apply history — "Rollback" always means "undo the most recently completed Apply." After a
  forward Apply, the backup is rewritten to keep only entries whose write actually succeeded; after
  a Rollback, it's rewritten to keep only entries that failed to restore (so a second Rollback click
  can retry just those) — this closes a real bug an external review caught (an unconditional
  backup delete could otherwise lose recovery data, or let a stale rollback overwrite an unrelated
  later change to a mod whose Apply had failed).
- Scope is deliberately `SetModPath` only — no `mod_data.db`/`organization.json` writes, no fix for
  Penumbra's orphaned-empty-folder behavior (still tracked below, next to detailed gear-slot
  sorting, as its own future scope-expansion decision).
- No `IModPathWriter`-style abstraction layer — `ApplyChanges`/`RollbackLastApply` are verified
  in-game only, consistent with `RunScan`/`SaveProtectionState`/`ExportReview`.

## Folder Cleanup (organization.json orphaned-folder prune) — implemented, pending in-game verification

Shipped: `OrganizationJson`/`OrganizationJsonCodec` (pure data model + status-carrying codec,
`PenumbraOrganizer.Plugin/Organizer/OrganizationJson.cs`/`OrganizationJsonCodec.cs`),
`OrganizationCleanupPlanner` (pure detection/prune logic — `GetVirtualParent`, `DetectOrphaned`,
`Prune`), `FolderCleanupExecutor` (file-I/O sequencing for cleanup + rollback, no IPC), plus
`Plugin.DetectOrphanedFolders()`/`CleanUpFolders()`/`RollbackFolderCleanup()` and an Orphaned
Folders section on the Review Changes tab. The plugin's second write target — but plain file I/O
against `organization.json`, not IPC, since no Penumbra IPC exposes folder-structure writes.

Design: `docs/superpowers/specs/2026-07-15-plugin-organizer-folder-cleanup-design.md`.
Plan: `docs/superpowers/plans/2026-07-15-plugin-organizer-folder-cleanup.md`.

Key decisions, in case they need revisiting:
- Occupancy is `CurrentPath` (detection, advisory, last-scan) plus a fresh IPC read at write time
  (enforcement) — never `ProposedPath`. A folder staged to receive a mod via Sort but not yet
  Applied still reads as orphaned; the write-time re-verification is what actually protects it if
  something changes occupancy between selection and the Clean Up click.
- The target `organization.json` write happens before backup promotion, not after — reversed, a
  failed target write would destroy the previous backup for nothing.
- `Folders` and `Separators` are disjoint in Penumbra's schema; pruning only ever touches `Folders`
  and carries `Separators`/unknown top-level `ExtensionData` through by reference, untouched.
- No reload IPC exists for `organization.json` — every cleanup/rollback requires a manual
  "Rediscover Mods" click in Penumbra, surfaced via a persistent `_folderReloadRequired` banner
  that only clears on the next Scan.

**Not yet in-game verified** — see the plan's Task 7 checklist (real-library orphan detection,
plain vs. customized-folder cleanup, rollback with customization intact, the stale-selection race
via Penumbra's own UI, the `HasScanned` vs. `Mods.Count == 0` distinction, and confirming
UTF-8-without-BOM against a real install's file).

## Phase 3 (later, unscoped) — remaining parity features

These are explicitly out of scope today and only worth discussing once Apply (with its built-in
backup/rollback) exists and has proven stable in real use:

- **Workbook import/export — shipped.** Links the standalone app's actual `WorkbookWorkflowService`
  (plus a small extracted `ScanIdentity` utility) into the plugin via `<Compile Include>`, the same
  pattern already used for `ModCategory.cs`/`CreatorCanonicalizer.cs`. A plugin-only `WorkbookAdapter`
  bridges the one real schema gap (full-path `ProposedPath` vs. the standalone app's folder-only
  `destination`). Design: `docs/superpowers/specs/2026-07-16-plugin-organizer-workbook-import-export-design.md`.
  Plan: `docs/superpowers/plans/2026-07-16-plugin-organizer-workbook-import-export.md`.
- **Self-update pipeline** — likely moot if this ever gets distributed via a plugin repository
  (see Phase 4), since that mechanism handles updates already.
- **Public Dalamud plugin repository submission** — packaging, versioning, and review requirements
  are unresearched. Only worth investigating once the feature set is stable enough to support
  external users' bug reports.

## Detailed gear-slot sorting (parking lot)

**Status: implemented, see the 'Where we are' entry above.** Splitting the coarse `Gear` bucket
into slot-level detail (Head/Top/Hand/Legs/Feet, plus a special `Body` bucket for Smallclothes/"The
Emperor's New ..." items specifically) came up alongside Phase 1e's brainstorming. Investigated and
found genuinely blocked on a data-source gap, not just unscoped:

- The standalone app already does this successfully (1,913-mod library, zero failed classifications)
  by reading real internal FFXIV file paths (`chara/equipment/e0755/model/c0101e0755_top.mdl`) and
  Penumbra's `Manipulations[].Slot` data — see `docs/superpowers/specs/2026-07-08-mod-category-overhaul-design.md`
  in `C:\Repo\PenumbraOrganizer` for the exact suffix→slot mapping (`_met` Head, `_top` Body, `_glv`
  Hands, `_dwn` Legs, `_sho` Feet, plus accessory suffixes). This signal is language-independent and
  not a guess.
- This plugin has no equivalent. A full survey of every IPC subscriber in `Penumbra.Api` 5.15.1 (2026-07-14)
  found no per-mod call that exposes real game paths or slot manipulation data — `GetChangedItems`/
  `GetChangedItemAdapterDictionary` (what this plugin uses) only return human-readable item-name
  strings; `GetGameObjectResourcePaths`/`GetGameObjectResourceTrees`/`GetPlayerResourcePaths` do expose
  real paths but are keyed by a live game object/actor (what's currently rendered), not by an arbitrary
  mod; `GetMetaManipulations` is keyed by collection, same problem. Penumbra's own "Changed Items" UI
  tab was also checked (screenshot review) — it renders the same item-name data this plugin already
  has, not raw paths.
- The standalone app gets its path data by reading Penumbra's mod storage **directly off disk**
  (`meta.json`, `group_*.json`, file-redirect JSON) as a separate desktop process. Replicating that in
  this plugin means gaining a new capability — reading Penumbra's mod files itself, bypassing IPC —
  which is a genuine scope expansion beyond this repo's documented decision (read-only, in-process IPC
  only; see [[dalamud-plugin-decision]]), not a quick data-source swap. Needs the same kind of explicit
  scope re-confirmation Phase 2 (Apply) already requires, before any design work starts.
- Rejected alternative: an English-keyword heuristic (`"...Boots"` → Feet, etc.) was considered and
  explicitly not adopted — item display names are translated payload text (same category as
  Mount/Minion/Emote names), so a keyword heuristic isn't locale-invariant and is guessing, which
  conflicts with every other part of this classifier's "never guess" principle.

## Auto mod tagging (parking lot)

**Status: researched, not designed, not scheduled.** Raised 2026-07-24 via a support-Discord idea
(relayed secondhand, not a recurring community ask — "testing the waters," one person's suggestion):
automatically detect a gear mod's body-base compatibility (G3, B+, YAB, Rue, etc.) by reading its
files, then assign the result as one of Penumbra's own "Predefined Tags" so Penumbra's native
tag-based search picks it up everywhere, not just inside this plugin. Investigated for feasibility
before any design work, per this project's established pattern (see Detailed gear-slot sorting
above for the precedent) — found genuinely mixed, not a clean yes or no:

**Reading existing tags is already free.** `Penumbra.Api.Helpers.ModWrapper` — the per-mod object
returned by `GetModListAdapter`, the exact IPC call this plugin already invokes on every scan —
already carries both `ModTags` (`IReadOnlyList<string>`) and `LocalTags` (`IReadOnlyList<string>`)
fields (confirmed via reflection against the actual `Penumbra.Api` 5.15.1 assembly this project
references, cross-checked against `Penumbra.Api.Enums.ModProperty`'s `ModTags`/`LocalTags` members
in the same package). This plugin just isn't mapping those two fields into `OrganizerModRow` yet —
doing so would need zero new IPC surface and zero new risk.

**Writing splits into two very different stories**, traced through Penumbra's actual source
(`xivdev/Penumbra`, not guessed):
- **`ModTags`** (the global, shareable "Predefined Tags" toggles shown in Arae's screenshot) are
  persisted by `ModDataEditor.ChangeTag` → `saveService.QueueSave(new ModMeta(saveService, mod))` —
  each mod's own `meta.json`, sitting inside that mod's own folder on disk, not a single shared
  file. Theoretically writable (same *category* of move as Folder Cleanup's `organization.json`
  edits — direct file I/O, no IPC writer exists for this), but the blast radius is fundamentally
  different: potentially thousands of individual per-mod files instead of one shared file, each a
  real risk of corrupting that mod's own metadata if a write goes wrong. Would also need
  `ReloadMod(modDirectory, modName)` (confirmed to exist, single-mod scope only) after every write
  to make Penumbra pick up the change — no bulk-reload IPC exists, same limitation Folder Cleanup
  already documented for `organization.json`.
- **`LocalTags`** (private, free-text tags) are persisted by `database.UpsertTags(mod)` — an
  internal database (`mod_data.db` in Penumbra's own config directory, confirmed present as a
  binary/structured file, not JSON, on a local Penumbra install), undocumented schema, no IPC
  writer. **Off the table entirely** — no safe external write path exists.
- `predefined_tags.json` itself (`PredefinedTagManager.cs`) is neither of the above — it's only the
  shared *registry* of tag names available to toggle, not where any mod's actual tag assignment
  lives. Appending a brand-new tag name there would be low-risk (simple JSON array), but doesn't by
  itself tag anything.

**The detection problem is more tractable than an earlier draft of this section claimed, for coarse
body-base detection specifically.** `ModEquipmentFileReader` (built for gear-slot classification,
`PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs`) already reads each
mod's `meta.json`/`group_*.json` `Files` dictionary off disk. Its keys are internal FFXIV game paths
(used today for slot detection) and its **values are the mod's own local file paths, chosen by the
mod author**, not generated or translated by the game. Those local paths commonly reference the
body base by name (`bibo`, `gen3`, etc., per real-world convention in the modding community) since
that is how authors signal compatibility to users. Matching known literal brand strings against
author-chosen file paths is the same category of signal this codebase already relies on
successfully elsewhere: the NPC name matcher and the `KnownEquipmentPlaceholders` table (Body
classification's Smallclothes/"Emperor's New..." detection) both match curated literal strings
against author/game-data text, not translated UI. This is not the same rejected category as the
gear-slot English-keyword heuristic above, which was rejected specifically because in-game item
display names are translated, locale-dependent payload text; mod authors' own file/folder naming is
neither.

**Detailed size variants (chest size, leg size, etc.) are a separate, harder problem**, not covered
by the above. A single mod commonly offers multiple sizes as option-group choices within one mod
(e.g. a "Chest Size" group with Small/Medium/Large/Flat suboptions), not one fixed value per mod, so
detecting size would mean parsing option-group structure and per-option file names, a materially
bigger surface than the flat "does this mod's file paths mention a known body-base brand" check.
Not yet investigated in that depth.

**Why this is still parked, not designed:** the detection signal for coarse body-base compatibility
is genuinely more promising than first assessed, but two things still block a design: (1) the write
side (per-mod `meta.json` edits, no IPC writer, thousands of individual per-mod files instead of one
shared file) remains a materially larger, riskier surface than any existing write feature in this
plugin, and (2) no one has yet built or validated a real curated brand-string list against a real
mod library the way the NPC name list was validated before it shipped. Revisit if either (a)
multiple independent users ask for this, not just one idea being tested, or (b) someone puts in the
same kind of real-library validation work the NPC name matcher and gear-slot classifier both went
through before shipping. If picked up, treat it as two separate decisions requiring their own
explicit scope confirmation (matching the pattern Apply/Folder Cleanup already established):
read-only tag surfacing (safe, cheap, useful on its own) is a much smaller ask than
auto-detect-and-write (real corruption risk, and the write side stays hard regardless of how good
detection gets).

## Cosmetic / non-blocking

- **Custom font** to better match the standalone app's Segoe UI look, via Dalamud's font-atlas API.
  Noted as possible but unattempted in the dark-theme handoff. Pure polish — pick up anytime, no
  dependencies on the phases above.

## How to use this doc

When starting a new work session on this repo, check here first for "what's next" instead of
re-deriving it from commit history. Update the status lines as phases move — this doc is only
useful if it stays current. Each phase above names its own spec/plan doc once one exists; keep
that naming convention (`docs/superpowers/specs/YYYY-MM-DD-<name>-design.md` /
`docs/superpowers/plans/YYYY-MM-DD-<name>.md`) for anything new.
