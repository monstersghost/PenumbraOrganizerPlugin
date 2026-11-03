# Folder protection rules and a tree-shaped Protect tab

Date: 2026-11-03

## Problem

Two reports, both from the Protect tab, both made worse by deep folder structures.

1. **A nested folder cannot be freed.** Protection inherits downward by prefix match: a mod is
   protected if it is Heliosphere-managed, ticked individually, or sits under *any* protected
   folder. With `haloween` ticked, the row for `haloween/animation` can do nothing useful. Its own
   checkbox is not the rule protecting those mods, and `Unprotect N inside` is hidden because
   `Freeable` is zero — the ancestor rule would immediately re-cover anything the action cleared.
   There is no way to express "protect `haloween`, except `haloween/animation`".
2. **The folder list is unreadable at scale.** It is one flat, fully-qualified, alphabetically
   sorted row per folder. Sorting hair mods into per-number subfolders multiplied the row count,
   and nothing collapses. (`PathTreeView`, despite the name, is a flat table of mods and is not a
   tree widget we can reuse.)

## Scope

The Protect tab only. The mod lists in Scan and Review keep their current flat tables; the
Current -> Proposed comparison there reads better flat, and nesting them is a separate job.

## Design

### 1. Protection model: nearest rule wins

`OrganizerState._protectedFolders` (a `HashSet<string>`) becomes a rule map, folder path ->
`bool` (`true` = protect, `false` = exclude).

Resolution walks the mod's virtual parent chain upward and takes the **deepest** rule found. With
no rule anywhere on the chain, the folder layer does not protect the mod.

```
haloween                  -> protect
haloween/animation        -> exclude
haloween/animation/sub    -> (no rule, inherits exclude)
```

`IsEffectivelyProtected` keeps its three layers, in the same order and with the same precedence:

```
Heliosphere-managed and not overridden
  OR explicitly ticked identifier
  OR resolved folder rule says protect
```

An exclude therefore cancels **only** the folder layer. A mod inside an excluded folder that was
ticked by hand, or that is Heliosphere-managed, stays protected. This is deliberate: those are
choices the user made per-mod, and the Heliosphere rule re-asserts itself on every scan, so having
an exclude silently beat them would reverse user intent the way the pre-override behaviour did.

`OrganizationCleanupPlanner.IsUnderAnyProtectedFolder` (an `Any`-prefix match over a set) is
replaced by a longest-prefix resolver taking the rule map. Every call site moves to it.

### 2. Freeable counts and the existing escape hatch

`BuildProtectedModCountsByFolder` is re-derived against the new resolver. On a folder carrying an
exclude, `Freeable` now counts exactly the stragglers the exclude cannot free — hand-ticked and
Heliosphere-managed mods — so the `Unprotect N inside` button shipped in 0.6.1.2 reappears on that
row and clears them in one deliberate, confirmed click. The two mechanisms compose: exclude the
folder, then optionally clear what remains.

### 3. Persistence

`Configuration` gains:

- `FolderProtectionRules` — `Dictionary<string, bool>`, replacing `ProtectedFolderPaths`.
- `ExpandedProtectFolders` — `HashSet<string>`, the Protect tree's open nodes.

`Version` goes to 2. On load, a v1 config migrates every entry of `ProtectedFolderPaths` into
`FolderProtectionRules` with value `true`: identical behaviour after upgrade, nothing to re-tick.
The old field stays readable for that migration and stops being written.

`ReplaceScanAtomically`'s `previouslyProtectedFolders` parameter carries the rule map through a
rescan unchanged, so excludes survive a scan exactly as ticks do.

### 4. UI: a collapsible tree with tri-state checkboxes

A new `FolderProtectionTree.cs` under `Windows/`:

- **Build.** A pure helper turns the folder paths (`KnownFolders` union the ruled folders) into a
  node tree. Ruled-but-empty folders still appear, keeping today's `(currently empty)` note.
  Intermediate path segments that are not themselves known folders become nodes too, so the tree
  has no gaps.
- **Draw.** `ImGui.TreeNodeEx` per node with an inline tri-state checkbox. A solid tick or cross
  means this folder owns the rule; a dimmed one means it is inherited, with a tooltip naming the
  ancestor it came from. Clicking cycles `inherit -> protect -> exclude -> inherit`.
- **Labels.** Each node shows its own protected/total mod count and its subfolder count, e.g.
  `hair (142 mods, 9 subfolders)`.
- **Expansion.** Collapsed to top level by default. Opened nodes are written to
  `ExpandedProtectFolders` and restored next time the window opens.
- **Search.** The existing search box filters to branches containing a match and force-expands
  them for the duration of the search, without writing to the stored expansion set; clearing the
  search restores what the user had open.

The resize grip below the folder list and the `Mods` list beneath it are untouched. The
`(via folder: X)` note on a mod row now names the folder whose rule actually resolved.

## Testing

`OrganizerStateTests`:

- deepest rule wins over a shallower one;
- exclude nested under protect frees the folder's own mods;
- protect nested under exclude re-protects that branch;
- an exclude does not free a hand-ticked mod;
- an exclude does not free a Heliosphere-managed mod;
- `Freeable` on an excluded folder counts exactly those stragglers.

`ConfigurationTests`: a v1 config with `ProtectedFolderPaths` migrates to v2 rules, all `true`.

`FolderProtectionTree` build helper: paths to nodes, including gap-filling intermediate segments
and ruled-but-empty folders. Kept pure so it tests without ImGui.

## Out of scope

- Tree rendering for the Scan and Review mod tables.
- Any change to how Heliosphere protection or its session-scoped override set works.
- Any change to sorting, Apply, or path semantics.
