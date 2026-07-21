# Operation Persistence Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the pure, Dalamud-free data layer that Phase A's operation controller depends on: atomic file persistence, the operation plan model, the operation journal model, and the recovery classifier — per the design at `docs/superpowers/specs/2026-07-21-incremental-operations-design.md`.

**Architecture:** Four independent, unit-testable units under `PenumbraOrganizer.Plugin/Organizer/Operations/`. No unit touches Dalamud, ImGui, or Penumbra IPC directly — `RecoveryClassifier` takes plain `LiveMod`/`RollbackSnapshot`/`OperationPlan` values, matching how `RollbackHistory.BuildRestorePlan` already works. This plan is a prerequisite for a follow-on plan that wires these into the operation controller, frame-budgeted Apply/Restore, and the recovery dialog (design sections 2, 4, 8, 10).

**Tech Stack:** .NET (project SDK per existing `PenumbraOrganizer.Plugin.csproj`), `System.Text.Json`, xUnit 2.5.3.

## Global Constraints

- Path comparisons for recovery classification MUST use `PenumbraPathSemantics.AreEquivalent`, never raw string equality (design §7; `PenumbraPathSemantics.cs:1-19` — a `" (N)"` duplicate-marker suffix is discarded on save and reassigned in arbitrary order on reload, so string equality produces false `AtNeither` results).
- The operation journal MUST NOT contain the work list or mod paths — only enough to identify and recover the operation (design §6).
- All journal/plan writes use atomic temp-write-flush-replace, temp file in the same directory as the destination (design §9).
- `targetHash` is computed over normalized paths, not raw path strings (design §3, §6).
- Follow existing codebase patterns: `sealed record` for data types, `static class` for pure logic (see `RollbackHistory.cs`, `ApplyPlanner.cs`).

---

### Task 1: Atomic file persistence helper

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/AtomicFile.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/AtomicFileTests.cs`

**Interfaces:**
- Produces: `AtomicFile.CreateOrReplace(string path, string contents)` (void); `AtomicFile.TryReadValidated(string path, out string? contents)` returns `bool` — `true` and populates `contents` only if the file exists and is non-empty after a successful read, `false` (with `contents = null`) if the file doesn't exist. Corruption/partial-write detection is the caller's job (the caller deserializes `contents` and checks its own schema version / integrity hash — see Tasks 2–3), because only the caller knows what "valid" means for its payload.

Existing prior art: `RollbackHistory.Save` (`RollbackHistory.cs:33-42`) already does temp-write + `File.Move(overwrite: true)`. This task generalizes that into a reusable helper and adds the two gaps design §9 calls out that `RollbackHistory.Save` doesn't handle: orphaned temp cleanup and first-write (no destination yet) behavior. `File.Move(overwrite: true)` already handles both create and replace identically on .NET's Windows implementation, so no separate first-write branch is needed in the implementation — but a test locks this in so a future refactor can't silently break it.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class AtomicFileTests
{
    [Fact]
    public void CreateOrReplace_WritesFileWhenDestinationDoesNotExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");

            AtomicFile.CreateOrReplace(path, "{\"a\":1}");

            Assert.True(File.Exists(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_ReplacesExistingDestination()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            File.WriteAllText(path, "old");

            AtomicFile.CreateOrReplace(path, "new");

            Assert.Equal("new", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_LeavesNoOrphanedTempFileOnSuccess()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");

            AtomicFile.CreateOrReplace(path, "contents");

            var leftover = Directory.GetFiles(dir.FullName).Where(f => f != path);
            Assert.Empty(leftover);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_RemovesPreExistingOrphanedTempFileBeforeWriting()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, "stale from a crashed previous write");

            AtomicFile.CreateOrReplace(path, "contents");

            Assert.Equal("contents", File.ReadAllText(path));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryReadValidated_ReturnsFalseWhenFileDoesNotExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "missing.json");

            var found = AtomicFile.TryReadValidated(path, out var contents);

            Assert.False(found);
            Assert.Null(contents);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryReadValidated_ReturnsTrueAndContentsWhenFileExists()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            File.WriteAllText(path, "{\"a\":1}");

            var found = AtomicFile.TryReadValidated(path, out var contents);

            Assert.True(found);
            Assert.Equal("{\"a\":1}", contents);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_CreatesDestinationDirectoryIfMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "journal.json");

            AtomicFile.CreateOrReplace(path, "contents");

            Assert.Equal("contents", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AtomicFileTests`
Expected: FAIL — `AtomicFile` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Atomic temp-write-flush-replace for small plugin-owned JSON files (operation plans, journals).
/// Design doc 2026-07-21-incremental-operations-design.md section 9: the temp file lives beside
/// the destination so the final move is same-volume, and any temp file left behind by a prior
/// crashed write is cleared before a new attempt rather than left to accumulate.
/// </summary>
public static class AtomicFile
{
    public static void CreateOrReplace(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public static bool TryReadValidated(string path, out string? contents)
    {
        if (!File.Exists(path))
        {
            contents = null;
            return false;
        }

        contents = File.ReadAllText(path);
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AtomicFileTests`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/AtomicFile.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/AtomicFileTests.cs
git commit -m "feat: add atomic file persistence helper for operation state"
```

---

### Task 2: Path normalization for hashing and comparison

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/PenumbraPathSemantics.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/PenumbraPathSemanticsTests.cs`

**Interfaces:**
- Consumes: existing private `SplitPath(string, out string, out string)`, `LeafToken(string, string)`, `FixName(string)` in the same file.
- Produces: `PenumbraPathSemantics.Normalize(string path, string displayName)` returns `string` — a canonical `folder + "\" + leafToken` form, lowercased, so two paths that `AreEquivalent` says are the same location produce identical strings (design §3, §6: `targetHash` and any hash-based comparison must operate on normalized paths, not raw strings — a raw-string hash would spuriously mismatch across a Penumbra reload that reshuffles duplicate-marker suffixes).

This is a small, targeted addition to existing production code: `AreEquivalent` already computes exactly this internally as two local values it then compares — it just never returns the canonical form. Extracting it is required because Task 3 (operation plan hashing) needs a single canonical string per path, not a pairwise comparison.

- [ ] **Step 1: Write the failing test**

Add to the existing `PenumbraPathSemanticsTests.cs` (do not create a new file — this extends coverage of the same class):

```csharp
[Theory]
[InlineData("Gear\\Foo", "Gear\\Foo (2)", "Foo")]      // duplicate marker on the display name itself
[InlineData("Gear\\Foo", "Gear\\Foo", "Foo")]           // already identical
public void Normalize_ProducesSameStringForEquivalentPaths(string a, string b, string displayName)
{
    var normalizedA = PenumbraPathSemantics.Normalize(a, displayName);
    var normalizedB = PenumbraPathSemantics.Normalize(b, displayName);

    Assert.Equal(normalizedA, normalizedB);
    Assert.True(PenumbraPathSemantics.AreEquivalent(a, b, displayName));
}

[Fact]
public void Normalize_ProducesDifferentStringsForDifferentLocations()
{
    var normalizedA = PenumbraPathSemantics.Normalize("Gear\\Foo", "Foo");
    var normalizedB = PenumbraPathSemantics.Normalize("Weapons\\Foo", "Foo");

    Assert.NotEqual(normalizedA, normalizedB);
}

[Fact]
public void Normalize_IsCaseInsensitive()
{
    var normalizedA = PenumbraPathSemantics.Normalize("Gear\\Foo", "Foo");
    var normalizedB = PenumbraPathSemantics.Normalize("gear\\foo", "Foo");

    Assert.Equal(normalizedA, normalizedB);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~PenumbraPathSemanticsTests.Normalize`
Expected: FAIL — `Normalize` does not exist on `PenumbraPathSemantics`.

- [ ] **Step 3: Implement `Normalize`**

Add to `PenumbraPathSemantics.cs`, directly below `AreEquivalent`:

```csharp
    /// <summary>
    /// Canonical form of a full virtual path for a mod with the given display name: same value
    /// for any two paths <see cref="AreEquivalent"/> would call equivalent. Used wherever paths
    /// need to be hashed or grouped rather than compared pairwise (operation plan integrity hash).
    /// </summary>
    public static string Normalize(string path, string displayName)
    {
        SplitPath(path, out var folder, out var leaf);
        var display = FixName(displayName);
        var token = LeafToken(leaf, display);
        return (folder + "\\" + token).ToLowerInvariant();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PenumbraPathSemanticsTests`
Expected: PASS (all existing tests plus the 4 new ones)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/PenumbraPathSemantics.cs PenumbraOrganizer.Plugin.Tests/Organizer/PenumbraPathSemanticsTests.cs
git commit -m "feat: add PenumbraPathSemantics.Normalize for hash-based comparison"
```

---

### Task 3: Operation plan model, integrity hash, and codec

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlan.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanTests.cs`

**Interfaces:**
- Consumes: `AtomicFile.CreateOrReplace`/`TryReadValidated` (Task 1); `PenumbraPathSemantics.Normalize` (Task 2).
- Produces:
  - `OperationType` enum: `Apply`, `Restore`.
  - `OperationPlanItem` record: `(string Identifier, string OriginalRawPath, string IntendedRawPath, string DisplayName)`.
  - `OperationPlan` record: `(Guid Id, OperationType Type, DateTimeOffset CreatedAt, int SchemaVersion, string IntegrityHash, IReadOnlyList<OperationPlanItem> Items)`.
  - `OperationPlan.Create(OperationType type, IReadOnlyList<OperationPlanItem> items)` — builds a plan with `Id = Guid.NewGuid()`, `CreatedAt = DateTimeOffset.UtcNow`, `SchemaVersion = 1`, and `IntegrityHash` computed from `items`.
  - `OperationPlan.ComputeIntegrityHash(IReadOnlyList<OperationPlanItem> items)` returns `string` — exposed separately so `Verify()` can recompute and compare without duplicating the hashing logic.
  - `OperationPlan.Verify()` returns `bool` — recomputes the hash over `Items` and compares to `IntegrityHash`.
  - `OperationPlanCodec.Save(string path, OperationPlan plan)` (void) — throws if `plan.Verify()` is false (a plan must never be persisted in a state it would itself reject on reload; catches a caller bug, not a storage bug).
  - `OperationPlanCodec.TryLoad(string path, out OperationPlan? plan)` returns `bool` — `false` if the file is missing, fails to deserialize, has an unrecognized `SchemaVersion`, or fails `Verify()` on reload. This is the "durably stored and independently verifiable" gate design §3 step 5 requires before a journal may reference the plan.

The integrity hash is computed over `(Identifier, Normalize(IntendedRawPath, DisplayName))` pairs, ordered by `Identifier` ordinally so hash computation doesn't depend on list order — matching design §3's "targetHash ... computed over normalized paths."

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationPlanTests
{
    private static readonly OperationPlanItem[] SampleItems =
    [
        new("mod-a", "Gear\\A", "Weapons\\A", "A"),
        new("mod-b", "Gear\\B", "Weapons\\B", "B"),
    ];

    [Fact]
    public void Create_ProducesAPlanThatVerifiesSuccessfully()
    {
        var plan = OperationPlan.Create(OperationType.Apply, SampleItems);

        Assert.True(plan.Verify());
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal(2, plan.Items.Count);
    }

    [Fact]
    public void ComputeIntegrityHash_IsOrderIndependent()
    {
        var forward = OperationPlan.ComputeIntegrityHash(SampleItems);
        var reversed = OperationPlan.ComputeIntegrityHash(SampleItems.Reverse().ToList());

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void ComputeIntegrityHash_ChangesWhenAnIntendedPathChanges()
    {
        var original = OperationPlan.ComputeIntegrityHash(SampleItems);
        var mutated = new[]
        {
            SampleItems[0] with { IntendedRawPath = "Weapons\\Different" },
            SampleItems[1],
        };

        var mutatedHash = OperationPlan.ComputeIntegrityHash(mutated);

        Assert.NotEqual(original, mutatedHash);
    }

    [Fact]
    public void ComputeIntegrityHash_IsUnchangedByPenumbraDuplicateMarkerReshuffling()
    {
        // "A" and "A (3)" are the same persisted location per PenumbraPathSemantics when the
        // duplicate-marker base equals the mod's own display name — the hash must not care.
        var withoutMarker = new[] { SampleItems[0], SampleItems[1] };
        var withMarker = new[]
        {
            SampleItems[0] with { IntendedRawPath = "Weapons\\A (3)" },
            SampleItems[1],
        };

        Assert.Equal(
            OperationPlan.ComputeIntegrityHash(withoutMarker),
            OperationPlan.ComputeIntegrityHash(withMarker));
    }

    [Fact]
    public void SaveThenTryLoad_RoundTripsAndVerifies()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Restore, SampleItems);

            OperationPlanCodec.Save(path, plan);
            var loaded = OperationPlanCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.NotNull(result);
            Assert.Equal(plan.Id, result!.Id);
            Assert.Equal(plan.IntegrityHash, result.IntegrityHash);
            Assert.True(result.Verify());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenFileMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationPlanCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenIntegrityHashHasBeenTampered()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Apply, SampleItems);
            OperationPlanCodec.Save(path, plan);

            var tamperedJson = File.ReadAllText(path).Replace(plan.IntegrityHash, "tampered-hash-value");
            File.WriteAllText(path, tamperedJson);

            var loaded = OperationPlanCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationPlanTests`
Expected: FAIL — `OperationPlan`, `OperationPlanItem`, `OperationType`, `OperationPlanCodec` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationType { Apply, Restore }

public sealed record OperationPlanItem(
    string Identifier, string OriginalRawPath, string IntendedRawPath, string DisplayName);

public sealed record OperationPlan(
    Guid Id,
    OperationType Type,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    string IntegrityHash,
    IReadOnlyList<OperationPlanItem> Items)
{
    public const int CurrentSchemaVersion = 1;

    public static OperationPlan Create(OperationType type, IReadOnlyList<OperationPlanItem> items) =>
        new(Guid.NewGuid(), type, DateTimeOffset.UtcNow, CurrentSchemaVersion, ComputeIntegrityHash(items), items);

    public bool Verify() => IntegrityHash == ComputeIntegrityHash(Items);

    // Ordered by Identifier so hash computation doesn't depend on list order, and hashed over
    // normalized (not raw) intended paths so a Penumbra reload that reshuffles a transient
    // " (N)" duplicate-marker suffix can never spuriously invalidate a saved plan. See
    // PenumbraPathSemantics.Normalize and design doc section 3/6.
    public static string ComputeIntegrityHash(IReadOnlyList<OperationPlanItem> items)
    {
        var canonical = items
            .OrderBy(i => i.Identifier, StringComparer.Ordinal)
            .Select(i => $"{i.Identifier}{PenumbraPathSemantics.Normalize(i.IntendedRawPath, i.DisplayName)}");
        var bytes = Encoding.UTF8.GetBytes(string.Join('', canonical));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public static class OperationPlanCodec
{
    public static void Save(string path, OperationPlan plan)
    {
        if (!plan.Verify())
            throw new InvalidOperationException("Refusing to persist an OperationPlan that fails its own integrity check.");

        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(plan));
    }

    public static bool TryLoad(string path, out OperationPlan? plan)
    {
        plan = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        OperationPlan? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<OperationPlan>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.SchemaVersion != OperationPlan.CurrentSchemaVersion || !candidate.Verify())
            return false;

        plan = candidate;
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationPlanTests`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationPlan.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationPlanTests.cs
git commit -m "feat: add operation plan model with integrity hash and codec"
```

---

### Task 4: Operation journal model, stages, and codec

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/OperationJournal.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationJournalTests.cs`

**Interfaces:**
- Consumes: `AtomicFile` (Task 1); `OperationType` (Task 3).
- Produces:
  - `OperationStage` enum: `Preparing`, `Prepared`, `Mutating`, `Refreshing`, `Verifying`, `Completed`, `CompletedWithItemFailures`, `FailedBeforeMutation`, `FailedPartiallyApplied`, `AcceptedCurrentState` — the terminal/non-terminal set from design §2. (`Idle`, `Interrupted`, `RecoveryRequired`, `Recovering` are controller-level states from design §2 that are never written into a journal — `Interrupted` is inferred at startup per design §13, and the other three describe the running plugin's in-memory reaction to a journal, not the journal's own persisted stage. They belong to the operation controller built in the follow-on plan, not this record.)
  - `OperationJournal` record: `(Guid OperationId, OperationType Type, OperationStage Status, DateTimeOffset StartedAt, int TotalItems, int CompletedItems, string? LastCompletedIdentifier, Guid SnapshotId, Guid PlanId, string TargetHash, Guid? RecoveryOfOperationId, DateTimeOffset UpdatedAt)`.
  - `OperationJournal.IsTerminal` (bool property) — true for `Completed`, `CompletedWithItemFailures`, `FailedBeforeMutation`, `FailedPartiallyApplied`, `AcceptedCurrentState`; false otherwise. This is what startup recovery detection checks (design §7: "if a journal exists with a non-terminal status").
  - `OperationJournalCodec.Save(string path, OperationJournal journal)` (void) — unconditional atomic write, no verification gate (unlike the plan codec, a journal has no self-referential integrity hash to check — its truth is external, per design §6/§7).
  - `OperationJournalCodec.TryLoad(string path, out OperationJournal? journal)` returns `bool`.
  - `CheckpointPolicy.IsDue(int completedSinceLastCheckpoint, TimeSpan elapsedSinceLastCheckpoint)` returns `bool` — true when `completedSinceLastCheckpoint >= 10` or `elapsedSinceLastCheckpoint >= TimeSpan.FromMilliseconds(500)` (design §6's "whichever comes first," using the low end of both stated ranges — 10 mutations / 500ms — as the concrete threshold; the design states this range explicitly rather than a target to tune, so the low end is the correct choice for a first implementation and is cheap to widen later if checkpoint I/O proves excessive in Phase A profiling).

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationJournalTests
{
    private static OperationJournal SampleJournal(OperationStage status = OperationStage.Mutating) => new(
        OperationId: Guid.NewGuid(),
        Type: OperationType.Apply,
        Status: status,
        StartedAt: DateTimeOffset.UtcNow,
        TotalItems: 401,
        CompletedItems: 173,
        LastCompletedIdentifier: "mod-173",
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "abc123",
        RecoveryOfOperationId: null,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(OperationStage.Preparing, false)]
    [InlineData(OperationStage.Prepared, false)]
    [InlineData(OperationStage.Mutating, false)]
    [InlineData(OperationStage.Refreshing, false)]
    [InlineData(OperationStage.Verifying, false)]
    [InlineData(OperationStage.Completed, true)]
    [InlineData(OperationStage.CompletedWithItemFailures, true)]
    [InlineData(OperationStage.FailedBeforeMutation, true)]
    [InlineData(OperationStage.FailedPartiallyApplied, true)]
    [InlineData(OperationStage.AcceptedCurrentState, true)]
    public void IsTerminal_MatchesDesignedTerminalSet(OperationStage status, bool expectedTerminal)
    {
        var journal = SampleJournal(status);

        Assert.Equal(expectedTerminal, journal.IsTerminal);
    }

    [Fact]
    public void SaveThenTryLoad_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var journal = SampleJournal();

            OperationJournalCodec.Save(path, journal);
            var loaded = OperationJournalCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.Equal(journal, result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenFileMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationJournalCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class CheckpointPolicyTests
{
    [Fact]
    public void IsDue_TrueWhenItemCountThresholdReached()
    {
        Assert.True(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 10, elapsedSinceLastCheckpoint: TimeSpan.Zero));
    }

    [Fact]
    public void IsDue_TrueWhenTimeThresholdReached()
    {
        Assert.True(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 1, elapsedSinceLastCheckpoint: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void IsDue_FalseWhenNeitherThresholdReached()
    {
        Assert.False(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 5, elapsedSinceLastCheckpoint: TimeSpan.FromMilliseconds(200)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~OperationJournalTests|FullyQualifiedName~CheckpointPolicyTests`
Expected: FAIL — `OperationJournal`, `OperationStage`, `OperationJournalCodec`, `CheckpointPolicy` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationStage
{
    Preparing,
    Prepared,
    Mutating,
    Refreshing,
    Verifying,
    Completed,
    CompletedWithItemFailures,
    FailedBeforeMutation,
    FailedPartiallyApplied,
    AcceptedCurrentState,
}

public sealed record OperationJournal(
    Guid OperationId,
    OperationType Type,
    OperationStage Status,
    DateTimeOffset StartedAt,
    int TotalItems,
    int CompletedItems,
    string? LastCompletedIdentifier,
    Guid SnapshotId,
    Guid PlanId,
    string TargetHash,
    Guid? RecoveryOfOperationId,
    DateTimeOffset UpdatedAt)
{
    private static readonly HashSet<OperationStage> TerminalStages =
    [
        OperationStage.Completed,
        OperationStage.CompletedWithItemFailures,
        OperationStage.FailedBeforeMutation,
        OperationStage.FailedPartiallyApplied,
        OperationStage.AcceptedCurrentState,
    ];

    public bool IsTerminal => TerminalStages.Contains(Status);
}

public static class OperationJournalCodec
{
    public static void Save(string path, OperationJournal journal) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(journal));

    public static bool TryLoad(string path, out OperationJournal? journal)
    {
        journal = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        try
        {
            journal = JsonSerializer.Deserialize<OperationJournal>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        return journal is not null;
    }
}

/// <summary>
/// Design doc section 6: checkpoint on whichever threshold is reached first, so a large library
/// doesn't rewrite the journal after every single mutation (filesystem churn on HDDs) while a
/// stalled operation still checkpoints promptly on wall-clock time.
/// </summary>
public static class CheckpointPolicy
{
    private const int ItemThreshold = 10;
    private static readonly TimeSpan TimeThreshold = TimeSpan.FromMilliseconds(500);

    public static bool IsDue(int completedSinceLastCheckpoint, TimeSpan elapsedSinceLastCheckpoint) =>
        completedSinceLastCheckpoint >= ItemThreshold || elapsedSinceLastCheckpoint >= TimeThreshold;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~OperationJournalTests|FullyQualifiedName~CheckpointPolicyTests`
Expected: PASS (13 tests)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/OperationJournal.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/OperationJournalTests.cs
git commit -m "feat: add operation journal model, stages, and checkpoint policy"
```

---

### Task 5: Recovery classifier — per-identifier classification

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs`

**Interfaces:**
- Consumes: `OperationPlan`, `OperationPlanItem` (Task 3); `PenumbraPathSemantics.AreEquivalent` (existing); `Organizer.LiveMod`, `Organizer.RollbackSnapshot` (existing, `RollbackHistory.cs:3-10`).
- Produces:
  - `ItemRecoveryState` enum: `AtOriginal`, `AtTarget`, `AtBoth`, `AtNeither`, `MissingLive`, `MissingSnapshot`, `MissingPlan`.
  - `ItemRecoveryClassification` record: `(string Identifier, ItemRecoveryState State)`.
  - `RecoveryClassifier.ClassifyItems(OperationPlan plan, RollbackSnapshot snapshot, IReadOnlyList<LiveMod> liveMods)` returns `IReadOnlyList<ItemRecoveryClassification>` — one entry per plan item, using `PenumbraPathSemantics.AreEquivalent` for every comparison (design §7, the non-negotiable correctness requirement carried over from the earlier review round).

This task covers only per-identifier classification (design §7's classification table). Operation-level outcome derivation (`RecoveryOutcome` — `NoMutationsDetected`, `CompletedButNotFinalized`, `PartiallyApplied`, `Indeterminate`) is Task 6, kept separate because it has its own distinct rule set (design §7's "operation-level outcomes" table) and its own test matrix.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RecoveryClassifierTests
{
    private static OperationPlanItem Item(string id, string original, string intended, string name = "Mod") =>
        new(id, original, intended, name);

    private static RollbackSnapshot Snapshot(params (string Id, string Path)[] entries) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, null, "test snapshot",
        entries.ToDictionary(e => e.Id, e => e.Path, StringComparer.Ordinal));

    private static LiveMod Live(string id, string path, string name = "Mod") =>
        new(id, name, path, HeliosphereManaged: false);

    [Fact]
    public void ClassifyItems_AtOriginal_WhenLiveMatchesSnapshotOnly()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Gear\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtOriginal, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_AtTarget_WhenLiveMatchesIntendedOnly()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Weapons\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtTarget, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_AtBoth_WhenSnapshotAndTargetAreTheSameLocation()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Gear\\A (2)")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Gear\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtBoth, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_AtNeither_WhenLiveMatchesNeitherOriginalNorTarget()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Somewhere\\Else") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtNeither, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_MissingLive_WhenPlannedModIsAbsentFromLiveMods()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = Array.Empty<LiveMod>();

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.MissingLive, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_MissingSnapshot_WhenPlannedModIsAbsentFromSnapshot()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(); // empty — m1 was never captured
        var live = new[] { Live("m1", "Gear\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.MissingSnapshot, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_DuplicateMarkerReshuffling_StillClassifiesAsAtTarget()
    {
        // Same regression the design review flagged for string-equality comparisons: Penumbra
        // discards " (N)" on save and reassigns it arbitrarily on load, so a live path carrying
        // a different suffix than the plan's IntendedRawPath must still classify as AtTarget.
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A (2)", "A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Weapons\\A (7)", "A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtTarget, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_ReturnsOneEntryPerPlanItem_IgnoringUnplannedLiveMods()
    {
        // MissingPlan is a diagnostics-only signal (design section 13), surfaced by the caller
        // comparing live mods against plan identifiers directly — ClassifyItems iterates the
        // plan, so an unplanned live mod simply never appears in its output.
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Gear\\A"), Live("m2", "Gear\\Unplanned") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Single(result);
        Assert.Equal("m1", result[0].Identifier);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~RecoveryClassifierTests`
Expected: FAIL — `RecoveryClassifier`, `ItemRecoveryState`, `ItemRecoveryClassification` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ItemRecoveryState
{
    AtOriginal,
    AtTarget,
    AtBoth,
    AtNeither,
    MissingLive,
    MissingSnapshot,
    MissingPlan,
}

public sealed record ItemRecoveryClassification(string Identifier, ItemRecoveryState State);

/// <summary>
/// Design doc section 7. Every comparison uses PenumbraPathSemantics.AreEquivalent, never raw
/// string equality — see PenumbraPathSemantics.cs for why a live path can legitimately differ
/// from a saved path only in its " (N)" duplicate-marker suffix and still be the same location.
/// </summary>
public static class RecoveryClassifier
{
    public static IReadOnlyList<ItemRecoveryClassification> ClassifyItems(
        OperationPlan plan, RollbackSnapshot snapshot, IReadOnlyList<LiveMod> liveMods)
    {
        var liveByIdentifier = liveMods.ToDictionary(m => m.Identifier, StringComparer.Ordinal);
        var results = new List<ItemRecoveryClassification>(plan.Items.Count);

        foreach (var item in plan.Items)
        {
            var hasSnapshot = snapshot.ModPaths.TryGetValue(item.Identifier, out var snapshotPath);
            var hasLive = liveByIdentifier.TryGetValue(item.Identifier, out var liveMod);

            var state = (hasSnapshot, hasLive) switch
            {
                (false, _) => ItemRecoveryState.MissingSnapshot,
                (true, false) => ItemRecoveryState.MissingLive,
                (true, true) => ClassifyPresent(item, snapshotPath!, liveMod!.FullPath),
            };

            results.Add(new ItemRecoveryClassification(item.Identifier, state));
        }

        return results;
    }

    private static ItemRecoveryState ClassifyPresent(OperationPlanItem item, string snapshotPath, string livePath)
    {
        var snapshotEqualsTarget = PenumbraPathSemantics.AreEquivalent(snapshotPath, item.IntendedRawPath, item.DisplayName);
        if (snapshotEqualsTarget)
            return ItemRecoveryState.AtBoth;

        var liveAtOriginal = PenumbraPathSemantics.AreEquivalent(livePath, snapshotPath, item.DisplayName);
        var liveAtTarget = PenumbraPathSemantics.AreEquivalent(livePath, item.IntendedRawPath, item.DisplayName);

        return (liveAtOriginal, liveAtTarget) switch
        {
            (true, false) => ItemRecoveryState.AtOriginal,
            (false, true) => ItemRecoveryState.AtTarget,
            _ => ItemRecoveryState.AtNeither,
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~RecoveryClassifierTests`
Expected: PASS (8 tests)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs
git commit -m "feat: add per-identifier recovery classification"
```

---

### Task 6: Recovery classifier — operation-level outcome

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs`

**Interfaces:**
- Consumes: `ItemRecoveryClassification`, `ItemRecoveryState` (Task 5).
- Produces:
  - `RecoveryOutcome` enum: `NoMutationsDetected`, `CompletedButNotFinalized`, `PartiallyApplied`, `Indeterminate`.
  - `RecoveryClassifier.DeriveOutcome(IReadOnlyList<ItemRecoveryClassification> classifications)` returns `RecoveryOutcome`, applying design §7's table over the "changed" subset — every classification except `AtBoth`, which design §7 explicitly excludes as a no-op that must not inflate either count.

- [ ] **Step 1: Write the failing tests**

Append to `RecoveryClassifierTests.cs`:

```csharp
public class RecoveryOutcomeTests
{
    private static ItemRecoveryClassification C(ItemRecoveryState state) => new("m", state);

    [Fact]
    public void DeriveOutcome_NoMutationsDetected_WhenAllChangedItemsAreAtOriginal()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtOriginal)]);

        Assert.Equal(RecoveryOutcome.NoMutationsDetected, outcome);
    }

    [Fact]
    public void DeriveOutcome_CompletedButNotFinalized_WhenAllChangedItemsAreAtTarget()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtTarget), C(ItemRecoveryState.AtTarget)]);

        Assert.Equal(RecoveryOutcome.CompletedButNotFinalized, outcome);
    }

    [Fact]
    public void DeriveOutcome_PartiallyApplied_WhenMixOfOriginalAndTargetWithNoNeither()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtTarget)]);

        Assert.Equal(RecoveryOutcome.PartiallyApplied, outcome);
    }

    [Fact]
    public void DeriveOutcome_Indeterminate_WhenAnyItemIsAtNeither()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtNeither)]);

        Assert.Equal(RecoveryOutcome.Indeterminate, outcome);
    }

    [Fact]
    public void DeriveOutcome_Indeterminate_WhenAnyItemIsUnexpectedlyMissing()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.MissingLive)]);

        Assert.Equal(RecoveryOutcome.Indeterminate, outcome);
    }

    [Fact]
    public void DeriveOutcome_AtBothItemsAreExcludedFromClassificationEntirely()
    {
        // AtBoth items must not inflate the "all AtOriginal" or "all AtTarget" checks in either
        // direction (design section 7) — mixing them in with real AtOriginal items must not flip
        // a genuinely-unstarted operation into looking "partially applied".
        var outcome = RecoveryClassifier.DeriveOutcome(
            [C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtBoth)]);

        Assert.Equal(RecoveryOutcome.NoMutationsDetected, outcome);
    }

    [Fact]
    public void DeriveOutcome_NoMutationsDetected_WhenOnlyAtBothItemsExist()
    {
        // Degenerate case: every planned item turned out to be a no-op (original and target
        // normalize to the same location). Nothing changed, so nothing was mutated.
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtBoth), C(ItemRecoveryState.AtBoth)]);

        Assert.Equal(RecoveryOutcome.NoMutationsDetected, outcome);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~RecoveryOutcomeTests`
Expected: FAIL — `RecoveryOutcome` and `DeriveOutcome` do not exist.

- [ ] **Step 3: Add to the implementation**

Append to `RecoveryClassifier.cs`, inside the existing `RecoveryClassifier` class, and add the enum alongside `ItemRecoveryState`:

```csharp
public enum RecoveryOutcome
{
    NoMutationsDetected,
    CompletedButNotFinalized,
    PartiallyApplied,
    Indeterminate,
}
```

```csharp
    public static RecoveryOutcome DeriveOutcome(IReadOnlyList<ItemRecoveryClassification> classifications)
    {
        // AtBoth items are no-ops (original and target are the same persisted location) and
        // are excluded from every rule below — design section 7.
        var changed = classifications.Where(c => c.State != ItemRecoveryState.AtBoth).ToList();

        if (changed.Count == 0)
            return RecoveryOutcome.NoMutationsDetected;

        if (changed.Any(c => c.State is ItemRecoveryState.AtNeither
                or ItemRecoveryState.MissingLive
                or ItemRecoveryState.MissingSnapshot))
            return RecoveryOutcome.Indeterminate;

        var allAtOriginal = changed.All(c => c.State == ItemRecoveryState.AtOriginal);
        if (allAtOriginal)
            return RecoveryOutcome.NoMutationsDetected;

        var allAtTarget = changed.All(c => c.State == ItemRecoveryState.AtTarget);
        if (allAtTarget)
            return RecoveryOutcome.CompletedButNotFinalized;

        return RecoveryOutcome.PartiallyApplied;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~RecoveryClassifierTests|FullyQualifiedName~RecoveryOutcomeTests`
Expected: PASS (15 tests total across both test classes in the file)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Operations/RecoveryClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Operations/RecoveryClassifierTests.cs
git commit -m "feat: add operation-level recovery outcome derivation"
```

---

### Task 7: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: PASS — every existing test plus all tests added in Tasks 1–6, zero failures.

- [ ] **Step 2: Confirm no stray temp files were left in the repo by the AtomicFile tests**

Run: `git status --short`
Expected: no output (tests write to `Directory.CreateTempSubdirectory()`, outside the repo, and clean up in their `finally` blocks — this step catches a test that forgot to).

---

## What this plan does not cover

Deliberately out of scope, per the scope-check in the writing-plans skill — these depend on Dalamud/ImGui/Penumbra IPC and belong in a follow-on plan once this foundation lands:

- The operation controller state machine wiring (`Idle`/`Interrupted`/`RecoveryRequired`/`Recovering`, design §2) and its integration into `Plugin.cs`.
- Frame-budgeted execution against the framework update handler, the over-budget rule, and the IPC failure continuation policy (design §4).
- `ApplyChanges`/`Restore` conversion to build an `OperationPlan` and drive it incrementally.
- The bounded verification settlement window (design §13).
- Recovery resolutions — Continue/Restore/Keep Current as new auditable operations (design §8) — and the recovery dialog UI in `MainWindow`.
- Storage layout and retention (design §15) — where `active-operation.json` and the `plans/`/`completed/` directories actually live under the plugin config directory, and the cleanup pass that enforces retention.
- Diagnostics wiring (design §14) — surfacing journal/plan state through the existing diagnostic dump.
