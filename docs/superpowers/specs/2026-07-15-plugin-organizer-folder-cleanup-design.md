# Plugin organizer, Folder Cleanup (organization.json orphaned-folder prune) — Design

**Status:** implemented, pending in-game verification. Reached via brainstorming with the user on 2026-07-15,
immediately after Phase 2 (Apply)'s in-game verification surfaced the orphaned-folder symptom live
(confirmed to survive Rollback, a full game restart, and disabling/re-enabling Penumbra). Revised
2026-07-15 after two rounds of external design review plus a final self-review pass — see "Revision
notes" at the end for what changed and why in each round.

## Context

`docs/HANDOFF_PHASE2_APPLY.md` and `docs/ROADMAP.md` both document Penumbra's orphaned-empty-folder
behavior as a known, deliberately out-of-scope limitation of Phase 2: folder *existence*
(`organization.json`) is tracked independently of mod *placement* (`mod_data.db`), and nothing in
Penumbra's own logic prunes an entry when a folder goes empty. No IPC exposes this — closing the gap
requires directly reading/writing `organization.json`, a genuine write-scope expansion beyond
`SetModPath`, requiring its own explicit re-confirmation (the same kind Phase 2 itself required
before it started). That re-confirmation happened in this brainstorming session — the user
explicitly opted for full automated prune capability, not just detection.

The sibling standalone WPF app (`C:\Repo\PenumbraOrganizer`) hit and solved a version of this same
problem already. Its investigation (`docs/KNOWN_ISSUE_EMPTY_FOLDERS_AFTER_RESORT.md`) and design
(`docs/superpowers/specs/2026-07-09-organization-json-cleanup-design.md`) in that repo establish the
file schema this spec reuses. But that app's entire safety model for this feature rests on a
constraint that does not carry over here: `docs/PROJECT_CONTEXT.md` in that repo hard-requires the
standalone app work fully offline, with no game running — so it only ever writes
`organization.json` while Penumbra isn't a live process at all. This plugin runs in-process, live,
exactly while Penumbra is running. An external design review of this spec's first draft correctly
identified that this plugin ported the standalone app's *file write* without verifying whether its
*safety model* (never writing to a file a running process might also touch) transfers — it doesn't,
and the gap needed its own investigation (see "Live-tree propagation," below).

### Ground truth (verified against real source, not guessed)

**Schema**, from the standalone app's spec, re-confirmed independently in this session by reading
`Ottermandias/Luna`'s `Luna/Filesystem/FileSystemSaver.cs` directly:

```csharp
public class Organization : BaseFile
{
    public Dictionary<string, FolderData> Folders = [];
    public Dictionary<string, SeparatorData> Separators = [];
}
public readonly record struct FolderData(uint? ExpandedColor, uint? CollapsedColor, string? SortMode, bool? IsSeparator);
public readonly record struct SeparatorData(uint? Color, bool Folder, long CreationDate);
```

Real path: `<Penumbra config dir>/mod_filesystem/organization.json`. All `FolderData`/
`SeparatorData` fields are optional and omitted when unset. Penumbra writes one `Folders` entry per
folder node that has *ever* existed in its live tree, with no emptiness filter, and recreates every
one of those entries on every load, forever. Mod placement (`mod_data.db`'s `Folder` field) is
completely unrelated to this file — pruning orphaned folder entries can never affect mod placement.

**Penumbra config directory location** (new finding — the standalone app doesn't need this, since it
runs standalone and the user points it at Penumbra's config folder manually). No IPC exposes it:
reflecting `Penumbra.Api` 5.15.1's full `IpcSubscribers` surface (90 members, `Penumbra.Api.xml` from
the resolved NuGet package) confirms `GetModDirectory` returns the mod *storage* root, not the
plugin-config directory, and `GetConfiguration` returns config *content*, not a path — nothing else
in the IPC surface is a candidate. Resolved instead via Dalamud's own API: `PluginInterface
.ConfigDirectory` (this plugin's own config dir) and Penumbra's config dir are siblings under the
same Dalamud `pluginConfigs` folder, so `Directory.GetParent(PluginInterface.ConfigDirectory
.FullName)/Penumbra` resolves it with zero IPC calls and zero discovery heuristics.

**Live-tree propagation** (round 1 review's Critical Blocker #2 — investigated, not assumed). The
same 90-member `Penumbra.Api` 5.15.1 IPC surface has no reload/rebuild/delete-folder call of any
kind — confirmed by grepping every `IpcSubscribers` type name; the closest matches (`ReloadMod`,
`RedrawObject`/`RedrawAll`/`RedrawCollectionMembers`) reload a *single mod's own data* or redraw
*actor models*, neither touches the folder tree. However, a real, user-reachable mechanism does
exist, confirmed by reading Penumbra's and Luna's actual source:

- `Luna/Filesystem/Path/ModFileSystem.cs:22` (via `gh api` code search against
  `Ottermandias/Luna`): `_communicator.ModDiscoveryFinished.Subscribe(_saver.Load, ...)` — every time
  Penumbra finishes a mod-discovery pass, it re-invokes `FileSystemSaver.Load()`, which (per
  `Luna/Filesystem/FileSystemSaver.cs`) unsubscribes change notifications, clears the live tree, and
  calls `HandleOrganization()` to rebuild it fresh from `organization.json` on disk.
- `xivdev/Penumbra`'s `Penumbra/UI/Tabs/SettingsTab.cs:305-306` (via `gh api` code search against
  `xivdev/Penumbra`): a manual **"Rediscover Mods"** button calls `_modManager.DiscoverMods()`, which
  fires `ModDiscoveryFinished`. This is reachable by the user today, in Penumbra's own Settings tab,
  with no IPC and no code from this plugin.

So: writing a pruned `organization.json` does **not** by itself update Penumbra's live tree. **This is
a documented operating constraint on the user, not just a residual risk to note:** after every
cleanup or rollback, the user must click Penumbra's own "Rediscover Mods" *before* making any other
folder-tree change inside Penumbra's own UI (renaming, recoloring, moving, creating a folder). Doing
anything else first risks Penumbra saving its still-stale live tree back over the file this plugin
just wrote, silently undoing the action. This governs the UI and Backup/rollback mechanics sections
below, and is restated as an explicit instruction there, not left implicit.

**`Folders`/`Separators` are disjoint by construction** (round 1 review's Critical Blocker #3 —
investigated, not assumed). Read `OrganizationData.Save()` directly in `Luna/Filesystem
/FileSystemSaver.cs`: it populates `Folders` from `saver.FileSystem.Root.GetDescendants()
.OfType<FileSystemFolder>()` and `Separators` from the same descendants filtered
`.OfType<FileSystemSeparator>()` — type-filtered, disjoint enumeration; no path can appear as a key in
both dictionaries. `HandleOrganization()`'s load path reconstructs each independently
(`ApplyFolder`/`ApplySeparator`), and `BaseFileSystem.Delete()` (`Luna/Filesystem
/BaseFileSystem.cs`) removes a node uniformly regardless of type, with no cross-dictionary cleanup —
none is needed, because the two are already mutually exclusive by the time `Save()` runs.
`FolderData.IsSeparator` exists as a field but is irrelevant here: a real `Folders` entry is never
also a `Separators` entry, so pruning `Folders` keys while leaving `Separators` completely untouched
is provably correct, not merely assumed safe by inheritance from the standalone app's own explicitly
*unresolved* open risk on this exact question.

**Occupancy comparer**, confirmed against the standalone app's actual shipped implementation
(`PenumbraOrganizer.Infrastructure/Apply/PenumbraVirtualFolderWriter.cs:192-196`,
`IsFolderOccupied`): `StringComparer.Ordinal`, exact match OR `StartsWith(folder + "/",
StringComparison.Ordinal)` — segment-boundary-safe, not a bare `StartsWith`. Matches this plugin's
own existing convention (`ApplyPlanner`/`CollisionDisambiguator` both use `StringComparer.Ordinal`
throughout).

## Goal

Detect orphaned (empty) folder entries in Penumbra's `organization.json` and let the user prune
selected ones, as a separate action from mod-move Apply/Rollback, with its own backup/rollback
safety net, using Penumbra's own current live placement (not any unapplied proposal) as the source
of truth for what's actually empty, and being explicit in the UI about the gap between "the file is
correct" and "Penumbra has loaded the change."

## Non-goals

- Bundling folder cleanup into the existing mod-move Apply button. Deliberately a separate action
  (own button, own list, own backup file) — decided during brainstorming, mirrors the standalone
  app's "separate, independent restore" decision for the same file.
- A numeric safety cap on folders prunable per click. Decided during brainstorming: the two-tier
  checkbox UI (see UI) is the real safety net; a cap would mostly add friction for large libraries.
  Matches how Phase 2 (Apply) itself shipped with no cap.
- Hash-based staleness tracking between "user reviewed the orphan list" and "user clicked clean up"
  (the standalone app's approach). Not adopted — this plugin's entire Apply/Rollback pattern is
  already "read live state, act immediately, no dry-run/transaction layer," and a multi-millisecond
  race window between an ImGui click and the write happening is negligible. Re-verification happens
  once, immediately before the write (see Backup/rollback mechanics), not via cached hashes.
- Speculating about a future, not-yet-applied mod-move Apply. Occupancy is computed from
  `OrganizerState.Mods[].CurrentPath` — Penumbra's actual current placement as of the last scan —
  never `ProposedPath`. Folder Cleanup has no transactional relationship to Apply; treating
  `ProposedPath` as "what will soon be true" would misclassify currently-occupied folders as
  orphaned whenever the user has sorted but not yet clicked Apply.
- Any change to `mod_data.db`/mod placement, or to the existing mod-move Apply/Rollback pipeline.
  Folder cleanup and mod-move Apply remain fully independent write targets.
- A SHA-256/metadata-wrapper validation file alongside the raw backup (`organizer-folder-
  backup.meta.json` or similar). Considered during round 1 review; not adopted, matching Phase 2's
  own precedent for the analogous question ("a corrupt/unreadable backup file is an edge case rare
  enough to handle if it actually comes up rather than design for now"). The backup file is written
  atomically by this plugin's own code, so external corruption is the only way it could be invalid;
  the existing Parse-and-version-gate check (already needed for normal reads) is reused before
  restore, which is proportionate without new infrastructure.
- A dedicated "Refresh Folder Status" button distinct from the existing Scan action. Considered
  during round 2 review (offered as one of two options); not adopted — `RunScan()` already exists,
  is already the plugin's one "give me fresh state" action, and already triggers an
  `_orphanedFolders` recompute (see UI). A second button with the same effect would be redundant.
- **A multi-generation backup history.** Same limitation Phase 2 already has for mod-move
  Rollback (one rolling backup, not a history — "Rollback" always means "undo the most recently
  completed action," and a second Clean Up / Apply before rolling back the first loses the ability
  to recover the earlier state). The user explicitly flagged this as worth a **future**, separate
  plan covering both mod-move and folder-cleanup backups together — not addressed by this spec. See
  Open risks.

## Architecture

`OrganizerState` gets one new property, needed because "no scan yet" and "scanned, zero mods" are
different states that the round 2 review correctly pointed out `Mods.Count == 0` can't distinguish —
and a genuinely empty mod library is exactly the population where *every* persisted folder may
legitimately be orphaned, so collapsing that case into "treat as unscanned, show nothing" would
disable the feature precisely where it's most useful:

```csharp
// Organizer/OrganizerState.cs
public bool HasScanned { get; private set; } // set true at the top of LoadScan(); never reset
```

Four new files/types, following the established pattern (`ApplyPlanner.cs`,
`CollisionDisambiguator.cs`), split so parsing/version-gating is pure and testable without file I/O:

```csharp
// Organizer/OrganizationJson.cs — plain data model, mirrors the confirmed schema
public sealed class FolderData
{
    public uint? ExpandedColor { get; set; }
    public uint? CollapsedColor { get; set; }
    public string? SortMode { get; set; }
    public bool? IsSeparator { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SeparatorData
{
    public uint? Color { get; set; }
    public bool Folder { get; set; }
    public long CreationDate { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class OrganizationJson
{
    public int Version { get; set; }
    public Dictionary<string, FolderData> Folders { get; set; } = new();
    public Dictionary<string, SeparatorData> Separators { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
```

`[JsonExtensionData]` on all three types: if a future Penumbra version adds optional fields without
bumping `Version`, a naive round-trip would silently discard them on the one path that reserializes
(pruning). This plugin is rewriting a config file it doesn't own; preserving unknown fields is cheap
and directly avoids real data loss.

```csharp
// Organizer/OrganizationJsonCodec.cs — pure parse/serialize, no file I/O
public enum OrganizationJsonParseStatus { Ok, MalformedJson, UnsupportedVersion }

public sealed record OrganizationJsonParseResult(OrganizationJson? Data, OrganizationJsonParseStatus Status);

public static class OrganizationJsonCodec
{
    // Never throws. Data is non-null exactly when Status == Ok. The two failure modes are
    // distinguished (not collapsed into one null) because FolderDetectionStatus and
    // FolderCleanupStatus both report them as different states to the user.
    public static OrganizationJsonParseResult Parse(string json);

    public static string Serialize(OrganizationJson data); // omits null properties; matches source conventions
}
```

```csharp
// Organizer/OrganizationCleanupPlanner.cs — pure, static
// CustomizedFolder carries a human-readable summary of what's customized ("custom expanded
// color, sort: FoldersFirst") because the UI renders it next to each unchecked entry — a bare
// path list can't carry that.
public sealed record CustomizedFolder(string Path, string Description);

public static class OrganizationCleanupPlanner
{
    public static (IReadOnlyList<string> PlainEmpty, IReadOnlyList<CustomizedFolder> CustomizedEmpty)
        DetectOrphaned(OrganizationJson data, IReadOnlySet<string> occupiedFolders);

    public static OrganizationJson Prune(OrganizationJson data, IReadOnlySet<string> selectedPaths);

    // Parent-folder extraction for Penumbra virtual paths (forward-slash separated, not
    // System.IO.Path-safe). A path with no '/' is a root-level mod — it occupies no folder at
    // all, so this returns null rather than an empty string or the mod's own name. Trailing
    // slashes are trimmed first (defensive — scan output shouldn't produce them, but a
    // trailing-slash path must not yield the mod's own path as its "parent"); a leading slash
    // falls out of the index > 0 check (index 0 → null, treated as root-level).
    public static string? GetVirtualParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index > 0 ? trimmed[..index] : null;
    }
}
```

`DetectOrphaned` classifies every key in `Folders` as occupied or orphaned: a folder counts as
occupied if it equals or is a prefix of any entry in `occupiedFolders`, using the confirmed
`StringComparer.Ordinal` / `StartsWith(folder + "/", Ordinal)` logic (see Ground truth) — not a bare
`StartsWith`, which would wrongly match `"Body"` against `"BodyMods/Author/Mod"`. Orphaned entries
split into `PlainEmpty` (every known `FolderData` field is `null` **and** `ExtensionData` is null or
empty — an entry customized only via a field this plugin doesn't know about must get the
higher-friction treatment, since `JsonExtensionData` exists precisely to protect data we can't
interpret) and `CustomizedEmpty` (anything else). `Prune` returns a copy of `data` with
`selectedPaths` removed from `Folders`; `Separators` is passed through unmodified — provably safe per
the Ground truth section's source-confirmed disjointness, not an assumption.

**Occupied-folder set — two different sources for two different jobs:**

- **Detection (the UI list)** derives it from `OrganizerState.Mods[].CurrentPath` (see Non-goals —
  not `ProposedPath`), via `GetVirtualParent` on each mod's `CurrentPath`, discarding any `null`
  result (root-level mods contribute no folder to the occupied set — there's no folder entry to
  protect on their behalf). Last-scan freshness is acceptable here: the list is advisory, and the
  write path re-derives everything below.
- **The write path (`CleanUpFolders`)** must not trust last-scan state: if the user moved a mod into
  a folder via Penumbra's own UI *after* the last scan, `OrganizerState` still shows that folder as
  empty, and a re-verification against the same stale data would wave through pruning an occupied
  folder. So `CleanUpFolders` makes a **fresh `GetModListAdapter` IPC read** at write time and
  computes occupancy from those live `FullPath` values instead. Deliberately *not* a `RunScan()`
  call — `RunScan` resets every row's `ProposedPath = CurrentPath`, which would silently wipe any
  staged-but-unapplied sort the user has sitting in the Review tab; the IPC read is used for
  occupancy only and never touches `OrganizerState`. If the IPC call throws (Penumbra unavailable),
  the exception propagates before any file is touched — an aborted cleanup, not a stale-data one.
  (Consequence note for why this matters more for customized folders: pruning an actually-occupied
  *plain* folder is nearly self-healing — Penumbra recreates folder nodes from mod placement on next
  load — but a customized folder's color/sort metadata would be permanently lost.)

The file-I/O sequencing (read → parse → re-verify → prune-write → backup-promote, and the rollback
mirror) lives in its own static class rather than inline in `Plugin.cs`, because the Testing
section's two required integration-style tests must drive this sequencing against a temp directory
— and `Plugin.CleanUpFolders` contains a live IPC call, so it can't be the test entry point. The
executor takes paths and an occupancy set as plain parameters and has no Dalamud/IPC dependency:

```csharp
// Organizer/FolderCleanupExecutor.cs — all file I/O sequencing, no IPC, no Dalamud types
public static class FolderCleanupExecutor
{
    public static FolderCleanupResult Execute(
        string organizationJsonPath,
        string backupFilePath,
        IReadOnlySet<string> selectedPaths,
        IReadOnlySet<string> occupiedFolders);

    public static FolderRollbackResult ExecuteRollback(
        string organizationJsonPath,
        string backupFilePath);
}
```

`Plugin.cs` gets thin wrappers that resolve the real paths, supply occupancy (fresh IPC read for
cleanup, last-scan `OrganizerState` for detection), and delegate:

```csharp
internal FolderDetectionResult DetectOrphanedFolders();
internal FolderCleanupResult CleanUpFolders(IReadOnlySet<string> selectedPaths);
internal FolderRollbackResult RollbackFolderCleanup();
```

```csharp
// Organizer/FolderCleanupResult.cs
public enum FolderDetectionStatus
{
    Detected,           // lists are meaningful (possibly both empty — genuinely no orphans)
    NotScanned,         // no scan yet this session; file not read at all
    FileMissing,
    UnsupportedVersion,
    MalformedJson,
}

public sealed record FolderDetectionResult(
    IReadOnlyList<string> PlainEmpty,
    IReadOnlyList<CustomizedFolder> CustomizedEmpty,
    FolderDetectionStatus Status);

public enum FolderCleanupStatus
{
    Success,               // pruned and backed up
    SucceededBackupFailed, // pruned, but the new backup could not be written — see Backup/rollback mechanics
    NothingSelected,
    NothingStillValid,
    FileMissing,
    UnsupportedVersion,
    MalformedJson,
}

public sealed record FolderCleanupResult(
    IReadOnlyList<string> Pruned,
    IReadOnlyList<string> SkippedStale,
    FolderCleanupStatus Status);

public enum FolderRollbackStatus
{
    Restored,
    NoBackup,
    InvalidBackup,
}

public sealed record FolderRollbackResult(FolderRollbackStatus Status);
```

Structured results (round 1 review's suggestion, extended in round 2 to cover rollback and in the
self-review pass to cover detection too) replace bare return types on all three operations — the UI
needs to render meaningfully different states everywhere (a missing/invalid backup, a
partially-completed cleanup, a fully clean success, and at detection time the difference between "no
orphans found" and "your organization.json couldn't be read at all"), none of which a bare list,
tuple, or `void` can express. Without a detection status, a malformed or unsupported-version file
would render identically to a perfectly healthy empty result, with the real reason buried in a log
line.

## Backup/rollback mechanics

Distinct from mod-move Apply/Rollback in one important way: this is a **single atomic file write**,
not a loop over N independent IPC calls. There is no per-folder partial-success/failure model to
track for the *prune* itself — that write either fully succeeds or throws. But the prune and the
backup-file write are two separate operations, and the second can fail independently of the first
succeeding — see step 7 below, which names that outcome explicitly instead of leaving it unhandled.

- `FolderBackupFilePath` → `organizer-folder-backup.json`, sibling to `organizer-backup.json` in the
  plugin's config directory. Fully independent file, independent of the mod-move backup.
- `FolderBackupExists` → `File.Exists(FolderBackupFilePath)`, same pattern as `BackupExists`, gates
  the Rollback Folder Cleanup button's visibility.
- **Backup content is the raw original bytes of `organization.json`**, not a reserialized model —
  restored byte-for-byte on rollback. This guarantees rollback fidelity regardless of any
  reserialization-formatting difference on the forward (prune) path.

`CleanUpFolders(selectedPaths)` — every step below operates on one in-memory variable,
`originalBytes`, read exactly once at step 1 and never re-read from disk afterward, so the backup
this method eventually writes can never accidentally be built from the *pruned* file:

1. Read `organization.json` fresh into `originalBytes` (a `byte[]`/`string`, retained for the rest of
   this call). Missing file → `FolderCleanupStatus.FileMissing`, nothing written, return.
2. `OrganizationJsonCodec.Parse(originalBytes)`. `null` (malformed JSON or `Version != 1`) →
   `MalformedJson`/`UnsupportedVersion` respectively, nothing written, return.
3. Compute the occupied-folder set from a **fresh `GetModListAdapter` IPC read** (see Architecture,
   "Occupied-folder set" — not from `OrganizerState`, which is only as fresh as the last scan and
   can't see mods the user moved via Penumbra's own UI since then; and not via `RunScan()`, which
   would wipe staged sort proposals). If the IPC call throws, the exception propagates — nothing has
   been written yet, so this is a clean abort.
4. Re-verify each path in `selectedPaths` is still present in the parsed `Folders` and still orphaned
   under that live occupancy. Anything that fails re-verification is dropped into `SkippedStale`.
5. **If nothing survives step 4** (`stillValidSelectedPaths` is empty): return immediately with
   `NothingStillValid` (or `NothingSelected` if `selectedPaths` was empty to start with).
   `originalBytes` is discarded unused. **Do not** write the backup file, serialize anything, or
   touch `organization.json` — a no-op attempt must never overwrite a previous valid rollback point
   (see Error handling for why this matters).
6. Compute `OrganizationCleanupPlanner.Prune(parsedData, stillValidSelectedPaths)`, serialize via
   `OrganizationJsonCodec.Serialize` into `prunedJson`.
7. Write `prunedJson` to `organization.json` via the temp-file-then-move atomic pattern `WriteBackup`
   already uses. If this throws, the exception propagates unhandled (caught by `MainWindow`'s
   existing `_lastError` pattern) — `organization.json` is left in its pre-attempt state (the atomic
   move means a failed write can't leave a half-written file), and no backup write is even attempted.
8. **Only after step 7 succeeds**, attempt to write `originalBytes` — the exact bytes retained in
   step 1, **never a fresh read of `organization.json`**, which by now holds the pruned content, not
   the original — to `organizer-folder-backup.json`, same atomic temp-then-move pattern.
   - If step 8 throws (e.g. disk full), catch it specifically and return
     `FolderCleanupResult(Pruned: stillValidSelectedPaths, SkippedStale: ..., Status:
     SucceededBackupFailed)` rather than letting the exception propagate as a generic error. **This
     is a partial-infrastructure failure, not a failed cleanup:** the prune in step 7 already
     succeeded and is not rolled back. Because the backup write is itself atomic, a failure here
     cannot corrupt or partially overwrite whatever backup file existed before this call (from an
     earlier, not-yet-rolled-back cleanup) — it's left exactly as it was. **But that surviving old
     backup is now misleading:** it captures the state before the *previous* cleanup, not before
     this one, and since `FolderBackupExists` is what shows the Rollback button, the button stays
     visible pointing at a state older than the user expects — clicking it would revert *both*
     cleanups, not just this one. The UI must surface this as a high-severity, distinctly worded
     message that says so explicitly (see UI), rather than the normal success line.
9. Return `FolderCleanupResult(Pruned: stillValidSelectedPaths, SkippedStale: ..., Status: Success)`.

`RollbackFolderCleanup()`:

1. If no backup file exists, return `FolderRollbackResult(NoBackup)` (defensive; UI already gates the
   button on `FolderBackupExists`).
2. Read the raw backup bytes and validate them with the same `OrganizationJsonCodec.Parse` check
   used for normal reads (well-formed JSON, `Version == 1`) before trusting them. If validation
   fails, return `FolderRollbackResult(InvalidBackup)` **without touching `organization.json`** —
   never overwrite a possibly-valid live file with unverified bytes.
3. Write the raw backup bytes back to `organization.json` (atomic temp-then-move).
4. Delete the backup file.
5. Return `FolderRollbackResult(Restored)`. No `RunScan()` call needed — folder cleanup never touches
   mod placement, so `OrganizerState`'s mod rows are unaffected. Only the cached `_orphanedFolders`
   UI field and `_folderReloadRequired` flag need updating (see UI).

**Required manual step, both directions, restated as an operating constraint (not merely a note):**
per the Ground truth section's live-tree-propagation finding, neither `CleanUpFolders` nor
`RollbackFolderCleanup` updates Penumbra's live folder tree by itself. After either one returns a
result that actually touched the file (`Success`, `SucceededBackupFailed`, or `Restored`), the user
must open Penumbra's own Settings tab and click **"Rediscover Mods"** *before* making any other
folder-tree change inside Penumbra's own UI — see UI for how this is surfaced and tracked.

## UI

`MainWindow.cs`, `DrawReviewTab()`, new section below the existing Apply/Rollback/Protect & Skip
block. New state:

```csharp
private FolderDetectionResult? _orphanedFolders;
private readonly HashSet<string> _selectedOrphans = new(StringComparer.Ordinal);
private bool _folderReloadRequired;
```

`_selectedOrphans` holds the checkbox state. Whenever `_orphanedFolders` recomputes (any of the
triggers below), the selection resets to defaults — all `PlainEmpty` entries selected, all
`CustomizedEmpty` entries deselected — rather than trying to carry old selections across a refresh:
the recompute means the world changed, and a stale selection surviving it is exactly the kind of
state the write-time re-verification exists to catch, better prevented here than relied on there.

When `_orphanedFolders.Status` is `UnsupportedVersion` or `MalformedJson`, the section renders a
one-line note ("organization.json couldn't be read — folder cleanup unavailable (unsupported
version / unreadable file)") instead of an empty list, so a broken file is distinguishable from a
healthy zero-orphan state without digging through logs. `NotScanned` and `FileMissing` render
nothing — both are ordinary, expected states.

`_folderReloadRequired` models the gap the round 2 review identified: after a successful write, the
*file* is correct but Penumbra's *live tree* isn't confirmed to reflect it yet, and this plugin has
no IPC signal that would tell it when — or whether — the user actually clicked "Rediscover Mods." So
this flag is never auto-cleared by a timer or a "looks probably fine now" heuristic; it's set exactly
when a write happens, and cleared only by the same explicit user action that also refreshes
`_orphanedFolders`:

```
Orphaned Folders (5 detected)

Empty, no customization (3) — pre-checked
  [x] Empty/Modding/OldCreatorA
  [x] Empty/Modding/OldCreatorB
  [x] Empty/Review/Unsorted

⚠ Empty but customized (2) — unchecked, review before pruning
  [ ] Favorites  (custom color, sort: FoldersFirst)
  [ ] Legacy/2025Mods  (custom color)

[Clean Up Selected Folders]   [Rollback Folder Cleanup]
```

(Corrected from the first draft, which wrongly showed a `Separators`-dictionary entry —
"Archive (separator)" — as a customized-*folder* prune candidate. Separators are a different,
disjoint node type never read for this feature at all; see Ground truth.)

**Result messaging, keyed on `FolderCleanupResult.Status` / `FolderRollbackResult.Status` — never
phrased as if the operation is fully complete when only the file write succeeded:**

| Status | UI text |
|---|---|
| `Success` | "5 folder entries removed from organization.json. **Penumbra hasn't loaded this change yet** — open Penumbra's Settings tab and click Rediscover Mods before making any other folder changes there." |
| `SucceededBackupFailed` | (high-severity styling) "5 folder entries removed, but the rollback backup could not be saved: `<error>`. Rediscover Mods in Penumbra now, then avoid running another cleanup until you've confirmed the result — there is no safety net for this action right now. If a Rollback button is visible, it restores an **older** backup that predates this cleanup — clicking it would undo more than just this action." |
| `NothingStillValid` | "Nothing was cleaned up — the selected folder(s) are no longer orphaned (or no longer exist). No files were changed." |
| `NothingSelected` / `FileMissing` / `UnsupportedVersion` / `MalformedJson` | Informational, no file-change implication either way. |
| Rollback `Restored` | "Backup restored to organization.json. **Penumbra hasn't loaded this change yet** — click Rediscover Mods." |
| Rollback `InvalidBackup` | "The backup file is unreadable or unsupported — rollback aborted, organization.json was not touched." |
| Rollback `NoBackup` | Not normally reachable (button is hidden), shown only defensively. |

Both `Success`/`SucceededBackupFailed`/`Restored` set `_folderReloadRequired = true` in addition to
their result text. While `_folderReloadRequired` is `true`, the Orphaned Folders section leads with a
persistent banner ahead of the checkbox list: *"⚠ Waiting on Rediscover Mods — the list below reflects
`organization.json` on disk, not Penumbra's confirmed live state. Click Rediscover Mods in Penumbra,
then Scan here, to re-check."* `_orphanedFolders` **is** still refreshed from disk immediately after a
write (it's honest about file state), but the banner makes clear that "not in this list anymore"
doesn't mean "confirmed gone from Penumbra's UI" — those are different claims, and only the file-state
one is something this plugin can actually verify.

`_folderReloadRequired` is cleared the next time `RunScan()` runs (see Non-goals — no separate
"Refresh Folder Status" button; `RunScan()` already exists and already triggers an
`_orphanedFolders` recompute). This is explicitly **not** a claim that rediscovery was confirmed to
have happened — there's no real signal for that — only that the user took a deliberate action after
being warned, which is the best acknowledgment available without an IPC that doesn't exist.

- Clicking **Clean Up Selected Folders** opens a confirmation popup listing the exact paths that
  will be pruned (matching the mod-move Apply button's existing confirmation-popup pattern), then
  calls `CleanUpFolders()` and renders the table above.
- **Rollback Folder Cleanup** is visible only while `FolderBackupExists`.
- **Performance:** `DetectOrphanedFolders()` reads a file and parses JSON — it must not run on every
  ImGui draw call (`DrawReviewTab` fires every frame while the window is open). `_orphanedFolders`
  only recomputes on explicit triggers: after `RunScan()` (which covers both a manual scan and the
  internal `RunScan()` call at the end of `ApplyChanges`/`RollbackLastApply`), after `CleanUpFolders()`,
  after `RollbackFolderCleanup()`. `DrawReviewTab` renders the cached value every frame — zero file
  I/O per frame. (`Validate()` is safe to call every frame today because it's pure in-memory
  computation over already-scanned state; `DetectOrphanedFolders()` is not, since it hits disk.)
- **No scan yet guard:** if `!OrganizerState.HasScanned`, `DetectOrphanedFolders()` returns
  `FolderDetectionStatus.NotScanned` without reading `organization.json` at all — distinct from
  "scanned, zero mods found," which must still detect orphans normally (a genuinely empty library is
  exactly where every persisted folder may legitimately be orphaned). Using `Mods.Count == 0` for
  this guard would incorrectly disable the feature for that population; `HasScanned` doesn't have
  that failure mode.

## Data flow

Fully independent of the mod-move data flow. `OrganizerState.Mods[].CurrentPath` and the new
`HasScanned` flag are read (never written by this feature) to compute the occupied-folder set and
gate detection; nothing about `OrganizerState`'s other public surface changes. Folder cleanup's own
state (the cached orphan lists, `_folderReloadRequired`, the backup file) lives entirely in
`Plugin`/`MainWindow`, parallel to but never intersecting the mod-move Apply/Rollback state.

## Error handling

Every failure mode degrades to "skip cleanup, don't touch `organization.json`, nothing else in the
Review tab is affected" — strictly additive, never a precondition for mod-move Apply/Rollback:

- File absent → `DetectOrphanedFolders()` returns `FolderDetectionStatus.FileMissing`;
  `CleanUpFolders()` returns `FolderCleanupStatus.FileMissing`. Neither is an error.
- `Version != 1` → `OrganizationJsonCodec.Parse` returns `null`; both callers treat it as
  `UnsupportedVersion`, logged via `Log.Warning`, not thrown — and surfaced in the UI via the
  detection status note (see UI), not only in the log.
- Malformed JSON → same treatment as version mismatch, `MalformedJson`, logged, not thrown. This
  differs deliberately from `RunScan` (which lets IPC failures throw and surfaces via `MainWindow`'s
  existing scan-error handling) — a broken `organization.json` must never take down the rest of the
  Review tab, since mod-move Apply/Rollback don't depend on it at all.
- Stale selection at write time (a selected path no longer orphaned, or no longer present): dropped
  into `SkippedStale`, not a hard failure. If *everything* selected goes stale, no file is touched at
  all and no backup is overwritten (Backup/rollback mechanics, step 5) — a no-op attempt must be
  indistinguishable from never having clicked the button, not "silently destroyed my last rollback
  point for nothing."
- **The prune succeeding but the backup write failing** (`SucceededBackupFailed`) is classified as a
  distinct, partial-infrastructure-failure outcome, not folded into either "success" or "failure" —
  see Backup/rollback mechanics step 8 and the UI table's dedicated, high-severity message. The
  previous backup (if any) is left untouched, not destroyed, but it may now be stale relative to the
  cleanup that just happened.
- A rollback backup that fails its own Parse/version check is treated as invalid
  (`FolderRollbackStatus.InvalidBackup`) and the rollback is aborted without touching
  `organization.json`, rather than overwriting a possibly-good live file with unverified bytes.
- Genuine I/O errors (permission denied, file locked, Penumbra config dir not found) at any step
  *other* than the backup write in step 8 propagate as exceptions, caught by `MainWindow`'s existing
  `try/catch` → `_lastError` pattern, same as `ApplyChanges`/`RollbackLastApply` today. No new
  error-handling infrastructure beyond the one deliberate catch in step 8.

## Testing

- `OrganizationJsonCodec.Parse`/`Serialize` are pure functions over strings, not file paths —
  directly unit-testable without any file I/O: well-formed input round-trips; `Version != 1` →
  `null`; malformed JSON → `null`; unknown fields survive a parse→serialize round-trip via
  `JsonExtensionData`; `Serialize` omits null properties. **Verify during implementation, before
  asserting it in a test:** the exact encoding Penumbra/Luna writes (assumed UTF-8 without BOM —
  check `Luna`'s save path, or hexdump a real install's `organization.json` first bytes) — this
  claim entered the spec from the reviewer's "preferably UTF-8 without BOM *if that matches the
  source*" and the conditional hasn't been discharged yet.
- `OrganizationCleanupPlanner` (`DetectOrphaned`, `Prune`, `GetVirtualParent`) is pure and fully
  unit-testable — new `OrganizationCleanupPlannerTests.cs` alongside `ApplyPlannerTests.cs`. Covers:
  plain-vs-customized classification, **including an entry whose only content is unknown fields
  (`ExtensionData`) classifying as `CustomizedEmpty`, never `PlainEmpty`**; prefix-based occupancy
  using the confirmed `Ordinal` + `+"/"`-boundary logic (specifically: a folder named `Body` must
  **not** be classified as an ancestor of `BodyMods/Author/Mod`); a folder with an occupied
  descendant is never orphaned even if nothing occupies that exact path; `Prune` leaves `Separators`
  byte-for-byte equivalent; `GetVirtualParent("ModName")` → `null` (root-level mod, no folder);
  `GetVirtualParent("A/B")` → `"A"`; `GetVirtualParent("A/B/C")` → `"A/B"`;
  `GetVirtualParent("A/B/")` → `"A"` (trailing slash trimmed, not the mod's own path);
  `GetVirtualParent("/Mod")` → `null` (leading slash, treated as root-level).
- `OrganizerState.HasScanned`: `false` before any `LoadScan` call; `true` after, including when the
  scanned collection is empty (the specific case `Mods.Count == 0` can't distinguish).
- `Plugin.cs`'s `DetectOrphanedFolders`/`CleanUpFolders`/`RollbackFolderCleanup` are **not** unit
  tested — matches the existing convention (`RunScan`/`ApplyChanges`/`RollbackLastApply`/
  `ExportReview` all touch live IPC or config-directory file I/O and have none either). But the
  file-I/O sequencing they delegate to (`FolderCleanupExecutor` — see Architecture) has no IPC or
  Dalamud dependency and **is** integration-tested against a real temp directory, covering at
  minimum: (a) the backup file's content, after a successful cleanup, is byte-identical to
  `organization.json`'s content *before* that cleanup ran — not a reread of the post-prune file;
  (b) if the backup write is forced to fail (e.g. point `backupFilePath` at a path under an existing
  *file* treated as a directory), `organization.json` still ends up pruned and the method returns
  `SucceededBackupFailed`, not an uncaught exception; plus the no-op (`NothingStillValid`) not
  touching either file, and the rollback restore/invalid-backup/missing-backup paths.
- In-game verification checklist (own section, run once implemented, separate from Phase 2's since
  this ships independently):
  1. Detect real pre-existing orphans on a real library.
  2. Sort (but do not Apply) so `ProposedPath` diverges from `CurrentPath` for some mods; confirm
     the currently-occupied source folders do **not** appear in the orphan list (this is the
     regression test for round 1's Critical Blocker #1 — it must fail loudly if `ProposedPath` is
     ever reintroduced).
  3. Clean up a plain-empty folder; confirm the UI shows the `Success` message with the
     Rediscover-Mods instruction, **not** a plain "done" message; confirm the folder is still
     visible in Penumbra's own UI until Rediscover Mods is clicked, then disappears after.
  4. Attempt a customized-empty folder and confirm the extra-friction UI behaves as designed.
  5. Roll back, confirm the `Restored` message and Rediscover-Mods instruction, click Rediscover
     Mods, confirm the folder reappears with its original color/sort/separator-adjacency intact.
  6. Confirm mod placements are completely unaffected throughout (re-run `RunScan()` and diff
     against pre-cleanup state).
  7. Attempt a cleanup where re-verification invalidates every selected folder — with occupancy now
     coming from a fresh IPC read at write time, the way to stage this is: select an orphaned
     folder in this plugin's list, then **move a mod into that folder using Penumbra's own UI**
     (without re-scanning here), then click Clean Up. Confirm `organization.json` and the backup
     file are both untouched, and the UI reports `NothingStillValid` rather than a false success.
     This is also the regression test for the stale-occupancy gap the self-review pass closed: it
     must fail loudly if the write path ever reverts to last-scan `OrganizerState` occupancy.
     (Note: running a *sort* cannot stage this anymore — sorting only changes `ProposedPath`, which
     no longer participates in occupancy anywhere.)
  8. Confirm `_folderReloadRequired`'s banner appears immediately after a cleanup, persists across
     redraws, and clears only after `RunScan()` runs — not on any timer or automatically.
  9. A genuinely empty Penumbra library (0 mods, if reachable on a test install): confirm orphan
     detection still runs and doesn't silently no-op the way it would under a `Mods.Count == 0`
     guard.

## Open risks

1. **Single rolling backup, not a history — flagged by the user as a future concern.** Once a second
   Clean Up (or a second mod-move Apply) happens, the previous backup is gone; there's no way to
   recover further back than "the most recent action of that type." The user explicitly asked that
   this be *noted for a further plan*, not addressed here — a possible future direction is a unified
   backup-history mechanism covering both mod-move and folder-cleanup backups together, but that is
   out of scope for this spec and needs its own brainstorming pass.
2. **The "Rediscover Mods" step is manual and easy to forget**, now a documented operating
   constraint rather than an implicit assumption (see Ground truth, Backup/rollback mechanics), with
   `_folderReloadRequired` making it hard to miss in the UI in the moment — but nothing in this
   plugin can detect whether the user actually clicked it, or block further Penumbra-side folder
   edits from re-introducing a just-pruned orphan before they do. The UI banner is the only
   mitigation short of Penumbra itself exposing a reload IPC, which does not currently exist.
3. **Other Penumbra versions' `organization.json` schema and `ModDiscoveryFinished`/"Rediscover
   Mods" wiring are unverified beyond the `stable`/`main` branch source read in this session.** If a
   future Penumbra version changes either, the `Version != 1` gate should catch a schema change
   (fail closed), but a change to *how* discovery/reload is triggered wouldn't be caught by anything
   in this design — it would just mean the documented manual step stops working, silently.
4. **Live-file write race against Penumbra's own process**, independent of the discovery-timing
   risk above: `organization.json` is only saved by Penumbra on live tree-mutation events, not
   constantly — but if Penumbra happens to rewrite the file between this plugin's read and its
   write, the last writer wins. Not addressed by any locking mechanism (none is available over a
   file owned by another process); considered acceptable given the single-user, synchronous,
   button-click-driven nature of this action, consistent with how Phase 2 (Apply) accepted the
   analogous "concurrent modification during a long Apply batch" risk.
5. **A cleanup that succeeds but whose backup write fails (`SucceededBackupFailed`) leaves that
   specific prune without a safety net** until the user manually verifies the result. This is
   inherent to target-first write ordering (chosen deliberately in round 1 to avoid destroying a
   *previous* valid backup on a *failed* write — see Revision notes) and is surfaced as a
   high-severity, distinct UI message rather than hidden inside a generic success or failure state.

## Revision notes

### Round 1

The first draft of this spec went through an external design review before implementation started.
Three points were raised as critical blockers; all three were investigated against real source
(Penumbra.Api's actual IPC surface, and Ottermandias/Luna's and xivdev/Penumbra's actual source via
`gh api` code search) rather than resolved by assumption in either direction:

- **Occupancy source (`ProposedPath` vs `CurrentPath`):** the review was right, and this was a real
  bug, not a matter of interpretation — confirmed by re-reading `OrganizerState`'s own scan/sort
  code. Fixed throughout.
- **Live-tree propagation:** the review was right that this was unverified, but the review's
  proposed options (require Penumbra disabled/game closed, or "explicitly instruct the user to
  reload") turned out to have a specific, confirmable answer rather than needing a guess among
  several options — Penumbra's own "Rediscover Mods" button, traced through `ModDiscoveryFinished`
  to `FileSystemSaver.Load()`. Adopted as a required manual step, with the residual risk named
  explicitly (Open risks #2, #3).
- **`Separators` cross-contamination:** the review was right to flag this as unverified — the first
  draft's confidence here was inherited from the standalone app's spec, which itself listed the same
  question as an *unresolved* open risk, not a settled fact. Investigated directly in
  `Luna/Filesystem/FileSystemSaver.cs`'s `OrganizationData.Save()`: `Folders` and `Separators` are
  populated via type-filtered, disjoint enumeration over the live tree, so no path can appear in
  both. The original design's conclusion ("leave `Separators` untouched") turned out to be correct,
  but for a provable reason rather than an assumption.

Also adopted from round 1's "Important correctness changes" and "Design refinements," all verified
as genuine gaps or low-cost improvements rather than taken at face value: exact
segment-boundary-safe prefix matching (matching the standalone app's actual shipped
`IsFolderOccupied`); the no-op-must-not-touch-the-backup fix; target-write-before-backup-promotion
ordering; rollback backup validation before restore; the structured `FolderCleanupResult` type;
splitting parsing into a pure `OrganizationJsonCodec`; `JsonExtensionData` for forward-compatibility.
Not adopted: a SHA-256/metadata-wrapper backup-validation file (Non-goals); preserving exact JSON
indentation (the reviewer's own words: "less important than semantic fidelity").

### Round 2

A second review pass, after round 1's fixes landed, found further gaps — this time in how those
fixes were specified rather than whether they were needed at all:

- **Round 1's "Rediscover Mods" step was documented as advisory; round 2 correctly pushed it to a
  documented operating constraint**, and identified that the original UI wording ("N folders
  cleaned up") implied completion before Penumbra had actually loaded the change. Fixed: the UI
  table now keys every message on the specific `Status`, with `Success`/`Restored` explicitly
  stating the file changed but Penumbra hasn't loaded it yet (Ground truth, Backup/rollback
  mechanics, UI).
- **The original design would have refreshed `_orphanedFolders` immediately from the pruned file
  with no signal that this doesn't confirm Penumbra's live state.** Fixed: added
  `_folderReloadRequired`, a flag with no auto-clear heuristic (there's no real signal available),
  cleared only by the same explicit `RunScan()` action that already refreshes the list — and the UI
  banner is explicit that the refreshed list reflects file state, not confirmed live state (UI).
- **Backup-promotion ordering (round 1's fix) didn't specify what happens if the *promotion itself*
  fails after the target write succeeds.** This is a real, distinct outcome the original flow didn't
  name: the prune succeeded, but the new safety net didn't get written. Fixed: a new
  `SucceededBackupFailed` status, a dedicated catch around the backup write specifically (not a
  broad catch around the whole method, which would have misclassified this as an ordinary failure),
  and a high-severity, distinctly worded UI message (Architecture, Backup/rollback mechanics, UI,
  Open risks #5). Also tightened the backup-write description itself to name the retained
  `originalBytes` variable explicitly and state outright that `organization.json` is never re-read
  after the target replacement — round 1's prose already implied this ("bytes read in step 1"), but
  round 2 correctly judged that implication wasn't unambiguous enough to survive an implementer
  skimming the steps, so it's now a named variable threaded through every step plus a dedicated
  test.
- **`Mods.Count == 0` was being used as a "no scan yet" signal, but it's also the correct state of a
  genuinely empty Penumbra library** — exactly the population where every persisted folder may
  legitimately be orphaned. Fixed: added `OrganizerState.HasScanned`, set once in `LoadScan` and
  never reset, replacing the count-based guard everywhere it was used (Architecture, UI, Testing).
- **Parent-folder extraction from a virtual path had no specified behavior for a path with no `/`**
  (a mod sitting directly at Penumbra's root, which is common — any never-sorted mod). Fixed:
  `OrganizationCleanupPlanner.GetVirtualParent`, returning `null` for a root-level path rather than
  an empty string or the mod's own name, with the planner discarding `null` parents rather than
  treating them as an occupied folder (Architecture, Testing).
- **Rollback returned `void`, but round 2 correctly pointed out it has just as many meaningful
  outcomes as cleanup does** (no backup, invalid backup, restored). Fixed: `FolderRollbackResult`/
  `FolderRollbackStatus`, parallel to `FolderCleanupResult`/`FolderCleanupStatus` (Architecture, UI).

Not adopted from round 2: a dedicated "Refresh Folder Status" button, offered as one of two options
alongside reusing the existing Scan action (Non-goals) — `RunScan()` already exists and already
triggers the same recompute, so a second button would duplicate it without adding capability.

### Round 3 (self-review pass)

A final self-review before writing the implementation plan, looking for gaps the two external
rounds missed:

- **Write-time occupancy was still stale-scan-based (the most significant find).** Round 1 fixed
  *which field* occupancy reads (`CurrentPath`, not `ProposedPath`) but not *how fresh* it is:
  `OrganizerState` only updates on scan, so a mod moved via Penumbra's own UI after the last scan
  was invisible to both detection and the write-time re-verification — the re-check would validate
  against the same stale data it was supposed to guard against, and an occupied folder could be
  pruned. Round 1's review had actually suggested the fix ("refresh the authoritative current mod
  paths immediately before detection and cleanup") and it wasn't fully adopted. Fixed:
  `CleanUpFolders` now makes a fresh `GetModListAdapter` IPC read at write time, used for occupancy
  only. Deliberately not a `RunScan()` call, which would silently wipe the user's
  staged-but-unapplied sort proposals — a trade-off the naive fix would have missed. Detection (the
  advisory UI list) intentionally stays last-scan-based; the write path is the enforcement point.
  Also fixed the in-game checklist's re-verification scenario, which still described staging
  invalidation via a sort — impossible once `ProposedPath` stopped participating in occupancy.
- **Entries customized only via unknown fields classified as `PlainEmpty`.** "Every `FolderData`
  field is null" ignored `ExtensionData` — an entry whose only content is a future-Penumbra field
  this plugin can't interpret would have been pre-checked for pruning, discarding exactly the data
  `[JsonExtensionData]` was added to protect. Fixed: non-empty `ExtensionData` → `CustomizedEmpty`.
- **After `SucceededBackupFailed`, the Rollback button silently pointed at the wrong state.** A
  surviving older backup keeps `FolderBackupExists` true, so the button stays visible — but clicking
  it restores the pre-*previous*-cleanup state, reverting both cleanups. Fixed: the
  `SucceededBackupFailed` UI message now says so explicitly; also fixed a garbled sentence in step 8
  that stated the staleness backwards.
- **`GetVirtualParent` trailing-slash behavior was unspecified** despite round 2 explicitly asking
  for it — `"A/Mod/"` would have returned the mod's own path as its "parent folder." Fixed:
  `TrimEnd('/')` first, with tests for both trailing and leading slashes.
- **`DetectOrphanedFolders` had no status channel**, so a malformed or unsupported-version file
  rendered identically to a healthy zero-orphan state, with the real reason only in a log line —
  inconsistent with the structured-result decision both external rounds pushed on the other two
  methods. Fixed: `FolderDetectionResult`/`FolderDetectionStatus`, with `UnsupportedVersion`/
  `MalformedJson` rendering a visible one-line note.
- **Checkbox-selection semantics on list refresh were unspecified.** Fixed: `_selectedOrphans`
  resets to defaults on every `_orphanedFolders` recompute — a refresh means the world changed, and
  carrying stale selections across it is the failure mode the write-time re-check exists to catch,
  better prevented than relied on.
- **The "UTF-8 without BOM" serialization claim was asserted without ever being verified** — it
  entered the spec from the reviewer's conditional phrasing ("if that matches the source") and the
  condition was never checked. Downgraded to an explicit verify-during-implementation item in
  Testing rather than a stated fact.
