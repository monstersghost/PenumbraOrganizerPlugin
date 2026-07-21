# Plugin organizer, Phase 2: Apply Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Every implementer MUST work in the assigned git worktree, never on `main`** — verify with `git rev-parse --show-toplevel` and `git branch --show-current` before the first edit and again immediately before the final commit.

**Goal:** Turn the Review Changes tab's disabled "Apply" button into a real Apply action that writes
every unprotected, changed mod's `ProposedPath` to Penumbra via `SetModPath`, backed by a rolling
one-Apply backup and a resumable Rollback, gated on `Validate()`, with a bulk "Protect & Skip All
Blocking Mods" bypass.

**Architecture:** A new pure static class `Organizer/ApplyPlanner.cs` (no live IPC, fully unit-tested)
computes the backup entries, the set of identifiers blocking Apply, and the retain-filter used to keep
the backup file honest after each batch. `Plugin.cs` gets the IPC-touching orchestration
(`ApplyChanges`, `RollbackLastApply`, `ProtectAndSkipBlockingMods`) plus the backup file's atomic
read/write. `MainWindow.cs`'s `DrawReviewTab()` wires up the real Apply button (with a confirmation
popup and result summary), the Protect & Skip button, and the Rollback button.

**Tech Stack:** C# / .NET, Dalamud plugin framework, `Penumbra.Api` 5.15.1, ImGui via
`Dalamud.Bindings.ImGui`, xUnit for tests.

**Spec:** `docs/superpowers/specs/2026-07-15-plugin-organizer-phase2-apply-design.md` (read this first —
it has the full rationale; this plan only restates what's needed to implement it).

## Global Constraints

- `SetModPath`'s real, reflection-confirmed signature is
  `PenumbraApiEc SetModPath.Invoke(string modDirectory, string newPath, string modName)` — **note the
  order: `newPath` is the second argument, `modName` the third.** `OrganizerModRow.Identifier` is
  `modDirectory`; `modName` is always passed `""`. Every call site in this plan uses this exact order.
  Getting this wrong (e.g. writing `SetModPath(id, "", path)`) silently passes the mod's mutable name as
  the path and an empty string as the actual desired path — this is the single most important detail in
  this plan.
- `PenumbraApiEc` (confirmed by reflection) has members including `Success`, `ModMissing`,
  `InvalidArgument`, `PathRenameFailed` (plus many unrelated to `SetModPath`). A `SetModPath` call is a
  success iff the returned `PenumbraApiEc` equals `PenumbraApiEc.Success`; on any other value, the
  `ApplyResult.FailureReason` is `ec.ToString()`.
- The touched-row predicate is used in exactly three places (Plugin.cs's `ApplyChanges`, `ApplyPlanner`
  test inputs, and `MainWindow`'s confirmation-count) and must be identical everywhere:
  `!row.Protected && !string.Equals(row.ProposedPath, row.CurrentPath, StringComparison.OrdinalIgnoreCase)`.
  Use `OrdinalIgnoreCase` — this matches `OrganizerState.Validate()`'s existing protected-violation
  comparison (`OrganizerState.cs:136`), not a plain `!=`.
- `Organizer/ApplyPlanner.cs`'s three functions (`BuildBackup`, `BlockingIdentifiers`, `Retain`) are the
  only new code in this feature that gets unit tests. `Plugin.cs`'s `ApplyChanges`/`RollbackLastApply`/
  `ProtectAndSkipBlockingMods` touch live IPC and plugin config-directory file I/O and are **not** unit
  tested — this matches the existing, already-shipped convention for `RunScan`/`SaveProtectionState`/
  `ExportReview`, none of which have unit tests either. Verification for Plugin.cs/MainWindow.cs changes
  is: `dotnet build` clean, full existing test suite still green, and in-game manual testing (final
  step of this plan, after all tasks land).
- No new NuGet package references. JSON serialization uses `System.Text.Json`, which ships in the
  `net10.0` shared framework already referenced by both projects — no `PackageReference` needed.
- Do not modify `OrganizerState.cs`, `CollisionDisambiguator.cs`, `OrganizerExportFormatter.cs`, or any
  sort strategy. This phase only adds new files and extends `Plugin.cs` / `MainWindow.cs`.
- Backup file: fixed filename `organizer-backup.json` in `PluginInterface.ConfigDirectory.FullName`.
  Written via write-to-`.tmp`-then-`File.Move(overwrite: true)`, never a direct overwrite of the real
  file — see Task 2.

---

### Task 1: `ApplyPlanner` — pure backup/blocking/retain logic

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/ApplyPlanner.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/ApplyPlannerTests.cs`

**Interfaces:**
- Consumes: `OrganizerModRow` (`Identifier`, `CurrentPath`, `ProposedPath` — all `string`, already defined
  in `Organizer/OrganizerModRow.cs`), `ReviewResult` (`ProtectedViolations: IReadOnlyList<string>`,
  `PathCollisions: IReadOnlyDictionary<string, List<string>>` — already defined in `Organizer/OrganizerState.cs`).
- Produces (used by Task 2 and Task 3):
  - `public sealed record BackupEntry(string Identifier, string PreviousPath);`
  - `public sealed record ApplyResult(string Identifier, bool Success, string? FailureReason);`
  - `public static IReadOnlyList<BackupEntry> ApplyPlanner.BuildBackup(IReadOnlyList<OrganizerModRow> touchedRows);`
  - `public static IReadOnlySet<string> ApplyPlanner.BlockingIdentifiers(ReviewResult validation);`
  - `public static IReadOnlyList<BackupEntry> ApplyPlanner.Retain(IReadOnlyList<BackupEntry> entries, IReadOnlyList<ApplyResult> results, bool keepSuccessful);`

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/ApplyPlannerTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ApplyPlannerTests
{
    private static OrganizerModRow MakeRow(string identifier, string currentPath, string proposedPath) => new()
    {
        Identifier = identifier,
        Name = identifier,
        Author = "SomeAuthor",
        CurrentPath = currentPath,
        ProposedPath = proposedPath,
    };

    [Fact]
    public void BuildBackup_EmptyInput_ReturnsEmpty()
    {
        var result = ApplyPlanner.BuildBackup([]);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildBackup_SingleRow_RecordsPreviousPathFromCurrentPathNotProposedPath()
    {
        var row = MakeRow("Foo", "Old/Foo", "New/Foo");

        var result = ApplyPlanner.BuildBackup([row]);

        var entry = Assert.Single(result);
        Assert.Equal("Foo", entry.Identifier);
        Assert.Equal("Old/Foo", entry.PreviousPath);
    }

    [Fact]
    public void BuildBackup_MultipleRows_SortedAscendingByIdentifier()
    {
        var rowB = MakeRow("Bravo", "Old/B", "New/B");
        var rowA = MakeRow("Alpha", "Old/A", "New/A");

        var result = ApplyPlanner.BuildBackup([rowB, rowA]);

        Assert.Equal(["Alpha", "Bravo"], result.Select(e => e.Identifier));
    }

    [Fact]
    public void BuildBackup_DuplicateIdentifier_DeduplicatesToOneEntry()
    {
        var first = MakeRow("Foo", "Old/Foo", "New/Foo");
        var duplicate = MakeRow("Foo", "Old/Foo", "New/Foo");

        var result = ApplyPlanner.BuildBackup([first, duplicate]);

        Assert.Single(result);
    }

    [Fact]
    public void BlockingIdentifiers_NoIssues_ReturnsEmptySet()
    {
        var validation = new ReviewResult([], new Dictionary<string, List<string>>());

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Empty(result);
    }

    [Fact]
    public void BlockingIdentifiers_ProtectedViolationsOnly_ReturnsThoseIdentifiers()
    {
        var validation = new ReviewResult(["Foo", "Bar"], new Dictionary<string, List<string>>());

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Equal(new HashSet<string> { "Foo", "Bar" }, result);
    }

    [Fact]
    public void BlockingIdentifiers_PathCollisionsOnly_ReturnsUnionOfAllIdentifiersInEveryGroup()
    {
        var collisions = new Dictionary<string, List<string>>
        {
            ["Creator/Foo"] = ["Foo_2", "Foo_3"],
        };
        var validation = new ReviewResult([], collisions);

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Equal(new HashSet<string> { "Foo_2", "Foo_3" }, result);
    }

    [Fact]
    public void BlockingIdentifiers_BothPresent_ReturnsUnionWithoutDuplicates()
    {
        var collisions = new Dictionary<string, List<string>>
        {
            ["Creator/Foo"] = ["Foo_2", "Shared"],
        };
        var validation = new ReviewResult(["Shared", "Bar"], collisions);

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Equal(new HashSet<string> { "Foo_2", "Shared", "Bar" }, result);
    }

    [Fact]
    public void Retain_KeepSuccessfulTrue_ReturnsOnlySuccessfulEntries()
    {
        var entries = new List<BackupEntry> { new("Foo", "Old/Foo"), new("Bar", "Old/Bar") };
        var results = new List<ApplyResult> { new("Foo", true, null), new("Bar", false, "ModMissing") };

        var result = ApplyPlanner.Retain(entries, results, keepSuccessful: true);

        var entry = Assert.Single(result);
        Assert.Equal("Foo", entry.Identifier);
    }

    [Fact]
    public void Retain_KeepSuccessfulFalse_ReturnsOnlyFailedEntries()
    {
        var entries = new List<BackupEntry> { new("Foo", "Old/Foo"), new("Bar", "Old/Bar") };
        var results = new List<ApplyResult> { new("Foo", true, null), new("Bar", false, "ModMissing") };

        var result = ApplyPlanner.Retain(entries, results, keepSuccessful: false);

        var entry = Assert.Single(result);
        Assert.Equal("Bar", entry.Identifier);
    }

    [Fact]
    public void Retain_EntryWithNoMatchingResult_ExcludedRegardlessOfKeepSuccessful()
    {
        var entries = new List<BackupEntry> { new("Foo", "Old/Foo") };
        var results = new List<ApplyResult>();

        Assert.Empty(ApplyPlanner.Retain(entries, results, keepSuccessful: true));
        Assert.Empty(ApplyPlanner.Retain(entries, results, keepSuccessful: false));
    }

    [Fact]
    public void Retain_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ApplyPlanner.Retain([], [], keepSuccessful: true));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail with a compile error (no `ApplyPlanner` type yet)**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~ApplyPlannerTests`
Expected: build FAILS — `ApplyPlanner`, `BackupEntry`, `ApplyResult` do not exist yet.

- [ ] **Step 3: Implement `ApplyPlanner`**

Create `PenumbraOrganizer.Plugin/Organizer/ApplyPlanner.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

public sealed record BackupEntry(string Identifier, string PreviousPath);

public sealed record ApplyResult(string Identifier, bool Success, string? FailureReason);

public static class ApplyPlanner
{
    public static IReadOnlyList<BackupEntry> BuildBackup(IReadOnlyList<OrganizerModRow> touchedRows) =>
        touchedRows
            .GroupBy(r => r.Identifier, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Identifier, StringComparer.Ordinal)
            .Select(r => new BackupEntry(r.Identifier, r.CurrentPath))
            .ToList();

    public static IReadOnlySet<string> BlockingIdentifiers(ReviewResult validation)
    {
        var identifiers = new HashSet<string>(validation.ProtectedViolations, StringComparer.Ordinal);
        foreach (var group in validation.PathCollisions.Values)
            identifiers.UnionWith(group);
        return identifiers;
    }

    public static IReadOnlyList<BackupEntry> Retain(
        IReadOnlyList<BackupEntry> entries, IReadOnlyList<ApplyResult> results, bool keepSuccessful)
    {
        var resultsById = results.ToDictionary(r => r.Identifier, StringComparer.Ordinal);
        return entries
            .Where(e => resultsById.TryGetValue(e.Identifier, out var result) && result.Success == keepSuccessful)
            .ToList();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~ApplyPlannerTests`
Expected: PASS, 13/13.

- [ ] **Step 5: Run the full existing suite to confirm no regressions**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, all tests (119 previously + 13 new = 132).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/ApplyPlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/ApplyPlannerTests.cs
git commit -m "feat: add ApplyPlanner (backup, blocking identifiers, retain-filter)"
```

---

### Task 2: `Plugin.cs` — Apply/Rollback/Protect & Skip orchestration

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `ApplyPlanner.BuildBackup`/`BlockingIdentifiers`/`Retain`, `BackupEntry`, `ApplyResult` (Task 1);
  `OrganizerState.Mods`, `OrganizerState.Validate()`, `OrganizerState.AssignManual(string, string)`,
  `OrganizerState.SetProtected(string, bool)` (all pre-existing, unchanged); `Plugin.RunScan()`,
  `Plugin.SaveProtectionState()` (pre-existing, unchanged); `Penumbra.Api.IpcSubscribers.SetModPath` and
  `Penumbra.Api.Enums.PenumbraApiEc` (external package, signature confirmed in Global Constraints).
- Produces (used by Task 3):
  - `internal IReadOnlyList<Organizer.ApplyResult> Plugin.ApplyChanges();`
  - `internal IReadOnlyList<Organizer.ApplyResult> Plugin.RollbackLastApply();`
  - `internal void Plugin.ProtectAndSkipBlockingMods();`
  - `internal bool Plugin.BackupExists { get; }`

This task has no new unit tests — see Global Constraints for why (matches the existing convention for
`RunScan`/`SaveProtectionState`/`ExportReview`, none of which are unit tested; live IPC + config-directory
file I/O are verified in-game in this plan's final step).

- [ ] **Step 1: Add the `SetModPath` IPC subscriber field**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add a new field next to `GetModListAdapterIpc` (after line 25):

```csharp
    internal readonly Penumbra.Api.IpcSubscribers.GetModListAdapter GetModListAdapterIpc;
    internal readonly Penumbra.Api.IpcSubscribers.SetModPath SetModPathIpc;
```

And initialize it in the constructor next to `GetModListAdapterIpc`'s initialization (after line 39):

```csharp
        GetModListAdapterIpc = new Penumbra.Api.IpcSubscribers.GetModListAdapter(PluginInterface);
        SetModPathIpc = new Penumbra.Api.IpcSubscribers.SetModPath(PluginInterface);
```

Also update the read-only-MVP comment above the event subscribers (line 41), since it's no longer
accurate — replace:

```csharp
        // Read-only MVP: observe live changes, never call any write endpoint (e.g. SetModPath).
```

with:

```csharp
        // Observe live changes. SetModPath is now called from ApplyChanges/RollbackLastApply only,
        // gated on OrganizerState.Validate() showing no issues (see those methods below).
```

- [ ] **Step 2: Add backup file path and atomic read/write helpers**

Add these private members to `Plugin.cs`, near `ExportReview()` (they follow the same
`PluginInterface.ConfigDirectory.FullName` pattern that method already uses):

```csharp
    private string BackupFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-backup.json");

    internal bool BackupExists => File.Exists(BackupFilePath);

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

- [ ] **Step 3: Implement `ApplyChanges()`**

Add after `ExportReview()`:

```csharp
    internal IReadOnlyList<Organizer.ApplyResult> ApplyChanges()
    {
        var validation = OrganizerState.Validate();
        if (validation.HasIssues)
            throw new InvalidOperationException("Cannot Apply while Validate() reports issues.");

        var touchedRows = OrganizerState.Mods
            .Where(m => !m.Protected && !string.Equals(m.ProposedPath, m.CurrentPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var backupEntries = Organizer.ApplyPlanner.BuildBackup(touchedRows);
        WriteBackup(backupEntries);

        var results = new List<Organizer.ApplyResult>();
        foreach (var row in touchedRows)
        {
            var ec = SetModPathIpc.Invoke(row.Identifier, row.ProposedPath, "");
            var success = ec == Penumbra.Api.Enums.PenumbraApiEc.Success;
            results.Add(new Organizer.ApplyResult(row.Identifier, success, success ? null : ec.ToString()));
        }

        var succeeded = Organizer.ApplyPlanner.Retain(backupEntries, results, keepSuccessful: true);
        if (succeeded.Count > 0)
            WriteBackup(succeeded);
        else
            DeleteBackup();

        RunScan();
        return results;
    }
```

Note the call `SetModPathIpc.Invoke(row.Identifier, row.ProposedPath, "")` — per Global Constraints,
this is `(modDirectory, newPath, modName)`, not `(modDirectory, modName, newPath)`.

- [ ] **Step 4: Implement `RollbackLastApply()`**

Add after `ApplyChanges()`:

```csharp
    internal IReadOnlyList<Organizer.ApplyResult> RollbackLastApply()
    {
        if (!BackupExists)
            return [];

        var entries = ReadBackup();
        var results = new List<Organizer.ApplyResult>();
        foreach (var entry in entries)
        {
            var ec = SetModPathIpc.Invoke(entry.Identifier, entry.PreviousPath, "");
            var success = ec == Penumbra.Api.Enums.PenumbraApiEc.Success;
            results.Add(new Organizer.ApplyResult(entry.Identifier, success, success ? null : ec.ToString()));
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

- [ ] **Step 5: Implement `ProtectAndSkipBlockingMods()`**

Add after `RollbackLastApply()`:

```csharp
    internal void ProtectAndSkipBlockingMods()
    {
        var rowsById = OrganizerState.Mods.ToDictionary(m => m.Identifier);
        foreach (var identifier in Organizer.ApplyPlanner.BlockingIdentifiers(OrganizerState.Validate()))
        {
            if (!rowsById.TryGetValue(identifier, out var mod))
                continue;
            OrganizerState.AssignManual(identifier, mod.CurrentPath);
            OrganizerState.SetProtected(identifier, true);
        }
        SaveProtectionState();
    }
```

The order (`AssignManual` before `SetProtected`) matters — `AssignManual` rejects already-protected
rows, so reverting must happen before protecting.

- [ ] **Step 6: Build and run the full existing test suite**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, all 132 tests (no regressions — nothing in this task touches previously-tested code
paths).

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: implement ApplyChanges/RollbackLastApply/ProtectAndSkipBlockingMods on Plugin"
```

---

### Task 3: `MainWindow.cs` — wire up the Review tab UI

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.ApplyChanges()`, `Plugin.RollbackLastApply()`, `Plugin.ProtectAndSkipBlockingMods()`,
  `Plugin.BackupExists` (Task 2); `Organizer.ApplyResult` (Task 1, for rendering summaries).
- Produces: nothing further downstream — this is the last task.

This task has no new unit tests — it's ImGui draw code with no pure logic to extract, consistent with
every other `Draw*Tab()` method in this file having none. Verified by in-game testing (this plan's final
step).

- [ ] **Step 1: Add result-tracking fields**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add two fields next to `_lastExportPath` (after
line 20):

```csharp
    private string? _lastExportPath;
    private IReadOnlyList<Organizer.ApplyResult>? _lastApplyResults;
    private IReadOnlyList<Organizer.ApplyResult>? _lastRollbackResults;
```

- [ ] **Step 2: Replace the disabled Apply button with the real Apply flow**

In `DrawReviewTab()`, replace this block (currently lines 194-197):

```csharp
        ImGui.Spacing();
        ImGui.BeginDisabled();
        ImGui.Button("Apply (disabled in Phase 1)");
        ImGui.EndDisabled();
```

with:

```csharp
        ImGui.Spacing();
        if (result.HasIssues && ImGui.Button("Protect & Skip All Blocking Mods"))
        {
            _plugin.ProtectAndSkipBlockingMods();
            result = _plugin.OrganizerState.Validate();
        }

        ImGui.Spacing();
        var touchedCount = _plugin.OrganizerState.Mods
            .Count(m => !m.Protected && !string.Equals(m.ProposedPath, m.CurrentPath, StringComparison.OrdinalIgnoreCase));

        ImGui.BeginDisabled(result.HasIssues);
        var applyClicked = ImGui.Button("Apply");
        ImGui.EndDisabled();
        if (applyClicked)
            ImGui.OpenPopup("Apply changes?");

        if (ImGui.BeginPopupModal("Apply changes?"))
        {
            ImGui.TextUnformatted($"Apply changes to {touchedCount} mods?");
            if (ImGui.Button("Yes, Apply"))
            {
                ApplyChanges();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (_lastApplyResults is not null)
        {
            var succeeded = _lastApplyResults.Count(r => r.Success);
            var failed = _lastApplyResults.Count - succeeded;
            ImGui.TextUnformatted($"Apply: {succeeded} succeeded, {failed} failed.");
            foreach (var failure in _lastApplyResults.Where(r => !r.Success))
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  {failure.Identifier}: {failure.FailureReason}");
        }

        ImGui.Spacing();
        if (_plugin.BackupExists)
        {
            if (ImGui.Button("Rollback"))
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
        }
```

Note: `result` is the `ReviewResult` already computed at the top of `DrawReviewTab()`
(`var result = _plugin.OrganizerState.Validate();` — pre-existing line, unchanged). Re-assigning it
after Protect & Skip keeps the Apply button's disabled state accurate within the same frame.

- [ ] **Step 3: Add the `ApplyChanges`/`RollbackLastApply` wrapper methods**

Add these private methods after the existing `RunScan()` wrapper (after line 211), following its exact
try/catch pattern:

```csharp
    private void ApplyChanges()
    {
        try
        {
            _lastApplyResults = _plugin.ApplyChanges();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Apply failed: {ex.Message}";
        }
    }

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
    }
```

- [ ] **Step 4: Build and run the full existing test suite**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, all 132 tests (no regressions — this task only touches ImGui draw code with no existing
test coverage).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: wire Apply/Rollback/Protect & Skip into the Review Changes tab"
```

---

## Final in-game verification (after all tasks land, before merge)

Per the spec's Testing section — this is the plugin's first write IPC call, so in-game verification
carries more weight than any prior phase. Deploy the built plugin and, using a small, deliberately-chosen
set of mods:

1. Scan, sort, confirm `Validate()` shows no issues, click Apply, confirm the mods actually move in
   Penumbra's own UI (not just this plugin's `CurrentPath` display).
2. Click Rollback, confirm the mods return to their original folders.
3. Force a failure case (e.g. protect a mod mid-flight before clicking Apply, or disable a mod that's
   part of the batch) — confirm the Apply summary correctly reports it as failed while the rest
   succeed, and that `RunScan()` afterward shows the true resulting state.
4. Force a *rollback* failure (e.g. disable a mod between Apply and Rollback so its restore fails) —
   confirm the backup file is retained afterward (not deleted), the Rollback button remains visible, and
   a second Rollback click only retries the still-pending entry.
5. Trigger `Validate().HasIssues` (e.g. via `AssignManual` to a colliding path), confirm the Apply
   button is disabled and the "Protect & Skip All Blocking Mods" button appears; click it and confirm
   `Validate()` becomes clean and Apply becomes enabled.
6. Confirm no physical mod directory moves on disk during any of the above (Apply/Rollback are virtual
   path writes only, per the spec's Context section).
