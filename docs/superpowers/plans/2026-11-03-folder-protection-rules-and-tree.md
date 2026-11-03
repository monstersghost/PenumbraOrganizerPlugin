# Folder Protection Rules and a Tree-Shaped Protect Tab — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a folder carry an *exclude* rule so "protect `haloween`, except `haloween/animation`" is expressible, and replace the Protect tab's flat folder list with a collapsible tree of tri-state checkboxes.

**Architecture:** `OrganizerState`'s protected-folder `HashSet<string>` becomes a rule map (`Dictionary<string, bool>`: `true` = protect, `false` = exclude). A new longest-prefix resolver in `OrganizationCleanupPlanner` replaces the `Any`-prefix match, so the deepest rule on a mod's parent chain decides. An exclude cancels only the folder layer of `IsEffectivelyProtected`; Heliosphere and per-mod ticks are untouched. The Protect tab draws folders through a new pure tree-builder plus an ImGui `TreeNodeEx` renderer.

**Tech Stack:** C# / .NET 9, Dalamud plugin, `Dalamud.Bindings.ImGui`, xUnit.

## Global Constraints

- Scope is the Protect tab only. Do not change the Scan or Review mod tables (`PathTreeView`).
- Protection layer order in `IsEffectivelyProtected` must stay: Heliosphere (minus override) → explicit identifier tick → resolved folder rule. An exclude cancels **only** the folder clause.
- Heliosphere override semantics are unchanged: `_heliosphereUnprotectOverrides` is never persisted and is cleared by every `ReplaceScanAtomically`.
- Path comparisons for folders use `StringComparer.Ordinal` throughout, and folder containment stays boundary-safe (`"Body"` must never match `"BodyMods/Author"`).
- All folder paths entering state go through `OrganizationCleanupPlanner.NormalizeFolderPath`.
- Every new `HelpTopic` constant must have an entry in `PenumbraOrganizer.Plugin/Resources/help-content.json` with a `title` and at least one of `short`/`body`/`step`, or `HelpContentTests` fails.
- Run tests with: `dotnet test PenumbraOrganizer.Plugin.sln`
- Commit after every task. Do not bump the plugin version or touch `repo.json` — releasing is a separate, explicitly authorised step.

---

### Task 1: Longest-prefix folder rule resolver

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/FolderProtectionRule.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs` (add after `IsUnderAnyProtectedFolder`, around line 49)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs`

**Interfaces:**
- Consumes: existing `OrganizationCleanupPlanner.GetVirtualParent(string) -> string?`.
- Produces: `enum FolderProtectionRule { None, Protected, Excluded }` in namespace `PenumbraOrganizer.Plugin.Organizer`; `OrganizationCleanupPlanner.ResolveFolderProtection(string currentPath, IReadOnlyDictionary<string, bool> folderRules) -> FolderProtectionRule`.

`IsUnderAnyProtectedFolder` stays exactly as it is. It is still used as a pure *containment* check (is this mod inside this one folder), and its existing tests must keep passing.

- [ ] **Step 1: Write the failing tests**

Append to `OrganizationCleanupPlannerTests.cs`:

```csharp
    // --- ResolveFolderProtection: deepest rule on the parent chain wins ---

    private static Dictionary<string, bool> Rules(params (string Folder, bool Protected)[] rules)
        => rules.ToDictionary(r => r.Folder, r => r.Protected, StringComparer.Ordinal);

    [Fact]
    public void ResolveFolderProtection_NoRules_ReturnsNone()
        => Assert.Equal(FolderProtectionRule.None, OrganizationCleanupPlanner.ResolveFolderProtection(
            "Gear/Feet/Boots", Rules()));

    [Fact]
    public void ResolveFolderProtection_RuleOnImmediateParent_Applies()
        => Assert.Equal(FolderProtectionRule.Protected, OrganizationCleanupPlanner.ResolveFolderProtection(
            "Gear/Feet/Boots", Rules(("Gear/Feet", true))));

    [Fact]
    public void ResolveFolderProtection_RuleOnAncestor_InheritsDownward()
        => Assert.Equal(FolderProtectionRule.Protected, OrganizationCleanupPlanner.ResolveFolderProtection(
            "Gear/Feet/Sub/Boots", Rules(("Gear", true))));

    [Fact]
    public void ResolveFolderProtection_ExcludeUnderProtect_DeepestWins()
        => Assert.Equal(FolderProtectionRule.Excluded, OrganizationCleanupPlanner.ResolveFolderProtection(
            "haloween/animation/Dance", Rules(("haloween", true), ("haloween/animation", false))));

    [Fact]
    public void ResolveFolderProtection_ProtectUnderExclude_DeepestWins()
        => Assert.Equal(FolderProtectionRule.Protected, OrganizationCleanupPlanner.ResolveFolderProtection(
            "haloween/animation/keep/Dance",
            Rules(("haloween", true), ("haloween/animation", false), ("haloween/animation/keep", true))));

    [Fact]
    public void ResolveFolderProtection_ExcludeInheritsToDeeperDescendants()
        => Assert.Equal(FolderProtectionRule.Excluded, OrganizationCleanupPlanner.ResolveFolderProtection(
            "haloween/animation/sub/deeper/Dance",
            Rules(("haloween", true), ("haloween/animation", false))));

    [Fact]
    public void ResolveFolderProtection_RootLevelMod_ReturnsNone()
        => Assert.Equal(FolderProtectionRule.None, OrganizationCleanupPlanner.ResolveFolderProtection(
            "LooseMod", Rules(("LooseMod", true))));

    [Fact]
    public void ResolveFolderProtection_SiblingWithSharedPrefix_DoesNotMatch()
        => Assert.Equal(FolderProtectionRule.None, OrganizationCleanupPlanner.ResolveFolderProtection(
            "BodyMods/Author/Mod", Rules(("Body", true))));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~ResolveFolderProtection"`
Expected: build failure — `FolderProtectionRule` and `ResolveFolderProtection` do not exist.

- [ ] **Step 3: Add the enum**

Create `PenumbraOrganizer.Plugin/Organizer/FolderProtectionRule.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// What a folder rule says, as resolved for a given path: the deepest rule on the parent chain
/// wins, so a folder can carve an exception out of a protected ancestor.
/// </summary>
public enum FolderProtectionRule
{
    /// <summary>No rule anywhere on the chain. The folder layer does not protect this path.</summary>
    None,

    /// <summary>The nearest rule protects this path.</summary>
    Protected,

    /// <summary>
    /// The nearest rule is an exception: the folder layer does not protect this path, even though
    /// an ancestor is protected. Per-mod ticks and Heliosphere management still do.
    /// </summary>
    Excluded,
}
```

- [ ] **Step 4: Add the resolver**

In `OrganizationCleanupPlanner.cs`, directly after `IsUnderAnyProtectedFolder`:

```csharp
    /// <summary>
    /// The folder layer's verdict for <paramref name="currentPath"/>: the rule on the NEAREST
    /// ruled ancestor of the mod's virtual parent, or <see cref="FolderProtectionRule.None"/> when
    /// no ancestor carries one.
    /// </summary>
    /// <remarks>
    /// This replaces an any-match over a set of protected folders. The set could only ever say
    /// "protected", so protecting "haloween" made every descendant protected with no way to carve
    /// out "haloween/animation" - which is how it was reported. Walking upward from the immediate
    /// parent and returning the first rule found gives deepest-rule-wins for free, and keeps the
    /// boundary-safe parent chain (GetVirtualParent), never a bare StartsWith.
    /// </remarks>
    public static FolderProtectionRule ResolveFolderProtection(
        string currentPath, IReadOnlyDictionary<string, bool> folderRules)
    {
        for (var folder = GetVirtualParent(currentPath); folder is not null; folder = GetVirtualParent(folder))
        {
            if (folderRules.TryGetValue(folder, out var isProtected))
                return isProtected ? FolderProtectionRule.Protected : FolderProtectionRule.Excluded;
        }

        return FolderProtectionRule.None;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~OrganizationCleanupPlannerTests"`
Expected: PASS, including the pre-existing `IsUnderAnyProtectedFolder_*` tests.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/FolderProtectionRule.cs PenumbraOrganizer.Plugin/Organizer/OrganizationCleanupPlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizationCleanupPlannerTests.cs
git commit -m "Resolve folder protection by nearest rule, not any match

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: OrganizerState holds rules instead of a protected-folder set

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (fields ~13, `ProtectedFolders` ~46, `LoadScan`/`ReplaceScanAtomically` ~68-122, `SetFolderProtected` ~185, `IsEffectivelyProtected*` ~316-342)
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs:68`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:406`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Consumes: `FolderProtectionRule`, `OrganizationCleanupPlanner.ResolveFolderProtection` (Task 1).
- Produces on `OrganizerState`:
  - `IReadOnlyDictionary<string, bool> FolderProtectionRules { get; }`
  - `IReadOnlyCollection<string> ProtectedFolders { get; }` — unchanged shape, now the keys whose value is `true`.
  - `void SetFolderProtectionRule(string folderPath, FolderProtectionRule rule)`
  - `void SetFolderProtected(string folderPath, bool value)` — kept, delegates to the above.
  - `readonly record struct FolderRuleResolution(FolderProtectionRule Rule, string? Source)`
  - `FolderRuleResolution ResolveFolderRule(string folderPath)` — for a FOLDER node (the folder's own rule counts), `Source` being the folder the rule came from.
  - `void LoadScan(IEnumerable<OrganizerModRow>, IReadOnlySet<string>, IReadOnlyDictionary<string, bool>? = null)` and the same third-parameter change on `ReplaceScanAtomically`. No test passes that third argument today, so no test call site changes.

- [ ] **Step 1: Write the failing tests**

Append to `OrganizerStateTests.cs`:

```csharp
    // --- Folder exclude rules: "protect haloween, except haloween/animation" ---

    [Fact]
    public void SetFolderProtectionRule_ExcludeUnderProtectedAncestor_FreesOnlyThatSubtree()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("root", "Mask", currentPath: "haloween/Mask"),
             MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance"),
             MakeRow("deep", "Wave", currentPath: "haloween/animation/sub/Wave")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.True(state.Mods.Single(m => m.Identifier == "root").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "anim").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "deep").Protected);
    }

    [Fact]
    public void SetFolderProtectionRule_ProtectNestedUnderAnExclude_ReprotectsThatBranch()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance"),
             MakeRow("keep", "Keep", currentPath: "haloween/animation/keep/Keep")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);
        state.SetFolderProtectionRule("haloween/animation/keep", FolderProtectionRule.Protected);

        Assert.False(state.Mods.Single(m => m.Identifier == "anim").Protected);
        Assert.True(state.Mods.Single(m => m.Identifier == "keep").Protected);
    }

    [Fact]
    public void SetFolderProtectionRule_None_WithdrawsTheRuleAndInheritsAgain()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.None);

        Assert.True(state.Mods.Single().Protected);
        Assert.DoesNotContain("haloween/animation", state.FolderProtectionRules.Keys);
    }

    [Fact]
    public void SetFolderProtectionRule_Exclude_DoesNotFreeAHandTickedMod()
    {
        // An exclude cancels the FOLDER layer only. A tick is a per-mod choice the user made.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetProtected("anim", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtectionRule_Exclude_DoesNotFreeAHeliosphereMod()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("anim", "Dance", heliosphere: true, currentPath: "haloween/animation/Dance")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void ProtectedFolders_ReportsOnlyProtectRules_NotExcludes()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.Contains("haloween", state.ProtectedFolders);
        Assert.DoesNotContain("haloween/animation", state.ProtectedFolders);
    }

    [Fact]
    public void ResolveFolderRule_ReportsOwnRuleAndItsSource()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);

        Assert.Equal(
            new OrganizerState.FolderRuleResolution(FolderProtectionRule.Protected, "haloween"),
            state.ResolveFolderRule("haloween"));
        // Inherited: the rule applies, and the source names the ancestor it came from.
        Assert.Equal(
            new OrganizerState.FolderRuleResolution(FolderProtectionRule.Protected, "haloween"),
            state.ResolveFolderRule("haloween/animation"));
        Assert.Equal(
            new OrganizerState.FolderRuleResolution(FolderProtectionRule.None, null),
            state.ResolveFolderRule("Gear"));
    }

    [Fact]
    public void ReplaceScanAtomically_CarriesExcludeRulesThroughARescan()
    {
        var state = new OrganizerState();
        var rules = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["haloween"] = true,
            ["haloween/animation"] = false,
        };

        state.LoadScan(
            [MakeRow("root", "Mask", currentPath: "haloween/Mask"),
             MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")],
            new HashSet<string>(),
            rules);

        Assert.True(state.Mods.Single(m => m.Identifier == "root").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "anim").Protected);
        Assert.False(state.FolderProtectionRules["haloween/animation"]);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: build failure — `SetFolderProtectionRule`, `FolderProtectionRules`, `ResolveFolderRule` do not exist.

- [ ] **Step 3: Replace the field and the derived properties**

In `OrganizerState.cs`, replace the field declaration:

```csharp
    private HashSet<string> _protectedFolders = new(StringComparer.Ordinal);
```

with:

```csharp
    // Folder path -> true (protect) / false (exclude). An exclude is what makes "protect haloween,
    // except haloween/animation" expressible: resolution takes the DEEPEST rule on a path, so a
    // rule here is a local override of whatever an ancestor said, not an addition to it.
    private Dictionary<string, bool> _folderProtectionRules = new(StringComparer.Ordinal);
```

Replace the `ProtectedFolders` property:

```csharp
    public IReadOnlyCollection<string> ProtectedFolders => _protectedFolders;
```

with:

```csharp
    /// <summary>Every folder carrying a rule, mapped to true (protect) or false (exclude).</summary>
    public IReadOnlyDictionary<string, bool> FolderProtectionRules => _folderProtectionRules;

    /// <summary>
    /// Folders whose own rule protects. Kept for callers that only ever meant "which folders are
    /// ticked" - notably the config writer - and deliberately excludes exclude-rule folders.
    /// </summary>
    public IReadOnlyCollection<string> ProtectedFolders =>
        _folderProtectionRules.Where(pair => pair.Value).Select(pair => pair.Key).ToList();
```

- [ ] **Step 4: Change the scan entry points to carry rules**

Replace the `LoadScan` signature and body:

```csharp
    // previouslyProtectedFolderRules defaults to null (treated as empty) so every existing call
    // site across this test project that predates folder protection keeps compiling unchanged.
    public void LoadScan(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlyDictionary<string, bool>? previouslyProtectedFolderRules = null) =>
        ReplaceScanAtomically(scanned, previouslyProtectedIdentifiers, previouslyProtectedFolderRules);
```

In `ReplaceScanAtomically`, change the third parameter the same way and replace the folder-set replacement line:

```csharp
    public void ReplaceScanAtomically(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlyDictionary<string, bool>? previouslyProtectedFolderRules = null)
    {
        var replacementProtectedIdentifiers = new HashSet<string>(previouslyProtectedIdentifiers, StringComparer.Ordinal);
        var replacementFolderRules = previouslyProtectedFolderRules is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(previouslyProtectedFolderRules, StringComparer.Ordinal);
```

Then in the same method, pass `replacementFolderRules` where `replacementProtectedFolders` was passed to `IsEffectivelyProtected`, and at the COMMIT block replace:

```csharp
        _protectedFolders = replacementProtectedFolders;
```

with:

```csharp
        _folderProtectionRules = replacementFolderRules;
```

- [ ] **Step 5: Replace the folder setter with the tri-state one**

Replace `SetFolderProtected`'s body (keeping its doc comment above) with:

```csharp
    public void SetFolderProtected(string folderPath, bool value) =>
        SetFolderProtectionRule(folderPath, value ? FolderProtectionRule.Protected : FolderProtectionRule.None);

    /// <summary>
    /// Writes (or withdraws) this folder's own rule. <see cref="FolderProtectionRule.None"/>
    /// removes the rule, so the folder inherits from its nearest ruled ancestor again.
    /// </summary>
    public void SetFolderProtectionRule(string folderPath, FolderProtectionRule rule)
    {
        var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folderPath);
        if (normalized is null)
            return;

        if (rule == FolderProtectionRule.None)
            _folderProtectionRules.Remove(normalized);
        else
            _folderProtectionRules[normalized] = rule == FolderProtectionRule.Protected;

        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedFull(row);
    }

    /// <summary>A folder's effective rule and the folder that supplied it.</summary>
    /// <param name="Source">
    /// The folder whose rule decided this - the folder itself when it carries its own rule, an
    /// ancestor when inherited, null when no rule applies. The Protect tab shows an inherited
    /// state dimmed and names this in the tooltip.
    /// </param>
    public readonly record struct FolderRuleResolution(FolderProtectionRule Rule, string? Source);

    /// <summary>
    /// Resolves the rule for a FOLDER node, where the folder's own rule counts - unlike the mod
    /// path resolver, which starts at the mod's virtual parent.
    /// </summary>
    public FolderRuleResolution ResolveFolderRule(string folderPath)
    {
        var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folderPath);
        for (var folder = normalized; folder is not null; folder = OrganizationCleanupPlanner.GetVirtualParent(folder))
        {
            if (_folderProtectionRules.TryGetValue(folder, out var isProtected))
                return new FolderRuleResolution(
                    isProtected ? FolderProtectionRule.Protected : FolderProtectionRule.Excluded, folder);
        }

        return new FolderRuleResolution(FolderProtectionRule.None, null);
    }
```

- [ ] **Step 6: Point the protection rule at the resolver**

Replace `IsEffectivelyProtectedFull` and the folder clause of `IsEffectivelyProtected`:

```csharp
    private bool IsEffectivelyProtectedFull(OrganizerModRow row) =>
        IsEffectivelyProtected(row, _protectedModIdentifiers, _folderProtectionRules, _heliosphereUnprotectOverrides);
```

and, in `IsEffectivelyProtected`, change the parameter type and the final clause:

```csharp
    private static bool IsEffectivelyProtected(
        OrganizerModRow row,
        IReadOnlySet<string> protectedModIdentifiers,
        IReadOnlyDictionary<string, bool> folderRules,
        IReadOnlySet<string> heliosphereUnprotectOverrides) =>
        (row.HeliosphereManaged && !heliosphereUnprotectOverrides.Contains(row.Identifier))
        || protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.ResolveFolderProtection(row.CurrentPath, folderRules)
            == FolderProtectionRule.Protected;
```

Leave the existing remarks block above it intact, and add to it:

```csharp
    /// <para>
    /// The folder clause is the only one an exclude rule can cancel. A mod inside an excluded
    /// folder that is Heliosphere-managed or ticked by hand stays protected, because those are
    /// per-mod instructions the folder tree has no business reversing.
    /// </para>
```

- [ ] **Step 7: Fix the two production call sites**

`PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs:68` — replace:

```csharp
            results, _plugin.Config.ProtectedModIdentifiers, _plugin.Config.ProtectedFolderPaths);
```

with:

```csharp
            results, _plugin.Config.ProtectedModIdentifiers, _plugin.Config.FolderProtectionRules);
```

`PenumbraOrganizer.Plugin/Plugin.cs:406` — replace:

```csharp
        Config.ProtectedFolderPaths = OrganizerState.ProtectedFolders.ToHashSet();
```

with:

```csharp
        Config.FolderProtectionRules = OrganizerState.FolderProtectionRules
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
```

Both reference `Configuration.FolderProtectionRules`, which Task 4 adds. To keep this task building on its own, add the property to `Configuration.cs` now, exactly as Task 4 specifies it:

```csharp
    /// <summary>Folder path -> true (protect) / false (exclude). Replaces ProtectedFolderPaths.</summary>
    public Dictionary<string, bool> FolderProtectionRules { get; set; } = [];
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.sln`
Expected: PASS. The pre-existing protection tests (`SetFolderProtected`, `UnprotectFolderContents`, `ProtectedFolders` assertions) must pass unchanged; if any fail, the rule map's behaviour has diverged from the set's for the protect-only case and that is a bug in this task, not a test to update.

- [ ] **Step 9: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Configuration.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "Let a folder exclude itself from a protected ancestor

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Counts and the contents-unprotect action under the new rules

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (`FolderProtectionCounts` ~210, `UnprotectFolderContents` ~233, `BuildProtectedModCountsByFolder` ~286)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Consumes: Task 2's `_folderProtectionRules`, `ResolveFolderProtection`.
- Produces: `readonly record struct FolderProtectionCounts(int Protected, int Freeable, int Total)` — `Total` is new and last, so existing two-argument readers are unaffected (no test constructs this type). `BuildProtectedModCountsByFolder()` now returns an entry for every folder containing **any** mod, not only folders containing a protected one; the tree needs `Total` for folders with nothing protected.

- [ ] **Step 1: Write the failing tests**

Append to `OrganizerStateTests.cs`:

```csharp
    [Fact]
    public void BuildProtectedModCountsByFolder_ExcludedFolder_CountsOnlyTheStragglersAsFreeable()
    {
        // The exclude already freed the folder-protected rows. What is left is a hand-ticked mod,
        // which only "Unprotect N inside" can clear - so the tab must still offer it, for 1.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("plain", "Dance", currentPath: "haloween/animation/Dance"),
             MakeRow("ticked", "Wave", currentPath: "haloween/animation/Wave")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetProtected("ticked", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(1, counts["haloween/animation"].Protected);
        Assert.Equal(1, counts["haloween/animation"].Freeable);
        Assert.Equal(2, counts["haloween/animation"].Total);
    }

    [Fact]
    public void BuildProtectedModCountsByFolder_ReportsTotalsForFoldersWithNothingProtected()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Dance", currentPath: "hair/01/Dance"),
             MakeRow("b", "Wave", currentPath: "hair/01/Wave")],
            new HashSet<string>());

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(2, counts["hair"].Total);
        Assert.Equal(2, counts["hair/01"].Total);
        Assert.Equal(0, counts["hair"].Protected);
    }

    [Fact]
    public void UnprotectFolderContents_InsideAnExcludedBranch_ClearsTicksAndLeavesTheAncestorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("root", "Mask", currentPath: "haloween/Mask"),
             MakeRow("ticked", "Wave", currentPath: "haloween/animation/Wave")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetProtected("ticked", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.Equal(1, state.UnprotectFolderContents("haloween/animation"));
        Assert.False(state.Mods.Single(m => m.Identifier == "ticked").Protected);
        // The ancestor's rule is untouched, so its own mods stay protected.
        Assert.True(state.Mods.Single(m => m.Identifier == "root").Protected);
    }

    [Fact]
    public void UnprotectFolderContents_WithdrawsAnExcludeRuleToo_SoTheFolderInheritsAgain()
    {
        // The action's contract is "this folder's own rule goes". For an exclude that means the
        // branch inherits the protected ancestor again - and with ticks cleared, that is what the
        // user sees: a folder back to plain inherited protection.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);
        state.UnprotectFolderContents("haloween/animation");

        Assert.DoesNotContain("haloween/animation", state.FolderProtectionRules.Keys);
        Assert.True(state.Mods.Single().Protected);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: FAIL — `Total` is not a member of `FolderProtectionCounts`, and the counts dictionary has no entry for folders with nothing protected.

- [ ] **Step 3: Extend the counts record**

Replace the `FolderProtectionCounts` declaration (keeping its doc comment, and adding the `Total` line to it):

```csharp
    /// <param name="Total">Every mod inside the folder at any depth, protected or not.</param>
    public readonly record struct FolderProtectionCounts(int Protected, int Freeable, int Total);
```

- [ ] **Step 4: Rebuild the counts pass**

Replace the body of `BuildProtectedModCountsByFolder` with:

```csharp
    public Dictionary<string, FolderProtectionCounts> BuildProtectedModCountsByFolder()
    {
        var counts = new Dictionary<string, FolderProtectionCounts>(StringComparer.Ordinal);
        foreach (var row in _mods.Values)
        {
            var ancestors = new List<string>();
            for (var folder = OrganizationCleanupPlanner.GetVirtualParent(row.CurrentPath);
                 folder is not null;
                 folder = OrganizationCleanupPlanner.GetVirtualParent(folder))
                ancestors.Add(folder);

            // Nearest-first, so the first ruled ancestor is the one deciding this row today and
            // the next one is what the row would fall back to if that rule were withdrawn. This
            // is why no dictionary copy is needed per folder: the only rule whose removal can
            // change the outcome is the deepest one.
            var deepestRuledIndex = ancestors.FindIndex(f => _folderProtectionRules.ContainsKey(f));
            var fallbackRuled = deepestRuledIndex < 0
                ? null
                : ancestors.Skip(deepestRuledIndex + 1).FirstOrDefault(f => _folderProtectionRules.ContainsKey(f));
            var protectedByDeepest = deepestRuledIndex >= 0 && _folderProtectionRules[ancestors[deepestRuledIndex]];
            var protectedByFallback = fallbackRuled is not null && _folderProtectionRules[fallbackRuled];

            for (var i = 0; i < ancestors.Count; i++)
            {
                var folder = ancestors[i];
                var current = counts.GetValueOrDefault(folder);
                if (!row.Protected)
                {
                    counts[folder] = current with { Total = current.Total + 1 };
                    continue;
                }

                // Withdrawing this folder's rule frees the row unless something else still
                // protects it: the fallback rule when this folder is the deepest ruled one, or
                // the deepest rule itself when it is some other folder's.
                var freeable = i == deepestRuledIndex ? !protectedByFallback : !protectedByDeepest;
                counts[folder] = new FolderProtectionCounts(
                    current.Protected + 1, current.Freeable + (freeable ? 1 : 0), current.Total + 1);
            }
        }

        return counts;
    }
```

Update the method's existing `<summary>` to say what it now returns:

```csharp
    /// <summary>
    /// Mod counts keyed by folder: every folder holding at least one mod at any depth, mapped to
    /// how many it holds, how many of those are protected, and how many its own unprotect action
    /// could free. Folders holding no mods are absent.
    /// </summary>
```

- [ ] **Step 5: Point the contents action at the resolver**

In `UnprotectFolderContents`, replace the rule-withdrawal line and the skip check:

```csharp
        // Removed first, so the check below reads the rules this action leaves behind: what
        // remains there is exactly the protection it is not withdrawing.
        _folderProtectionRules.Remove(normalized);
        foreach (var row in contents)
        {
            if (OrganizationCleanupPlanner.ResolveFolderProtection(row.CurrentPath, _folderProtectionRules)
                == FolderProtectionRule.Protected)
                continue;
```

The `contents` query above it keeps using `IsUnderAnyProtectedFolder` with the single-folder `scope` set — that call is a containment test, not a protection test, and must not change.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.sln`
Expected: PASS, including the pre-existing `BuildProtectedModCountsByFolder_*` tests that assert `Freeable` is 0 when two protected folders cover a row.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "Count freeable mods and clear folder contents under nearest-rule semantics

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Config v2 — persist rules and tree expansion, migrate v1

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:69` (config load)
- Test: `PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs`

**Interfaces:**
- Produces: `Configuration.CurrentVersion` (const int = 2), `Configuration.FolderProtectionRules` (added in Task 2 step 7), `Configuration.ExpandedProtectFolders` (`HashSet<string>`), `Configuration.MigrateToCurrentVersion()`.
- `ProtectedFolderPaths` stays declared so a v1 file still deserialises into it; after migration it is empty and nothing writes it again.

- [ ] **Step 1: Write the failing tests**

In `ConfigurationTests.cs`, update the existing version assertion and add migration tests. Replace:

```csharp
        Assert.Equal(1, config.Version);
```

with:

```csharp
        Assert.Equal(Configuration.CurrentVersion, config.Version);
        Assert.Equal(2, Configuration.CurrentVersion);
```

Then append:

```csharp
    [Fact]
    public void MigrateToCurrentVersion_V1ProtectedFolders_BecomeProtectRules()
    {
        // An upgrading user must not have to re-tick anything: every v1 folder was a protect.
        const string legacyJson =
            """{"Version":1,"ProtectedModIdentifiers":["a"],"ProtectedFolderPaths":["Gear","haloween"]}""";

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(legacyJson)!;
        config.MigrateToCurrentVersion();

        Assert.Equal(Configuration.CurrentVersion, config.Version);
        Assert.True(config.FolderProtectionRules["Gear"]);
        Assert.True(config.FolderProtectionRules["haloween"]);
        Assert.Empty(config.ProtectedFolderPaths);
    }

    [Fact]
    public void MigrateToCurrentVersion_IsIdempotent_AndDoesNotRevivePostMigrationEdits()
    {
        var config = new Configuration { FolderProtectionRules = { ["haloween/animation"] = false } };

        config.MigrateToCurrentVersion();
        config.MigrateToCurrentVersion();

        Assert.False(config.FolderProtectionRules["haloween/animation"]);
        Assert.Single(config.FolderProtectionRules);
    }

    [Fact]
    public void FolderProtectionRules_RoundTripThroughJson_IncludingExcludes()
    {
        var config = new Configuration
        {
            FolderProtectionRules = { ["haloween"] = true, ["haloween/animation"] = false },
            ExpandedProtectFolders = { "haloween" },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.True(roundTripped.FolderProtectionRules["haloween"]);
        Assert.False(roundTripped.FolderProtectionRules["haloween/animation"]);
        Assert.Contains("haloween", roundTripped.ExpandedProtectFolders);
    }

    [Fact]
    public void DefaultConfiguration_HasNoExpandedProtectFolders()
        => Assert.Empty(new Configuration().ExpandedProtectFolders);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~ConfigurationTests"`
Expected: build failure — `CurrentVersion`, `ExpandedProtectFolders`, `MigrateToCurrentVersion` do not exist.

- [ ] **Step 3: Add the version, the fields and the migration**

In `Configuration.cs`, replace the `Version` and `ProtectedFolderPaths` declarations with:

```csharp
    /// <summary>
    /// 2: folder protection is a rule map (protect/exclude) rather than a set of protected
    /// folders. <see cref="MigrateToCurrentVersion"/> carries a v1 file across.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Pre-v2 protected folders. Read only by <see cref="MigrateToCurrentVersion"/>, which empties
    /// it; nothing writes it again. Still declared so a v1 file on disk deserialises into it.
    /// </summary>
    public HashSet<string> ProtectedFolderPaths { get; set; } = [];

    /// <summary>Folders whose node is open in the Protect tab's tree.</summary>
    /// <remarks>
    /// Persisted because the tree defaults to collapsed, and re-digging to the same hair subfolder
    /// every session is the complaint the tree exists to answer. Holds paths that no longer exist
    /// harmlessly: a path with no node is simply never consulted.
    /// </remarks>
    public HashSet<string> ExpandedProtectFolders { get; set; } = [];
```

Keep the `FolderProtectionRules` property added in Task 2 step 7, and add the migration method at the end of the class (before the `ScrapedNpcListFeatureEnabled` const):

```csharp
    /// <summary>
    /// Brings a config loaded from disk up to <see cref="CurrentVersion"/>. Idempotent, and safe to
    /// call on a fresh config (which is already current).
    /// </summary>
    /// <remarks>
    /// v1 -> v2: every entry of <see cref="ProtectedFolderPaths"/> becomes a protect rule, which is
    /// exactly what it meant, so behaviour after upgrade is identical and nothing needs re-ticking.
    /// TryAdd, not an assignment: if a rule for that folder already exists it was written by this
    /// version and is newer than the legacy field.
    /// </remarks>
    public void MigrateToCurrentVersion()
    {
        if (Version >= CurrentVersion)
            return;

        foreach (var folder in ProtectedFolderPaths)
            FolderProtectionRules.TryAdd(folder, true);

        ProtectedFolderPaths.Clear();
        Version = CurrentVersion;
    }
```

- [ ] **Step 4: Migrate on load**

In `Plugin.cs`, replace line 69:

```csharp
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
```

with:

```csharp
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Before anything reads the folder rules: a v1 file carries its protected folders in the
        // legacy field, and a scan that ran first would see no rules at all and unprotect them.
        Config.MigrateToCurrentVersion();
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.sln`
Expected: PASS. The legacy-JSON tests in `ConfigurationTests` still deserialise a `"Version":1` document; they assert nothing about `Version`, so they are unaffected.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Configuration.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs
git commit -m "Persist folder rules and tree expansion as config v2

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Pure folder-tree builder

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/FolderProtectionTree.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/FolderProtectionTreeTests.cs`

**Interfaces:**
- Produces, in namespace `PenumbraOrganizer.Plugin.Windows`:
  - `sealed record FolderTreeNode(string Path, string Name, IReadOnlyList<FolderTreeNode> Children)` with `int DescendantFolderCount` (computed property).
  - `static IReadOnlyList<FolderTreeNode> FolderProtectionTree.Build(IEnumerable<string> folderPaths)`
  - `static bool FolderProtectionTree.MatchesFilter(FolderTreeNode node, string filter)` — true when the node's own path or any descendant's path contains `filter` (ordinal, case-insensitive). Empty filter is true.

This file holds only the pure helpers in this task; Task 6 adds the ImGui `Draw` to it.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Windows/FolderProtectionTreeTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class FolderProtectionTreeTests
{
    [Fact]
    public void Build_NestsChildrenUnderTheirParent()
    {
        var roots = FolderProtectionTree.Build(["haloween", "haloween/animation"]);

        var haloween = Assert.Single(roots);
        Assert.Equal("haloween", haloween.Path);
        Assert.Equal("haloween", haloween.Name);
        var animation = Assert.Single(haloween.Children);
        Assert.Equal("haloween/animation", animation.Path);
        Assert.Equal("animation", animation.Name);
    }

    [Fact]
    public void Build_FillsInIntermediateSegmentsThatWereNotListed()
    {
        // "hair" may be absent from the input while "hair/01" is present; a tree with a hole in it
        // would leave the subfolder unreachable.
        var roots = FolderProtectionTree.Build(["hair/01/Long"]);

        var hair = Assert.Single(roots);
        Assert.Equal("hair", hair.Path);
        var one = Assert.Single(hair.Children);
        Assert.Equal("hair/01", one.Path);
        Assert.Equal("hair/01/Long", Assert.Single(one.Children).Path);
    }

    [Fact]
    public void Build_DeduplicatesRepeatedPaths()
    {
        var roots = FolderProtectionTree.Build(["Gear", "Gear", "Gear/Feet", "Gear/Feet"]);

        var gear = Assert.Single(roots);
        Assert.Single(gear.Children);
    }

    [Fact]
    public void Build_SortsSiblingsOrdinally()
    {
        var roots = FolderProtectionTree.Build(["hair/10", "hair/02", "hair/01"]);

        Assert.Equal(["hair/01", "hair/02", "hair/10"], Assert.Single(roots).Children.Select(c => c.Path));
    }

    [Fact]
    public void Build_IgnoresBlankAndRootOnlyEntries()
    {
        Assert.Empty(FolderProtectionTree.Build(["", "   ", "/"]));
    }

    [Fact]
    public void DescendantFolderCount_CountsEveryFolderBelow()
    {
        var roots = FolderProtectionTree.Build(["hair/01", "hair/02/Long", "hair/02/Short"]);

        var hair = Assert.Single(roots);
        Assert.Equal(4, hair.DescendantFolderCount);
        Assert.Equal(0, hair.Children.Single(c => c.Path == "hair/01").DescendantFolderCount);
    }

    [Fact]
    public void MatchesFilter_MatchesOwnPathCaseInsensitively()
        => Assert.True(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween"])), "HALO"));

    [Fact]
    public void MatchesFilter_MatchesThroughADescendant()
        => Assert.True(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween/animation/Dance"])), "dance"));

    [Fact]
    public void MatchesFilter_NonMatchingBranch_IsFalse()
        => Assert.False(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween/animation"])), "gear"));

    [Fact]
    public void MatchesFilter_EmptyFilter_MatchesEverything()
        => Assert.True(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween"])), ""));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~FolderProtectionTreeTests"`
Expected: build failure — `FolderProtectionTree` does not exist.

- [ ] **Step 3: Write the builder**

Create `PenumbraOrganizer.Plugin/Windows/FolderProtectionTree.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>One folder in the Protect tab's tree.</summary>
/// <param name="Path">Full virtual path, e.g. "haloween/animation".</param>
/// <param name="Name">The last segment, which is what the row shows.</param>
public sealed record FolderTreeNode(string Path, string Name, IReadOnlyList<FolderTreeNode> Children)
{
    /// <summary>Every folder below this one, at any depth. Shown in the row's label.</summary>
    public int DescendantFolderCount => Children.Count + Children.Sum(c => c.DescendantFolderCount);
}

/// <summary>
/// Builds and draws the Protect tab's folder tree. The flat one-row-per-folder list it replaces
/// became unusable once mods were sorted into per-number subfolders: hundreds of fully-qualified
/// rows, nothing collapsible.
/// </summary>
public static class FolderProtectionTree
{
    /// <summary>
    /// Turns folder paths into a tree. Intermediate segments missing from the input become nodes
    /// too, so the tree has no holes; siblings are ordinal-sorted, matching the flat list's order.
    /// </summary>
    public static IReadOnlyList<FolderTreeNode> Build(IEnumerable<string> folderPaths)
    {
        var children = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var roots = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in folderPaths)
        {
            var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(path);
            if (normalized is null)
                continue;

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                var current = string.Join('/', segments.Take(i + 1));
                if (i == 0)
                {
                    roots.Add(current);
                    continue;
                }

                var parent = string.Join('/', segments.Take(i));
                if (!children.TryGetValue(parent, out var siblings))
                    children[parent] = siblings = new SortedSet<string>(StringComparer.Ordinal);
                siblings.Add(current);
            }
        }

        return roots.Select(path => BuildNode(path, children)).ToList();
    }

    private static FolderTreeNode BuildNode(string path, Dictionary<string, SortedSet<string>> children)
    {
        var childNodes = children.TryGetValue(path, out var paths)
            ? paths.Select(child => BuildNode(child, children)).ToList()
            : [];
        var name = path[(path.LastIndexOf('/') + 1)..];
        return new FolderTreeNode(path, name, childNodes);
    }

    /// <summary>
    /// Whether this branch survives the tab's search box: the node's own path matches, or some
    /// descendant's does. A branch kept only by a descendant is force-expanded when drawn, so the
    /// match is visible without digging.
    /// </summary>
    public static bool MatchesFilter(FolderTreeNode node, string filter)
    {
        if (filter.Length == 0)
            return true;
        return node.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.Children.Any(child => MatchesFilter(child, filter));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~FolderProtectionTreeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/FolderProtectionTree.cs PenumbraOrganizer.Plugin.Tests/Windows/FolderProtectionTreeTests.cs
git commit -m "Add a folder tree builder for the Protect tab

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Draw the tree with tri-state checkboxes

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/FolderProtectionTree.cs` (add `Draw`)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.ProtectTab.cs` (replace the folder list loop, ~lines 72-128)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (expansion state + saver)
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs` (after line 43)
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopicUsage.cs` (Protect block, after `ProtectFolderUnprotectContents`)
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json` (after the `protect.folder-unprotect-contents` entry)

**Interfaces:**
- Consumes: `FolderProtectionTree.Build`, `FolderProtectionTree.MatchesFilter` (Task 5); `OrganizerState.FolderProtectionRules`, `ResolveFolderRule`, `SetFolderProtectionRule`, `BuildProtectedModCountsByFolder`, `KnownFolders` (Tasks 2-3); `Configuration.ExpandedProtectFolders` (Task 4).
- Produces: `FolderProtectionTree.Draw(...)`, signature given below, and `HelpTopics.ProtectFolderRuleCycle`.

- [ ] **Step 1: Add the help topic and its content**

In `HelpTopics.cs`, after the `ProtectFolderUnprotectContents` line:

```csharp
    public static readonly HelpTopic ProtectFolderRuleCycle = new("protect.folder-rule-cycle");
```

In `HelpTopicUsage.cs`, in the Protect block, after `HelpTopics.ProtectFolderUnprotectContents,`:

```csharp
        HelpTopics.ProtectFolderRuleCycle,
```

That list is hand-maintained and a test (`EveryTopicWithAShort_IsDeclaredAsUsedByAControl`) fails without it.

In `Resources/help-content.json`, after the `protect.folder-unprotect-contents` entry:

```json
    {
      "id": "protect.folder-rule-cycle",
      "title": "Protect, exclude, or inherit",
      "short": "Click a folder's box to cycle it: protected, excluded, or inheriting from the folder above. A dim mark means the state comes from a parent folder; a solid one means this folder sets it. Excluding a folder frees it even while its parent stays protected - mods you ticked yourself, and Heliosphere mods, stay protected."
    },
```

Also update the `protect.folders` entry's `short` to describe the tree:

```json
      "short": "Protects every mod in a folder and its subfolders. Click a box to cycle protect, exclude, inherit - so you can protect a folder and still free one subfolder inside it. Folders are a tree: click the arrow to open one."
```

- [ ] **Step 2: Run the help tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.sln --filter "FullyQualifiedName~HelpContentTests"`
Expected: PASS — a topic constant with no resource entry fails `EveryTopicConstant_ResolvesInTheResource`, so this confirms the JSON edit landed. (Rerun after step 3 too.)

- [ ] **Step 3: Add the renderer**

Append to `FolderProtectionTree.cs`, inside the `FolderProtectionTree` class:

```csharp
    /// <summary>
    /// Draws the tree. Each row carries one tri-state control that cycles
    /// inherit -> protect -> exclude -> inherit; the state is drawn dimmed when it comes from an
    /// ancestor rather than this folder's own rule, so a user can tell what they have set from what
    /// they have inherited.
    /// </summary>
    /// <param name="nodes">Roots from <see cref="Build"/>.</param>
    /// <param name="resolve">A folder's effective rule and the folder that supplied it.</param>
    /// <param name="counts">Per-folder mod counts, from BuildProtectedModCountsByFolder.</param>
    /// <param name="knownFolders">Folders that currently hold mods; others are labelled empty.</param>
    /// <param name="filter">The tab's search text. Non-matching branches are hidden; matching ones
    /// are force-expanded without touching <paramref name="expanded"/>.</param>
    /// <param name="expanded">
    /// Open folders, persisted. Mutated in place as the user opens and closes nodes; the caller
    /// saves the config when <c>true</c> comes back.
    /// </param>
    /// <param name="onCycle">Called with the folder and the rule it should now carry.</param>
    /// <param name="onUnprotectContents">Called with the folder whose contents should be cleared.</param>
    /// <returns>True when <paramref name="expanded"/> changed and should be persisted.</returns>
    public static bool Draw(
        IReadOnlyList<FolderTreeNode> nodes,
        Func<string, OrganizerState.FolderRuleResolution> resolve,
        IReadOnlyDictionary<string, OrganizerState.FolderProtectionCounts> counts,
        IReadOnlySet<string> knownFolders,
        string filter,
        HashSet<string> expanded,
        Action<string, FolderProtectionRule> onCycle,
        Action<string> onUnprotectContents)
    {
        var expansionChanged = false;
        foreach (var node in nodes)
            DrawNode(node, resolve, counts, knownFolders, filter, expanded, onCycle, onUnprotectContents,
                ref expansionChanged);
        return expansionChanged;
    }

    private static void DrawNode(
        FolderTreeNode node,
        Func<string, OrganizerState.FolderRuleResolution> resolve,
        IReadOnlyDictionary<string, OrganizerState.FolderProtectionCounts> counts,
        IReadOnlySet<string> knownFolders,
        string filter,
        HashSet<string> expanded,
        Action<string, FolderProtectionRule> onCycle,
        Action<string> onUnprotectContents,
        ref bool expansionChanged)
    {
        if (!MatchesFilter(node, filter))
            return;

        var resolution = resolve(node.Path);
        var ownsRule = string.Equals(resolution.Source, node.Path, StringComparison.Ordinal);

        // The tri-state control, drawn before the tree node so the whole row reads
        // "[state] name (counts)". ImGui's Checkbox is two-state, so this is a small button
        // whose glyph carries the state: solid when this folder owns the rule, dimmed when the
        // state is inherited from an ancestor.
        var glyph = resolution.Rule switch
        {
            FolderProtectionRule.Protected => "[x]",
            FolderProtectionRule.Excluded => "[-]",
            _ => "[ ]",
        };
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey, !ownsRule && resolution.Rule != FolderProtectionRule.None))
        {
            if (ImGui.SmallButton($"{glyph}##protect-folder-cycle-{node.Path}"))
                onCycle(node.Path, NextRule(ownsRule ? resolution.Rule : FolderProtectionRule.None));
        }

        if (ImGui.IsItemHovered())
        {
            var state = resolution.Rule switch
            {
                FolderProtectionRule.Protected when ownsRule => "Protected by this folder.",
                FolderProtectionRule.Protected => $"Protected by \"{resolution.Source}\".",
                FolderProtectionRule.Excluded when ownsRule => "Excluded by this folder: sorting may move these mods.",
                FolderProtectionRule.Excluded => $"Excluded by \"{resolution.Source}\": sorting may move these mods.",
                _ => "No rule: not protected by any folder.",
            };
            ImGui.SetTooltip($"{state}\nClick to cycle protect / exclude / inherit.");
        }

        ImGui.SameLine();

        var folderCounts = counts.GetValueOrDefault(node.Path);
        var label = knownFolders.Contains(node.Path)
            ? $"{node.Name}  ({folderCounts.Protected}/{folderCounts.Total} protected"
                + (node.DescendantFolderCount > 0 ? $", {node.DescendantFolderCount} subfolders)" : ")")
            : $"{node.Name}  (currently empty)";

        // Forced open while a search is running so a match deeper in the branch is visible without
        // digging - and deliberately not written to `expanded`, so clearing the search restores
        // whatever the user had open.
        var searching = filter.Length > 0;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth
            | (node.Children.Count == 0 ? ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen : ImGuiTreeNodeFlags.None);
        if (searching || expanded.Contains(node.Path))
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        var open = ImGui.TreeNodeEx($"{label}##protect-folder-node-{node.Path}", flags);
        // On the label, not the cycle button: that button already shows a tooltip naming the rule's
        // source, and ImGui binds a tooltip to the last submitted item, so two on one item fight.
        Help.Tooltip(HelpTopics.ProtectFolderRuleCycle);
        if (!searching && node.Children.Count > 0 && open != expanded.Contains(node.Path))
        {
            if (open)
                expanded.Add(node.Path);
            else
                expanded.Remove(node.Path);
            expansionChanged = true;
        }

        if (folderCounts.Freeable > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Unprotect {folderCounts.Freeable} inside##protect-folder-clear-{node.Path}"))
                onUnprotectContents(node.Path);
            Help.Tooltip(HelpTopics.ProtectFolderUnprotectContents);
        }

        if (!open)
            return;

        foreach (var child in node.Children)
            DrawNode(child, resolve, counts, knownFolders, filter, expanded, onCycle, onUnprotectContents,
                ref expansionChanged);

        // Leaves pass NoTreePushOnOpen, so only a node with children pushed anything to pop.
        if (node.Children.Count > 0)
            ImGui.TreePop();
    }

    private static FolderProtectionRule NextRule(FolderProtectionRule current) => current switch
    {
        FolderProtectionRule.None => FolderProtectionRule.Protected,
        FolderProtectionRule.Protected => FolderProtectionRule.Excluded,
        _ => FolderProtectionRule.None,
    };
```

Add the usings this needs at the top of the file:

```csharp
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using PenumbraOrganizer.Plugin.Organizer;
```

- [ ] **Step 4: Call it from the Protect tab**

In `MainWindow.ProtectTab.cs`, replace everything from

```csharp
        var filter = _protectFilter.Trim();
```

down to the end of the `using (var folderChild = ...)` block (the whole flat folder loop) with:

```csharp
        var filter = _protectFilter.Trim();
        var knownFolders = _plugin.OrganizerState.KnownFolders.ToHashSet(StringComparer.Ordinal);
        var protectedCountsByFolder = _plugin.OrganizerState.BuildProtectedModCountsByFolder();
        // Ruled folders are included even when empty, so a rule is never invisible: a folder whose
        // mods have all moved away still shows its own tick and can be untangled.
        var folderTree = FolderProtectionTree.Build(
            knownFolders.Union(_plugin.OrganizerState.FolderProtectionRules.Keys, StringComparer.Ordinal));

        ImGui.TextUnformatted("Folders");
        Help.Tooltip(HelpTopics.ProtectFolders);
        using (var folderChild = ImRaii.Child("ProtectedFolderList", new Vector2(0, _protectedFolderListHeight), border: true))
        {
            if (folderChild)
            {
                var expansionChanged = FolderProtectionTree.Draw(
                    folderTree,
                    _plugin.OrganizerState.ResolveFolderRule,
                    protectedCountsByFolder,
                    knownFolders,
                    filter,
                    _expandedProtectFolders,
                    (folder, rule) =>
                    {
                        _plugin.OrganizerState.SetFolderProtectionRule(folder, rule);
                        SaveProtectionStateSafely();
                    },
                    folder => _pendingUnprotectFolder = folder);

                if (expansionChanged)
                    SaveExpandedProtectFoldersSafely();
            }
        }
```

Delete the now-unused `protectedFolders` local and the `folderRows` query at the top of that section. The `Mods` list below still needs a protected-folder lookup for its `(via folder: X)` note — replace that block's `coveringFolder` computation with the resolver:

```csharp
                        else
                        {
                            var resolution = _plugin.OrganizerState.ResolveFolderRule(
                                Organizer.OrganizationCleanupPlanner.GetVirtualParent(mod.CurrentPath) ?? string.Empty);
                            ImGui.TextDisabled(resolution.Rule == FolderProtectionRule.Protected
                                ? $"(via folder: {resolution.Source})"
                                : "(protected)");
                        }
```

Add the using for the enum at the top of the file:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
```

- [ ] **Step 5: Add the window state and its saver**

In `MainWindow.cs`, after `_unprotectFolderPopupOpened` (line 39), add:

```csharp
    // Mirrors Config.ExpandedProtectFolders, mutated in place by the folder tree as nodes open and
    // close. Held here rather than read from the config each frame because the tree writes to it.
    private readonly HashSet<string> _expandedProtectFolders;
```

Initialise it in the constructor, from the config (find the constructor that already takes `Plugin`; add after the existing assignments):

```csharp
        _expandedProtectFolders = new HashSet<string>(plugin.Config.ExpandedProtectFolders, StringComparer.Ordinal);
```

Next to `SaveProtectionStateSafely` (line 409), add the same try/catch shape:

```csharp
    private void SaveExpandedProtectFoldersSafely()
    {
        try
        {
            _plugin.Config.ExpandedProtectFolders = new HashSet<string>(_expandedProtectFolders, StringComparer.Ordinal);
            _plugin.SaveConfig();
        }
        catch (Exception ex)
        {
            // Same stance as SaveProtectionStateSafely: a failed save of a UI preference must never
            // take the window down mid-frame.
            _plugin.Log.Error(ex, "Failed to save Protect tab folder expansion.");
        }
    }
```

Match the exact logging call and field name `SaveProtectionStateSafely` uses — read lines 409-420 and mirror them rather than assuming `_plugin.Log`.

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` then `dotnet test PenumbraOrganizer.Plugin.sln`
Expected: build succeeds with no warnings about unused locals in `MainWindow.ProtectTab.cs`; all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows PenumbraOrganizer.Plugin/Resources/help-content.json
git commit -m "Draw Protect tab folders as a collapsible tri-state tree

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: In-game verification pass

**Files:** none (manual).

No unit test covers ImGui rendering, and this plugin's history is that protection and sorting bugs only show up against a real library. Do not promote this to the stable channel before a tester confirms all of the following in game.

- [ ] **Step 1: Verify the exclude case that prompted this**

With `haloween` protected and `haloween/animation` cycled to excluded: mods directly in `haloween` stay protected; mods in `haloween/animation` and below are free; Sort then Apply moves the animation mods and leaves the rest.

- [ ] **Step 2: Verify the layered sources**

A hand-ticked mod and a Heliosphere mod inside the excluded folder stay protected, and the row's `Unprotect N inside` button appears with the right count and clears exactly those.

- [ ] **Step 3: Verify the tree**

The hair subfolders are collapsed on open; opening some, closing the window and reopening it restores them; searching `01` shows only matching branches, expanded, and clearing the search restores the previous expansion.

- [ ] **Step 4: Verify the upgrade path**

A config written by 0.6.1.2 with protected folders loads with those folders still protected and no re-ticking needed.

- [ ] **Step 5: Record the result**

Note the outcome in the session's memory file for this work, then decide on a release separately.
