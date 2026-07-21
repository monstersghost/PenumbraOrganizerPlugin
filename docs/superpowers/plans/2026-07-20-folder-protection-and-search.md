# Folder-Level Protection and Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add folder-level protection (protecting a folder protects every mod under it, recursively, and re-applies on every Scan) and a search bar to the Protect tab, plus a checkbox-based multi-select rework of the Sort tab's manual assign, without breaking the existing individual/Heliosphere protection model.

**Architecture:** Three explicit, non-conflated protection sources (`ProtectedModIdentifiers`, `ProtectedFolders`, live `HeliosphereManaged`) feed one derived `OrganizerModRow.Protected` boolean via two recompute formulas — a full one used at Scan time and whenever the folder rule set changes, and an individual-toggle one that deliberately preserves Heliosphere's existing transient-override behavior while making folder protection immediately/synchronously correct. `Plugin.Restore` gets a targeted fix so it also respects folder protection, since it reads persisted config directly rather than scanning.

**Tech Stack:** C#/.NET (Dalamud.NET.Sdk 15.0.0), xUnit for tests, ImGui via `Dalamud.Bindings.ImGui` for UI.

## Global Constraints

- `row.Protected` is always derived, never itself a persisted source of truth. `SaveProtectionState()` must persist the explicit `ProtectedModIdentifiers`/`ProtectedFolders` sets, never derive them by reading `row.Protected` back out.
- Folder protection is recursive: protecting `Gear/Feet` also protects everything under `Gear/Feet/...`. Matching is boundary-safe — never a bare `StartsWith` (`Body` must not match `BodyMods/Author`).
- Comparer for folder identity and matching: `StringComparison.Ordinal` (matches `OrganizationCleanupPlanner.IsOccupied`'s existing adjacent convention in the same file, not `OrdinalIgnoreCase`).
- Matching always uses `row.CurrentPath`'s virtual parent, never `ProposedPath`.
- Individual checkbox toggles (`SetProtected`, `SetAllProtection`) deliberately do **not** immediately re-assert `HeliosphereManaged`-derived protection (preserves existing, previously-confirmed "unprotect until next Scan" behavior for Heliosphere) but **do** immediately re-assert folder-derived protection (the safety fix this plan exists for).
- `Plugin.Restore` must account for folder protection at its own call site — `RollbackHistory.BuildRestorePlan`'s signature and existing tests are unchanged.
- `LoadScan`'s new third parameter must default to `null` (treated as empty) so the ~40 existing unrelated test call sites across this test project keep compiling unchanged.
- Manual-assign selection is a `HashSet<string>`, persists across search-text changes, and is reconciled against currently-eligible mods before every render and before every Assign.

---

### Task 1: Folder-path normalization and boundary-safe protection matching

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs`

**Interfaces:**
- Produces: `OrganizationCleanupPlanner.NormalizeFolderPath(string? path) : string?`; `OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(string currentPath, IReadOnlySet<string> protectedFolders) : bool`. Both `public static`, consumed by Task 2 (`OrganizerState`) and Task 4 (`Plugin.Restore`).

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs`, inside the existing `OrganizationCleanupPlannerTests` class (the file already has an `Occupied(params string[])` helper building an `IReadOnlySet<string>` — reused here as-is for building protected-folder sets, despite the name mismatch, since it's just a plain `HashSet<string>` builder):

```csharp
    // --- NormalizeFolderPath ---

    [Fact]
    public void NormalizeFolderPath_TrimsLeadingAndTrailingSlashes()
        => Assert.Equal("Gear/Feet", OrganizationCleanupPlanner.NormalizeFolderPath("/Gear/Feet/"));

    [Fact]
    public void NormalizeFolderPath_ConvertsBackslashesToForwardSlashes()
        => Assert.Equal("Gear/Feet", OrganizationCleanupPlanner.NormalizeFolderPath(@"Gear\Feet"));

    [Fact]
    public void NormalizeFolderPath_CollapsesRepeatedSeparators()
        => Assert.Equal("Gear/Feet", OrganizationCleanupPlanner.NormalizeFolderPath("Gear//Feet"));

    [Fact]
    public void NormalizeFolderPath_WhitespaceOnly_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.NormalizeFolderPath("   "));

    [Fact]
    public void NormalizeFolderPath_EmptyString_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.NormalizeFolderPath(""));

    [Fact]
    public void NormalizeFolderPath_Null_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.NormalizeFolderPath(null));

    // --- IsUnderAnyProtectedFolder ---

    [Fact]
    public void IsUnderAnyProtectedFolder_ExactFolderMatch_ReturnsTrue()
        => Assert.True(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Gear/Feet/Boots", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_NestedUnderProtectedFolder_ReturnsTrue()
        => Assert.True(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Gear/Feet/Sub/Boots", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_UnrelatedFolder_ReturnsFalse()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Face/Boots", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_BareStartsWithWouldFalseMatch_ButDoesNot()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "BodyMods/Author/Mod", Occupied("Body")));

    [Fact]
    public void IsUnderAnyProtectedFolder_SiblingWithSharedPrefix_DoesNotMatch()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Gear/FeetExtra/Mod", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_RootLevelMod_ReturnsFalse()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "ModAtRoot", Occupied("Gear")));
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizationCleanupPlannerTests`
Expected: FAIL (compile error — `NormalizeFolderPath`/`IsUnderAnyProtectedFolder` don't exist yet)

- [ ] **Step 3: Write the implementation**

Add to `PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs`, inside the `OrganizationCleanupPlanner` class, after `GetVirtualParent`:

```csharp
    // Applied to a folder path before storing it as protected, and before comparing a live
    // scanned path's virtual parent against stored protected folders, so both sides of every
    // comparison go through the same normalization. The only way a path enters protected-folder
    // storage today is via a checkbox next to an already-GetVirtualParent-derived value (never
    // free-typed), which narrows the practical need for this — kept anyway to defend a
    // persisted path from a prior session against a live one that could differ in incidental
    // formatting.
    public static string? NormalizeFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var withForwardSlashes = path.Replace('\\', '/');
        var collapsed = System.Text.RegularExpressions.Regex.Replace(withForwardSlashes, "/+", "/");
        var trimmed = collapsed.Trim('/', ' ');
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Recursive, boundary-safe: a mod under "Gear/Feet/Sub" is protected by "Gear/Feet". Never a
    // bare StartsWith — mirrors IsOccupied's exact rule below ("Body" must not match
    // "BodyMods/Author").
    public static bool IsUnderAnyProtectedFolder(string currentPath, IReadOnlySet<string> protectedFolders)
    {
        var parent = GetVirtualParent(currentPath);
        if (parent is null)
            return false;
        return protectedFolders.Any(folder =>
            parent.Equals(folder, StringComparison.Ordinal) ||
            parent.StartsWith(folder + "/", StringComparison.Ordinal));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizationCleanupPlannerTests`
Expected: PASS (all tests in this file, including the new ones)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs
git commit -m "feat: add folder-path normalization and boundary-safe protection matching"
```

---

### Task 2: Three-source protection model in OrganizerState

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Consumes: `OrganizationCleanupPlanner.NormalizeFolderPath`, `OrganizationCleanupPlanner.IsUnderAnyProtectedFolder`, `OrganizationCleanupPlanner.GetVirtualParent` (Task 1, all `public static`, existing).
- Produces: `Configuration.ProtectedFolderPaths : HashSet<string>`; `OrganizerState.ProtectedModIdentifiers : IReadOnlyCollection<string>`; `OrganizerState.ProtectedFolders : IReadOnlyCollection<string>`; `OrganizerState.KnownFolders : IReadOnlyList<string>`; `OrganizerState.SetFolderProtected(string folderPath, bool value) : void`; extended `OrganizerState.LoadScan(IEnumerable<OrganizerModRow> scanned, IReadOnlySet<string> previouslyProtectedIdentifiers, IReadOnlySet<string>? previouslyProtectedFolders = null) : void`. Consumed by Task 3 (same class), Task 4 (`Plugin.cs`), Task 5/6 (`MainWindow.cs`).

This is the most safety-critical task in the plan — it fixes the confirmed bug where `SaveProtectionState` (Task 4) would otherwise permanently persist folder-derived protection as individual protection, and the confirmed bug where unprotecting one folder could incorrectly disable protection from an unrelated source.

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`, inside the existing `OrganizerStateTests` class (it already has a `MakeRow(id, name, heliosphere)` helper — used below, with `.CurrentPath` overwritten per-test where a specific folder path is needed):

```csharp
    [Fact]
    public void SetFolderProtected_ProtectsCurrentlyScannedModsUnderFolder()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_ProtectsNestedSubfolderMods()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Sub/Boots";
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_UnprotectingAncestor_LeavesDescendantFolderProtectionIntact()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear", true);
        state.SetFolderProtected("Gear/Feet", true);
        state.SetFolderProtected("Gear", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_Unprotecting_DoesNotDisableIndividualProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";
        state.LoadScan([row], new HashSet<string>());

        state.SetProtected("a", true);
        state.SetFolderProtected("Gear/Feet", true);
        state.SetFolderProtected("Gear/Feet", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_Unprotecting_DoesNotDisableHeliosphereProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", heliosphere: true);
        row.CurrentPath = "Gear/Feet/Boots";
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);
        state.SetFolderProtected("Gear/Feet", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void FolderOnlyProtectedMod_NeverEntersProtectedModIdentifiers()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);

        Assert.DoesNotContain("a", state.ProtectedModIdentifiers);
    }

    [Fact]
    public void SetProtected_OnHeliosphereMod_PreservesTransientOverrideUntilNextScan()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple", heliosphere: true)], new HashSet<string>());
        Assert.True(state.Mods.Single().Protected);

        state.SetProtected("a", false);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetProtected_OnFolderProtectedMod_RecomputesImmediatelyBackToProtected()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";
        state.LoadScan([row], new HashSet<string>());
        state.SetFolderProtected("Gear/Feet", true);
        Assert.True(state.Mods.Single().Protected);

        state.SetProtected("a", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetAllProtection_False_DoesNotDisableFolderProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";
        state.LoadScan([row], new HashSet<string>());
        state.SetFolderProtected("Gear/Feet", true);

        state.SetAllProtection(false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void KnownFolders_DerivesDistinctParentsFromScannedMods()
    {
        var state = new OrganizerState();
        var a = MakeRow("a", "Boots");
        a.CurrentPath = "Gear/Feet/Boots";
        var b = MakeRow("b", "Hat");
        b.CurrentPath = "Gear/Feet/Hat";
        var c = MakeRow("c", "Root");
        c.CurrentPath = "RootMod";
        state.LoadScan([a, b, c], new HashSet<string>());

        Assert.Equal(["Gear/Feet"], state.KnownFolders);
    }

    [Fact]
    public void LoadScan_WithPersistedProtectedFolder_AppliesFolderProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";

        state.LoadScan([row], new HashSet<string>(), new HashSet<string> { "Gear/Feet" });

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_WithoutThirdArgument_StillCompilesAndAppliesNoFolderProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots");
        row.CurrentPath = "Gear/Feet/Boots";

        state.LoadScan([row], new HashSet<string>());

        Assert.False(state.Mods.Single().Protected);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizerStateTests`
Expected: FAIL (compile error — `SetFolderProtected`, `ProtectedModIdentifiers`, `KnownFolders` don't exist; `LoadScan`'s third-argument overload doesn't exist)

- [ ] **Step 3: Write the implementation**

In `PenumbraOrganizer.Plugin/Configuration.cs`, add alongside the existing property:

```csharp
    public HashSet<string> ProtectedFolderPaths { get; set; } = [];
```

Replace the full contents of `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` from the top through `AssignManual` (everything from `SortByCreator` onward — `Sort`, `TypeFolder`, `KnownFolder`, `KnownSegment`, `BuildPath`, `FinishProposals`, `Validate`, and the `ReviewResult` record at the bottom — is unchanged, do not touch it) with:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerState
{
    private readonly Dictionary<string, OrganizerModRow> _mods = new();
    private readonly HashSet<string> _protectedModIdentifiers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _protectedFolders = new(StringComparer.Ordinal);

    public IReadOnlyList<OrganizerModRow> Mods =>
        _mods.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool HasScanned { get; private set; }

    public IReadOnlyCollection<string> ProtectedModIdentifiers => _protectedModIdentifiers;

    public IReadOnlyCollection<string> ProtectedFolders => _protectedFolders;

    // Distinct virtual-parent folders among currently scanned mods — deliberately does NOT
    // include persisted-but-currently-empty ProtectedFolders entries; callers that need the
    // union (the Protect tab's folder list) build it themselves from both properties.
    public IReadOnlyList<string> KnownFolders =>
        _mods.Values
            .Select(m => OrganizationCleanupPlanner.GetVirtualParent(m.CurrentPath))
            .Where(f => f is not null)
            .Select(f => f!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    // previouslyProtectedFolders defaults to null (treated as empty) so every existing call
    // site across this test project that predates folder protection keeps compiling unchanged.
    public void LoadScan(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlySet<string>? previouslyProtectedFolders = null)
    {
        HasScanned = true;
        _mods.Clear();
        _protectedModIdentifiers.Clear();
        _protectedModIdentifiers.UnionWith(previouslyProtectedIdentifiers);
        _protectedFolders.Clear();
        _protectedFolders.UnionWith(previouslyProtectedFolders ?? new HashSet<string>(StringComparer.Ordinal));

        foreach (var row in scanned)
        {
            row.Protected = IsEffectivelyProtectedFull(row);
            row.ProposedPath = row.CurrentPath;
            _mods[row.Identifier] = row;
        }
    }

    public void SetProtected(string identifier, bool value)
    {
        if (value)
            _protectedModIdentifiers.Add(identifier);
        else
            _protectedModIdentifiers.Remove(identifier);

        if (_mods.TryGetValue(identifier, out var row))
            row.Protected = IsEffectivelyProtectedAfterIndividualToggle(row);
    }

    // Deliberately unchanged from the pre-folder-protection behavior: directly toggles
    // HeliosphereManaged rows, re-asserted on the next Scan. This existing, previously-confirmed
    // transient-override behavior is not touched by folder protection.
    public void SetHeliosphereProtection(bool value)
    {
        foreach (var row in _mods.Values.Where(m => m.HeliosphereManaged))
            row.Protected = value;
    }

    public void SetAllProtection(bool value)
    {
        if (value)
            _protectedModIdentifiers.UnionWith(_mods.Keys);
        else
            _protectedModIdentifiers.Clear();

        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedAfterIndividualToggle(row);
    }

    // A folder-rule change is a system/bulk event, not a single-mod interactive toggle, so it
    // always runs the full recompute (including HeliosphereManaged) over every row - not just
    // rows under this folder, because unprotecting one folder must correctly leave a still-
    // protected descendant or ancestor folder's rows alone.
    public void SetFolderProtected(string folderPath, bool value)
    {
        var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folderPath);
        if (normalized is null)
            return;

        if (value)
            _protectedFolders.Add(normalized);
        else
            _protectedFolders.Remove(normalized);

        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedFull(row);
    }

    private bool IsEffectivelyProtectedFull(OrganizerModRow row) =>
        row.HeliosphereManaged
        || _protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(row.CurrentPath, _protectedFolders);

    // Deliberately excludes HeliosphereManaged - see SetProtected/SetAllProtection's doc comment
    // context above and the plan's Global Constraints for why.
    private bool IsEffectivelyProtectedAfterIndividualToggle(OrganizerModRow row) =>
        _protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(row.CurrentPath, _protectedFolders);

    public bool AssignManual(string identifier, string proposedPath)
    {
        if (!_mods.TryGetValue(identifier, out var row) || row.Protected)
            return false;

        row.ProposedPath = proposedPath;
        return true;
    }
```

(Everything from `public int SortByCreator(...)` through the end of the file, including the closing brace of the class and the `ReviewResult` record, stays exactly as it is today — this replacement only covers the span from the top of the file through `AssignManual`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizerStateTests`
Expected: PASS (all tests in this file, including the new ones — this includes every pre-existing test in the file too, since none of their call sites changed)

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test`
Expected: All tests PASS (the ~40 other `LoadScan` call sites across `WorkbookAdapterInventoryTests.cs`, `WorkbookInteropTests.cs`, `WorkbookAdapterApplyImportResultTests.cs` all still compile unchanged, since the new parameter defaults to `null`)

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Configuration.cs PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add three-source protection model (individual/folder/Heliosphere) to OrganizerState"
```

---

### Task 3: Batch manual assign

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Consumes: `AssignManual` (existing, unchanged, same file).
- Produces: `OrganizerState.AssignManualBatch(IReadOnlySet<string> identifiers, string folder) : IReadOnlyList<(string Identifier, bool Success)>`. Consumed by Task 6 (`MainWindow.cs`).

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`:

```csharp
    [Fact]
    public void AssignManualBatch_BlankFolder_ReportsAllFailedWithoutMutating()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var results = state.AssignManualBatch(new HashSet<string> { "a" }, "   ");

        Assert.All(results, r => Assert.False(r.Success));
        Assert.Equal("Unsorted/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void AssignManualBatch_AssignsEveryIdentifierUnderSameFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana")], new HashSet<string>());

        var results = state.AssignManualBatch(new HashSet<string> { "a", "b" }, "MyFolder");

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal("MyFolder/Apple", state.Mods.Single(m => m.Identifier == "a").ProposedPath);
        Assert.Equal("MyFolder/Banana", state.Mods.Single(m => m.Identifier == "b").ProposedPath);
    }

    [Fact]
    public void AssignManualBatch_UnknownIdentifier_ReportsFailedWithoutAffectingOthers()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var results = state.AssignManualBatch(new HashSet<string> { "a", "missing" }, "MyFolder");

        Assert.True(results.Single(r => r.Identifier == "a").Success);
        Assert.False(results.Single(r => r.Identifier == "missing").Success);
    }

    [Fact]
    public void AssignManualBatch_ProtectedIdentifier_ReportsFailed()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        var results = state.AssignManualBatch(new HashSet<string> { "a" }, "MyFolder");

        Assert.False(results.Single().Success);
    }

    [Fact]
    public void AssignManualBatch_TrimsSlashesFromFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        state.AssignManualBatch(new HashSet<string> { "a" }, "/MyFolder/");

        Assert.Equal("MyFolder/Apple", state.Mods.Single().ProposedPath);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizerStateTests`
Expected: FAIL (compile error — `AssignManualBatch` doesn't exist yet)

- [ ] **Step 3: Write the implementation**

Add to `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, directly after `AssignManual`:

```csharp
    // Each item's only failure modes (unknown identifier, protected row) are independent of
    // every other item - assigning mod A never affects whether mod B can succeed - so this
    // reports per-item results rather than pre-validating the whole batch before mutating
    // anything. Same-name collisions among the batch are caught afterward by the existing
    // Validate() on the Review Changes tab, exactly as a single manual assign can already
    // produce one today - not a new mechanism.
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~OrganizerStateTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add AssignManualBatch for multi-select manual assign"
```

---

### Task 4: Wire folder protection into Plugin.cs, fix Restore's protection check

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `Config.ProtectedFolderPaths` (Task 2), `OrganizerState.ProtectedModIdentifiers`/`.ProtectedFolders` (Task 2), `OrganizationCleanupPlanner.IsUnderAnyProtectedFolder` (Task 1).
- Produces: corrected `RunScan()`, corrected `SaveProtectionState()`, corrected `Restore()`. No new public members.

This task has no isolated unit tests of its own — `Plugin.cs` requires live Dalamud/Penumbra services and is not unit-tested anywhere in this codebase (established convention throughout this project). Verification is build + full existing test suite staying green.

- [ ] **Step 1: Fix `RunScan()` to pass protected folders into `LoadScan`**

In `PenumbraOrganizer.Plugin/Plugin.cs`, change:

```csharp
        OrganizerState.LoadScan(rows, Config.ProtectedModIdentifiers);
        SaveProtectionState();
    }
```

to:

```csharp
        OrganizerState.LoadScan(rows, Config.ProtectedModIdentifiers, Config.ProtectedFolderPaths);
        SaveProtectionState();
    }
```

- [ ] **Step 2: Fix `SaveProtectionState()` to persist the explicit sets, not the derived boolean**

Replace:

```csharp
    internal void SaveProtectionState()
    {
        Config.ProtectedModIdentifiers = OrganizerState.Mods
            .Where(m => m.Protected)
            .Select(m => m.Identifier)
            .ToHashSet();
        PluginInterface.SavePluginConfig(Config);
    }
```

with:

```csharp
    internal void SaveProtectionState()
    {
        Config.ProtectedModIdentifiers = OrganizerState.ProtectedModIdentifiers.ToHashSet();
        Config.ProtectedFolderPaths = OrganizerState.ProtectedFolders.ToHashSet();
        PluginInterface.SavePluginConfig(Config);
    }
```

This is the fix for the confirmed bug: the old version derived persisted identifiers from the *effective* `row.Protected` boolean, which would have silently turned folder-derived (or Heliosphere-derived) protection into permanent individual protection the moment it was saved. The corrected version persists only the explicit sets `OrganizerState` itself tracks.

- [ ] **Step 3: Fix `Restore()` to account for folder protection**

In `Restore(Guid snapshotId)`, replace this line:

```csharp
            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods, Config.ProtectedModIdentifiers);
```

with:

```csharp
            // Restore doesn't scan (OrganizerState/row.Protected may be stale or empty), so it
            // can't rely on the derived boolean - it must independently combine explicit
            // individual protection with folder protection here. BuildRestorePlan already ORs
            // in mod.HeliosphereManaged internally, so that term isn't duplicated.
            var lockedIdentifiers = Config.ProtectedModIdentifiers
                .Union(currentMods
                    .Where(m => Organizer.OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(m.FullPath, Config.ProtectedFolderPaths))
                    .Select(m => m.Identifier))
                .ToHashSet(StringComparer.Ordinal);
            var plan = Organizer.RollbackHistory.BuildRestorePlan(target, currentMods, lockedIdentifiers);
```

This is the fix for the confirmed bug: `Restore` previously read `Config.ProtectedModIdentifiers` directly, bypassing folder protection entirely. `RollbackHistory.BuildRestorePlan`'s signature and existing tests (from the multi-save-point rollback plan) are untouched — only this call site changes.

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS (no regressions).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "fix: persist explicit protection sets and account for folder protection in Restore"
```

---

### Task 5: Protect tab — search bar and folder list

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `OrganizerState.KnownFolders`, `.ProtectedFolders`, `.ProtectedModIdentifiers`, `.SetFolderProtected` (Task 2); `Plugin.SaveProtectionState()`, `Plugin.Log` (existing).

No isolated unit tests — `MainWindow.cs` is ImGui rendering code with no existing test coverage in this codebase. Verification is build + full test suite.

- [ ] **Step 1: Add new state fields**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add near the other private fields (after `private string? _lastError;`):

```csharp
    private string _protectFilter = string.Empty;
```

- [ ] **Step 2: Add a shared error-surfacing wrapper around SaveProtectionState**

Add a new private method, placed near `RunScan()`/`ApplyChanges()` (the existing wrapper methods further down the file):

```csharp
    private void SaveProtectionStateSafely()
    {
        try
        {
            _plugin.SaveProtectionState();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to save protection settings: {ex.Message}";
            Plugin.Log.Error(ex, "Failed to save protection settings.");
        }
    }
```

- [ ] **Step 3: Rewrite DrawProtectTab**

Replace the full body of `DrawProtectTab()`:

```csharp
    private void DrawProtectTab()
    {
        using var tab = ImRaii.TabItem("Protect");
        if (!tab)
            return;

        if (ImGui.Button("Toggle protect all"))
        {
            var allProtected = _plugin.OrganizerState.Mods.All(m => m.Protected);
            _plugin.OrganizerState.SetAllProtection(!allProtected);
            SaveProtectionStateSafely();
        }

        ImGui.SameLine();
        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            SaveProtectionStateSafely();
        }

        ImGui.Spacing();
        ImGui.InputText("Search mods and folders", ref _protectFilter, 256);
        ImGui.Spacing();

        var filter = _protectFilter.Trim();
        var protectedFolders = _plugin.OrganizerState.ProtectedFolders.ToHashSet(StringComparer.Ordinal);
        var knownFolders = _plugin.OrganizerState.KnownFolders.ToHashSet(StringComparer.Ordinal);
        var folderRows = knownFolders
            .Union(protectedFolders, StringComparer.Ordinal)
            .Where(f => filter.Length == 0 || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        ImGui.TextUnformatted("Folders");
        using (var folderChild = ImRaii.Child("ProtectedFolderList", new Vector2(0, 150), border: true))
        {
            if (folderChild)
            {
                foreach (var folder in folderRows)
                {
                    var isExactlyProtected = protectedFolders.Contains(folder);
                    var label = knownFolders.Contains(folder) ? folder : $"{folder} (currently empty)";
                    var isChecked = isExactlyProtected;
                    if (ImGui.Checkbox($"{label}##protect-folder-{folder}", ref isChecked))
                    {
                        _plugin.OrganizerState.SetFolderProtected(folder, isChecked);
                        SaveProtectionStateSafely();
                    }

                    if (!isExactlyProtected)
                    {
                        var ancestor = protectedFolders.FirstOrDefault(f =>
                            !f.Equals(folder, StringComparison.Ordinal)
                            && folder.StartsWith(f + "/", StringComparison.Ordinal));
                        if (ancestor is not null)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled($"(covered by protected folder \"{ancestor}\")");
                        }
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Mods");
        var explicitIdentifiers = _plugin.OrganizerState.ProtectedModIdentifiers.ToHashSet(StringComparer.Ordinal);
        foreach (var mod in _plugin.OrganizerState.Mods)
        {
            if (filter.Length > 0
                && !mod.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !mod.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !mod.Author.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !mod.CurrentPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var isProtected = mod.Protected;
            if (ImGui.Checkbox($"{mod.Name}##protect-{mod.Identifier}", ref isProtected))
            {
                _plugin.OrganizerState.SetProtected(mod.Identifier, isProtected);
                SaveProtectionStateSafely();
            }

            if (mod.Protected && !explicitIdentifiers.Contains(mod.Identifier))
            {
                ImGui.SameLine();
                if (mod.HeliosphereManaged)
                {
                    ImGui.TextDisabled("(Heliosphere)");
                }
                else
                {
                    var parent = Organizer.OrganizationCleanupPlanner.GetVirtualParent(mod.CurrentPath);
                    var coveringFolder = parent is null
                        ? null
                        : protectedFolders.FirstOrDefault(f =>
                            parent.Equals(f, StringComparison.Ordinal) || parent.StartsWith(f + "/", StringComparison.Ordinal));
                    ImGui.TextDisabled(coveringFolder is not null ? $"(via folder: {coveringFolder})" : "(protected)");
                }
            }
        }
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add search bar and folder-level protection UI to Protect tab"
```

---

### Task 6: Sort tab manual assign — checkboxes, search, batch assign

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `OrganizerState.AssignManualBatch` (Task 3), `OrganizerState.Mods` (existing).

No isolated unit tests — same reasoning as Task 5. Verification is build + full test suite.

- [ ] **Step 1: Replace the single-selection field with a multi-selection set and add supporting fields**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, remove:

```csharp
    private string? _selectedManualModIdentifier;
```

Add in its place:

```csharp
    private readonly HashSet<string> _selectedManualModIdentifiers = new(StringComparer.Ordinal);
    private string _manualAssignFilter = string.Empty;
    private string? _lastManualAssignSummary;
```

- [ ] **Step 2: Rewrite the manual-assign section of DrawSortTab**

Replace this block (the manual-assign section at the end of `DrawSortTab()`):

```csharp
        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: pick a mod below, type a folder, click Assign.");

        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        ImGui.SameLine();
        if (ImGui.Button("Assign") && _selectedManualModIdentifier is not null && _manualFolderInput.Length > 0)
        {
            var mod = _plugin.OrganizerState.Mods.FirstOrDefault(m => m.Identifier == _selectedManualModIdentifier);
            if (mod is not null)
                _plugin.OrganizerState.AssignManual(_selectedManualModIdentifier, $"{_manualFolderInput}/{mod.Name}");
        }

        ImGui.Spacing();
        using (var child = ImRaii.Child("ManualModList", new Vector2(0, 300), border: true))
        {
            if (child)
                foreach (var mod in _plugin.OrganizerState.Mods.Where(m => !m.Protected))
                {
                    if (ImGui.RadioButton(mod.Name, _selectedManualModIdentifier == mod.Identifier))
                        _selectedManualModIdentifier = mod.Identifier;
                }
        }
    }
```

with:

```csharp
        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: check mods below, type a folder, click Assign.");

        ImGui.InputText("Search mods##manual-assign", ref _manualAssignFilter, 256);
        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        ImGui.SameLine();
        if (ImGui.Button($"Assign {_selectedManualModIdentifiers.Count} selected mods")
            && _manualFolderInput.Length > 0 && _selectedManualModIdentifiers.Count > 0)
        {
            var batchResults = _plugin.OrganizerState.AssignManualBatch(_selectedManualModIdentifiers, _manualFolderInput);
            var succeeded = batchResults.Count(r => r.Success);
            _lastManualAssignSummary = $"{succeeded} assigned, {batchResults.Count - succeeded} skipped (no longer eligible)";
        }

        if (_lastManualAssignSummary is not null)
            ImGui.TextUnformatted(_lastManualAssignSummary);

        // Reconcile before rendering: drop any selected identifier that is no longer present or
        // has since become protected (by any source, including a folder rule toggled on the
        // Protect tab), so stale checkmarks never display and Assign never targets them.
        var eligibleIdentifiers = _plugin.OrganizerState.Mods
            .Where(m => !m.Protected)
            .Select(m => m.Identifier)
            .ToHashSet(StringComparer.Ordinal);
        _selectedManualModIdentifiers.IntersectWith(eligibleIdentifiers);

        ImGui.Spacing();
        var manualFilter = _manualAssignFilter.Trim();
        using (var child = ImRaii.Child("ManualModList", new Vector2(0, 300), border: true))
        {
            if (child)
            {
                foreach (var mod in _plugin.OrganizerState.Mods.Where(m => !m.Protected))
                {
                    if (manualFilter.Length > 0
                        && !mod.Name.Contains(manualFilter, StringComparison.OrdinalIgnoreCase)
                        && !mod.Identifier.Contains(manualFilter, StringComparison.OrdinalIgnoreCase)
                        && !mod.CurrentPath.Contains(manualFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isSelected = _selectedManualModIdentifiers.Contains(mod.Identifier);
                    if (ImGui.Checkbox($"{mod.Name} ({mod.CurrentPath})##manual-{mod.Identifier}", ref isSelected))
                    {
                        if (isSelected)
                            _selectedManualModIdentifiers.Add(mod.Identifier);
                        else
                            _selectedManualModIdentifiers.Remove(mod.Identifier);
                    }
                }
            }
        }
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`
Expected: Build succeeds with no errors.

Run: `dotnet test`
Expected: All existing tests PASS.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: rework Sort tab manual assign to checkbox multi-select with search"
```

---

## Self-Review

**Spec coverage:** every section of the design doc has a task — folder identity/normalization/matching (Task 1), the three-source model with corrected mutation semantics and the Heliosphere-vs-folder recompute asymmetry (Task 2), batch manual assign (Task 3), the downstream audit fixes for `SaveProtectionState`/`Restore` (Task 4), Protect tab search + folder list + exact-vs-inherited checkbox semantics + stale-folder visibility (Task 5), Sort tab manual-assign search + selection persistence/reconciliation + count label (Task 6). Folder Cleanup's audit (no code change needed, reasoning demonstrated) required no task, matching the design doc.

**Placeholder scan:** no TBD/TODO; every step shows complete code.

**Type consistency:** `LoadScan`'s new third parameter (`IReadOnlySet<string>? previouslyProtectedFolders = null`) is used identically in Task 2's implementation and Task 4's `RunScan()` call site. `AssignManualBatch`'s return type (`IReadOnlyList<(string Identifier, bool Success)>`) matches between Task 3's implementation and Task 6's consumption (`.Count(r => r.Success)`). `OrganizationCleanupPlanner.IsUnderAnyProtectedFolder`'s signature (`string currentPath, IReadOnlySet<string> protectedFolders`) matches across Task 1 (definition), Task 2 (`OrganizerState`'s two recompute methods), and Task 4 (`Plugin.Restore`'s `lockedIdentifiers` construction, called with `m.FullPath` — `LiveMod.FullPath`, matching the existing `LiveMod` record from the multi-save-point rollback work).
