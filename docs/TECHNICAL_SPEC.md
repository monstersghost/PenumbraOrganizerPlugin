# Technical Spec

Architecture and module reference for `PenumbraOrganizer.Plugin`. For end-user instructions, see
[USER_GUIDE.md](USER_GUIDE.md). For classification and sort algorithm details, see
[HOW_SORTING_WORKS.md](HOW_SORTING_WORKS.md). For the feature-by-feature build history, including
why each decision was made and in what order, see the `HANDOFF_*.md` files in this folder and
[ROADMAP.md](ROADMAP.md).

## What this is

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin (`IDalamudPlugin`) that organizes
installed [Penumbra](https://github.com/xivdev/Penumbra) mods into a folder structure. It uses
Penumbra's in-process IPC (`Penumbra.Api`) for almost everything, plus three narrow, individually
approved filesystem crossings described below. It's a companion to, not a replacement for, the
standalone [Penumbra Organizer](https://github.com/monstersghost/PenumbraOrganizer) app, and
shares a subset of that app's business logic through cross-repo file linking (see "Cross-repo
linking" below).

## Build

SDK: `Dalamud.NET.Sdk/15.0.0`. It resolves the Dalamud API references on its own, so no local
XIVLauncher or Dalamud install path needs configuring to build.

Target framework: `net10.0-windows7.0`, inherited from the Dalamud SDK.

Key NuGet dependencies: `Penumbra.Api` 5.15.1; `AngleSharp` 1.5.2, used to parse wiki pages for NPC
name scraping; `ClosedXML` 0.104.2, used for workbook import and export; and
`Microsoft.Extensions.Logging.Abstractions` 8.0.2, which adapts Dalamud's `IPluginLog` to the
`ILogger<T>` contract the standalone app's shared code expects.

```
dotnet build
dotnet test PenumbraOrganizer.Plugin.Tests
```

The test suite stood at 300 tests as of the gear-slot classification feature.

## Module map

Entry point:

- `Plugin.cs` is the `IDalamudPlugin` implementation. It owns the Dalamud service references, the
  `/porganizer` command handler, the IPC subscribers, `OrganizerState`, and every public operation
  the UI calls: `RunScan`, `ApplyChanges`, `RollbackLastApply`, `ExportWorkbook`/`ImportWorkbook`,
  `ExportReview`, `DetectOrphanedFolders`/`CleanUpFolders`/`RollbackFolderCleanup`, and
  `RefreshNpcNamesAsync`. It contains no ImGui code.
- `Windows/MainWindow.cs` holds all UI (ImGui, through Dalamud's binding) across six tabs. Beyond
  thin try/catch wrappers around `Plugin`'s methods and local UI state (selected checkboxes,
  last-result caches for display), it has no business logic of its own.

Classification (`Organizer/Classification/`):

- `ChangedItemKeyParser.cs` and `ChangedItemKey.cs` parse each raw `GetChangedItems` key string
  into a typed shape: Gear, Customization, Npc, Mount, Minion, Emote, Action, Icon, or
  CategoryLiteral.
- `ModTypeClassifier.cs` reduces a mod's parsed keys and name to one `ClassificationResult`
  (`Category`, `SubCategory`, `Source`). It also holds the separate `EnrichGearSubCategory`
  post-processing step and `ModTypeFolders.GetFolder`, which maps a category and subcategory to a
  folder-name string.
- `NpcNameMatcher.cs` does regex-based NPC, enemy, and boss name matching against a mod's display
  name.
- `EquipmentSlot.cs` and `EquipmentSlotMapper.cs` are linked from the standalone app repo, not
  physically present here (see "Cross-repo linking"). They define the equipment-slot enum and its
  path-suffix and Manipulation-slot mapping tables.
- `ModEquipmentFileReader.cs` reads a Gear mod's on-disk Penumbra config files
  (`default_mod.json`, `group_*.json`) to resolve which equipment slot or slots it touches. It's
  the only class in this plugin that reads Penumbra's mod-library filesystem directly (see
  "Filesystem crossings" below).

NPC name list (`Organizer/NpcNames/`):

- `NpcNameListDocument.cs` and `NpcNameListCodec.cs` define a versioned schema with never-throws
  parsing and serialization for the on-disk name list.
- `NpcNameListStore.cs` loads that list, falling back to the bundled seed on failure, and builds
  an `NpcNameMatcher` from whatever document it loaded.
- `NpcWikiScraper.cs` is a bounded, defensive MediaWiki category-page scraper built on
  `AngleSharp`, with auto-redirect following turned off.
- `NpcNameRefreshService.cs` orchestrates the scrape, merges the result additively (it never
  removes existing names), and writes the updated list.

Sorting and state:

- `Organizer/OrganizerState.cs` holds the in-memory mod list, the four sort strategies (the
  `SortBy*` methods), `Validate()` (protected-violation and path-collision detection), and manual
  assignment.
- `Organizer/OrganizerModRow.cs` is one mod's mutable state: `Identifier`, `Name`, `Author`,
  `CurrentPath`, `ProposedPath`, `Protected`, `HeliosphereManaged`, `Category`, `SubCategory`.
- `Organizer/CollisionDisambiguator.cs` renumbers `(2)`, `(3)`, and so on when a sort strategy
  produces duplicate proposed paths, for example when two Penumbra installs share a display name.
- `Organizer/HeliosphereDetector.cs` checks the `hs-` directory prefix and `heliosphere.json`
  presence.

Apply, the write path:

- `Organizer/ApplyPlanner.cs` is pure logic: `BuildBackup`, `Retain` (backup pruning after a
  partial success or failure), `BlockingIdentifiers`, and `FolderPathCollisions`, a defensive
  pre-check against orphaned `organization.json` folder entries that would otherwise make Penumbra
  reject `SetModPath`.
- `Plugin.ApplyChanges()` and `RollbackLastApply()` are the only two call sites of `SetModPath` in
  the whole plugin.

Folder Cleanup, which writes `organization.json` directly rather than through IPC:

- `Organizer/OrganizationJson.cs` and `OrganizationJsonCodec.cs` define the schema and
  never-throws parsing and serialization for Penumbra's own folder-metadata file.
- `Organizer/OrganizationCleanupPlanner.cs` is pure logic: `GetVirtualParent`, `DetectOrphaned`,
  `Prune`.
- `Organizer/FolderCleanupExecutor.cs` is the only file-I/O writer for `organization.json`. It
  writes the target file before promoting the backup, keeps a byte-fidelity backup, and handles
  the BOM correctly.

Workbook import and export, for interop with the standalone app:

- `Organizer/WorkbookAdapter.cs` bridges this plugin's full-path `ProposedPath` model to the
  standalone app's folder-only `destination` model, the one real schema gap between the two.
- `WorkbookWorkflowService`, `WorkbookWorkflowModels.cs`, `DomainModels.cs`, `OrganizerModels.cs`,
  `ModClassificationModels.cs`, and `ScanIdentity.cs` are linked from the standalone app repo.
- `Organizer/PluginLogAdapter.cs` adapts Dalamud's `IPluginLog` to `ILogger<T>`, so the linked
  `WorkbookWorkflowService` can log without needing a Dalamud dependency of its own.

Export:

- `Organizer/OrganizerExportFormatter.cs` produces the plain-text Review export format.

## Filesystem crossings

The plugin sticks to in-process IPC for almost everything. It has exactly three deliberate,
individually approved exceptions to that boundary:

`SetModPath`, a write IPC call rather than a filesystem write, powers Apply and Rollback. It's the
plugin's first live write call, and it's scoped to this one IPC method only: no `ReloadMod`, no
`InstallMod`, and no direct `mod_data.db` access.

`organization.json` is read and pruned with plain file I/O, since no IPC exposes this file. Folder
Cleanup reads it from Penumbra's own config directory, which sits as a sibling of this plugin's
config directory at `Directory.GetParent(ConfigDirectory)/Penumbra/mod_filesystem/
organization.json`.

Penumbra's mod-library files, `default_mod.json` and `group_*.json` under each mod's `ModPath`,
are read but never written, for gear-slot sub-classification. No Penumbra IPC exposes per-mod
equipment-slot data, so `ModEquipmentFileReader` reads it directly, the same way the standalone
app already does. This read only ever runs for mods the IPC-derived `GetChangedItems` signal has
already classified as Gear; see [HOW_SORTING_WORKS.md](HOW_SORTING_WORKS.md).

Everything else, the mod list, changed items, mod directory path, and add/delete/move events, comes
from `Penumbra.Api.IpcSubscribers`: `GetModListAdapter`, `GetChangedItemAdapterDictionary`,
`GetModDirectory`, `ModAdded`, `ModDeleted`, `ModMoved`.

## Cross-repo linking

Instead of a shared NuGet package, this plugin links source files directly from the standalone app
repo (`C:\Repo\PenumbraOrganizer`) through `<Compile Include>` entries in
`PenumbraOrganizer.Plugin.csproj`, using relative paths such as
`..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\EquipmentSlotMapper.cs`. In a
worktree checkout, a symlink at `.claude/worktrees/PenumbraOrganizer`, pointing at the real
standalone-app checkout, makes these same relative paths resolve correctly no matter which
worktree the build runs from. This keeps classification and workbook logic defined exactly once,
shared by both the plugin and the standalone app, without a package-publish step.

## Testing

xUnit, no custom test framework. Pure logic classes such as `ModTypeClassifier`,
`ChangedItemKeyParser`, `ApplyPlanner`, `CollisionDisambiguator`, `OrganizationCleanupPlanner`,
`WorkbookAdapter`, and `ModEquipmentFileReader` are unit tested directly. `Plugin.cs` and
`MainWindow.cs` are thin IPC and UI wiring, and aren't unit tested; they're verified by manual
in-game checks instead. Each feature's design spec, under `docs/superpowers/specs/`, carries its
own checklist for that.

## Scope boundary

Not submitted to any plugin repository, testing or live. Load it locally through Dalamud's
dev-plugin mechanism. Out of scope until there's a fresh, explicit decision to change it: any
write IPC call beyond `SetModPath` (`ReloadMod`, `InstallMod`, or direct `mod_data.db` writes), a
self-update pipeline, and submitting to a public plugin repository.
