# Handoff: folder protection rules and the Protect tab tree

Written and closed 2026-11-04.

**SHIPPED.** 0.6.2.1 is tagged, released and live on **both** channels: tag `0.6.2.1`, release
**v0.6.2.1** (not a prerelease), `repo.json` carrying it as `AssemblyVersion` *and*
`TestingAssemblyVersion`. Verified in game by the maintainer before release.

This file is a record, not a work queue. The parts worth reading before touching protection again
are **The model**, **Traps already hit** and **Known debt**.

## What it answers

Two reports, both about the Protect tab:

1. **A nested folder could not be freed.** Protection inherited downward by an any-prefix match
   over a *set* of protected folders. With `haloween` protected there was no way to let
   `haloween/animation` sort: its own checkbox was not the rule protecting those mods, and
   "Unprotect N inside" was hidden because the ancestor would re-cover anything it cleared.
2. **The folder list was unreadable at scale.** One flat fully-qualified row per folder, nothing
   collapsible. Sorting hair into per-number subfolders turned it into hundreds of rows.

## The model

Folder rules are a **map**, path to `true` (protect) / `false` (exclude), and the **deepest rule on
a path wins**. `OrganizationCleanupPlanner.ResolveFolderProtection` walks up from a mod's virtual
parent and returns the first rule it finds.

`IsEffectivelyProtected` is unchanged in shape and still the single rule:

```
(Heliosphere && !override) || explicit tick || resolved folder rule == Protected
```

An exclude is structurally incapable of cancelling anything but the folder clause, because
`Excluded` and `None` both mean "the folder layer says nothing". That is the property to preserve.

Three consequences that are easy to get wrong, and are each pinned by a test:

- **`UnprotectFolderContents` withdraws a protect rule and KEEPS an exclude.** Withdrawing the
  exclude would hand the branch back to the protected ancestor and re-protect exactly the rows the
  action exists to free.
- **It never writes a rule**, so it still cannot escape a protected ancestor on its own - the
  0.6.1.2 behaviour. Escaping an ancestor is the tri-state box's job.
- **`Freeable` drives whether the button is shown at all**, so an under-count hides a working
  action and an over-count offers one that does nothing. A reviewer walked all 27 combinations of
  (own rule) x (ancestor rule) x (protection source) against the formula; that walk is the real
  evidence it is right.

## Persistence

Config is **v2**. See the long remarks on `Configuration.MigrateToCurrentVersion`, and in short:

- v1 `ProtectedFolderPaths` entries become protect rules on load.
- That legacy field is **still written** on every save, as a protect-only mirror, and the migration
  is **not** guarded on `Version`. Both are deliberate: a pre-v2 build cannot read the rule map,
  reads the mirror, and re-saves `Version` as 2, so a version-guarded migration would skip on the
  way back up and drop every folder protected while on the old build. This project ships testing
  builds people roll back from, so the upgrade -> rollback -> upgrade round trip has to survive.
  `UpgradeRollbackReupgrade_KeepsEveryFolderProtectedOnEitherBuild` is the test; restoring the
  guard fails exactly it.

## Traps already hit

Each of these cost real time on this branch. Two were defects in the plan's own example code.

- **The plugin would not load at all.** `MainWindow`'s constructor read `plugin.Config` for the
  saved tree expansion, and `Plugin()` built the window *two lines before* assigning `Config` -
  an unconditional `NullReferenceException` on every load, for every user. No per-task review could
  see it; the two lines were in different files from different tasks. Caught only by the
  whole-branch review. **Config is now loaded and migrated before any window is constructed.**
- **`ImGuiTreeNodeFlags.DefaultOpen` is once-only.** It cannot force a node already seen closed to
  open, and a node a search forced open stays open afterwards - which then leaked into the saved
  expansion set. The tree now drives open state every frame from one source:
  `SetNextItemOpen(searching || expanded.Contains(path), ImGuiCond.Always)`.
- **ImGui hashes the text before `##` into an item's ID.** The node label embeds live counts, so
  every protect/exclude silently gave the node a new identity and discarded its open state. Use
  `###` on anything whose label changes.
- **`SetFolderProtected(path, false)` maps to `None`, not `Excluded`.** Route UI through
  `SetFolderProtectionRule` only, or you will silently drop an exclude.
- **`IsUnderAnyProtectedFolder` survives but is no longer a protection path.** It is a plain
  "is descendant of" containment test despite the name, used only by `UnprotectFolderContents`.

## Known debt

- **The tree is rebuilt every frame**, along with `KnownFolders.ToHashSet` and a `Union`, and the
  counts map is recomputed per frame. Deliberately not cached: invalidation has to cover both scans
  and rule changes, and showing stale folders is worse than the cost. `ScanGeneration` plus a
  rule-mutation counter is the obvious key if an in-game frame-rate check ever says it matters.
  `MatchesFilter` only recurses while the search box has text, so the steady state is cheaper than
  it looks.
- **`IsUnderAnyProtectedFolder` should be renamed** to say what it now does.
- **Reaching "exclude" from an inherited row takes two clicks** - the first gives the folder its own
  rule, visible only as the glyph turning solid. Deliberate, but it is the exact gesture from the
  bug report.
- **None of the ImGui drawing has automated coverage**, by nature. `FolderProtectionTree`'s pure
  half (`Build`, `MatchesFilter`) is tested; `Draw` is not.

## Where the pieces are

| Piece | File |
|---|---|
| Rule resolution | `Organizer/OrganizationCleanupPlanner.cs`, `Organizer/FolderProtectionRule.cs` |
| State, counts, bulk unprotect | `Organizer/OrganizerState.cs` |
| Persistence and migration | `Configuration.cs`, `Plugin.cs` |
| Tree build + draw | `Windows/FolderProtectionTree.cs` |
| Tab wiring | `Windows/MainWindow.ProtectTab.cs`, `Windows/MainWindow.cs` |
| Spec and plan | `docs/superpowers/specs/2026-11-03-...-design.md`, `docs/superpowers/plans/2026-11-03-...md` |
| Screenshots and the mock they come from | `docs/images/`, `docs/ui-mock/` |

1483 tests pass; build clean apart from four pre-existing xUnit analyzer warnings in files this
work never touched.
