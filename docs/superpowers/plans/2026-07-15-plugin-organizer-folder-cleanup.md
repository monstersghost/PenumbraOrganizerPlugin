# Folder Cleanup (organization.json orphaned-folder prune) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect orphaned (empty) folder entries in Penumbra's `organization.json` and let the user prune selected ones, with byte-fidelity backup/rollback, as a separate action from mod-move Apply/Rollback.

**Architecture:** Pure data model + codec (`OrganizationJson`/`OrganizationJsonCodec`) and pure planner (`OrganizationCleanupPlanner`) feed a no-IPC file-I/O executor (`FolderCleanupExecutor`); `Plugin.cs` adds thin wrappers that resolve real paths and supply occupancy (fresh `GetModListAdapter` IPC read at write time, last-scan `OrganizerState` for the advisory UI list); `MainWindow.cs` gets an Orphaned Folders section in the Review Changes tab with a two-tier checkbox list and a "Rediscover Mods required" banner.

**Tech Stack:** C# / .NET (Dalamud plugin, `net10.0-windows7.0`), `System.Text.Json`, xUnit tests, `Penumbra.Api` 5.15.1 (read-only IPC use here: `GetModListAdapter`).

**Spec:** `docs/superpowers/specs/2026-07-15-plugin-organizer-folder-cleanup-design.md` — read it before starting any task; it contains the verified ground truth (schema, config-dir derivation, live-tree propagation, `Folders`/`Separators` disjointness) and the rationale for every decision below.

## Global Constraints

- All string/path comparisons in this feature: `StringComparer.Ordinal` / `StringComparison.Ordinal` (spec: "Occupancy comparer"). Never `OrdinalIgnoreCase`, never default.
- Occupancy prefix logic, verbatim: occupied when `occupied.Equals(folder, Ordinal) || occupied.StartsWith(folder + "/", Ordinal)` — never a bare `StartsWith`.
- Occupancy source is `CurrentPath` (detection) / fresh IPC `FullPath` (write) — **never `ProposedPath`**.
- `organization.json` writes: atomic temp-file-then-move only (`File.Move(tmp, target, overwrite: true)`), matching `Plugin.WriteBackup`'s existing pattern.
- Backup content is the raw original bytes read before pruning — never a reread, never a reserialization.
- Every failure mode degrades to "don't touch `organization.json`"; the one deliberate catch is around the backup-promotion write only (`SucceededBackupFailed`).
- File encoding for writes: UTF-8 without BOM (`new UTF8Encoding(false)`) — flagged in the spec as verify-against-a-real-install during in-game verification (Task 7).
- Test command: `dotnet test PenumbraOrganizer.Plugin.Tests` (full), `--filter "FullyQualifiedName~<ClassName>"` (targeted). 131 tests pass before this plan starts; every task ends with the full suite green.
- Build command: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug` — must stay at 0 warnings, 0 errors.
- Commit trailer (repo convention): `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- **Execution process note (repo memory):** every implementer dispatch must begin with a working-directory verification step confirming it is inside its assigned worktree, not the main checkout — implementer subagents have drifted onto `main` before.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `PenumbraOrganizer.Plugin/Organizer/OrganizationJson.cs` | Create | Data model mirroring Penumbra's confirmed schema, with `[JsonExtensionData]` on every type |
| `PenumbraOrganizer.Plugin/Organizer/OrganizationJsonCodec.cs` | Create | Pure parse/serialize with status-carrying parse result; no file I/O |
| `PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs` | Create | Pure detection/prune logic: `GetVirtualParent`, `DetectOrphaned`, `DescribeCustomization`, `Prune` |
| `PenumbraOrganizer.Plugin/Organizer/FolderCleanupResult.cs` | Create | Result/status types for detection, cleanup, rollback |
| `PenumbraOrganizer.Plugin/Organizer/FolderCleanupExecutor.cs` | Create | All file-I/O sequencing (cleanup + rollback); no IPC, no Dalamud types |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` | Modify | Add `HasScanned` property |
| `PenumbraOrganizer.Plugin/Plugin.cs` | Modify | Path derivation + thin wrappers `DetectOrphanedFolders`/`CleanUpFolders`/`RollbackFolderCleanup` |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` | Modify | Orphaned Folders UI section in `DrawReviewTab()` |
| `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationJsonCodecTests.cs` | Create | Codec unit tests |
| `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs` | Create | Planner unit tests |
| `PenumbraOrganizer.Plugin.Tests/Organizer/FolderCleanupExecutorTests.cs` | Create | Executor integration-style tests against temp directories |
| `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs` | Modify | `HasScanned` tests |
| `docs/ROADMAP.md` | Modify | New phase status entry (Task 7) |

---

### Task 1: OrganizationJson data model + codec

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/OrganizationJson.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/OrganizationJsonCodec.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationJsonCodecTests.cs`

**Interfaces:**
- Consumes: nothing (leaf task).
- Produces: `OrganizationJson` (with `Version:int`, `Folders:Dictionary<string,FolderData>`, `Separators:Dictionary<string,SeparatorData>`, all three types carrying `[JsonExtensionData] Dictionary<string,JsonElement>? ExtensionData`); `FolderData` (`uint? ExpandedColor`, `uint? CollapsedColor`, `string? SortMode`, `bool? IsSeparator`); `SeparatorData` (`uint? Color`, `bool Folder`, `long CreationDate`); `OrganizationJsonParseStatus { Ok, MalformedJson, UnsupportedVersion }`; `OrganizationJsonParseResult(OrganizationJson? Data, OrganizationJsonParseStatus Status)`; `OrganizationJsonCodec.Parse(string json) → OrganizationJsonParseResult` (never throws; `Data` non-null exactly when `Status == Ok`); `OrganizationJsonCodec.Serialize(OrganizationJson data) → string` (omits null properties, indented).

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationJsonCodecTests.cs` (no `using Xunit;` needed — the test project uses implicit usings, same as `ApplyPlannerTests.cs`):

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizationJsonCodecTests
{
    private const string WellFormed = """
        {
          "Version": 1,
          "Folders": {
            "Plain/Empty": {},
            "Colored": { "ExpandedColor": 4294901760, "SortMode": "FoldersFirst" }
          },
          "Separators": {
            "MySep": { "Folder": false, "Color": null, "CreationDate": 638123456789 }
          }
        }
        """;

    [Fact]
    public void Parse_WellFormed_ReturnsOkWithData()
    {
        var result = OrganizationJsonCodec.Parse(WellFormed);

        Assert.Equal(OrganizationJsonParseStatus.Ok, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Folders.Count);
        Assert.Single(result.Data.Separators);
        Assert.Equal(4294901760u, result.Data.Folders["Colored"].ExpandedColor);
        Assert.Equal("FoldersFirst", result.Data.Folders["Colored"].SortMode);
        Assert.Null(result.Data.Folders["Plain/Empty"].ExpandedColor);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsMalformedWithNullData()
    {
        var result = OrganizationJsonCodec.Parse("{ not valid json !");

        Assert.Equal(OrganizationJsonParseStatus.MalformedJson, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_VersionTwo_ReturnsUnsupportedVersionWithNullData()
    {
        var result = OrganizationJsonCodec.Parse("""{ "Version": 2, "Folders": {}, "Separators": {} }""");

        Assert.Equal(OrganizationJsonParseStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_MissingVersion_ReturnsUnsupportedVersion()
    {
        // Version defaults to 0 when absent — fail closed, same as any non-1 value.
        var result = OrganizationJsonCodec.Parse("""{ "Folders": {}, "Separators": {} }""");

        Assert.Equal(OrganizationJsonParseStatus.UnsupportedVersion, result.Status);
    }

    [Fact]
    public void UnknownFields_SurviveParseSerializeRoundTrip()
    {
        const string withUnknowns = """
            {
              "Version": 1,
              "Folders": { "F": { "FutureFlag": true } },
              "Separators": {},
              "FutureTopLevel": "kept"
            }
            """;

        var parsed = OrganizationJsonCodec.Parse(withUnknowns);
        Assert.Equal(OrganizationJsonParseStatus.Ok, parsed.Status);

        var reserialized = OrganizationJsonCodec.Serialize(parsed.Data!);

        Assert.Contains("FutureFlag", reserialized);
        Assert.Contains("FutureTopLevel", reserialized);
    }

    [Fact]
    public void Serialize_OmitsNullProperties()
    {
        var parsed = OrganizationJsonCodec.Parse(WellFormed);

        var reserialized = OrganizationJsonCodec.Serialize(parsed.Data!);

        // "Plain/Empty" has every field null — none of the known field names may appear for it.
        // Cheap proxy: CollapsedColor is null on every entry in the fixture, so it must not
        // appear anywhere in the output.
        Assert.DoesNotContain("CollapsedColor", reserialized);
    }

    [Fact]
    public void Serialize_ThenParse_RoundTripsFolderData()
    {
        var parsed = OrganizationJsonCodec.Parse(WellFormed);

        var roundTripped = OrganizationJsonCodec.Parse(OrganizationJsonCodec.Serialize(parsed.Data!));

        Assert.Equal(OrganizationJsonParseStatus.Ok, roundTripped.Status);
        Assert.Equal(4294901760u, roundTripped.Data!.Folders["Colored"].ExpandedColor);
        Assert.Equal(638123456789L, roundTripped.Data.Separators["MySep"].CreationDate);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizationJsonCodecTests"`
Expected: build FAILS with "The type or namespace name 'OrganizationJsonCodec' does not exist" (compile error is the failure mode for a missing type in C# — that counts as the red step).

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/OrganizationJson.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer;

// Mirrors Penumbra's organization.json schema (Luna FileSystemSaver.Organization, Version 1).
// ExtensionData on every type: this plugin rewrites a config file it doesn't own, and a future
// Penumbra field added without a Version bump must survive the prune round-trip.
public sealed class FolderData
{
    public uint? ExpandedColor { get; set; }
    public uint? CollapsedColor { get; set; }
    public string? SortMode { get; set; }
    public bool? IsSeparator { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SeparatorData
{
    public uint? Color { get; set; }
    public bool Folder { get; set; }
    public long CreationDate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class OrganizationJson
{
    public int Version { get; set; }
    public Dictionary<string, FolderData> Folders { get; set; } = new();
    public Dictionary<string, SeparatorData> Separators { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
```

Create `PenumbraOrganizer.Plugin/Organizer/OrganizationJsonCodec.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer;

public enum OrganizationJsonParseStatus
{
    Ok,
    MalformedJson,
    UnsupportedVersion,
}

public sealed record OrganizationJsonParseResult(OrganizationJson? Data, OrganizationJsonParseStatus Status);

public static class OrganizationJsonCodec
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    // Never throws. Data is non-null exactly when Status == Ok. MalformedJson and
    // UnsupportedVersion stay distinct because the UI reports them as different states.
    public static OrganizationJsonParseResult Parse(string json)
    {
        OrganizationJson? data;
        try
        {
            data = JsonSerializer.Deserialize<OrganizationJson>(json);
        }
        catch (JsonException)
        {
            return new OrganizationJsonParseResult(null, OrganizationJsonParseStatus.MalformedJson);
        }

        if (data is null)
            return new OrganizationJsonParseResult(null, OrganizationJsonParseStatus.MalformedJson);
        if (data.Version != 1)
            return new OrganizationJsonParseResult(null, OrganizationJsonParseStatus.UnsupportedVersion);

        return new OrganizationJsonParseResult(data, OrganizationJsonParseStatus.Ok);
    }

    public static string Serialize(OrganizationJson data) =>
        JsonSerializer.Serialize(data, SerializeOptions);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizationJsonCodecTests"`
Expected: 7 tests PASS.

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 138 tests pass (131 existing + 7 new), 0 failures.
Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizationJson.cs PenumbraOrganizer.Plugin/Organizer/OrganizationJsonCodec.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationJsonCodecTests.cs
git commit -m "feat: add organization.json data model and status-carrying codec"
```

---

### Task 2: OrganizationCleanupPlanner (pure detection/prune logic)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs`

**Interfaces:**
- Consumes (Task 1): `OrganizationJson`, `FolderData` — exact shapes as produced there.
- Produces: `CustomizedFolder(string Path, string Description)` record; `OrganizationCleanupPlanner.GetVirtualParent(string path) → string?`; `OrganizationCleanupPlanner.DetectOrphaned(OrganizationJson data, IReadOnlySet<string> occupiedFolders) → (IReadOnlyList<string> PlainEmpty, IReadOnlyList<CustomizedFolder> CustomizedEmpty)` (both lists sorted ascending Ordinal by path); `OrganizationCleanupPlanner.Prune(OrganizationJson data, IReadOnlySet<string> selectedPaths) → OrganizationJson` (copy with keys removed from `Folders` only; `Separators` and `ExtensionData` carried through by reference).

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs`:

```csharp
using System.Text.Json;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizationCleanupPlannerTests
{
    private static OrganizationJson Make(params (string Path, FolderData Data)[] folders)
    {
        var result = new OrganizationJson { Version = 1 };
        foreach (var (path, data) in folders)
            result.Folders[path] = data;
        return result;
    }

    private static IReadOnlySet<string> Occupied(params string[] folders) =>
        folders.ToHashSet(StringComparer.Ordinal);

    // --- GetVirtualParent ---

    [Fact]
    public void GetVirtualParent_RootLevelMod_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.GetVirtualParent("ModName"));

    [Fact]
    public void GetVirtualParent_OneLevel_ReturnsFolder()
        => Assert.Equal("A", OrganizationCleanupPlanner.GetVirtualParent("A/B"));

    [Fact]
    public void GetVirtualParent_TwoLevels_ReturnsFullParentPath()
        => Assert.Equal("A/B", OrganizationCleanupPlanner.GetVirtualParent("A/B/C"));

    [Fact]
    public void GetVirtualParent_TrailingSlash_TrimmedNotOwnPath()
        => Assert.Equal("A", OrganizationCleanupPlanner.GetVirtualParent("A/B/"));

    [Fact]
    public void GetVirtualParent_LeadingSlash_TreatedAsRootLevel()
        => Assert.Null(OrganizationCleanupPlanner.GetVirtualParent("/Mod"));

    // --- DetectOrphaned ---

    [Fact]
    public void DetectOrphaned_OccupiedExactMatch_NotOrphaned()
    {
        var data = Make(("Creators/Alice", new FolderData()));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied("Creators/Alice"));

        Assert.Empty(plain);
        Assert.Empty(customized);
    }

    [Fact]
    public void DetectOrphaned_AncestorOfOccupied_NotOrphaned()
    {
        // "Creators" has no mod directly in it, but its descendant does — never prunable.
        var data = Make(("Creators", new FolderData()));

        var (plain, _) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied("Creators/Alice"));

        Assert.Empty(plain);
    }

    [Fact]
    public void DetectOrphaned_PrefixWithoutSegmentBoundary_IsOrphaned()
    {
        // "Body" must NOT count as ancestor of "BodyMods/Author" — segment boundary required.
        var data = Make(("Body", new FolderData()));

        var (plain, _) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied("BodyMods/Author"));

        Assert.Equal(["Body"], plain);
    }

    [Fact]
    public void DetectOrphaned_PlainEmpty_AllKnownFieldsNullAndNoExtensionData()
    {
        var data = Make(("Old/Empty", new FolderData()));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Equal(["Old/Empty"], plain);
        Assert.Empty(customized);
    }

    [Fact]
    public void DetectOrphaned_KnownCustomization_ClassifiesCustomized()
    {
        var data = Make(("Favorites", new FolderData { ExpandedColor = 123, SortMode = "FoldersFirst" }));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Empty(plain);
        var entry = Assert.Single(customized);
        Assert.Equal("Favorites", entry.Path);
        Assert.Contains("color", entry.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FoldersFirst", entry.Description);
    }

    [Fact]
    public void DetectOrphaned_UnknownFieldsOnly_ClassifiesCustomizedNotPlain()
    {
        // An entry customized only via a field this plugin doesn't know about must get the
        // higher-friction treatment — ExtensionData exists to protect data we can't interpret.
        using var doc = JsonDocument.Parse("true");
        var folder = new FolderData
        {
            ExtensionData = new Dictionary<string, JsonElement> { ["FutureFlag"] = doc.RootElement.Clone() },
        };
        var data = Make(("Mystery", folder));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Empty(plain);
        var entry = Assert.Single(customized);
        Assert.Equal("Mystery", entry.Path);
    }

    [Fact]
    public void DetectOrphaned_OutputSortedAscendingOrdinal()
    {
        var data = Make(("Zebra", new FolderData()), ("Alpha", new FolderData()));

        var (plain, _) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Equal(["Alpha", "Zebra"], plain);
    }

    // --- Prune ---

    [Fact]
    public void Prune_RemovesSelectedFoldersOnly()
    {
        var data = Make(("Keep", new FolderData()), ("Remove", new FolderData()));

        var pruned = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.True(pruned.Folders.ContainsKey("Keep"));
        Assert.False(pruned.Folders.ContainsKey("Remove"));
    }

    [Fact]
    public void Prune_LeavesSeparatorsUntouched()
    {
        var data = Make(("Remove", new FolderData()));
        data.Separators["MySep"] = new SeparatorData { Folder = false, CreationDate = 42 };

        var pruned = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.Same(data.Separators, pruned.Separators);
    }

    [Fact]
    public void Prune_DoesNotMutateInput()
    {
        var data = Make(("Remove", new FolderData()));

        _ = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.True(data.Folders.ContainsKey("Remove"));
    }

    [Fact]
    public void Prune_CarriesVersionAndExtensionData()
    {
        using var doc = JsonDocument.Parse("\"kept\"");
        var data = Make(("Remove", new FolderData()));
        data.ExtensionData = new Dictionary<string, JsonElement> { ["FutureTopLevel"] = doc.RootElement.Clone() };

        var pruned = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.Equal(1, pruned.Version);
        Assert.Same(data.ExtensionData, pruned.ExtensionData);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizationCleanupPlannerTests"`
Expected: build FAILS ("'OrganizationCleanupPlanner' does not exist").

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

// Description is a human-readable summary of what's customized, rendered by the UI next to
// each unchecked customized-empty entry.
public sealed record CustomizedFolder(string Path, string Description);

public static class OrganizationCleanupPlanner
{
    // Parent-folder extraction for Penumbra virtual paths (forward-slash separated —
    // System.IO.Path assumes the OS separator and is not safe here). A path with no '/' is a
    // root-level mod: it occupies no folder, so this returns null rather than an empty string
    // or the mod's own name. Trailing slashes are trimmed defensively; a leading slash falls
    // out of the index > 0 check (index 0 → null, treated as root-level).
    public static string? GetVirtualParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index > 0 ? trimmed[..index] : null;
    }

    public static (IReadOnlyList<string> PlainEmpty, IReadOnlyList<CustomizedFolder> CustomizedEmpty)
        DetectOrphaned(OrganizationJson data, IReadOnlySet<string> occupiedFolders)
    {
        var plain = new List<string>();
        var customized = new List<CustomizedFolder>();

        foreach (var (path, folder) in data.Folders)
        {
            if (IsOccupied(path, occupiedFolders))
                continue;

            if (IsPlain(folder))
                plain.Add(path);
            else
                customized.Add(new CustomizedFolder(path, DescribeCustomization(folder)));
        }

        plain.Sort(StringComparer.Ordinal);
        customized.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (plain, customized);
    }

    public static OrganizationJson Prune(OrganizationJson data, IReadOnlySet<string> selectedPaths)
    {
        var remaining = data.Folders
            .Where(kvp => !selectedPaths.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        return new OrganizationJson
        {
            Version = data.Version,
            Folders = remaining,
            Separators = data.Separators,
            ExtensionData = data.ExtensionData,
        };
    }

    // Occupied when it equals, or is a segment-boundary-safe ancestor of, any occupied folder.
    // Never a bare StartsWith — "Body" must not match "BodyMods/Author".
    private static bool IsOccupied(string folder, IReadOnlySet<string> occupiedFolders) =>
        occupiedFolders.Any(occupied =>
            occupied.Equals(folder, StringComparison.Ordinal) ||
            occupied.StartsWith(folder + "/", StringComparison.Ordinal));

    private static bool IsPlain(FolderData folder) =>
        folder.ExpandedColor is null &&
        folder.CollapsedColor is null &&
        folder.SortMode is null &&
        folder.IsSeparator is null &&
        (folder.ExtensionData is null || folder.ExtensionData.Count == 0);

    private static string DescribeCustomization(FolderData folder)
    {
        var parts = new List<string>();
        if (folder.ExpandedColor is not null || folder.CollapsedColor is not null)
            parts.Add("custom color");
        if (folder.SortMode is not null)
            parts.Add($"sort: {folder.SortMode}");
        if (folder.IsSeparator is not null)
            parts.Add("separator flag");
        if (folder.ExtensionData is { Count: > 0 })
            parts.Add("unknown settings");
        return string.Join(", ", parts);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizationCleanupPlannerTests"`
Expected: 16 tests PASS.

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 154 tests pass, 0 failures.
Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs
git commit -m "feat: add pure orphaned-folder detection and prune planner"
```

---

### Task 3: OrganizerState.HasScanned

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (property + one line in `LoadScan`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs` (append two tests)

**Interfaces:**
- Consumes: existing `OrganizerState.LoadScan(IEnumerable<OrganizerModRow>, IReadOnlySet<string>)`.
- Produces: `OrganizerState.HasScanned → bool` (get-only; `false` until first `LoadScan`, `true` afterward including for an empty scan; never reset).

**Why this exists (from the spec):** `Mods.Count == 0` conflates "never scanned" with "scanned, genuinely empty library" — and an empty library is exactly where every persisted folder may legitimately be orphaned, so the two states must be distinguishable.

- [ ] **Step 1: Write the failing tests**

Append to the existing class in `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs` (open the file first and match its existing row-construction helper if one exists; otherwise construct `OrganizerModRow` inline as below):

```csharp
    [Fact]
    public void HasScanned_FalseBeforeAnyScan()
    {
        var state = new OrganizerState();

        Assert.False(state.HasScanned);
    }

    [Fact]
    public void HasScanned_TrueAfterEmptyScan()
    {
        // The specific case Mods.Count == 0 can't distinguish: a scan that found zero mods.
        var state = new OrganizerState();

        state.LoadScan([], new HashSet<string>());

        Assert.True(state.HasScanned);
        Assert.Empty(state.Mods);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: build FAILS ("'OrganizerState' does not contain a definition for 'HasScanned'").

- [ ] **Step 3: Write the implementation**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, add the property between the `Mods` property and `LoadScan`, and set it at the top of `LoadScan`:

```csharp
    public bool HasScanned { get; private set; }

    public void LoadScan(IEnumerable<OrganizerModRow> scanned, IReadOnlySet<string> previouslyProtected)
    {
        HasScanned = true;
        _mods.Clear();
        // ... rest of the existing method body unchanged ...
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: all tests in the class PASS (existing ones plus the 2 new).

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 156 tests pass, 0 failures.
Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add OrganizerState.HasScanned to distinguish unscanned from empty"
```

---

### Task 4: Result types + FolderCleanupExecutor (file-I/O sequencing)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/FolderCleanupResult.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/FolderCleanupExecutor.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/FolderCleanupExecutorTests.cs`

**Interfaces:**
- Consumes (Task 1): `OrganizationJsonCodec.Parse/Serialize`, `OrganizationJsonParseStatus`. (Task 2): `OrganizationCleanupPlanner.DetectOrphaned/Prune`, `CustomizedFolder`.
- Produces: `FolderDetectionStatus { Detected, NotScanned, FileMissing, UnsupportedVersion, MalformedJson }`; `FolderDetectionResult(IReadOnlyList<string> PlainEmpty, IReadOnlyList<CustomizedFolder> CustomizedEmpty, FolderDetectionStatus Status)`; `FolderCleanupStatus { Success, SucceededBackupFailed, NothingSelected, NothingStillValid, FileMissing, UnsupportedVersion, MalformedJson }`; `FolderCleanupResult(IReadOnlyList<string> Pruned, IReadOnlyList<string> SkippedStale, FolderCleanupStatus Status)`; `FolderRollbackStatus { Restored, NoBackup, InvalidBackup }`; `FolderRollbackResult(FolderRollbackStatus Status)`; `FolderCleanupExecutor.Execute(string organizationJsonPath, string backupFilePath, IReadOnlySet<string> selectedPaths, IReadOnlySet<string> occupiedFolders) → FolderCleanupResult`; `FolderCleanupExecutor.ExecuteRollback(string organizationJsonPath, string backupFilePath) → FolderRollbackResult`.

- [ ] **Step 1: Write the result types** (no test of their own — plain declarations consumed by the executor tests below)

Create `PenumbraOrganizer.Plugin/Organizer/FolderCleanupResult.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

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
    SucceededBackupFailed, // pruned, but the new backup could not be written
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

- [ ] **Step 2: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/FolderCleanupExecutorTests.cs`. These run against real temp directories — `IDisposable` per-test cleanup, same style as any temp-dir fixture:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class FolderCleanupExecutorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("folder-cleanup-tests").FullName;

    private string OrgPath => Path.Combine(_dir, "organization.json");
    private string BackupPath => Path.Combine(_dir, "organizer-folder-backup.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string TwoFolderFile = """
        {
          "Version": 1,
          "Folders": {
            "Old/Empty": {},
            "Creators/Alice": {}
          },
          "Separators": {}
        }
        """;

    private static IReadOnlySet<string> Set(params string[] items) =>
        items.ToHashSet(StringComparer.Ordinal);

    // --- Execute: happy path ---

    [Fact]
    public void Execute_Success_PrunesSelectedAndReturnsSuccess()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.Success, result.Status);
        Assert.Equal(["Old/Empty"], result.Pruned);
        Assert.Empty(result.SkippedStale);
        var reparsed = OrganizationJsonCodec.Parse(File.ReadAllText(OrgPath));
        Assert.False(reparsed.Data!.Folders.ContainsKey("Old/Empty"));
        Assert.True(reparsed.Data.Folders.ContainsKey("Creators/Alice"));
    }

    [Fact]
    public void Execute_Success_BackupIsByteIdenticalToPrePruneFile()
    {
        // The regression test for the backup-source rule: backup content must be the ORIGINAL
        // bytes retained in memory before pruning — never a reread of the post-prune file.
        File.WriteAllText(OrgPath, TwoFolderFile);
        var originalBytes = File.ReadAllBytes(OrgPath);

        FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(originalBytes, File.ReadAllBytes(BackupPath));
    }

    // --- Execute: no-op guards ---

    [Fact]
    public void Execute_NothingSelected_TouchesNoFiles()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        var before = File.ReadAllBytes(OrgPath);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set(), Set());

        Assert.Equal(FolderCleanupStatus.NothingSelected, result.Status);
        Assert.Equal(before, File.ReadAllBytes(OrgPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void Execute_AllSelectionsStale_TouchesNoFilesAndPreservesExistingBackup()
    {
        // "Old/Empty" is occupied at write time (a mod was moved into it via Penumbra's UI
        // after selection) and "Ghost" no longer exists in the file — nothing survives
        // re-verification. A pre-existing backup from an earlier cleanup must survive untouched.
        File.WriteAllText(OrgPath, TwoFolderFile);
        File.WriteAllText(BackupPath, "previous-backup-content");

        var result = FolderCleanupExecutor.Execute(
            OrgPath, BackupPath, Set("Old/Empty", "Ghost"), Set("Old/Empty"));

        Assert.Equal(FolderCleanupStatus.NothingStillValid, result.Status);
        Assert.Equal(2, result.SkippedStale.Count);
        Assert.Equal("previous-backup-content", File.ReadAllText(BackupPath));
        Assert.Contains("Old/Empty", File.ReadAllText(OrgPath));
    }

    [Fact]
    public void Execute_PartiallyStale_PrunesValidAndReportsStale()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty", "Ghost"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.Success, result.Status);
        Assert.Equal(["Old/Empty"], result.Pruned);
        Assert.Equal(["Ghost"], result.SkippedStale);
    }

    // --- Execute: file-state failures ---

    [Fact]
    public void Execute_FileMissing_ReturnsFileMissing()
    {
        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("X"), Set());

        Assert.Equal(FolderCleanupStatus.FileMissing, result.Status);
    }

    [Fact]
    public void Execute_UnsupportedVersion_ReturnsUnsupportedVersionAndTouchesNothing()
    {
        File.WriteAllText(OrgPath, """{ "Version": 2, "Folders": { "X": {} }, "Separators": {} }""");
        var before = File.ReadAllBytes(OrgPath);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("X"), Set());

        Assert.Equal(FolderCleanupStatus.UnsupportedVersion, result.Status);
        Assert.Equal(before, File.ReadAllBytes(OrgPath));
    }

    [Fact]
    public void Execute_MalformedJson_ReturnsMalformedJsonAndTouchesNothing()
    {
        File.WriteAllText(OrgPath, "{ broken !");

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("X"), Set());

        Assert.Equal(FolderCleanupStatus.MalformedJson, result.Status);
        Assert.Equal("{ broken !", File.ReadAllText(OrgPath));
    }

    [Fact]
    public void Execute_FileWithUtf8Bom_StillParsesAndSucceeds()
    {
        // File.ReadAllText auto-detects a BOM but raw-byte decoding does not — the executor
        // must strip an EF BB BF prefix before parsing, or a BOM'd real-install file would be
        // misreported as MalformedJson. (Backup fidelity is unaffected: raw bytes, BOM and all.)
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(OrgPath, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(TwoFolderFile)]);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.Success, result.Status);
        Assert.Equal(0xEF, File.ReadAllBytes(BackupPath)[0]); // backup preserves the original BOM
    }

    // --- Execute: backup promotion failure ---

    [Fact]
    public void Execute_BackupWriteFails_PruneStandsAndReturnsSucceededBackupFailed()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        // A path UNDER an existing regular file is unwritable on every platform — forces the
        // backup promotion (and its temp file) to throw while the target write succeeds.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "i am a file, not a directory");
        var unwritableBackup = Path.Combine(blocker, "backup.json");

        var result = FolderCleanupExecutor.Execute(OrgPath, unwritableBackup, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.SucceededBackupFailed, result.Status);
        Assert.Equal(["Old/Empty"], result.Pruned);
        var reparsed = OrganizationJsonCodec.Parse(File.ReadAllText(OrgPath));
        Assert.False(reparsed.Data!.Folders.ContainsKey("Old/Empty")); // prune stands
    }

    // --- ExecuteRollback ---

    [Fact]
    public void ExecuteRollback_NoBackup_ReturnsNoBackup()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.NoBackup, result.Status);
    }

    [Fact]
    public void ExecuteRollback_RestoresBytesAndDeletesBackup()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        var originalBytes = File.ReadAllBytes(OrgPath);
        FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.Restored, result.Status);
        Assert.Equal(originalBytes, File.ReadAllBytes(OrgPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void ExecuteRollback_InvalidBackup_AbortsWithoutTouchingLiveFile()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        File.WriteAllText(BackupPath, "{ not valid json");
        var before = File.ReadAllBytes(OrgPath);

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.InvalidBackup, result.Status);
        Assert.Equal(before, File.ReadAllBytes(OrgPath));
        Assert.True(File.Exists(BackupPath)); // not deleted either
    }

    [Fact]
    public void ExecuteRollback_UnsupportedVersionBackup_TreatedAsInvalid()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        File.WriteAllText(BackupPath, """{ "Version": 99, "Folders": {}, "Separators": {} }""");

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.InvalidBackup, result.Status);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~FolderCleanupExecutorTests"`
Expected: build FAILS ("'FolderCleanupExecutor' does not exist").

- [ ] **Step 4: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/FolderCleanupExecutor.cs`:

```csharp
using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer;

// All file-I/O sequencing for folder cleanup and its rollback. Deliberately no IPC and no
// Dalamud types: Plugin.cs resolves the real paths and supplies occupancy; this class is what
// the integration-style tests drive against a temp directory.
public static class FolderCleanupExecutor
{
    // UTF-8 without BOM — matches Penumbra's own JSON files. Flagged in the spec for
    // confirmation against a real install during in-game verification.
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);

    public static FolderCleanupResult Execute(
        string organizationJsonPath,
        string backupFilePath,
        IReadOnlySet<string> selectedPaths,
        IReadOnlySet<string> occupiedFolders)
    {
        if (selectedPaths.Count == 0)
            return new FolderCleanupResult([], [], FolderCleanupStatus.NothingSelected);

        if (!File.Exists(organizationJsonPath))
            return new FolderCleanupResult([], [], FolderCleanupStatus.FileMissing);

        // Read exactly once and retain: these bytes — never a reread — become the backup, so
        // the backup can never accidentally be built from the pruned file.
        var originalBytes = File.ReadAllBytes(organizationJsonPath);

        var parse = OrganizationJsonCodec.Parse(DecodeText(originalBytes));
        if (parse.Status == OrganizationJsonParseStatus.MalformedJson)
            return new FolderCleanupResult([], [], FolderCleanupStatus.MalformedJson);
        if (parse.Status == OrganizationJsonParseStatus.UnsupportedVersion)
            return new FolderCleanupResult([], [], FolderCleanupStatus.UnsupportedVersion);

        // Re-verify every selection against the file as it exists now and live occupancy:
        // still present, and still orphaned. Reuses DetectOrphaned so the write path can never
        // disagree with detection about what "orphaned" means.
        var (plainEmpty, customizedEmpty) = OrganizationCleanupPlanner.DetectOrphaned(parse.Data!, occupiedFolders);
        var orphanedNow = plainEmpty
            .Concat(customizedEmpty.Select(c => c.Path))
            .ToHashSet(StringComparer.Ordinal);

        var stillValid = selectedPaths.Where(orphanedNow.Contains)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var skippedStale = selectedPaths.Where(p => !orphanedNow.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        // A no-op attempt must be indistinguishable from never clicking the button: no file
        // writes, and above all no overwrite of a previous valid rollback point.
        if (stillValid.Count == 0)
            return new FolderCleanupResult([], skippedStale, FolderCleanupStatus.NothingStillValid);

        var pruned = OrganizationCleanupPlanner.Prune(parse.Data!, stillValid.ToHashSet(StringComparer.Ordinal));
        var prunedJson = OrganizationJsonCodec.Serialize(pruned);

        // Target write first; backup promotion only after it succeeds. Reversed, a failed
        // target write would already have destroyed the previous backup for nothing. If this
        // write throws, the caller's error handling surfaces it — nothing has been backed up
        // over, and the atomic move means no half-written target.
        AtomicWrite(organizationJsonPath, Encoding.GetBytes(prunedJson));

        try
        {
            AtomicWrite(backupFilePath, originalBytes);
        }
        catch (Exception)
        {
            // Partial infrastructure failure, not a failed cleanup: the prune stands, but this
            // action has no rollback point. Any pre-existing backup file was left untouched
            // (the atomic temp write failed before the move) — it is now stale relative to
            // this cleanup, which the UI warns about.
            return new FolderCleanupResult(stillValid, skippedStale, FolderCleanupStatus.SucceededBackupFailed);
        }

        return new FolderCleanupResult(stillValid, skippedStale, FolderCleanupStatus.Success);
    }

    public static FolderRollbackResult ExecuteRollback(string organizationJsonPath, string backupFilePath)
    {
        if (!File.Exists(backupFilePath))
            return new FolderRollbackResult(FolderRollbackStatus.NoBackup);

        var backupBytes = File.ReadAllBytes(backupFilePath);

        // Validate before trusting: never overwrite a possibly-valid live file with bytes that
        // don't parse as a supported organization.json.
        if (OrganizationJsonCodec.Parse(DecodeText(backupBytes)).Status != OrganizationJsonParseStatus.Ok)
            return new FolderRollbackResult(FolderRollbackStatus.InvalidBackup);

        AtomicWrite(organizationJsonPath, backupBytes);
        File.Delete(backupFilePath);
        return new FolderRollbackResult(FolderRollbackStatus.Restored);
    }

    // UTF8Encoding.GetString does not strip a byte-order mark the way File.ReadAllText does —
    // without this, a BOM'd file would fail parsing as MalformedJson. The raw bytes (BOM
    // included) are still what gets backed up and restored, so fidelity is unaffected.
    private static string DecodeText(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.GetString(bytes);

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~FolderCleanupExecutorTests"`
Expected: 14 tests PASS.

- [ ] **Step 6: Run the full suite and build**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 170 tests pass, 0 failures.
Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/FolderCleanupResult.cs PenumbraOrganizer.Plugin/Organizer/FolderCleanupExecutor.cs PenumbraOrganizer.Plugin.Tests/Organizer/FolderCleanupExecutorTests.cs
git commit -m "feat: add folder cleanup executor with backup-safe write ordering"
```

---

### Task 5: Plugin.cs wiring (paths, detection, cleanup, rollback)

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (add after the existing `ProtectAndSkipBlockingMods()` method, around line 221; path properties go next to the existing `BackupFilePath` at line ~133)

**Interfaces:**
- Consumes (Task 1): `OrganizationJsonCodec.Parse`, `OrganizationJsonParseStatus`. (Task 2): `OrganizationCleanupPlanner.GetVirtualParent/DetectOrphaned`. (Task 3): `OrganizerState.HasScanned`. (Task 4): `FolderCleanupExecutor.Execute/ExecuteRollback`, all result types. Existing: `GetModListAdapterIpc` (mod items expose `.FullPath`, same as `RunScan` uses), `PluginInterface.ConfigDirectory`, `Log`.
- Produces (consumed by Task 6): `internal FolderDetectionResult DetectOrphanedFolders()`; `internal FolderCleanupResult CleanUpFolders(IReadOnlySet<string> selectedPaths)`; `internal FolderRollbackResult RollbackFolderCleanup()`; `internal bool FolderBackupExists { get; }`.

**No unit tests** — matches the repo convention: `RunScan`/`ApplyChanges`/`RollbackLastApply`/`ExportReview` all touch live IPC or config-directory I/O and have none. All logic these wrappers delegate to is already tested in Tasks 1–4. Verified by build + in-game (Task 7).

- [ ] **Step 1: Add path derivation properties**

In `Plugin.cs`, next to the existing `BackupFilePath` property (line ~133):

```csharp
    // Penumbra's config dir is a sibling of this plugin's own under Dalamud's pluginConfigs
    // folder — no IPC exposes it (confirmed against the full Penumbra.Api 5.15.1 surface; see
    // the folder-cleanup design spec's Ground truth section).
    private static string PenumbraConfigDirectory =>
        Path.Combine(Directory.GetParent(PluginInterface.ConfigDirectory.FullName)!.FullName, "Penumbra");

    private static string OrganizationJsonPath =>
        Path.Combine(PenumbraConfigDirectory, "mod_filesystem", "organization.json");

    private string FolderBackupFilePath =>
        Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-folder-backup.json");

    internal bool FolderBackupExists => File.Exists(FolderBackupFilePath);
```

- [ ] **Step 2: Add the three wrapper methods**

After `ProtectAndSkipBlockingMods()`:

```csharp
    internal Organizer.FolderDetectionResult DetectOrphanedFolders()
    {
        // Before any scan, the occupied set would be empty and every real folder would look
        // orphaned — an active false positive. Distinct from "scanned, zero mods", which must
        // detect normally (an empty library is where everything may legitimately be orphaned).
        if (!OrganizerState.HasScanned)
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.NotScanned);

        if (!File.Exists(OrganizationJsonPath))
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.FileMissing);

        var parse = Organizer.OrganizationJsonCodec.Parse(File.ReadAllText(OrganizationJsonPath));
        if (parse.Status == Organizer.OrganizationJsonParseStatus.MalformedJson)
        {
            Log.Warning("organization.json is not valid JSON; folder cleanup unavailable.");
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.MalformedJson);
        }

        if (parse.Status == Organizer.OrganizationJsonParseStatus.UnsupportedVersion)
        {
            Log.Warning("organization.json has an unsupported Version; folder cleanup unavailable.");
            return new Organizer.FolderDetectionResult([], [], Organizer.FolderDetectionStatus.UnsupportedVersion);
        }

        // Advisory list: last-scan occupancy is acceptable here — the write path re-derives
        // occupancy from a fresh IPC read and is the enforcement point.
        var occupied = OccupiedFolders(OrganizerState.Mods.Select(m => m.CurrentPath));
        var (plain, customized) = Organizer.OrganizationCleanupPlanner.DetectOrphaned(parse.Data!, occupied);
        return new Organizer.FolderDetectionResult(plain, customized, Organizer.FolderDetectionStatus.Detected);
    }

    internal Organizer.FolderCleanupResult CleanUpFolders(IReadOnlySet<string> selectedPaths)
    {
        // Fresh IPC read at write time — OrganizerState is only as fresh as the last scan and
        // can't see mods moved via Penumbra's own UI since then. Deliberately NOT RunScan(),
        // which would reset every ProposedPath and wipe staged sort proposals. If this throws
        // (Penumbra unavailable), nothing has been written: a clean abort surfaced by the
        // caller's error handling.
        using var modList = GetModListAdapterIpc.Invoke();
        var occupied = OccupiedFolders(modList.Select(m => m.FullPath));

        return Organizer.FolderCleanupExecutor.Execute(
            OrganizationJsonPath, FolderBackupFilePath, selectedPaths, occupied);
    }

    internal Organizer.FolderRollbackResult RollbackFolderCleanup() =>
        Organizer.FolderCleanupExecutor.ExecuteRollback(OrganizationJsonPath, FolderBackupFilePath);

    private static HashSet<string> OccupiedFolders(IEnumerable<string> fullPaths) =>
        fullPaths
            .Select(Organizer.OrganizationCleanupPlanner.GetVirtualParent)
            .Where(parent => parent is not null)
            .Select(parent => parent!)
            .ToHashSet(StringComparer.Ordinal);
```

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 170 tests pass, 0 failures (no new tests this task).

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: wire folder cleanup detection/execute/rollback into Plugin"
```

---

### Task 6: MainWindow Orphaned Folders UI section

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (new fields at top of class ~line 22; new section at the end of `DrawReviewTab()` after the Rollback summary ~line 250; refresh calls in `RunScan()`/`ApplyChanges()`/`RollbackLastApply()` ~lines 253–290)

**Interfaces:**
- Consumes (Task 5): `_plugin.DetectOrphanedFolders()`, `_plugin.CleanUpFolders(IReadOnlySet<string>)`, `_plugin.RollbackFolderCleanup()`, `_plugin.FolderBackupExists`. (Tasks 2/4): `CustomizedFolder`, `FolderDetectionResult`, `FolderDetectionStatus`, `FolderCleanupResult`, `FolderCleanupStatus`, `FolderRollbackResult`, `FolderRollbackStatus`.
- Produces: UI only — nothing downstream consumes it.

**No unit tests** — ImGui draw code, same convention as every existing tab. Verified in-game (Task 7).

- [ ] **Step 1: Add the new state fields**

After the existing `_lastRollbackResults` field (line ~22):

```csharp
    private Organizer.FolderDetectionResult? _orphanedFolders;
    private readonly HashSet<string> _selectedOrphans = new(StringComparer.Ordinal);
    private bool _folderReloadRequired;
    private Organizer.FolderCleanupResult? _lastCleanupResult;
    private Organizer.FolderRollbackResult? _lastFolderRollbackResult;
```

- [ ] **Step 2: Add the refresh helper and wire the triggers**

Add alongside the existing private helpers (`RunScan`/`ApplyChanges`/`RollbackLastApply`, line ~253):

```csharp
    // Detection reads a file and parses JSON — never callable from a draw method directly
    // (DrawReviewTab runs every frame). Recomputed only on explicit triggers; selection resets
    // to defaults on every recompute: a refresh means the world changed, and a stale selection
    // surviving it is the failure mode the write-time re-verification exists to catch.
    private void RefreshOrphanedFolders()
    {
        try
        {
            _orphanedFolders = _plugin.DetectOrphanedFolders();
            _selectedOrphans.Clear();
            if (_orphanedFolders.Status == Organizer.FolderDetectionStatus.Detected)
                foreach (var path in _orphanedFolders.PlainEmpty)
                    _selectedOrphans.Add(path);
        }
        catch (Exception ex)
        {
            _lastError = $"Orphaned-folder detection failed: {ex.Message}";
        }
    }
```

Then modify the three existing helpers to refresh after their authoritative-state change (each gets one or two added lines; existing bodies otherwise unchanged):

```csharp
    private void RunScan()
    {
        try
        {
            _plugin.RunScan();
            _lastError = null;
            _folderReloadRequired = false; // the banner's instruction is "Rediscover Mods, then Scan here"
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to reach Penumbra IPC: {ex.Message}";
        }

        RefreshOrphanedFolders();
    }

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

        RefreshOrphanedFolders(); // ApplyChanges() ran RunScan() internally — occupancy changed
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

        RefreshOrphanedFolders(); // same: internal RunScan() means occupancy changed
    }
```

- [ ] **Step 3: Add the cleanup/rollback action helpers**

```csharp
    private void CleanUpSelectedFolders()
    {
        try
        {
            _lastCleanupResult = _plugin.CleanUpFolders(_selectedOrphans.ToHashSet(StringComparer.Ordinal));
            _lastError = null;
            if (_lastCleanupResult.Status is Organizer.FolderCleanupStatus.Success
                or Organizer.FolderCleanupStatus.SucceededBackupFailed)
                _folderReloadRequired = true;
        }
        catch (Exception ex)
        {
            _lastError = $"Folder cleanup failed: {ex.Message}";
        }

        RefreshOrphanedFolders();
    }

    private void RollbackFolderCleanup()
    {
        try
        {
            _lastFolderRollbackResult = _plugin.RollbackFolderCleanup();
            _lastError = null;
            if (_lastFolderRollbackResult.Status == Organizer.FolderRollbackStatus.Restored)
                _folderReloadRequired = true;
        }
        catch (Exception ex)
        {
            _lastError = $"Folder cleanup rollback failed: {ex.Message}";
        }

        RefreshOrphanedFolders();
    }
```

- [ ] **Step 4: Add the draw section**

At the end of `DrawReviewTab()`, after the mod-move Rollback summary block (line ~250), add a call to a new private method, then implement it:

```csharp
        ImGui.Spacing();
        ImGui.Separator();
        DrawOrphanedFoldersSection();
```

```csharp
    private void DrawOrphanedFoldersSection()
    {
        var detection = _orphanedFolders;
        if (detection is null || detection.Status == Organizer.FolderDetectionStatus.NotScanned)
            return; // nothing meaningful before the first scan

        if (detection.Status is Organizer.FolderDetectionStatus.UnsupportedVersion
            or Organizer.FolderDetectionStatus.MalformedJson)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "organization.json couldn't be read — folder cleanup unavailable "
                + (detection.Status == Organizer.FolderDetectionStatus.UnsupportedVersion
                    ? "(unsupported version)."
                    : "(unreadable file)."));
            return;
        }

        if (detection.Status == Organizer.FolderDetectionStatus.FileMissing)
            return; // ordinary state — Penumbra has never written the file on this install

        var total = detection.PlainEmpty.Count + detection.CustomizedEmpty.Count;

        if (_folderReloadRequired)
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "Waiting on Rediscover Mods — the list below reflects organization.json on disk, "
                + "not Penumbra's confirmed live state. Click Rediscover Mods in Penumbra, then Scan here, to re-check.");

        ImGui.TextUnformatted($"Orphaned Folders ({total} detected)");

        if (total > 0)
        {
            ImGui.TextUnformatted($"Empty, no customization ({detection.PlainEmpty.Count}) — pre-checked");
            foreach (var path in detection.PlainEmpty)
                DrawOrphanCheckbox(path, path);

            if (detection.CustomizedEmpty.Count > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    $"Empty but customized ({detection.CustomizedEmpty.Count}) — unchecked, review before pruning");
                foreach (var folder in detection.CustomizedEmpty)
                    DrawOrphanCheckbox(folder.Path, $"{folder.Path}  ({folder.Description})");
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(_selectedOrphans.Count == 0);
            var cleanClicked = ImGui.Button("Clean Up Selected Folders");
            ImGui.EndDisabled();
            if (cleanClicked)
                ImGui.OpenPopup("Clean up folders?");

            if (ImGui.BeginPopupModal("Clean up folders?"))
            {
                ImGui.TextUnformatted($"Remove {_selectedOrphans.Count} folder entries from Penumbra's organization.json?");
                foreach (var path in _selectedOrphans.OrderBy(p => p, StringComparer.Ordinal))
                    ImGui.TextUnformatted($"  {path}");
                if (ImGui.Button("Yes, Clean Up"))
                {
                    CleanUpSelectedFolders();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (_plugin.FolderBackupExists)
        {
            ImGui.SameLine();
            if (ImGui.Button("Rollback Folder Cleanup"))
                RollbackFolderCleanup();
        }

        DrawFolderActionResults();
    }

    private void DrawOrphanCheckbox(string path, string label)
    {
        var selected = _selectedOrphans.Contains(path);
        if (ImGui.Checkbox($"{label}##orphan-{path}", ref selected))
        {
            if (selected)
                _selectedOrphans.Add(path);
            else
                _selectedOrphans.Remove(path);
        }
    }

    private void DrawFolderActionResults()
    {
        if (_lastCleanupResult is not null)
        {
            var r = _lastCleanupResult;
            switch (r.Status)
            {
                case Organizer.FolderCleanupStatus.Success:
                    ImGui.TextUnformatted($"{r.Pruned.Count} folder entries removed from organization.json.");
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "Penumbra hasn't loaded this change yet — open Penumbra's Settings tab and click "
                        + "Rediscover Mods before making any other folder changes there.");
                    if (r.SkippedStale.Count > 0)
                        ImGui.TextUnformatted(
                            $"{r.SkippedStale.Count} selected folder(s) were no longer orphaned and were skipped.");
                    break;
                case Organizer.FolderCleanupStatus.SucceededBackupFailed:
                    ImGui.TextColored(ImGuiColors.DalamudRed,
                        $"{r.Pruned.Count} folder entries removed, but the rollback backup could not be saved. "
                        + "Rediscover Mods in Penumbra now, then avoid running another cleanup until you've "
                        + "confirmed the result — there is no safety net for this action right now.");
                    if (_plugin.FolderBackupExists)
                        ImGui.TextColored(ImGuiColors.DalamudRed,
                            "The Rollback button restores an OLDER backup that predates this cleanup — "
                            + "clicking it would undo more than just this action.");
                    break;
                case Organizer.FolderCleanupStatus.NothingStillValid:
                    ImGui.TextUnformatted(
                        "Nothing was cleaned up — the selected folder(s) are no longer orphaned (or no longer exist). "
                        + "No files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.NothingSelected:
                    ImGui.TextUnformatted("Nothing selected — no files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.FileMissing:
                    ImGui.TextUnformatted("organization.json does not exist on this install — nothing to clean up.");
                    break;
                case Organizer.FolderCleanupStatus.UnsupportedVersion:
                case Organizer.FolderCleanupStatus.MalformedJson:
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "organization.json couldn't be read — no files were changed.");
                    break;
            }
        }

        if (_lastFolderRollbackResult is not null)
        {
            switch (_lastFolderRollbackResult.Status)
            {
                case Organizer.FolderRollbackStatus.Restored:
                    ImGui.TextUnformatted("Backup restored to organization.json.");
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "Penumbra hasn't loaded this change yet — click Rediscover Mods.");
                    break;
                case Organizer.FolderRollbackStatus.InvalidBackup:
                    ImGui.TextColored(ImGuiColors.DalamudRed,
                        "The backup file is unreadable or unsupported — rollback aborted, organization.json was not touched.");
                    break;
                case Organizer.FolderRollbackStatus.NoBackup:
                    ImGui.TextUnformatted("No folder-cleanup backup exists.");
                    break;
            }
        }
    }
```

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 170 tests pass, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add Orphaned Folders section to the Review Changes tab"
```

---

### Task 7: Docs update + final verification gate

**Files:**
- Modify: `docs/ROADMAP.md` (add a status line under "Where we are" and a new phase section after the Phase 2 section)
- Modify: `docs/superpowers/specs/2026-07-15-plugin-organizer-folder-cleanup-design.md` (status line only: "approved, not yet implemented" → "implemented, pending in-game verification")

**Interfaces:**
- Consumes: nothing from code — documentation and process only.
- Produces: the in-game verification gate for whoever runs it.

- [ ] **Step 1: Update ROADMAP.md**

Add to the "Where we are" list:

```markdown
- **Folder Cleanup (organization.json orphaned-folder prune) — implemented, pending in-game
  verification.** The plugin's second write target (plain file I/O, not IPC — no IPC exposes
  folder-structure writes). Detects orphaned folder entries, prunes selected ones with a
  byte-fidelity rolling backup, separate from mod-move Apply/Rollback. Requires a manual
  "Rediscover Mods" click in Penumbra after every cleanup/rollback (no reload IPC exists).
```

Add a new section after the "## Phase 2 — Apply..." section, following the same format (link the spec at `docs/superpowers/specs/2026-07-15-plugin-organizer-folder-cleanup-design.md` and this plan at `docs/superpowers/plans/2026-07-15-plugin-organizer-folder-cleanup.md`, and name the key decisions: `CurrentPath`-plus-fresh-IPC occupancy, target-write-before-backup-promotion, `Folders`/`Separators` disjointness, the Rediscover Mods operating constraint).

- [ ] **Step 2: Update the spec's status line**

In the spec, change `**Status:** approved, not yet implemented.` to `**Status:** implemented, pending in-game verification.` (leave the rest of the status paragraph intact).

- [ ] **Step 3: Full suite + build one final time**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 170 tests pass, 0 failures.
Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add docs/ROADMAP.md docs/superpowers/specs/2026-07-15-plugin-organizer-folder-cleanup-design.md
git commit -m "docs: mark Folder Cleanup implemented, pending in-game verification"
```

- [ ] **Step 5: In-game verification (manual, user-driven — NOT automatable)**

The feature is not done until the spec's in-game checklist passes (spec, "Testing" section, items 1–9). Summarized:

1. Detect real pre-existing orphans on a real library.
2. Sort (don't Apply) — confirm currently-occupied source folders do **not** appear in the orphan list.
3. Clean up a plain-empty folder — confirm the Success message demands Rediscover Mods, the folder survives in Penumbra's UI until Rediscover Mods is clicked, and disappears after.
4. Attempt a customized-empty folder — confirm the extra-friction UI.
5. Roll back, Rediscover Mods — confirm the folder returns with customization intact.
6. Confirm mod placements unaffected throughout (re-scan and diff).
7. Select an orphan, move a mod into it via **Penumbra's own UI** (no re-scan), click Clean Up — confirm `NothingStillValid`, both files untouched. (A sort cannot stage this — occupancy no longer reads `ProposedPath`.)
8. Confirm the `_folderReloadRequired` banner appears after cleanup, persists, and clears only via Scan.
9. If reachable: a 0-mod library still detects orphans (the `HasScanned` guard, not `Mods.Count`).

Also during this step (spec requirement): hexdump the first bytes of the real install's `organization.json` to confirm UTF-8-without-BOM before trusting the writer's encoding choice — if a BOM is present, change `FolderCleanupExecutor.Encoding` to match and re-verify.

---

## Self-review notes (plan vs. spec)

- Every spec section maps to a task: schema/model/codec (T1), planner + `GetVirtualParent` + two-tier classification incl. `ExtensionData` (T2), `HasScanned` (T3), executor sequencing incl. `originalBytes` retention, target-first ordering, `SucceededBackupFailed`, no-op guard, rollback validation (T4), path derivation + fresh-IPC occupancy + detection statuses + `Log.Warning` (T5), UI incl. banner, status-keyed messages, selection reset, refresh triggers, popup (T6), docs + in-game gate incl. encoding verification (T7).
- Type names/signatures are identical across tasks (`CustomizedFolder`, `OrganizationJsonParseResult`, `FolderCleanupExecutor.Execute/ExecuteRollback`, `OccupiedFolders` helper local to Plugin.cs).
- Test counts assume 131 passing at start; if the baseline differs when execution begins, adjust the expected totals (the deltas per task are what matter: +7, +16 planner (5 GetVirtualParent + 7 DetectOrphaned + 4 Prune), +2, +14 executor).
- Known judgment call an implementer must not "fix": detection uses last-scan occupancy on purpose (advisory list); only the write path pays for a fresh IPC read. Do not add an IPC call to `DetectOrphanedFolders`.
