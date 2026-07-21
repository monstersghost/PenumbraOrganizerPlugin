# Folder-Level Protection and Search Design

**Status:** Revised after external review, ready for a second pass
**Date:** 2026-07-20

## Revision note

The first version of this spec was reviewed and found not implementation-ready:
it conflated three different protection sources into one, had a bug where
removing one protection rule could disable an unrelated one, left folder
checkbox and search semantics undefined, and understated how much
downstream code (`Restore`) actually needed to change. Every point from
that review is addressed below. Two claims from the review were verified
directly against the current code before writing this revision:

- `Plugin.SaveProtectionState()` (Plugin.cs:148-152) derives
  `Config.ProtectedModIdentifiers` from `OrganizerState.Mods.Where(m =>
  m.Protected)` — confirmed: this would silently turn folder-derived
  protection into permanent individual protection once saved. Real bug.
- `Plugin.Restore` (Plugin.cs:346) passes `Config.ProtectedModIdentifiers`
  directly into `RollbackHistory.BuildRestorePlan`, never touching
  `OrganizerState`/`row.Protected` at all (Restore doesn't require a scan).
  Confirmed: folder protection would be invisible to Restore without an
  explicit fix at that call site. The original spec's "no downstream
  changes needed" claim was wrong for this one case.

## Problem

Protection today is per-mod only (`OrganizerModRow.Protected`, backed by
`Configuration.ProtectedModIdentifiers`, a set of mod identifiers). If a
user organizes a folder and later installs a new mod into it, or runs a
Sort, that new mod is not automatically protected: protection never
follows *location*, only the specific mods checked at the time.

Separately, the Protect tab's mod list and the Sort tab's manual-assign
list have no way to filter a large mod library, and manual assign only
lets you pick one mod at a time via radio buttons.

## Scope

1. **Folder-level protection** on the Protect tab.
2. **A search bar on the Protect tab**, filtering both the mod list and a
   new folder list.
3. **A rework of Sort tab's manual assign**: checkboxes for multi-select,
   one destination folder applied to every checked mod, its own
   independent search bar.

## Core model: three explicit sources, one derived state

There are exactly three things that can make a mod protected, and they
must never be conflated into each other:

```csharp
// Explicit, persisted, user-editable:
HashSet<string> ProtectedModIdentifiers   // individual checkbox
HashSet<string> ProtectedFolders          // folder checkbox

// Live, recomputed every scan directly from Penumbra IPC, never persisted
// as its own set (this part is unchanged from today):
bool OrganizerModRow.HeliosphereManaged
```

`row.Protected` is **always a derived value, never itself a source of
truth**. Nothing is ever allowed to write into `ProtectedModIdentifiers` by
reading `row.Protected` back out — that is exactly the bug confirmed
above. `OrganizerState` owns its own copies of the two explicit sets
(`_protectedModIdentifiers`, `_protectedFolders`), populated fresh from
`Config` on every `LoadScan` call by copying into new `HashSet` instances
(never holding the same reference `Config` holds — issue #19), so that
draw-time UI code can't mutate persisted config before an explicit save.

Two recompute formulas are needed, not one, because of an existing,
intentional, previously-confirmed behavior for Heliosphere that must not
be silently broken by this change (detail below):

```csharp
// Used by: LoadScan (start of every scan), SetFolderProtected (a folder
// rule change is a system/bulk event — always fully correct, no
// transient window for anything).
bool IsEffectivelyProtectedFull(row) =>
    row.HeliosphereManaged
    || _protectedModIdentifiers.Contains(row.Identifier)
    || IsUnderAnyProtectedFolder(row.CurrentPath, _protectedFolders);

// Used by: SetProtected (single checkbox), SetAllProtection (bulk
// button). Deliberately excludes HeliosphereManaged.
bool IsEffectivelyProtectedAfterIndividualToggle(row) =>
    _protectedModIdentifiers.Contains(row.Identifier)
    || IsUnderAnyProtectedFolder(row.CurrentPath, _protectedFolders);
```

**Why two formulas (this is the resolution to review point #4):** today,
unchecking a Heliosphere-managed mod's individual checkbox is a deliberate,
already-shipped, already-confirmed feature — "Heliosphere-managed mods are
always re-protected on scan, even if a user previously unprotected them...
manual unprotect only 'sticks' for non-Heliosphere mods" (existing code
comment in `OrganizerState.LoadScan`). That transient-until-next-scan
window is intentional for Heliosphere specifically. Folder protection has
no such intentional design goal — the review's safety-hole concern
(checkbox visually shows unprotected while a folder rule still applies,
during which Apply could move it) is entirely correct and must not exist
for folders. Excluding `HeliosphereManaged` only from the
per-click-recompute path preserves the existing, confirmed Heliosphere UX
exactly as-is, while making folder-protection recompute **immediately and
synchronously correct** on every mutation: `SetProtected` mutates the
explicit set and recomputes before returning, so by the time the next
frame draws, a mod that's still folder-protected is shown checked again
with no unsafe window (Apply can't run inside that frame). This is
disclosed here as a deliberate asymmetry, not an oversight — if you'd
rather unify the two (also make Heliosphere immediately-reassert, changing
existing behavior), say so and I'll change the recompute call sites
accordingly; my default is "don't silently change confirmed behavior
that's out of scope for this request."

`SetFolderProtected(folder, value)` mutates `_protectedFolders`, then runs
`IsEffectivelyProtectedFull` over **every** row, not just rows under that
folder — this is what fixes review point #2's example directly: `Gear`
protected, `Gear/Feet` also protected, user unchecks `Gear` →
`_protectedFolders` loses `Gear` but keeps `Gear/Feet` → full recompute
correctly leaves every `Gear/Feet` mod protected, because
`IsUnderAnyProtectedFolder` still matches `Gear/Feet` on its own.

## Folder identity, normalization, and matching

Folder identity reuses `OrganizationCleanupPlanner.GetVirtualParent(string
path)` (existing, pure, unchanged) — the substring up to the last `/`, or
`null` for a root-level mod.

**Where folder paths come from (narrows the normalization problem):** the
*only* way a path enters `ProtectedFolders` is by checking a checkbox next
to a folder row that was itself produced by `GetVirtualParent` on a live
scanned mod's `CurrentPath` — never free-typed by the user. That
eliminates most of the wild-input surface (stray backslashes, garbage
whitespace, arbitrary Unicode) as a *live* concern. It does not eliminate
it for a **persisted** path from a prior session being compared against a
**newly scanned** path this session, so normalization is still specified:

```csharp
// Applied when reading a persisted folder path back from Config, and
// when deriving a folder key from a live scanned path, so both sides of
// every comparison go through the same function.
static string? NormalizeFolderPath(string? path)
{
    if (string.IsNullOrWhiteSpace(path)) return null;
    var withForwardSlashes = path.Replace('\\', '/');
    var collapsed = Regex.Replace(withForwardSlashes, "/+", "/");
    var trimmed = collapsed.Trim('/', ' ');
    return trimmed.Length == 0 ? null : trimmed;
}
```

**Comparer: `StringComparison.OrdinalIgnoreCase`** for both the
`HashSet<string>` (constructed with `StringComparer.OrdinalIgnoreCase`)
and the boundary-match function (review point #6) — matching
`PenumbraPathSemantics`'s existing choice elsewhere in this codebase
("Comparison is OrdinalIgnoreCase, matching Penumbra's own sibling
comparer"), not left to `HashSet<string>`'s ordinal-case-sensitive default.

**Boundary-safe recursive matching**, corrected test cases per the review
(the original `Gear/Fee` vs `Gear/Feet` example doesn't actually exercise
the bare-`StartsWith` bug — neither is a prefix of the other):

```csharp
static bool IsUnderAnyProtectedFolder(string currentPath, IReadOnlySet<string> protectedFolders)
{
    var parent = OrganizationCleanupPlanner.GetVirtualParent(currentPath);
    if (parent is null) return false; // root-level mod: see acknowledgment below
    foreach (var folder in protectedFolders)
    {
        if (string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase))
            return true;
        if (parent.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            return true; // recursive: Gear/Feet protects Gear/Feet/Sub/Mod
    }
    return false;
}
```

Corrected boundary test cases for the implementation plan: protected
`Body` must not match candidate parent `BodyMods/Author` (an example
already used verbatim elsewhere in this codebase's comments); protected
`Gear/Feet` must not match candidate parent `Gear/FeetExtra`.

**Which path field (review point #7):** matching always uses
`row.CurrentPath`'s virtual parent, never `ProposedPath`. Stated
explicitly: protection follows live location, so Sorting a *currently*
protected mod remains blocked (existing Sort behavior, unchanged), and
proposing a move *into* a protected folder does not retroactively protect
that mod before Apply — it only becomes protected after Apply completes
and the next Scan re-derives state from the new `CurrentPath`.

**Root-level acknowledgment (review point #8):** mods sitting directly at
Penumbra's root have no virtual parent, so there is no way to protect "all
root-level mods" as a folder rule. Explicitly out of scope, not hidden —
if root-level protection is wanted later, it needs its own design (e.g. a
sentinel value), not squeezed into this one.

## Downstream audit (review point #1)

Every place that decides whether a mod may move was checked against
"reads `row.Protected` (correct) vs. reads `ProtectedModIdentifiers`
directly (wrong, would miss folder protection)":

- **Apply** (`Plugin.ApplyChanges`, Plugin.cs): touched-rows filter is
  `Where(m => !m.Protected && ...)` — already reads the derived field.
  Correct today, no change needed, verified by reading the line, not
  assumed.
- **Sort** (`OrganizerState.Sort`): already filters `Where(m =>
  !m.Protected)`. Correct, no change.
- **Restore** (`Plugin.Restore` → `RollbackHistory.BuildRestorePlan`):
  **confirmed incorrect** — passes `Config.ProtectedModIdentifiers`
  directly, bypassing `row.Protected` and therefore folder protection,
  entirely. **Fix required** at the `Plugin.Restore` call site (not inside
  `BuildRestorePlan`, which stays untouched — its existing tests and
  signature are unaffected):
  ```csharp
  var currentMods = ReadCurrentMods();
  var lockedIdentifiers = Config.ProtectedModIdentifiers
      .Union(currentMods
          .Where(m => Organizer.RollbackHistory.IsUnderAnyProtectedFolder(m.FullPath, Config.ProtectedFolderPaths))
          .Select(m => m.Identifier))
      .ToHashSet(StringComparer.Ordinal);
  var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods, lockedIdentifiers);
  ```
  `BuildRestorePlan` already separately ORs in `mod.HeliosphereManaged`
  internally, so that term isn't duplicated here.
- **Folder Cleanup** (`OrganizationCleanupPlanner.DetectOrphaned`/`Prune`):
  audited, no change needed — and here is the demonstration the original
  spec was missing, not just an assertion. Folder Cleanup only ever prunes
  a folder that is **currently structurally empty**, verified via a fresh
  IPC read of every mod's live `CurrentPath` at cleanup-write time
  (`Plugin.CleanUpFolders`'s `OccupiedFolders(modList...)`). A protected
  folder can only become structurally empty if every mod that was in it
  moved out — which Apply/Sort cannot do to a protected mod. The only way
  a protected folder becomes genuinely empty is (a) the user removes
  protection and then applies a move, which makes emptying it *correct*
  and expected, or (b) mods are moved via Penumbra's own UI directly,
  bypassing this plugin entirely — a pre-existing gap that already applies
  identically to individually-protected mods today (this plugin's
  protection never governed Penumbra's native UI, and can't). Folder
  Cleanup never deletes an occupied folder, protected or not, so there is
  no new risk introduced here.
- **`ProtectAndSkipBlockingMods`**: calls the (now-corrected)
  `SetProtected`, unchanged call pattern.
- **`ExecuteOrderedMoves`/`ApplyPlanner`**: operate purely on an
  already-filtered move list built after protection is checked; no
  protection logic lives here, nothing to change.

## Mutation methods (fixing the persistence and clobber bugs — review points #2, #3)

```csharp
// OrganizerState
public void SetProtected(string identifier, bool value)
{
    if (value) _protectedModIdentifiers.Add(identifier);
    else _protectedModIdentifiers.Remove(identifier);
    if (_mods.TryGetValue(identifier, out var row))
        row.Protected = IsEffectivelyProtectedAfterIndividualToggle(row);
}

public void SetAllProtection(bool value)
{
    if (value) _protectedModIdentifiers.UnionWith(_mods.Keys);
    else _protectedModIdentifiers.Clear(); // clears explicit set only
    foreach (var row in _mods.Values)
        row.Protected = IsEffectivelyProtectedAfterIndividualToggle(row);
}

public void SetFolderProtected(string folderPath, bool value)
{
    var normalized = NormalizeFolderPath(folderPath);
    if (normalized is null) return;
    if (value) _protectedFolders.Add(normalized);
    else _protectedFolders.Remove(normalized);
    foreach (var row in _mods.Values)
        row.Protected = IsEffectivelyProtectedFull(row);
}
```

`Plugin.SaveProtectionState()` is corrected to persist the **explicit
sets**, never the derived boolean:

```csharp
internal void SaveProtectionState()
{
    Config.ProtectedModIdentifiers = OrganizerState.ProtectedModIdentifiers.ToHashSet();
    Config.ProtectedFolderPaths = OrganizerState.ProtectedFolders.ToHashSet();
    PluginInterface.SavePluginConfig(Config);
}
```

(`OrganizerState.ProtectedModIdentifiers`/`.ProtectedFolders` are new
`IReadOnlyList<string>`-returning accessors over the private explicit
sets.) This is the direct fix for review point #3: a mod protected only
via a folder rule is never written into `ProtectedModIdentifiers`, so
removing the folder rule correctly leaves it unprotected.

**Save timing (review point #21):** every mutation method above is called
synchronously from its button/checkbox handler in `MainWindow.cs`,
immediately followed by `_plugin.SaveProtectionState()` — the identical
pattern individual protection already uses today, extended to folder
protection. `SaveProtectionState()`'s call to `PluginInterface.
SavePluginConfig` is wrapped in try/catch at the `MainWindow` call site
(matching the error-handling/logging pattern already added elsewhere this
session): on failure, surface via `_lastError` and `Plugin.Log.Error`,
never silently pretend the toggle persisted.

**Backward compatibility (review point #20):** `Configuration.
ProtectedFolderPaths` is added with a non-null default (`= [];`),
identical to the existing `ProtectedModIdentifiers` pattern already
proven safe across this plugin's config version history.

## UI: Protect tab

- Search text input at the top (`string _protectFilter`, Protect-tab-local
  state — confirmed: separate from the Sort tab's, not shared).
- **Search fields, explicit (review point #9):** mods match on Name,
  Identifier, Author, or CurrentPath (case-insensitive substring); folders
  match on their full normalized path (case-insensitive substring).
- **Folder list = `KnownFolders ∪ ProtectedFolders` (review point #12),
  not just `KnownFolders`.** `KnownFolders` is every distinct
  `GetVirtualParent` result among currently scanned mods. Persisted
  protected folders that currently have zero scanned mods still appear in
  the list, marked (e.g. grayed, "currently empty") so a stale rule is
  never invisible or un-removable.
- **Folder row checkbox semantics (review point #11):** the checkbox
  reflects **exact membership** in `ProtectedFolders`, never inherited
  coverage from an ancestor. If a folder is covered by an ancestor's
  protection but isn't itself in `ProtectedFolders`, it shows unchecked
  with disabled annotation text: `Covered by protected folder "Gear"`.
  This is what makes the checkbox editable and honest — checking/
  unchecking it always means "add/remove this exact path," never a
  confusing derived state.
- **Individual mod row annotation:** a mod that's effectively protected
  but not present in `ProtectedModIdentifiers` (i.e., protected via
  Heliosphere or a folder) shows its checkbox checked with grayed suffix
  text explaining why, e.g. `(via folder: Gear/Feet)` or `(Heliosphere)`,
  addressing the review's "communicate why" ask (part of point #4's
  Option 1) without disabling the control (Option 2's mechanics, chosen
  because it keeps `SetProtected`'s explicit-set semantics simple and
  uniform).
- **"Toggle protect all" / "Toggle Heliosphere protection" (review point
  #10):** both remain global/unfiltered — they act on every mod
  regardless of the current search text, identical scope to today. This
  is a deliberate choice to avoid a silent behavior change to two buttons
  whose existing meaning ("all") the search bar shouldn't quietly
  redefine to "all visible." Their labels stay as-is since their scope
  doesn't change. If filtered-only bulk actions are wanted later, that's
  an additive, separately-named button ("Protect visible"), not a
  redefinition of these two.

## UI: Sort tab, manual assign

- `HashSet<string> _selectedManualModIdentifiers` replaces
  `_selectedManualModIdentifier`; checkboxes replace radio buttons.
- Its own independent search field (`string _manualAssignFilter`),
  matching mod Name, Identifier, **and CurrentPath** (review point #17 —
  not name-only, since duplicate display names are common and need a way
  to disambiguate). Each row always renders the mod's current path
  alongside its name regardless of search state, for the same reason.
- **Selection persists across search text changes (review point #13)** —
  checking a mod, then narrowing the search, then widening it again, does
  not lose the selection. The Assign button's label states the count:
  `Assign 7 selected mods to folder`.
- **Reconciliation before render and before Assign (review points #14,
  #18):** on every draw, `_selectedManualModIdentifiers.
  IntersectWith(currentEligibleIdentifiers)` where eligible = present in
  `OrganizerState.Mods` and `!Protected` — silently drops selections for
  mods that disappeared, became protected (by any source, including a
  folder rule toggled on the Protect tab moments earlier), or were
  otherwise invalidated. This runs before rendering the checklist (so
  stale checkmarks never display) and is inherently already current by
  the time Assign executes (same draw frame).
- **Batch assign method, not an unguarded loop (review point #15):**
  ```csharp
  // OrganizerState
  public IReadOnlyList<(string Identifier, bool Success)> AssignManualBatch(
      IReadOnlySet<string> identifiers, string folder)
  {
      var normalizedFolder = folder.Trim().Trim('/');
      if (normalizedFolder.Length == 0)
          return identifiers.Select(id => (id, false)).ToList();
      var results = new List<(string, bool)>();
      foreach (var identifier in identifiers)
      {
          if (!_mods.TryGetValue(identifier, out var mod))
          {
              results.Add((identifier, false));
              continue;
          }
          results.Add((identifier, AssignManual(identifier, $"{normalizedFolder}/{mod.Name}")));
      }
      return results;
  }
  ```
  **Why a report-per-item batch, not pre-validate-then-mutate-all (review
  point #15's second option, chosen over the first):** each item's only
  failure modes (unknown identifier, protected row — both already checked
  inside the existing single-item `AssignManual`) are independent of every
  other item; assigning mod A never affects whether mod B's assignment can
  succeed. True pre-validation would add complexity without preventing any
  real partial-failure scenario, since there is no shared mutable state
  between items to corrupt. The UI reports the returned per-item results:
  `"18 assigned, 2 skipped (no longer eligible)"`.
  **Blank-folder guard (review point #16):** the batch method rejects an
  empty/whitespace destination up front rather than constructing `/ModName`
  paths. Existing `AssignManual` already writes `ProposedPath` directly
  without its own collision check — **same-name-collision handling is
  unchanged and pre-existing**: `Validate()` on the Review Changes tab
  already catches a same-destination collision after the fact (exactly as
  a single manual assign can already produce one today). Multi-select
  makes this more likely to occur, not newly possible; no new
  disambiguation logic is added by this design — the existing
  `CollisionDisambiguator` machinery is Sort-only and stays that way,
  consistent with today.

## Files

**Modified:**
- `Configuration.cs` — add `ProtectedFolderPaths` (`HashSet<string> = [];`).
- `Organizer/OrganizerState.cs` — private explicit `_protectedModIdentifiers`/
  `_protectedFolders` sets (constructed with `StringComparer.Ordinal` /
  `.OrdinalIgnoreCase` respectively), `ProtectedModIdentifiers`/
  `ProtectedFolders` read accessors, `SetFolderProtected`, `KnownFolders`,
  `AssignManualBatch`, corrected `SetProtected`/`SetAllProtection`,
  extended `LoadScan` signature (now also takes protected folder paths),
  `NormalizeFolderPath`, `IsUnderAnyProtectedFolder` (may live on
  `RollbackHistory` instead if `Plugin.Restore` needs it standalone — see
  below), the two recompute-formula private methods.
- `Organizer/RollbackHistory.cs` — expose `IsUnderAnyProtectedFolder` as
  `internal static` (or duplicate the small pure function; a shared
  location avoids duplication, since both `OrganizerState` and
  `Plugin.Restore` need it and neither currently references the other).
- `Plugin.cs` — `RunScan()` passes `Config.ProtectedFolderPaths` into
  `LoadScan`; corrected `SaveProtectionState()`; `Restore()` builds
  `lockedIdentifiers` as shown above before calling `BuildRestorePlan`.
- `Windows/MainWindow.cs` — Protect tab search + folder list + annotations;
  Sort tab manual-assign rework (checkboxes, search, batch assign, count
  label); try/catch around protection-toggle handlers' `SaveProtectionState()`.

## Testing

In addition to the original list, covering every review-flagged gap:

- Overlapping protected folders (`Gear` and `Gear/Feet` both protected,
  unprotect `Gear`, confirm `Gear/Feet` mods remain protected).
- Unprotecting a folder while a mod under it remains individually
  protected (confirm it stays protected).
- Unprotecting a folder while a mod under it remains Heliosphere-managed
  (confirm it stays protected).
- Boundary non-matches: protected `Body` vs. candidate parent
  `BodyMods/Author`; protected `Gear/Feet` vs. candidate parent
  `Gear/FeetExtra`.
- Recursive match: protected `Gear/Feet` matches candidate parent
  `Gear/Feet/Sub`.
- `NormalizeFolderPath`: backslashes, repeated separators, leading/
  trailing slashes, whitespace-only, empty string.
- Case-insensitive folder matching and set membership.
- `KnownFolders ∪ ProtectedFolders`: a persisted protected folder absent
  from the current scan still appears in the union.
- Root-level mod (`GetVirtualParent` returns null) is never matched by any
  protected folder.
- `SaveProtectionState` round-trip: a folder-only-protected mod's
  identifier is never present in the persisted `ProtectedModIdentifiers`.
- `SetProtected` on a Heliosphere mod: confirm the existing
  transient-until-next-scan behavior is preserved unchanged (not broken by
  this change).
- `SetProtected` on a folder-protected mod: confirm it recomputes to
  `true` in the same call (no transient window), unlike the Heliosphere
  case above.
- `AssignManualBatch`: blank folder rejected without mutation; unknown
  identifier reported as failed without affecting other items; a
  now-protected mid-batch identifier (simulated) reported as failed;
  duplicate destination from two same-named mods produces two successful
  `ProposedPath` writes that later trip `Validate()`'s existing collision
  detection (not a new mechanism).
- Selection reconciliation: a selected identifier that becomes protected
  or disappears from `Mods` between selection and render/Assign is
  dropped automatically.
- `Plugin.Restore`'s `lockedIdentifiers` construction: a mod protected
  only via a folder rule is included in the locked set passed to
  `BuildRestorePlan` (this is an integration-level check at the
  `Plugin.cs` call site, not a `BuildRestorePlan` unit test, since
  `BuildRestorePlan` itself is unchanged).

`MainWindow.cs` changes remain unverified by unit tests, consistent with
this codebase's existing convention for the UI layer — verified by build +
full suite + in-game check after implementation.

## Explicitly out of scope

- Reading Penumbra's `organization.json` to show empty folders as
  protectable before any mod occupies them.
- A per-mod "exception" override that opts a specific mod out of an
  otherwise-protected folder.
- Sharing search-bar state between the Protect tab and Sort tab.
- Root-level protection as a location rule.
- Unifying Heliosphere's transient-override behavior with folder
  protection's immediate-recompute behavior — flagged above as a
  deliberate, disclosed choice, reversible on request.
- Any change to `ApplyPlanner`, `CollisionDisambiguator`, or Folder
  Cleanup's own logic — audited above, none needed.
