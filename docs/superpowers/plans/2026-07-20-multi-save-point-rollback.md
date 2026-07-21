# Multi-Save-Point Rollback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Apply's single-rollback-point mechanism with an indefinitely-retained, user-browsable history of full-state snapshots that can be restored to (or manually deleted) at any time.

**Architecture:** A new pure module, `Organizer/RollbackHistory.cs`, owns the snapshot data model, JSON persistence (atomic write, mirroring `FolderCleanupExecutor`'s pattern), snapshot capture, and the restore-diff logic — all file-I/O-free except `Load`/`Save`, and fully unit-testable without Dalamud/Penumbra. `Plugin.cs` wires this to live Penumbra IPC (reading current mod state, executing moves via the existing cycle-safe `ApplyPlanner.OrderMovesForApply`/`ExecuteOrderedMoves` path) and a new `MainWindow.cs` "History" tab exposes it to the user.

**Tech Stack:** C#/.NET (Dalamud.NET.Sdk 15.0.0), `System.Text.Json`, xUnit for tests, ImGui via `Dalamud.Bindings.ImGui` for UI.

## Global Constraints

- Scope: Apply's rollback only. Folder Cleanup's separate `organizer-folder-backup.json` mechanism is untouched by this plan.
- Each snapshot is a **full state capture** (every currently-installed mod's identifier → full path), not a diff of only the mods an operation touched.
- `CaptureSnapshot` must fail (throw) if Penumbra reports two mods under the same identifier — this is `Dictionary`'s/`ToDictionary`'s existing duplicate-key behavior, used deliberately, not accidentally.
- Restore never overrides current protection: a mod that is currently protected or Heliosphere-managed is skipped regardless of what any snapshot says.
- A mod present now but not in the target snapshot ("not represented in the snapshot") is moved to the Penumbra root (`PenumbraPathSemantics.FixName(mod.Name)`), not into a named subfolder — this avoids all folder-name-collision questions since there is no folder to collide with.
- A mod present in the target snapshot but not currently installed is skipped and reported, never treated as an error.
- No migration of the old `organizer-backup.json` format; the new code never reads it, and nothing in this plan deletes it either — it's simply left on disk, unreferenced.
- History grows indefinitely; the only pruning mechanism is the user manually deleting a snapshot from the History tab. No retention cap or auto-pruning.
- Apply, Restore, Create Backup, and Delete all check a single `_operationInProgress` flag on `Plugin` before running (re-entrancy/double-click guard, not a real concurrency primitive — ImGui draws are single-threaded on the game's render thread).
- Reuse the existing `ApplyPlanner.OrderMovesForApply` → `ExecuteOrderedMoves` path for all Restore moves; do not write new move-ordering/cycle-breaking logic.

---

### Task 1: Snapshot data model and JSON persistence

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryTests.cs`

**Interfaces:**
- Produces: `public sealed record RollbackSnapshot(Guid Id, DateTimeOffset CreatedAt, string? Label, string AutoDescription, IReadOnlyDictionary<string, string> ModPaths)`; `public static class RollbackHistory` with `Load(string historyFilePath) : IReadOnlyList<RollbackSnapshot>` and `Save(string historyFilePath, IReadOnlyList<RollbackSnapshot> snapshots) : void`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class RollbackHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rollback-history-tests").FullName;

    private string HistoryPath => Path.Combine(_dir, "organizer-history.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        var result = RollbackHistory.Load(HistoryPath);

        Assert.Empty(result);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSnapshotContent()
    {
        var snapshot = new RollbackSnapshot(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "My Label", "5 mods moved",
            new Dictionary<string, string> { ["mod-a"] = "Creators/Alice/Mod A", ["mod-b"] = "Gear/Mod B" });

        RollbackHistory.Save(HistoryPath, [snapshot]);
        var loaded = RollbackHistory.Load(HistoryPath);

        var reloaded = Assert.Single(loaded);
        Assert.Equal(snapshot.Id, reloaded.Id);
        Assert.Equal(snapshot.Label, reloaded.Label);
        Assert.Equal(snapshot.AutoDescription, reloaded.AutoDescription);
        Assert.Equal(snapshot.ModPaths, reloaded.ModPaths);
    }

    [Fact]
    public void Save_WritesAtomically_NoLeftoverTempFile()
    {
        RollbackHistory.Save(HistoryPath, []);

        Assert.True(File.Exists(HistoryPath));
        Assert.False(File.Exists(HistoryPath + ".tmp"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryTests`
Expected: FAIL (compile error — `RollbackHistory` and `RollbackSnapshot` don't exist yet)

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

public sealed record RollbackSnapshot(
    Guid Id,
    DateTimeOffset CreatedAt,
    string? Label,
    string AutoDescription,
    IReadOnlyDictionary<string, string> ModPaths);

public static class RollbackHistory
{
    public static IReadOnlyList<RollbackSnapshot> Load(string historyFilePath)
    {
        if (!File.Exists(historyFilePath))
            return [];

        var json = File.ReadAllText(historyFilePath);
        return System.Text.Json.JsonSerializer.Deserialize<List<RollbackSnapshot>>(json) ?? [];
    }

    public static void Save(string historyFilePath, IReadOnlyList<RollbackSnapshot> snapshots)
    {
        var directory = Path.GetDirectoryName(historyFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = historyFilePath + ".tmp";
        File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(snapshots));
        File.Move(tempPath, historyFilePath, overwrite: true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryTests`
Expected: PASS (3/3)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryTests.cs
git commit -m "feat: add RollbackSnapshot model with atomic JSON persistence"
```

---

### Task 2: Snapshot capture, append, and delete

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryTests.cs`

**Interfaces:**
- Consumes: `RollbackSnapshot` record, `Load`/`Save` from Task 1.
- Produces: `public sealed record LiveMod(string Identifier, string Name, string FullPath, bool HeliosphereManaged)`; `RollbackHistory.CaptureSnapshot(IReadOnlyList<LiveMod> currentMods, string? label, string autoDescription) : RollbackSnapshot`; `RollbackHistory.AppendSnapshot(string historyFilePath, RollbackSnapshot snapshot) : IReadOnlyList<RollbackSnapshot>`; `RollbackHistory.DeleteSnapshot(string historyFilePath, Guid id) : IReadOnlyList<RollbackSnapshot>`.

- [ ] **Step 1: Write the failing tests**

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryTests.cs` (inside the existing `RollbackHistoryTests` class, before the closing brace):

```csharp
    [Fact]
    public void CaptureSnapshot_BuildsModPathsFromLiveMods()
    {
        var mods = new List<LiveMod>
        {
            new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false),
            new("mod-b", "Mod B", "Gear/Mod B", HeliosphereManaged: true),
        };

        var snapshot = RollbackHistory.CaptureSnapshot(mods, label: "Before test", autoDescription: "2 mods moved");

        Assert.Equal("Before test", snapshot.Label);
        Assert.Equal("2 mods moved", snapshot.AutoDescription);
        Assert.Equal(2, snapshot.ModPaths.Count);
        Assert.Equal("Creators/Alice/Mod A", snapshot.ModPaths["mod-a"]);
        Assert.Equal("Gear/Mod B", snapshot.ModPaths["mod-b"]);
    }

    [Fact]
    public void CaptureSnapshot_DuplicateIdentifier_Throws()
    {
        var mods = new List<LiveMod>
        {
            new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false),
            new("mod-a", "Mod A Copy", "Gear/Mod A Copy", HeliosphereManaged: false),
        };

        Assert.Throws<ArgumentException>(() => RollbackHistory.CaptureSnapshot(mods, null, "n/a"));
    }

    [Fact]
    public void AppendSnapshot_AddsToExistingHistoryAndPersists()
    {
        var first = RollbackHistory.CaptureSnapshot([], null, "first");
        RollbackHistory.AppendSnapshot(HistoryPath, first);

        var second = RollbackHistory.CaptureSnapshot([], null, "second");
        var result = RollbackHistory.AppendSnapshot(HistoryPath, second);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, RollbackHistory.Load(HistoryPath).Count);
    }

    [Fact]
    public void DeleteSnapshot_RemovesOnlyMatchingIdAndPersists()
    {
        var keep = RollbackHistory.CaptureSnapshot([], null, "keep");
        var remove = RollbackHistory.CaptureSnapshot([], null, "remove");
        RollbackHistory.Save(HistoryPath, [keep, remove]);

        var result = RollbackHistory.DeleteSnapshot(HistoryPath, remove.Id);

        var remaining = Assert.Single(result);
        Assert.Equal(keep.Id, remaining.Id);
        Assert.Single(RollbackHistory.Load(HistoryPath));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryTests`
Expected: FAIL (compile error — `LiveMod`, `CaptureSnapshot`, `AppendSnapshot`, `DeleteSnapshot` don't exist yet)

- [ ] **Step 3: Write the implementation**

Add to `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`, after the `RollbackSnapshot` record:

```csharp
public sealed record LiveMod(string Identifier, string Name, string FullPath, bool HeliosphereManaged);
```

Add inside `RollbackHistory`, after `Save`:

```csharp
    // ToDictionary throws ArgumentException if two live mods share an identifier - deliberate:
    // a capture must fail loudly on a duplicate identity rather than silently keep one and drop
    // the other (design spec, Data Model & Storage: "capture fails if Penumbra reports duplicate
    // identifiers").
    public static RollbackSnapshot CaptureSnapshot(
        IReadOnlyList<LiveMod> currentMods, string? label, string autoDescription)
    {
        var modPaths = currentMods.ToDictionary(m => m.Identifier, m => m.FullPath, StringComparer.Ordinal);
        return new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, label, autoDescription, modPaths);
    }

    public static IReadOnlyList<RollbackSnapshot> AppendSnapshot(string historyFilePath, RollbackSnapshot snapshot)
    {
        var updated = Load(historyFilePath).Append(snapshot).ToList();
        Save(historyFilePath, updated);
        return updated;
    }

    public static IReadOnlyList<RollbackSnapshot> DeleteSnapshot(string historyFilePath, Guid id)
    {
        var updated = Load(historyFilePath).Where(s => s.Id != id).ToList();
        Save(historyFilePath, updated);
        return updated;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryTests`
Expected: PASS (7/7)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryTests.cs
git commit -m "feat: add snapshot capture, append, and delete to RollbackHistory"
```

---

### Task 3: Restore-diff planning logic

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryBuildRestorePlanTests.cs`

**Interfaces:**
- Consumes: `RollbackSnapshot`, `LiveMod` (Tasks 1–2); `ModMove` from `PenumbraOrganizer.Plugin.Organizer.ApplyPlanner` (existing: `public sealed record ModMove(string Identifier, string CurrentPath, string TargetPath)`); `PenumbraPathSemantics.FixName(string name) : string` (existing).
- Produces: `public enum RestoreOutcome { Moved, Unchanged, SkippedUninstalled, RootRelocated, SkippedProtected, Failed }`; `public sealed record RestoreResult(string Identifier, RestoreOutcome Outcome, string? FailureReason)`; `public sealed record RestorePlan(IReadOnlyList<ModMove> Moves, IReadOnlyList<string> UnchangedIdentifiers, IReadOnlyList<string> SkippedUninstalledIdentifiers, IReadOnlyList<string> RootRelocatedIdentifiers, IReadOnlyList<string> SkippedProtectedIdentifiers)`; `RollbackHistory.BuildRestorePlan(RollbackSnapshot target, IReadOnlyList<LiveMod> currentMods, IReadOnlySet<string> protectedIdentifiers) : RestorePlan`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryBuildRestorePlanTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class RollbackHistoryBuildRestorePlanTests
{
    private static readonly HashSet<string> NoProtected = new(StringComparer.Ordinal);

    private static RollbackSnapshot Snapshot(params (string Id, string Path)[] entries) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "n/a",
            entries.ToDictionary(e => e.Id, e => e.Path, StringComparer.Ordinal));

    [Fact]
    public void BuildRestorePlan_MatchingModDifferentPath_ProducesMove()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current, NoProtected);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Gear/Mod A", move.CurrentPath);
        Assert.Equal("Creators/Alice/Mod A", move.TargetPath);
        Assert.Empty(plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MatchingModSamePath_IsUnchanged()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current, NoProtected);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInSnapshot_IsSkippedUninstalled()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod>();

        var plan = RollbackHistory.BuildRestorePlan(target, current, NoProtected);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.SkippedUninstalledIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInCurrentState_MovesToRoot()
    {
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current, NoProtected);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Creators/Alice/Mod A", move.CurrentPath);
        Assert.Equal("Mod A", move.TargetPath);
        Assert.Equal(["mod-a"], plan.RootRelocatedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInCurrentStateAlreadyAtRoot_IsUnchanged()
    {
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current, NoProtected);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ProtectedModWithDifferentHistoricalPath_IsSkippedProtected()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false) };
        var protectedIds = new HashSet<string>(StringComparer.Ordinal) { "mod-a" };

        var plan = RollbackHistory.BuildRestorePlan(target, current, protectedIds);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.SkippedProtectedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_HeliosphereManagedModNotInSnapshot_IsSkippedProtectedNotRootRelocated()
    {
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: true) };

        var plan = RollbackHistory.BuildRestorePlan(target, current, NoProtected);

        Assert.Empty(plan.Moves);
        Assert.Empty(plan.RootRelocatedIdentifiers);
        Assert.Equal(["mod-a"], plan.SkippedProtectedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MultipleMods_ClassifiesEachIndependently()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"), ("mod-c", "Gear/Mod C"));
        var current = new List<LiveMod>
        {
            new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false), // move
            new("mod-b", "Mod B", "Gear/Mod B", HeliosphereManaged: false), // root-relocated
        };

        var plan = RollbackHistory.BuildRestorePlan(target, current, NoProtected);

        Assert.Equal(["mod-a"], plan.Moves.Select(m => m.Identifier));
        Assert.Equal(["mod-b"], plan.RootRelocatedIdentifiers);
        Assert.Equal(["mod-c"], plan.SkippedUninstalledIdentifiers);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryBuildRestorePlanTests`
Expected: FAIL (compile error — `RestorePlan`, `BuildRestorePlan` don't exist yet)

- [ ] **Step 3: Write the implementation**

Add to `PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs`, after the `LiveMod` record:

```csharp
public enum RestoreOutcome { Moved, Unchanged, SkippedUninstalled, RootRelocated, SkippedProtected, Failed }

public sealed record RestoreResult(string Identifier, RestoreOutcome Outcome, string? FailureReason);

public sealed record RestorePlan(
    IReadOnlyList<ModMove> Moves,
    IReadOnlyList<string> UnchangedIdentifiers,
    IReadOnlyList<string> SkippedUninstalledIdentifiers,
    IReadOnlyList<string> RootRelocatedIdentifiers,
    IReadOnlyList<string> SkippedProtectedIdentifiers);
```

Add inside `RollbackHistory`, after `DeleteSnapshot`:

```csharp
    // Every currently-installed mod is classified into exactly one bucket by comparing it
    // against the target snapshot's ModPaths and the live "is this mod locked right now" set
    // (protected or Heliosphere-managed). Current protection always wins over historical
    // snapshot content - see design spec, Restore section: a snapshot must never be a way to
    // move a mod the user has since locked. Mods present only in the snapshot (uninstalled
    // since capture) are reported, never moved.
    public static RestorePlan BuildRestorePlan(
        RollbackSnapshot target, IReadOnlyList<LiveMod> currentMods, IReadOnlySet<string> protectedIdentifiers)
    {
        var moves = new List<ModMove>();
        var unchanged = new List<string>();
        var rootRelocated = new List<string>();
        var skippedProtected = new List<string>();

        foreach (var mod in currentMods)
        {
            var isLocked = protectedIdentifiers.Contains(mod.Identifier) || mod.HeliosphereManaged;

            if (target.ModPaths.TryGetValue(mod.Identifier, out var historicalPath))
            {
                if (string.Equals(mod.FullPath, historicalPath, StringComparison.OrdinalIgnoreCase))
                    unchanged.Add(mod.Identifier);
                else if (isLocked)
                    skippedProtected.Add(mod.Identifier);
                else
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, historicalPath));
            }
            else
            {
                var rootPath = PenumbraPathSemantics.FixName(mod.Name);
                if (string.Equals(mod.FullPath, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    unchanged.Add(mod.Identifier);
                }
                else if (isLocked)
                {
                    skippedProtected.Add(mod.Identifier);
                }
                else
                {
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, rootPath));
                    rootRelocated.Add(mod.Identifier);
                }
            }
        }

        var currentIdentifiers = currentMods.Select(m => m.Identifier).ToHashSet(StringComparer.Ordinal);
        var skippedUninstalled = target.ModPaths.Keys
            .Where(id => !currentIdentifiers.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new RestorePlan(moves, unchanged, skippedUninstalled, rootRelocated, skippedProtected);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~RollbackHistoryBuildRestorePlanTests`
Expected: PASS (8/8)

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test`
Expected: All tests PASS (no regressions in `ApplyPlannerTests`, `PenumbraPathSemanticsTests`, etc.)

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/RollbackHistory.cs PenumbraOrganizer.Plugin.Tests/Organizer/RollbackHistoryBuildRestorePlanTests.cs
git commit -m "feat: add restore-diff planning logic to RollbackHistory"
```

---

### Task 4: Wire snapshot capture into Plugin.cs, replace the old single-backup mechanism

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `RollbackHistory.Load`, `RollbackHistory.CaptureSnapshot`, `RollbackHistory.AppendSnapshot`, `RollbackHistory.DeleteSnapshot`, `LiveMod` (Tasks 1–2); existing `GetModListAdapterIpc`, `Organizer.HeliosphereDetector.IsHeliosphereManaged(string, DirectoryInfo)`, `PluginInterface.ConfigDirectory`, `Config.ProtectedModIdentifiers` (all pre-existing).
- Produces: `private bool _operationInProgress` field; `private string HistoryFilePath { get; }`; `private List<Organizer.LiveMod> ReadCurrentMods()`; `internal IReadOnlyList<Organizer.RollbackSnapshot> LoadHistory()`; `internal void CreateBackup(string? label)`; `internal void DeleteHistorySnapshot(Guid id)`. Consumed by Task 5 (`Restore`) and Task 6 (`MainWindow.DrawHistoryTab`).

This task has no isolated unit tests of its own — `Plugin.cs` requires live Dalamud/Penumbra services and is not unit-tested anywhere in this codebase (confirmed: no `PluginTests.cs` exists). Verification is a full build plus the existing test suite staying green, matching how prior `Plugin.cs`-touching tasks in this project were verified.

- [ ] **Step 1: Remove the old single-backup members**

In `PenumbraOrganizer.Plugin/Plugin.cs`, delete these members entirely (they're superseded by `RollbackHistory`):

```csharp
    private string BackupFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-backup.json");

    internal bool BackupExists => File.Exists(BackupFilePath);
```

and:

```csharp
    private void WriteBackup(IReadOnlyList<Organizer.BackupEntry> entries)
    {
        Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
        var tempPath = BackupFilePath + ".tmp";
        File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(entries));
        File.Move(tempPath, BackupFilePath, overwrite: true);
    }

    private void DeleteBackup()
    {
        if (File.Exists(BackupFilePath))
            File.Delete(BackupFilePath);
    }

    private IReadOnlyList<Organizer.BackupEntry> ReadBackup() =>
        System.Text.Json.JsonSerializer.Deserialize<List<Organizer.BackupEntry>>(File.ReadAllText(BackupFilePath))
        ?? [];
```

and the entire `RollbackLastApply()` method (it's replaced by `Restore(Guid)` in Task 5):

```csharp
    internal IReadOnlyList<Organizer.ApplyResult> RollbackLastApply()
    {
        if (!BackupExists)
            return [];

        var entries = ReadBackup();

        // A rollback undoing several Applied mods at once is the same swap/rotation-deadlock risk
        // as Apply itself, just in reverse - so it needs the same cycle-safe ordering. Current
        // location has to come from a fresh live read (not cached scan state): the backup file
        // persists across sessions/restarts, so OrganizerState.Mods may not reflect reality yet.
        var currentPaths = ReadCurrentModPaths();
        var resolvable = new List<Organizer.ModMove>();
        var resolvableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (currentPaths.TryGetValue(entry.Identifier, out var current) &&
                !string.Equals(current, entry.PreviousPath, StringComparison.OrdinalIgnoreCase))
            {
                resolvable.Add(new Organizer.ModMove(entry.Identifier, current, entry.PreviousPath));
                resolvableIds.Add(entry.Identifier);
            }
        }

        var failureByIdentifier = ExecuteOrderedMoves(resolvable);

        // Entries whose mod couldn't be resolved to a live current path (e.g. disabled/removed
        // since Apply) or that are already at PreviousPath aren't part of any cycle - attempt them
        // directly, exactly as before this fix, and let Penumbra's own error code surface.
        var results = new List<Organizer.ApplyResult>();
        foreach (var entry in entries)
        {
            if (resolvableIds.Contains(entry.Identifier))
            {
                results.Add(new Organizer.ApplyResult(
                    entry.Identifier,
                    !failureByIdentifier.ContainsKey(entry.Identifier),
                    failureByIdentifier.GetValueOrDefault(entry.Identifier)));
            }
            else
            {
                var ec = SetModPathIpc.Invoke(entry.Identifier, entry.PreviousPath, "");
                var success = ec == Penumbra.Api.Enums.PenumbraApiEc.Success;
                results.Add(new Organizer.ApplyResult(entry.Identifier, success, success ? null : ec.ToString()));
            }
        }

        var stillPending = Organizer.ApplyPlanner.Retain(entries, results, keepSuccessful: false);
        if (stillPending.Count > 0)
            WriteBackup(stillPending);
        else
            DeleteBackup();

        RunScan();
        return results;
    }
```

Leave `ReadCurrentModPaths()` in place — it's still used elsewhere in this file. Leave `FolderBackupFilePath`/`FolderBackupExists` untouched (Folder Cleanup is out of scope for this plan).

- [ ] **Step 2: Add the concurrency guard field, history file path, and ReadCurrentMods helper**

Add near the top of the class, next to the other `private readonly`/state fields (after `internal Configuration Config = null!;` is a reasonable spot):

```csharp
    private bool _operationInProgress;
```

Add near `FolderBackupFilePath` (after the `BackupFilePath`/`BackupExists` block you just removed, i.e. where they used to sit):

```csharp
    private string HistoryFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-history.json");
```

Add near `ReadCurrentModPaths()`:

```csharp
    private List<Organizer.LiveMod> ReadCurrentMods()
    {
        using var modList = GetModListAdapterIpc.Invoke();
        return modList.Select(mod => new Organizer.LiveMod(
                mod.Identifier,
                mod.Name,
                mod.FullPath,
                Organizer.HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath)))
            .ToList();
    }
```

- [ ] **Step 3: Add LoadHistory, CreateBackup, and DeleteHistorySnapshot**

Add near `ExportReview()`:

```csharp
    internal IReadOnlyList<Organizer.RollbackSnapshot> LoadHistory() =>
        Organizer.RollbackHistory.Load(HistoryFilePath);

    internal void CreateBackup(string? label)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var currentMods = ReadCurrentMods();
            var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label, "Manual backup");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    internal void DeleteHistorySnapshot(Guid id)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        Organizer.RollbackHistory.DeleteSnapshot(HistoryFilePath, id);
    }
```

- [ ] **Step 4: Rewire ApplyChanges() to use the concurrency guard and RollbackHistory instead of the old single backup**

Replace the full `ApplyChanges()` method with:

```csharp
    internal IReadOnlyList<Organizer.ApplyResult> ApplyChanges()
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var validation = OrganizerState.Validate();
            if (validation.HasIssues)
                throw new InvalidOperationException("Cannot Apply while Validate() reports issues.");

            // Equivalence, not raw string equality: a path differing only by a transient " (N)"
            // duplicate marker (or Penumbra's own name-trimming) is the same persisted location —
            // moving it would be a no-op write that Penumbra reshuffles on the next reload anyway.
            var touchedRows = OrganizerState.Mods
                .Where(m => !m.Protected && !Organizer.PenumbraPathSemantics.AreEquivalent(m.CurrentPath, m.ProposedPath, m.Name))
                .ToList();

            var folderCollisions = Organizer.ApplyPlanner.FolderPathCollisions(touchedRows, ReadExistingOrganizationFolderPaths());
            if (folderCollisions.Count > 0)
                throw new InvalidOperationException(
                    "Cannot Apply: the proposed path for the following mods matches an existing (likely orphaned) " +
                    "folder entry in Penumbra's organization.json, which Penumbra's own SetModPath will reject: " +
                    $"{string.Join(", ", folderCollisions)}. Run Folder Cleanup on the Review Changes tab to prune " +
                    "orphaned folders, then try Apply again.");

            var currentMods = ReadCurrentMods();
            var snapshot = Organizer.RollbackHistory.CaptureSnapshot(currentMods, label: null, $"{touchedRows.Count} mods moved");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, snapshot);

            var moves = touchedRows
                .Select(r => new Organizer.ModMove(r.Identifier, r.CurrentPath, r.ProposedPath))
                .ToList();
            var failureByIdentifier = ExecuteOrderedMoves(moves);
            var results = touchedRows
                .Select(r => new Organizer.ApplyResult(
                    r.Identifier, !failureByIdentifier.ContainsKey(r.Identifier), failureByIdentifier.GetValueOrDefault(r.Identifier)))
                .ToList();

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }
```

(This removes the old `BuildBackup`/`WriteBackup`/`Retain`/`DeleteBackup` sequence entirely — `RollbackHistory` needs no "retain only the successful entries" step, since each snapshot is a full state capture rather than a partial per-move backup.)

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS (no regressions).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: replace single-backup Apply rollback with RollbackHistory snapshots"
```

---

### Task 5: Restore(Guid) in Plugin.cs

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `RollbackHistory.Load`, `RollbackHistory.CaptureSnapshot`, `RollbackHistory.AppendSnapshot`, `RollbackHistory.BuildRestorePlan`, `RestorePlan`, `RestoreOutcome`, `RestoreResult` (Tasks 1–3); `_operationInProgress`, `HistoryFilePath`, `ReadCurrentMods()` (Task 4); existing `ExecuteOrderedMoves`, `Config.ProtectedModIdentifiers`, `RunScan()`.
- Produces: `internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId)`. Consumed by Task 6 (`MainWindow`).

No isolated unit tests — same reasoning as Task 4 (`Plugin.cs` is not unit-testable in this codebase; the diff logic it calls into was already fully tested in Task 3). Verification is build + full test suite.

- [ ] **Step 1: Add Restore(Guid) to Plugin.cs**

Add near `RunScan()` (or directly after the `ApplyChanges()` method from Task 4):

```csharp
    internal IReadOnlyList<Organizer.RestoreResult> Restore(Guid snapshotId)
    {
        if (_operationInProgress)
            throw new InvalidOperationException("Another organizer operation is already in progress.");
        _operationInProgress = true;
        try
        {
            var history = Organizer.RollbackHistory.Load(HistoryFilePath);
            var target = history.FirstOrDefault(s => s.Id == snapshotId)
                ?? throw new InvalidOperationException("Snapshot not found.");

            var currentMods = ReadCurrentMods();

            // Pre-restore snapshot makes the restore itself undoable - captured and persisted
            // before any moves happen, same as Apply's own pre-operation capture.
            var preRestoreLabel = target.Label ?? target.CreatedAt.ToString("u");
            var preRestoreSnapshot = Organizer.RollbackHistory.CaptureSnapshot(
                currentMods, label: null, autoDescription: $"Snapshot before restoring to \"{preRestoreLabel}\"");
            Organizer.RollbackHistory.AppendSnapshot(HistoryFilePath, preRestoreSnapshot);

            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods, Config.ProtectedModIdentifiers);
            var failureByIdentifier = ExecuteOrderedMoves(plan.Moves);

            var results = new List<Organizer.RestoreResult>();
            foreach (var identifier in plan.UnchangedIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.Unchanged, null));
            foreach (var identifier in plan.SkippedUninstalledIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedUninstalled, null));
            foreach (var identifier in plan.SkippedProtectedIdentifiers)
                results.Add(new Organizer.RestoreResult(identifier, Organizer.RestoreOutcome.SkippedProtected, null));

            var rootRelocatedIds = plan.RootRelocatedIdentifiers.ToHashSet(StringComparer.Ordinal);
            foreach (var move in plan.Moves)
            {
                var failed = failureByIdentifier.TryGetValue(move.Identifier, out var reason);
                var outcome = failed
                    ? Organizer.RestoreOutcome.Failed
                    : rootRelocatedIds.Contains(move.Identifier)
                        ? Organizer.RestoreOutcome.RootRelocated
                        : Organizer.RestoreOutcome.Moved;
                results.Add(new Organizer.RestoreResult(move.Identifier, outcome, failed ? reason : null));
            }

            RunScan();
            return results;
        }
        finally
        {
            _operationInProgress = false;
        }
    }
```

- [ ] **Step 2: Build and run the full test suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: add Restore(Guid) to replay a historical rollback snapshot"
```

---

### Task 6: History tab UI in MainWindow.cs

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `_plugin.LoadHistory()`, `_plugin.CreateBackup(string?)`, `_plugin.DeleteHistorySnapshot(Guid)`, `_plugin.Restore(Guid)` (Tasks 4–5); `Organizer.RollbackSnapshot`, `Organizer.RestoreResult`, `Organizer.RestoreOutcome` (Tasks 1–3).

No isolated unit tests — `MainWindow.cs` is ImGui rendering code with no existing test coverage in this codebase. Verification is build + a manual pass description for in-game check (this plan doesn't include in-game verification steps, matching the design spec's Testing section).

- [ ] **Step 1: Remove the old Rollback UI from DrawReviewTab and its wrapper method**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, remove this block from `DrawReviewTab()` (it sits directly after the `_lastApplyResults` block, before the closing `ImGui.Spacing(); ImGui.Separator(); DrawOrphanedFoldersSection();` lines):

```csharp
        ImGui.Spacing();
        if (_plugin.BackupExists && ImGui.Button("Rollback"))
            RollbackLastApply();

        if (_lastRollbackResults is not null)
        {
            var restored = _lastRollbackResults.Count(r => r.Success);
            var pending = _lastRollbackResults.Count - restored;
            ImGui.TextUnformatted($"Rollback: {restored} restored, {pending} pending.");
            if (pending > 0)
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "Some entries could not be restored. The recovery record has been retained.");
            foreach (var failure in _lastRollbackResults.Where(r => !r.Success))
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  {failure.Identifier}: {failure.FailureReason}");
        }
```

Remove the `_lastRollbackResults` field declaration:

```csharp
    private IReadOnlyList<Organizer.ApplyResult>? _lastRollbackResults;
```

Remove the `RollbackLastApply()` wrapper method:

```csharp
    private void RollbackLastApply()
    {
        try
        {
            _lastRollbackResults = _plugin.RollbackLastApply();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Rollback failed: {ex.Message}";
        }

        RefreshOrphanedFolders(); // same: internal RunScan() means occupancy changed
    }
```

- [ ] **Step 2: Add state fields for the History tab**

Add next to the other private fields near the top of the class (after `_lastApplyResults`):

```csharp
    private string _createBackupLabelInput = string.Empty;
    private IReadOnlyList<Organizer.RestoreResult>? _lastRestoreResults;
    private Guid? _pendingRestoreSnapshotId;
```

- [ ] **Step 3: Wire the History tab into the tab bar**

In `Draw()`, change:

```csharp
        using (var tabBar = ImRaii.TabBar("MainTabs"))
        {
            if (tabBar)
            {
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
            }
        }
```

to:

```csharp
        using (var tabBar = ImRaii.TabBar("MainTabs"))
        {
            if (tabBar)
            {
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
                DrawHistoryTab();
            }
        }
```

- [ ] **Step 4: Implement DrawHistoryTab()**

Add a new method, placed after `DrawReviewTab()` and before `DrawOrphanedFoldersSection()`:

```csharp
    private void DrawHistoryTab()
    {
        using var tab = ImRaii.TabItem("History");
        if (!tab)
            return;

        ImGui.InputText("Label (optional)", ref _createBackupLabelInput, 200);
        ImGui.SameLine();
        if (ImGui.Button("Create Backup"))
        {
            var label = _createBackupLabelInput.Trim();
            _plugin.CreateBackup(label.Length > 0 ? label : null);
            _createBackupLabelInput = string.Empty;
        }

        ImGui.Spacing();
        ImGui.Separator();

        var history = _plugin.LoadHistory()
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        if (history.Count == 0)
        {
            ImGui.TextDisabled("No backups yet. Backups are created automatically before every Apply and Restore.");
        }

        foreach (var snapshot in history)
        {
            // Per-row widget uniqueness follows this codebase's existing convention (see
            // DrawProtectTab's "{mod.Name}##protect-{mod.Identifier}") rather than ImRaii.PushId,
            // whose exact signature in this Dalamud version wasn't worth gambling on.
            var title = snapshot.Label is { Length: > 0 } label
                ? $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {label}"
                : $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {snapshot.AutoDescription}";
            ImGui.TextUnformatted($"{title} ({snapshot.ModPaths.Count} mods)");

            ImGui.SameLine();
            if (ImGui.Button($"Restore##restore-{snapshot.Id}"))
            {
                _pendingRestoreSnapshotId = snapshot.Id;
                ImGui.OpenPopup("Restore snapshot?");
            }

            ImGui.SameLine();
            if (ImGui.Button($"Delete##delete-{snapshot.Id}"))
            {
                _plugin.DeleteHistorySnapshot(snapshot.Id);
            }

            if (_pendingRestoreSnapshotId == snapshot.Id && ImGui.BeginPopupModal("Restore snapshot?"))
            {
                var currentIdentifiers = _plugin.OrganizerState.Mods.Select(m => m.Identifier).ToHashSet(StringComparer.Ordinal);
                var willMove = currentIdentifiers.Count(id2 => snapshot.ModPaths.ContainsKey(id2));
                var missingFromSnapshot = currentIdentifiers.Count(id2 => !snapshot.ModPaths.ContainsKey(id2));
                var uninstalledSinceSnapshot = snapshot.ModPaths.Keys.Count(id2 => !currentIdentifiers.Contains(id2));

                ImGui.TextUnformatted($"Restore to: {title}");
                ImGui.TextUnformatted($"Up to {willMove} mods known to this snapshot may move.");
                ImGui.TextUnformatted($"Up to {missingFromSnapshot} mods installed since this snapshot may be moved to the Penumbra root.");
                ImGui.TextUnformatted($"{uninstalledSinceSnapshot} mods from this snapshot are no longer installed and will be skipped.");
                ImGui.TextColored(ImGuiColors.DalamudYellow, "Currently protected or Heliosphere-managed mods are never moved.");

                if (ImGui.Button("Yes, Restore"))
                {
                    RestoreSnapshot(snapshot.Id);
                    _pendingRestoreSnapshotId = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _pendingRestoreSnapshotId = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        if (_lastRestoreResults is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            var moved = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Moved);
            var rootRelocated = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.RootRelocated);
            var skippedUninstalled = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedUninstalled);
            var skippedProtected = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.SkippedProtected);
            var failed = _lastRestoreResults.Count(r => r.Outcome == Organizer.RestoreOutcome.Failed);
            ImGui.TextUnformatted(
                $"Restore: {moved} moved, {rootRelocated} relocated to root, {skippedUninstalled} skipped (uninstalled), " +
                $"{skippedProtected} skipped (protected), {failed} failed.");
            foreach (var failure in _lastRestoreResults.Where(r => r.Outcome == Organizer.RestoreOutcome.Failed))
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  {failure.Identifier}: {failure.FailureReason}");
        }
    }

    private void RestoreSnapshot(Guid snapshotId)
    {
        try
        {
            _lastRestoreResults = _plugin.Restore(snapshotId);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Restore failed: {ex.Message}";
        }

        RefreshOrphanedFolders(); // Restore() ran RunScan() internally — occupancy changed
    }
```

- [ ] **Step 5: Build**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add History tab for browsing, restoring, and deleting rollback snapshots"
```

---

## Post-plan note

No in-game verification steps are included in this plan (consistent with the design spec's Testing section — verification happens after implementation, same as prior phases). Once all 6 tasks are merged, the plugin should be rebuilt and tested in-game: Create Backup, Apply (confirm a snapshot appears automatically), Restore to an older snapshot with mods added/removed/protected in between, and Delete.
