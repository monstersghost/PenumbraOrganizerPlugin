# Tool-Managed Mods (Proteus + Heliosphere modes) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect Proteus-made mods and its managed mod, protect them by default, and give both Heliosphere and Proteus one saved three-mode setting (Protected / Sort inside their folder / Treat as normal), plus an option to place the Proteus managed mod at the root.

**Architecture:** Pure pieces first (`ToolMode`, `ProteusRole`, `ProteusDetector`, `ToolFolders.ApplyAnchor`), then the config values, the row and the scan, then the single protection rule in `OrganizerState` reads a `ToolPolicy`, then the sort applies the anchor, then the Protect tab gets mode dropdowns. The existing Heliosphere detection and its `HeliosphereManaged` flag are untouched.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), Dalamud + `Dalamud.Bindings.ImGui`, xUnit.

Spec: `docs/superpowers/specs/2026-11-05-tool-managed-mods-design.md` (read it first).

## Global Constraints

- **Prerequisite:** the Sort/Review UX pass (`docs/superpowers/plans/2026-11-05-sort-review-ux-pass.md`) is merged into `main`. It rewrites the Protect tab's two toggle buttons (via `ProtectToggleLabels`) and the help registration this plan builds on. Task 1 Step 0 verifies it and STOPS if it is missing.
- Work in the main checkout `C:\Repo\PenumbraOrganizer.Plugin` on a NEW branch `feat/tool-managed-mods` cut from `main`. Do not touch `.claude\worktrees\*`. Run commands from the repo root.
- Test command: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~<Name>"`. Build: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj -c Debug`. Record the passing-test baseline at Step 0.
- No `InternalsVisibleTo`: anything a test reaches is `public`.
- `LibraryWorkPurityTests` forbids Dalamud/Penumbra types in `LibraryWork/Pure`. `ProteusDetector` and `ToolFolders` are pure.
- Proteus detection is exact and ordinal: author equals `Proteus` (case-sensitive); the managed mod is a mod whose directory name equals `Proteus` (ignore case) AND whose author equals `Proteus` (Proteus's own code writes both).
- Only `Made` and the existing Heliosphere flag are covered by the three modes. The managed mod is outside them: protected unless `ProteusManagedAtRoot` is on or a per-mod override is set.
- Protection order is unchanged: an explicit mod protection or a protected folder beats the per-mod override.
- Hidden means not rendered. Every `Begin*` has its `End*` on every path; `Help.Tooltip` goes right after the widget it explains. `help-content.json` `short` text is at most 200 characters, and every `HelpTopics` constant needs a json entry and a `HelpTopicUsage` entry.
- Plain ASCII in docs and test strings. Commit trailer on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- No release, no version bump, no tutorial, guide or PDF changes (deferred).

## Deliberate deviations from the spec (small, both safer)

- The row gets an additive `ProteusRole` (None, Made, Managed) plus a computed `IsToolManaged`, instead of replacing `HeliosphereManaged` with a `ManagedBy` enum. Fewer test and call-site changes, same behavior.
- The managed mod requires BOTH the directory name `Proteus` and the author `Proteus`, so an unrelated mod that happens to live in a directory called `Proteus` is not flagged.

## File Structure

| File | Change |
|---|---|
| `PenumbraOrganizer.Plugin/Organizer/ToolMode.cs` | create: `ToolMode`, `ProteusRole`, `ToolPolicy` |
| `PenumbraOrganizer.Plugin/Organizer/ProteusDetector.cs` | create: pure detector |
| `PenumbraOrganizer.Plugin/Organizer/ToolFolders.cs` | create: pure anchor helper |
| `PenumbraOrganizer.Plugin/Configuration.cs` | three new values |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs` | `ProteusRole`, `IsToolManaged` |
| `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` | set `ProteusRole` |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` | policy, the rule, override rename, sort-inside, root placement |
| `PenumbraOrganizer.Plugin/Plugin.cs` | push the policy into the state; save on change |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.ProtectTab.cs`, `ToolModeLabels.cs` | dropdowns, counts, notes |
| `OrganizerExportFormatter.cs`, `MainWindow.cs` | `ManagedBy` line, diagnostic counts |
| help files | new topics, remove the Heliosphere toggle topic and label |

---

### Task 1: `ToolMode`, `ProteusRole`, `ToolPolicy`, `ProteusDetector`, `ToolFolders`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/ToolMode.cs`, `ProteusDetector.cs`, `ToolFolders.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/ProteusDetectorTests.cs`, `ToolFoldersTests.cs`, `ToolPolicyTests.cs`

**Interfaces:**
- Produces:
  - `public enum ToolMode { Protected = 0, SortInside = 1, Normal = 2 }`
  - `public enum ProteusRole { None = 0, Made = 1, Managed = 2 }`
  - `public readonly record struct ToolPolicy(ToolMode Heliosphere, ToolMode Proteus, bool ProteusManagedAtRoot)` with `static ToolPolicy Default` (Protected, Protected, false) and `static ToolMode Normalize(ToolMode)` (an undefined value reads as `Protected`) and `static ToolPolicy From(int heliosphere, int proteus, bool managedAtRoot)` that normalizes.
  - `ProteusDetector.Classify(string directoryName, string? author) -> ProteusRole`
  - `ToolFolders.ApplyAnchor(string currentPath, string normalPath) -> string`

- [ ] **Step 0: Prerequisite and branch.**
  1. `git checkout main && git status --short` must be clean. Run `git grep -n "ProtectToggleLabels" -- PenumbraOrganizer.Plugin` : it MUST print matches (the UX pass landed). If it prints nothing, STOP and report "UX pass not merged".
  2. `git checkout -b feat/tool-managed-mods`.
  3. Record the baseline: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj` and note the passed count.

- [ ] **Step 1: Write the failing tests.**

`ProteusDetectorTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ProteusDetectorTests
{
    [Theory]
    [InlineData("refit-abc", "Proteus", ProteusRole.Made)]
    [InlineData("anything", "Proteus", ProteusRole.Made)]
    [InlineData("refit-abc", "proteus", ProteusRole.None)]   // Proteus's own check is ordinal
    [InlineData("refit-abc", "PROTEUS", ProteusRole.None)]
    [InlineData("refit-abc", "Proteus ", ProteusRole.None)]
    [InlineData("refit-abc", "Solona", ProteusRole.None)]
    [InlineData("refit-abc", "", ProteusRole.None)]
    public void Classify_MadeMods_AreExactlyTheOnesAuthoredByProteus(string dir, string author, ProteusRole expected) =>
        Assert.Equal(expected, ProteusDetector.Classify(dir, author));

    [Fact]
    public void Classify_NullAuthor_IsNone() =>
        Assert.Equal(ProteusRole.None, ProteusDetector.Classify("refit-abc", null));

    [Theory]
    [InlineData("Proteus")]
    [InlineData("proteus")]
    [InlineData("PROTEUS")]
    public void Classify_TheManagedMod_NeedsTheDirectoryNameAndTheAuthor(string dir) =>
        Assert.Equal(ProteusRole.Managed, ProteusDetector.Classify(dir, "Proteus"));

    [Fact]
    public void Classify_ADirectoryCalledProteusWithAnotherAuthor_IsNotFlagged()
    {
        Assert.Equal(ProteusRole.None, ProteusDetector.Classify("Proteus", "Someone Else"));
        Assert.Equal(ProteusRole.None, ProteusDetector.Classify("Proteus", null));
    }
}
```

`ToolFoldersTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ToolFoldersTests
{
    [Fact]
    public void TheModsOwnFolder_BecomesThePrefix() =>
        Assert.Equal("Proteus Upscales/Gear/Rue+/Top/Name",
            ToolFolders.ApplyAnchor("Proteus Upscales/Name", "Gear/Rue+/Top/Name"));

    [Fact]
    public void AModAtTheRoot_IsSortedNormally() =>
        Assert.Equal("Gear/Top/Name", ToolFolders.ApplyAnchor("Name", "Gear/Top/Name"));

    [Theory]
    [InlineData("Gear/Top/Name")]
    [InlineData("gear/top/Name")]
    [InlineData("Body/Name")]
    [InlineData("Hair/Name")]
    [InlineData("NPC/Name")]
    [InlineData("Animation and VFX/Name")]
    [InlineData("Review/Name")]
    public void AnAnchorThatIsOneOfTheSortersOwnFolders_IsSortedNormally(string currentPath) =>
        Assert.Equal("Gear/Top/Name", ToolFolders.ApplyAnchor(currentPath, "Gear/Top/Name"));

    [Fact]
    public void ANormalPathThatAlreadyStartsWithTheAnchor_IsUsedAsItIs()
    {
        // Group by Creator first: the sorted path already begins with the creator folder the mod sits in.
        Assert.Equal("Solona/Gear/Name", ToolFolders.ApplyAnchor("Solona/Name", "Solona/Gear/Name"));
        Assert.Equal("solona/Gear/Name", ToolFolders.ApplyAnchor("Solona/Name", "solona/Gear/Name"));
    }

    [Fact]
    public void OnlyTheFirstSegmentOfTheCurrentPathIsTheAnchor() =>
        Assert.Equal("Tools/Gear/Name", ToolFolders.ApplyAnchor("Tools/Deep/Deeper/Name", "Gear/Name"));

    [Fact]
    public void TheUnclassifiedBucket_IsPrefixedLikeAnyOtherNormalPath() =>
        Assert.Equal("Proteus Upscales/Review/Name", ToolFolders.ApplyAnchor("Proteus Upscales/Name", "Review/Name"));

    [Theory]
    [InlineData("", "Gear/Name")]
    [InlineData("/Name", "Gear/Name")]
    public void AnEmptyOrLeadingSlashCurrentPath_HasNoAnchor(string currentPath, string normal) =>
        Assert.Equal(normal, ToolFolders.ApplyAnchor(currentPath, normal));
}
```

`ToolPolicyTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ToolPolicyTests
{
    [Fact]
    public void Default_ProtectsBothTools_AndKeepsTheManagedModProtected()
    {
        var d = ToolPolicy.Default;

        Assert.Equal(ToolMode.Protected, d.Heliosphere);
        Assert.Equal(ToolMode.Protected, d.Proteus);
        Assert.False(d.ProteusManagedAtRoot);
    }

    [Theory]
    [InlineData(0, ToolMode.Protected)]
    [InlineData(1, ToolMode.SortInside)]
    [InlineData(2, ToolMode.Normal)]
    [InlineData(3, ToolMode.Protected)]
    [InlineData(-1, ToolMode.Protected)]
    [InlineData(99, ToolMode.Protected)]
    public void From_ReadsAnUnknownStoredValueAsProtected(int stored, ToolMode expected)
    {
        var p = ToolPolicy.From(stored, stored, managedAtRoot: true);

        Assert.Equal(expected, p.Heliosphere);
        Assert.Equal(expected, p.Proteus);
        Assert.True(p.ProteusManagedAtRoot);
    }
}
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~ProteusDetectorTests|FullyQualifiedName~ToolFoldersTests|FullyQualifiedName~ToolPolicyTests"`. Expected: build FAIL, the types do not exist.

- [ ] **Step 3: Implement.**

`ToolMode.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// What the sorter does with the mods a tool (Heliosphere or Proteus) made or installed. Both tools
/// find their mods by directory, not by Penumbra path, so moving them is safe; the modes choose how
/// much to move. The numeric values are what the config stores.
/// </summary>
public enum ToolMode
{
    /// <summary>Never moved. The default.</summary>
    Protected = 0,

    /// <summary>Moved only beneath the folder they already sit in.</summary>
    SortInside = 1,

    /// <summary>Sorted like any other mod.</summary>
    Normal = 2,
}

/// <summary>How a mod relates to Proteus.</summary>
public enum ProteusRole
{
    None = 0,

    /// <summary>Made by Proteus: its meta.json author is exactly "Proteus".</summary>
    Made = 1,

    /// <summary>Proteus's own managed output mod (directory "Proteus", author "Proteus").</summary>
    Managed = 2,
}

/// <summary>The saved tool settings the protection rule and the sort consult.</summary>
public readonly record struct ToolPolicy(ToolMode Heliosphere, ToolMode Proteus, bool ProteusManagedAtRoot)
{
    public static ToolPolicy Default => new(ToolMode.Protected, ToolMode.Protected, false);

    /// <summary>A stored value this build does not know reads as Protected, the safe default.</summary>
    public static ToolMode Normalize(ToolMode mode) =>
        Enum.IsDefined(mode) ? mode : ToolMode.Protected;

    public static ToolPolicy From(int heliosphere, int proteus, bool managedAtRoot) =>
        new(Normalize((ToolMode)heliosphere), Normalize((ToolMode)proteus), managedAtRoot);
}
```

`ProteusDetector.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Recognises mods Proteus made, from facts the scan already has (no disk read).
/// </summary>
/// <remarks>
/// Proteus's own <c>IsMadeByProteus</c> checks that the manifest's Author is exactly "Proteus", so
/// this matches ordinally: "proteus" is somebody else's mod. The managed output mod is the one in a
/// directory named "Proteus", and Proteus writes the same author into it, so BOTH must hold; a
/// mod that merely lives in a directory called "Proteus" is not flagged. Mods Proteus imports
/// (Onion, Eye, Luminis, emissive) keep the imported author and are deliberately not detected.
/// </remarks>
public static class ProteusDetector
{
    public const string AuthorName = "Proteus";
    public const string ManagedDirectoryName = "Proteus";

    public static ProteusRole Classify(string directoryName, string? author)
    {
        if (!string.Equals(author, AuthorName, StringComparison.Ordinal))
            return ProteusRole.None;

        return string.Equals(directoryName, ManagedDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? ProteusRole.Managed
            : ProteusRole.Made;
    }
}
```

`ToolFolders.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// "Sort inside their folder": keeps a tool's mod under the folder it already sits in.
/// </summary>
/// <remarks>
/// The anchor is the first segment of the mod's CURRENT path, so it works in any language (Proteus
/// names its folder in the player's language) and respects where the user put the mod. Three cases
/// need no prefix: the mod is at the root, the anchor is one of the sorter's own folders (else
/// <c>Gear/Gear/...</c>), or the sorted path already starts with the anchor (Group by Creator).
/// </remarks>
public static class ToolFolders
{
    // Every folder the sorter itself creates at the top level: one per mod category, the combined
    // Animation and VFX parent, and the unclassified bucket.
    private static readonly HashSet<string> SorterFolders = new(
        [.. Enum.GetNames<ModCategory>(), "Animation and VFX", "Review"],
        StringComparer.OrdinalIgnoreCase);

    public static string ApplyAnchor(string currentPath, string normalPath)
    {
        var slash = currentPath.IndexOf('/');
        if (slash <= 0)
            return normalPath;

        var anchor = currentPath[..slash];
        if (SorterFolders.Contains(anchor))
            return normalPath;

        var normalFirst = normalPath.Split('/')[0];
        if (string.Equals(normalFirst, anchor, StringComparison.OrdinalIgnoreCase))
            return normalPath;

        return $"{anchor}/{normalPath}";
    }
}
```

(If `ModTypeFolders` exposes the Animation/VFX parent name as a public constant, use it instead of the literal `"Animation and VFX"`; the literal must still match what `ModTypeFolders.GetFolder(ModCategory.Animation, "x")` produces - assert that in a test if you switch.)

- [ ] **Step 4: Run.** The Step 2 filter. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer PenumbraOrganizer.Plugin.Tests/Organizer
git commit -m "feat: tool modes, Proteus detection and the sort-inside anchor helper" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Config values

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs` (after `MidlanderFallback`)
- Test: `PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs`

**Interfaces:**
- Produces: `Configuration.HeliosphereMode` and `Configuration.ProteusMode` (`ToolMode`, default `Protected`), `Configuration.ProteusManagedAtRoot` (`bool`, default false), and `ToolPolicy Configuration.ToolPolicy` (computed, normalized).

- [ ] **Step 1: Write the failing tests** (append to `ConfigurationTests`)

```csharp
    [Fact]
    public void ToolModes_DefaultToProtected_AndTheManagedModStaysProtected()
    {
        var config = new Configuration();

        Assert.Equal(ToolMode.Protected, config.HeliosphereMode);
        Assert.Equal(ToolMode.Protected, config.ProteusMode);
        Assert.False(config.ProteusManagedAtRoot);
        Assert.Equal(ToolPolicy.Default, config.ToolPolicy);
    }

    [Fact]
    public void ToolModes_RoundTripThroughJson_AndAnOldConfigWithoutThemLoadsAsDefaults()
    {
        var config = new Configuration
        {
            HeliosphereMode = ToolMode.Normal,
            ProteusMode = ToolMode.SortInside,
            ProteusManagedAtRoot = true,
        };

        var loaded = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            System.Text.Json.JsonSerializer.Serialize(config))!;
        Assert.Equal(ToolMode.Normal, loaded.HeliosphereMode);
        Assert.Equal(ToolMode.SortInside, loaded.ProteusMode);
        Assert.True(loaded.ProteusManagedAtRoot);

        var old = System.Text.Json.JsonSerializer.Deserialize<Configuration>("""{"Version":2}""")!;
        Assert.Equal(ToolPolicy.Default, old.ToolPolicy);
    }

    [Fact]
    public void ToolPolicy_ReadsAnUnknownStoredModeAsProtected()
    {
        var config = new Configuration { HeliosphereMode = (ToolMode)42, ProteusMode = (ToolMode)(-3) };

        Assert.Equal(ToolMode.Protected, config.ToolPolicy.Heliosphere);
        Assert.Equal(ToolMode.Protected, config.ToolPolicy.Proteus);
    }
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~ConfigurationTests"`. Expected: build FAIL.

- [ ] **Step 3: Implement** (in `Configuration.cs`; add `using PenumbraOrganizer.Plugin.Organizer;` only if the file does not already reach the namespace by its existing `Organizer.` references - the file uses `Organizer.ApplyOperationSummary`, so write `Organizer.ToolMode` / `Organizer.ToolPolicy` there, or add the using):

```csharp
    /// <summary>What the sorter does with Heliosphere-managed mods. Protected until the user says otherwise.</summary>
    public Organizer.ToolMode HeliosphereMode { get; set; } = Organizer.ToolMode.Protected;

    /// <summary>What the sorter does with mods Proteus made. Protected until the user says otherwise.</summary>
    public Organizer.ToolMode ProteusMode { get; set; } = Organizer.ToolMode.Protected;

    /// <summary>
    /// Place Proteus's own managed mod at the root instead of keeping it protected. It is plumbing the
    /// user did not choose, so it is protected by default and outside the three modes.
    /// </summary>
    public bool ProteusManagedAtRoot { get; set; }

    /// <summary>The three settings above, with an unknown stored mode read as Protected.</summary>
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Organizer.ToolPolicy ToolPolicy =>
        Organizer.ToolPolicy.From((int)HeliosphereMode, (int)ProteusMode, ProteusManagedAtRoot);
```

(Check whether `Newtonsoft.Json` is referenced by this project: Dalamud serializes `IPluginConfiguration` with Newtonsoft. If the attribute does not compile, drop the Newtonsoft one and keep only the System.Text.Json one, but then confirm with a quick Newtonsoft round-trip that a read-only computed property is not written: a property with only a getter is serialized by Newtonsoft unless ignored, which is why the attribute matters.)

- [ ] **Step 4: Run.** `--filter "FullyQualifiedName~ConfigurationTests"`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Configuration.cs PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs
git commit -m "feat: saved tool modes and the managed-mod root option" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The row and the scan

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`, `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` (the row initializer, ~line 136)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs`

**Interfaces:**
- Produces: `OrganizerModRow.ProteusRole` (`ProteusRole`, `init`, default `None`) and `OrganizerModRow.IsToolManaged` (`HeliosphereManaged || ProteusRole != None`).
- The scan sets `ProteusRole = ProteusDetector.Classify(item.Identifier, item.Author)` (`Identifier` is the mod's directory name, as the Heliosphere call already treats it).

- [ ] **Step 1: Write the failing tests** (append to `ScanProcessorTests`, using its existing `NewProcessor()` and `Seed(...)` helpers; read the file first and copy the way neighbouring tests build a seed with a given author and identifier. If `Seed` has no author or identifier parameter, extend the helper with optional parameters; do not change existing calls):

```csharp
    [Fact]
    public void Process_AModAuthoredByProteus_IsMadeByProteus()
    {
        var row = ProcessWith(identifier: "refit-abc", author: "Proteus");

        Assert.Equal(ProteusRole.Made, row.ProteusRole);
        Assert.True(row.IsToolManaged);
    }

    [Fact]
    public void Process_TheProteusManagedDirectory_IsTheManagedMod()
    {
        var row = ProcessWith(identifier: "Proteus", author: "Proteus");

        Assert.Equal(ProteusRole.Managed, row.ProteusRole);
    }

    [Fact]
    public void Process_AnOrdinaryMod_HasNoProteusRole_AndIsNotToolManaged()
    {
        var row = ProcessWith(identifier: "some-mod", author: "Solona");

        Assert.Equal(ProteusRole.None, row.ProteusRole);
        Assert.False(row.IsToolManaged);
    }

    [Fact]
    public void Process_AHeliosphereMod_IsToolManagedButNotAProteusMod()
    {
        var row = ProcessWith(identifier: "hs-Aeonium-1.1.9-aa-bb", author: "Solona");

        Assert.True(row.HeliosphereManaged);
        Assert.Equal(ProteusRole.None, row.ProteusRole);
        Assert.True(row.IsToolManaged);
    }
```

`ProcessWith(identifier, author)` is a small helper you add: create a temp mod directory named `identifier` if the existing fixture does that, build a `ScanSeed` with that identifier and author through the existing `Seed` helper, call `NewProcessor().Process(seed, CancellationToken.None)` and return the non-null row.

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~ScanProcessorTests"`. Expected: build FAIL (`ProteusRole` is not a row member).

- [ ] **Step 3: Implement.** `OrganizerModRow.cs`, after `HeliosphereManaged`:

```csharp
    /// <summary>
    /// How this mod relates to Proteus: made by it, its own managed mod, or neither. Detected from the
    /// author and directory name the scan already has, so it costs no disk read.
    /// </summary>
    public ProteusRole ProteusRole { get; init; } = ProteusRole.None;

    /// <summary>Managed by a tool whose mods get a saved mode: Heliosphere, or made by Proteus.</summary>
    public bool IsToolManaged => HeliosphereManaged || ProteusRole != ProteusRole.None;
```

`ScanProcessor.cs`, in the returned row initializer next to `HeliosphereManaged = ...`:

```csharp
            ProteusRole = ProteusDetector.Classify(item.Identifier, item.Author),
```

- [ ] **Step 4: Run.** `--filter "FullyQualifiedName~ScanProcessorTests|FullyQualifiedName~LibraryWorkPurityTests"`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: the scan detects Proteus-made mods and the managed mod" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The protection rule reads the policy

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateToolPolicyTests.cs` (new)

**Interfaces:**
- Produces: `OrganizerState.ToolPolicy` (get, current policy), `OrganizerState.SetToolPolicy(ToolPolicy policy)` (stores it and recomputes every row's protection through the one rule).
- Internal changes: `_heliosphereUnprotectOverrides` is renamed `_toolUnprotectOverrides` and `SetHeliosphereOverride` becomes `SetToolOverride`; the three places that check `row.HeliosphereManaged` before setting an override check `row.IsToolManaged`; `IsEffectivelyProtected` takes the policy and uses the rule below. `SetHeliosphereProtection(bool)` is KEPT unchanged (its existing tests still pass; the Protect tab stops calling it in Task 6).

The rule (replaces the Heliosphere clause in `IsEffectivelyProtected`; everything else in that method is unchanged):

```csharp
    // The tool clause of the single protection rule. A per-mod override (the user unticked a protected
    // tool mod) cancels it; an explicit mod protection or a protected folder, checked by the caller,
    // still beats the override. The Proteus managed mod is outside the three modes: protected unless
    // the user asked for it at the root.
    private static bool ToolProtects(
        OrganizerModRow row, ToolPolicy policy, IReadOnlySet<string> toolUnprotectOverrides)
    {
        if (toolUnprotectOverrides.Contains(row.Identifier))
            return false;

        return row.ProteusRole switch
        {
            ProteusRole.Managed => !policy.ProteusManagedAtRoot,
            ProteusRole.Made => policy.Proteus == ToolMode.Protected,
            _ => row.HeliosphereManaged && policy.Heliosphere == ToolMode.Protected,
        };
    }
```

- [ ] **Step 1: Write the failing tests** `OrganizerStateToolPolicyTests.cs` (new file; follows `OrganizerStateSortBodyTests`' style):

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateToolPolicyTests
{
    private static OrganizerModRow Row(
        string id, string path, bool hs = false, ProteusRole role = ProteusRole.None) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = path, ProposedPath = path,
        Category = ModCategory.Gear, SubCategory = "Top",
        HeliosphereManaged = hs, ProteusRole = role,
    };

    private static OrganizerState StateWith(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static bool IsProtected(OrganizerState state, string id) =>
        state.Mods.Single(m => m.Identifier == id).Protected;

    [Fact]
    public void Default_ProtectsBothToolsMods_AndTheManagedMod()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("hs", "x/hs", hs: true), Row("pt", "Proteus Upscales/pt", role: ProteusRole.Made),
            Row("pm", "Proteus", role: ProteusRole.Managed), Row("plain", "Gear/plain"));

        Assert.True(IsProtected(state, "hs"));
        Assert.True(IsProtected(state, "pt"));
        Assert.True(IsProtected(state, "pm"));
        Assert.False(IsProtected(state, "plain"));
    }

    [Theory]
    [InlineData(ToolMode.SortInside)]
    [InlineData(ToolMode.Normal)]
    public void NonProtectedModes_LeaveTheToolsModsUnprotected(ToolMode mode)
    {
        var state = StateWith(new ToolPolicy(mode, mode, false),
            Row("hs", "x/hs", hs: true), Row("pt", "Proteus Upscales/pt", role: ProteusRole.Made));

        Assert.False(IsProtected(state, "hs"));
        Assert.False(IsProtected(state, "pt"));
    }

    [Fact]
    public void TheTwoToolsHaveIndependentModes()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.Normal, false),
            Row("hs", "x/hs", hs: true), Row("pt", "p/pt", role: ProteusRole.Made));

        Assert.True(IsProtected(state, "hs"));
        Assert.False(IsProtected(state, "pt"));
    }

    [Fact]
    public void TheManagedMod_IgnoresTheThreeModes_ButFollowsTheRootOption()
    {
        var modes = new ToolPolicy(ToolMode.Normal, ToolMode.Normal, false);
        var atRoot = new ToolPolicy(ToolMode.Protected, ToolMode.Protected, true);

        Assert.True(IsProtected(StateWith(modes, Row("pm", "Proteus", role: ProteusRole.Managed)), "pm"));
        Assert.False(IsProtected(StateWith(atRoot, Row("pm", "Proteus", role: ProteusRole.Managed)), "pm"));
    }

    [Fact]
    public void ChangingThePolicy_RecomputesEveryRowAtOnce()
    {
        var state = StateWith(ToolPolicy.Default, Row("pt", "p/pt", role: ProteusRole.Made));
        Assert.True(IsProtected(state, "pt"));

        state.SetToolPolicy(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false));
        Assert.False(IsProtected(state, "pt"));

        state.SetToolPolicy(ToolPolicy.Default);
        Assert.True(IsProtected(state, "pt"));
        Assert.Equal(ToolPolicy.Default, state.ToolPolicy);
    }

    [Fact]
    public void APerModUntick_IsAnOverride_ThatARescanClears_AndTheModeSurvives()
    {
        var state = StateWith(ToolPolicy.Default, Row("pt", "p/pt", role: ProteusRole.Made));

        state.SetProtected("pt", false);
        Assert.False(IsProtected(state, "pt"));

        // an unrelated recompute (a folder rule change) must not re-protect it
        state.SetFolderProtected("somewhere/else", true);
        Assert.False(IsProtected(state, "pt"));

        state.LoadScan([Row("pt", "p/pt", role: ProteusRole.Made)], new HashSet<string>());
        Assert.True(IsProtected(state, "pt"));            // the rescan clears the override
        Assert.Equal(ToolPolicy.Default, state.ToolPolicy); // and keeps the mode
    }

    [Fact]
    public void AnExplicitProtectionOrAProtectedFolder_BeatsTheOverride_ForAProteusMod()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.Normal, false),
            Row("pt", "Tools/pt", role: ProteusRole.Made));

        state.SetProtected("pt", true);
        Assert.True(IsProtected(state, "pt"));

        state.SetProtected("pt", false);
        state.SetFolderProtected("Tools", true);
        Assert.True(IsProtected(state, "pt"));
    }

    [Fact]
    public void SetHeliosphereProtection_StillWorksForHeliosphereMods_AndIgnoresProteusMods()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("hs", "x/hs", hs: true), Row("pt", "p/pt", role: ProteusRole.Made));

        state.SetHeliosphereProtection(false);

        Assert.False(IsProtected(state, "hs"));
        Assert.True(IsProtected(state, "pt"));
    }
}
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~OrganizerStateToolPolicyTests"`. Expected: build FAIL (`SetToolPolicy`, `ToolPolicy` on the state).

- [ ] **Step 3: Implement** in `OrganizerState.cs`:
  - Add the field and members:

```csharp
    private ToolPolicy _toolPolicy = ToolPolicy.Default;

    /// <summary>The saved tool modes the protection rule and the sort consult.</summary>
    public ToolPolicy ToolPolicy => _toolPolicy;

    /// <summary>
    /// Stores the tool modes and re-derives every row's protection through the one rule. The state
    /// never reads Configuration; the plugin pushes the policy in at startup and on every change.
    /// </summary>
    public void SetToolPolicy(ToolPolicy policy)
    {
        _toolPolicy = policy;
        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedFull(row);
    }
```

  - Rename `_heliosphereUnprotectOverrides` to `_toolUnprotectOverrides`, `SetHeliosphereOverride` to `SetToolOverride`, and the local `replacementHeliosphereOverrides` to `replacementToolOverrides`; update the comment on the field to say it now covers both tools, is not persisted, and is cleared by a rescan.
  - In `SetProtected`, `SetAllProtection` and the "unprotect folder contents" loop change `row.HeliosphereManaged` to `row.IsToolManaged` before calling `SetToolOverride`.
  - `IsEffectivelyProtectedFull` passes `_toolPolicy`; `IsEffectivelyProtected` gains a `ToolPolicy policy` parameter (before the overrides parameter) and its first clause becomes `ToolProtects(row, policy, toolUnprotectOverrides)`; `ReplaceScanAtomically` passes `_toolPolicy` where it calls `IsEffectivelyProtected` (the policy is a setting, not scan state, so it is NOT replaced there). Add `ToolProtects` as given above.
  - Update the XML remarks on `IsEffectivelyProtected`: "Heliosphere still protects by default" becomes "tool-managed mods are protected by default according to their tool's mode".

- [ ] **Step 4: Run.** `--filter "FullyQualifiedName~OrganizerStateToolPolicyTests|FullyQualifiedName~OrganizerState|FullyQualifiedName~Protect"`, then the full project. Expected: PASS (the 16 existing `SetHeliosphereProtection` usages keep their meaning because the default policy is Protected for Heliosphere).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: the protection rule reads a per-tool policy" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Sort inside their folder, and the managed mod at the root

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (the private `Sort(Func<...>)`, ~line 588)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateToolSortTests.cs` (new)

**Interfaces:**
- Consumes: `ToolFolders.ApplyAnchor`, `ToolPolicy`, `ProteusRole`, `PenumbraPathSemantics.FixName`.
- Behavior: in the sort loop (unprotected rows only), after the normal path is built: if the row is the Proteus managed mod and `ProteusManagedAtRoot` is on, the proposed path is just its name (`FixName(row.Name)`) at the root; otherwise if the row's own tool is in `SortInside`, the proposed path is `ToolFolders.ApplyAnchor(row.CurrentPath, builtPath)`. Template apply and Manual assign are NOT changed.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateToolSortTests
{
    private static string Canon(string s) => s;

    private static OrganizerModRow Row(
        string id, string path, string name, string author = "A", bool hs = false,
        ProteusRole role = ProteusRole.None, ModCategory category = ModCategory.Gear) => new()
    {
        Identifier = id, Name = name, Author = author, CurrentPath = path, ProposedPath = path,
        Category = category, SubCategory = "Top", HeliosphereManaged = hs, ProteusRole = role,
    };

    private static OrganizerState StateWith(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static string PathOf(OrganizerState state, string id) =>
        state.Mods.Single(m => m.Identifier == id).ProposedPath;

    private static void SortByType(OrganizerState state) =>
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon);

    [Fact]
    public void SortInside_FilesAProteusModBeneathItsCurrentTopFolder()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Proteus Upscales/Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Proteus Upscales/Gear/Top/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_AModAlreadyInASorterFolder_IsSortedNormally()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Gear/Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Gear/Top/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_AModAtTheRoot_IsSortedNormally()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Gear/Top/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_AppliesToHeliosphereToo_UnderItsOwnMode()
    {
        var state = StateWith(new ToolPolicy(ToolMode.SortInside, ToolMode.Protected, false),
            Row("hs", "My Tools/[HS] Aeonium", "[HS] Aeonium", hs: true));

        SortByType(state);

        Assert.Equal("My Tools/Gear/Top/[HS] Aeonium", PathOf(state, "hs"));
    }

    [Fact]
    public void NormalMode_SortsToolModsLikeAnyOtherMod()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Normal, ToolMode.Normal, false),
            Row("pt", "Proteus Upscales/Cool Dress", "Cool Dress", role: ProteusRole.Made),
            Row("hs", "x/[HS] A", "[HS] A", hs: true));

        SortByType(state);

        Assert.Equal("Gear/Top/Cool Dress", PathOf(state, "pt"));
        Assert.Equal("Gear/Top/[HS] A", PathOf(state, "hs"));
    }

    [Fact]
    public void ProtectedMode_LeavesToolModsWhereTheyAre()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("pt", "Proteus Upscales/Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Proteus Upscales/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_WithGroupByCreator_DoesNotDoubleACreatorFolderTheModAlreadySitsIn()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Solona/Cool Dress", "Cool Dress", author: "Solona", role: ProteusRole.Made));

        state.Sort(SortStrategy.CreatorOnly, splitGear: false, splitNpc: false, splitHair: false, c => c);

        Assert.Equal("Solona/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_TwoModsWithTheSameNameUnderTheSameFolder_AreDisambiguated()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("a", "Proteus Upscales/Dress", "Dress", role: ProteusRole.Made),
            Row("b", "Proteus Upscales/Dress (2)", "Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.NotEqual(PathOf(state, "a"), PathOf(state, "b"));
        Assert.StartsWith("Proteus Upscales/Gear/Top/Dress", PathOf(state, "a"));
        Assert.StartsWith("Proteus Upscales/Gear/Top/Dress", PathOf(state, "b"));
    }

    [Fact]
    public void TheManagedMod_AtRoot_IsProposedAtTheRootWhateverTheStrategy()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.Protected, true),
            Row("Proteus", "Proteus Upscales/Proteus", "Proteus", author: "Proteus",
                role: ProteusRole.Managed, category: ModCategory.Others));

        SortByType(state);

        Assert.Equal("Proteus", PathOf(state, "Proteus"));
    }

    [Fact]
    public void TheManagedMod_NotAtRoot_StaysProtectedAndUnmoved()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("Proteus", "Proteus Upscales/Proteus", "Proteus", author: "Proteus",
                role: ProteusRole.Managed, category: ModCategory.Others));

        SortByType(state);

        Assert.Equal("Proteus Upscales/Proteus", PathOf(state, "Proteus"));
    }
}
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~OrganizerStateToolSortTests"`. Expected: the SortInside, root and Normal tests FAIL on wrong paths (the tests compile because Tasks 3 and 4 already added every member they use).

- [ ] **Step 3: Implement** in the private `Sort(Func<OrganizerModRow, (string?, string?)> folderSelector)`:

```csharp
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var (primary, secondary) = folderSelector(row);
            var built = BuildPath(primary, secondary, row.Name);
            row.ProposedPath = ToolAdjustedPath(row, built);
            touched.Add(row);
            count++;
        }
```

and add:

```csharp
    // The two tool-specific placements, applied AFTER the normal path is built and BEFORE collision
    // disambiguation, so the tail sees the final path. Only unprotected rows reach here.
    private string ToolAdjustedPath(OrganizerModRow row, string builtPath)
    {
        if (row.ProteusRole == ProteusRole.Managed)
            return _toolPolicy.ProteusManagedAtRoot ? PenumbraPathSemantics.FixName(row.Name) : builtPath;

        var mode = row.ProteusRole == ProteusRole.Made ? _toolPolicy.Proteus
            : row.HeliosphereManaged ? _toolPolicy.Heliosphere
            : ToolMode.Normal;

        return mode == ToolMode.SortInside
            ? ToolFolders.ApplyAnchor(row.CurrentPath, builtPath)
            : builtPath;
    }
```

- [ ] **Step 4: Run.** `--filter "FullyQualifiedName~OrganizerStateToolSortTests"`, then the full project. Expected: PASS (no existing sort test changes, because mods without a tool are untouched).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: sort tool mods inside their folder, and the Proteus managed mod at the root" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Plugin wiring, the Protect tab, help, and the dump

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/ToolModeLabels.cs`; Test: `PenumbraOrganizer.Plugin.Tests/Windows/ToolModeLabelsTests.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`, `Windows/MainWindow.ProtectTab.cs`, `Windows/MainWindow.cs` (diagnostic counts), `Windows/ProtectToggleLabels.cs` and its test (remove `Heliosphere`), `Organizer/OrganizerExportFormatter.cs` and its test, `Windows/HelpTopics.cs`, `HelpTopicUsage.cs`, `HelpTab.cs`, `Resources/help-content.json`

**Interfaces:**
- Produces: `public static class ToolModeLabels { public static IReadOnlyList<string> All { get; } /* Protected, Sort inside their folder, Treat as normal */; public static int IndexOf(ToolMode mode); public static ToolMode FromIndex(int index); }`; `Plugin.ApplyToolPolicy()` (pushes `Config.ToolPolicy` into `OrganizerState` and saves the config).
- Help topics: add `ProtectHeliosphereMode` (`protect.heliosphere-mode`), `ProtectProteusMode` (`protect.proteus-mode`), `ProtectProteusManagedRoot` (`protect.proteus-managed-root`), `ProtectProteusNote` (`protect.proteus-note`); REMOVE `ProtectToggleHeliosphere` (`protect.toggle-heliosphere`) everywhere (constant, usage list, HelpTab Protect section, json entry) and `ProtectToggleLabels.Heliosphere` with its test.

- [ ] **Step 1: Write the failing tests.**

`ToolModeLabelsTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class ToolModeLabelsTests
{
    [Fact]
    public void TheDropdownHasOneLabelPerMode_InTheOrderTheEnumDefines()
    {
        Assert.Equal(
            ["Protected", "Sort inside their folder", "Treat as normal"],
            ToolModeLabels.All);
        Assert.Equal(Enum.GetValues<ToolMode>().Length, ToolModeLabels.All.Count);
    }

    [Fact]
    public void IndexAndMode_RoundTrip_ForEveryMode()
    {
        foreach (var mode in Enum.GetValues<ToolMode>())
            Assert.Equal(mode, ToolModeLabels.FromIndex(ToolModeLabels.IndexOf(mode)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void AnOutOfRangeIndex_ReadsAsProtected(int index) =>
        Assert.Equal(ToolMode.Protected, ToolModeLabels.FromIndex(index));
}
```

Add to `OrganizerExportFormatterTests`:

```csharp
    [Fact]
    public void Format_ManagedBy_NamesTheToolOrNone()
    {
        static OrganizerModRow R(string id, bool hs, ProteusRole role) => new()
        {
            Identifier = id, Name = id, Author = "A", CurrentPath = "p/" + id, ProposedPath = "p/" + id,
            HeliosphereManaged = hs, ProteusRole = role,
        };
        var review = new ReviewResult([], new Dictionary<string, List<string>>());

        var text = OrganizerExportFormatter.Format(
            [R("h", true, ProteusRole.None), R("m", false, ProteusRole.Made),
             R("x", false, ProteusRole.Managed), R("n", false, ProteusRole.None)], review);

        Assert.Contains("ManagedBy: Heliosphere", text);
        Assert.Contains("ManagedBy: Proteus", text);
        Assert.Contains("ManagedBy: ProteusManaged", text);
        Assert.Contains("ManagedBy: (none)", text);
    }
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~ToolModeLabelsTests|FullyQualifiedName~OrganizerExportFormatterTests"`. Expected: build FAIL.

- [ ] **Step 3: Implement.**

`ToolModeLabels.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>The labels of the Protect tab's tool mode dropdowns, kept beside their index mapping.</summary>
/// <remarks>public, not internal: this repo has no InternalsVisibleTo.</remarks>
public static class ToolModeLabels
{
    public static IReadOnlyList<string> All { get; } =
        ["Protected", "Sort inside their folder", "Treat as normal"];

    public static int IndexOf(ToolMode mode) => (int)ToolPolicy.Normalize(mode);

    /// <summary>An index outside the list reads as Protected, the safe default.</summary>
    public static ToolMode FromIndex(int index) =>
        index >= 0 && index < All.Count ? (ToolMode)index : ToolMode.Protected;
}
```

`OrganizerExportFormatter.cs`, after the `HeliosphereManaged:` line:

```csharp
            sb.AppendLine($"ManagedBy: {ManagedBy(mod)}");
```

and add `private static string ManagedBy(OrganizerModRow mod) => mod.ProteusRole switch { ProteusRole.Managed => "ProteusManaged", ProteusRole.Made => "Proteus", _ => mod.HeliosphereManaged ? "Heliosphere" : "(none)" };`. In `MainWindow.cs` (~line 538) under the `Heliosphere-managed:` count add `sb.AppendLine($"Proteus-made: {mods.Count(m => m.ProteusRole == ProteusRole.Made)}");` and `sb.AppendLine($"Proteus managed mod: {mods.Count(m => m.ProteusRole == ProteusRole.Managed)}");`.

`Plugin.cs`: add

```csharp
    /// <summary>Pushes the saved tool modes into the organizer state and saves them.</summary>
    internal void ApplyToolPolicy()
    {
        OrganizerState.SetToolPolicy(Config.ToolPolicy);
        PluginInterface.SavePluginConfig(Config);
    }
```

and call `OrganizerState.SetToolPolicy(Config.ToolPolicy);` (no save needed) right after both `Config` is loaded/migrated and `OrganizerState` is created, BEFORE the first scan can publish (find them with `grep -n "OrganizerState =\|MigrateToCurrentVersion" Plugin.cs`; the policy must be set before the first `LoadScan`/`ReplaceScanAtomically`, because that is where each row's protection is first derived).

`MainWindow.ProtectTab.cs`: replace the Heliosphere toggle button (post-UX code: `var allHeliosphereProtected = ...; if (ImGui.Button($"{ProtectToggleLabels.Heliosphere(...)}##toggle-protect-heliosphere")) {...}; Help.Tooltip(HelpTopics.ProtectToggleHeliosphere);`) AND the `heliosphereMods` count text that follows it with a call `DrawToolModes();`, keeping the "Protect all"/"Unprotect all" button as it is. Add:

```csharp
    // One mode dropdown per tool, each shown only while the library has that tool's mods. The mode is
    // a saved setting (Configuration), pushed into the state; a per-mod untick below is separate.
    private void DrawToolModes()
    {
        var mods = _plugin.OrganizerState.Mods;
        var heliosphere = mods.Count(m => m.HeliosphereManaged && m.ProteusRole == ProteusRole.None);
        var proteus = mods.Count(m => m.ProteusRole == ProteusRole.Made);
        var managedExists = mods.Any(m => m.ProteusRole == ProteusRole.Managed);
        var gateOpen = CurrentGates().CanStageProposals;
        var config = _plugin.Config;

        if (heliosphere > 0)
        {
            var index = ToolModeLabels.IndexOf(config.HeliosphereMode);
            ImGui.BeginDisabled(!gateOpen);
            ImGui.SetNextItemWidth(220);
            var changed = ImGui.Combo($"Heliosphere mods ({heliosphere})##heliosphere-mode",
                ref index, [.. ToolModeLabels.All], ToolModeLabels.All.Count);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.ProtectHeliosphereMode, gateOpen ? null : ActivityGateReason);
            if (changed)
            {
                config.HeliosphereMode = ToolModeLabels.FromIndex(index);
                _plugin.ApplyToolPolicy();
            }
        }

        if (proteus > 0)
        {
            var index = ToolModeLabels.IndexOf(config.ProteusMode);
            ImGui.BeginDisabled(!gateOpen);
            ImGui.SetNextItemWidth(220);
            var changed = ImGui.Combo($"Proteus mods ({proteus})##proteus-mode",
                ref index, [.. ToolModeLabels.All], ToolModeLabels.All.Count);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.ProtectProteusMode, gateOpen ? null : ActivityGateReason);
            if (changed)
            {
                config.ProteusMode = ToolModeLabels.FromIndex(index);
                _plugin.ApplyToolPolicy();
            }
        }

        if (managedExists)
        {
            var atRoot = config.ProteusManagedAtRoot;
            ImGui.BeginDisabled(!gateOpen);
            var changed = ImGui.Checkbox("Place the Proteus managed mod at the root##proteus-managed-root", ref atRoot);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.ProtectProteusManagedRoot, gateOpen ? null : ActivityGateReason);
            if (changed)
            {
                config.ProteusManagedAtRoot = atRoot;
                _plugin.ApplyToolPolicy();
            }
        }
    }
```

In the same file's mod list loop: order tool mods first with `OrderByDescending(m => m.IsToolManaged)`, and in the protected-note branch replace the `if (mod.HeliosphereManaged) {...}` with:

```csharp
                        if (mod.IsToolManaged)
                        {
                            var proteusMod = mod.ProteusRole != ProteusRole.None;
                            ImGui.TextDisabled(proteusMod ? "(Proteus)" : "(Heliosphere)");
                            Help.Tooltip(proteusMod ? HelpTopics.ProtectProteusNote : HelpTopics.ProtectHeliosphereNote);
                        }
```

(Add `using PenumbraOrganizer.Plugin.Organizer;` if the file lacks it. `ImGui.Combo` takes the items as a `string[]`, as the existing Sort panel combos do.)

Help: in `HelpTopics.cs` add the four constants and remove `ProtectToggleHeliosphere`; mirror in `HelpTopicUsage.cs` and the Protect section of `HelpTab.cs`; in `help-content.json` remove `protect.toggle-heliosphere` and add (each `short` at most 200 characters):

```json
    {
      "id": "protect.heliosphere-mode",
      "title": "Heliosphere mods",
      "short": "Protected: never moved. Sort inside their folder: moved only beneath the folder they sit in. Treat as normal: sorted like any other mod. Saved between sessions."
    },
    {
      "id": "protect.proteus-mode",
      "title": "Proteus mods",
      "short": "Mods Proteus made. Protected: never moved. Sort inside their folder: moved only beneath the folder they sit in. Treat as normal: sorted like any other mod. Saved between sessions."
    },
    {
      "id": "protect.proteus-managed-root",
      "title": "Proteus managed mod at the root",
      "short": "Proteus's own working mod. Kept protected by default. Tick this to place it at the top level of Penumbra's mod list on the next sort; moving it does not affect Proteus."
    },
    {
      "id": "protect.proteus-note",
      "title": "Proteus mod",
      "short": "Made by Proteus. It is protected because Proteus mods are set to Protected; change that in the Proteus mods dropdown above."
    },
```

Remove `ProtectToggleLabels.Heliosphere` from `ProtectToggleLabels.cs` and its test in `ProtectToggleLabelsTests.cs` (the `All` label and its test stay).

- [ ] **Step 4: Build and run.** Build (0 errors, 0 warnings); `--filter "FullyQualifiedName~ToolModeLabelsTests|FullyQualifiedName~OrganizerExportFormatterTests|FullyQualifiedName~ProtectToggleLabelsTests|FullyQualifiedName~Help"`; then the full project. Expected: PASS. The drawing is verified in game.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: Protect tab mode dropdowns for Heliosphere and Proteus, the managed-mod root option, and the dump lines" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: In-game checklist and the full run

**Files:**
- Create: `docs/testing/2026-11-05-tool-managed-mods-checklist.md`

- [ ] **Step 1: Write the checklist** (plain ASCII):

```markdown
# Manual check: tool-managed mods (Heliosphere and Proteus modes)

Build, load in game (dev plugin path: the MAIN checkout's bin\Debug), scan. You need at least one
Proteus-made mod, or hand-edit a test mod's meta.json to `"Author": "Proteus"` (exact case) and
rescan. The managed mod needs a mod in a directory named `Proteus` with that author.

## Defaults and detection
- [ ] On a fresh config both tools are Protected: Review shows no moves for their mods after Sort.
- [ ] The Protect tab shows "Heliosphere mods (N)" and "Proteus mods (N)" dropdowns only while that
      tool has mods, each defaulting to Protected, and a "(Proteus)" / "(Heliosphere)" note beside a
      protected tool mod.
- [ ] A mod whose author is `proteus` (lower case) is NOT treated as a Proteus mod.
- [ ] The export dump has a `ManagedBy:` line per mod and the diagnostic header counts each tool.

## Modes
- [ ] Proteus -> "Sort inside their folder", Sort: a mod in `Proteus Upscales` proposes
      `Proteus Upscales/Gear/.../Name`; a Proteus mod already in `Gear` proposes the normal path.
- [ ] Proteus -> "Treat as normal": Proteus mods sort like any other mod.
- [ ] Restart the game: the chosen modes persist. Rescan: the modes persist.
- [ ] Untick one protected Proteus mod: it is unprotected until the next scan, which protects it again
      (the mode is unchanged).
- [ ] Heliosphere -> "Sort inside their folder" with a HS mod in a folder of its own: it stays under
      that folder. HS mods already in Gear/Body/NPC sort normally.

## The managed mod
- [ ] The `Proteus` managed mod is protected whichever mode Proteus mods use.
- [ ] Tick "Place the Proteus managed mod at the root", Sort, Review: it proposes the top level
      (just `Proteus`). Untick it: protected again.
- [ ] After applying that move, Proteus still works (overlays still apply).

## Heliosphere and Proteus keep working
- [ ] After sorting Proteus mods inside their folder, make a new refit in Proteus: it still files into
      its usual folder and everything keeps working.
- [ ] After sorting an HS mod inside a folder, run an HS update on it: the update works and the mod
      keeps the path it was filed at.
```

- [ ] **Step 2: Full verification.** `dotnet build PenumbraOrganizer.Plugin.sln -c Debug` (0 errors), the full test project (count above the Task 1 baseline, nothing skipped), and `git grep -n "ProtectToggleHeliosphere\|toggle-heliosphere" -- ':!docs'` returns nothing.

- [ ] **Step 3: Commit**

```bash
git add docs/testing/2026-11-05-tool-managed-mods-checklist.md
git commit -m "docs: in-game checklist for tool-managed mods" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-Review (against the spec)

- Detection: author exactly `Proteus` (ordinal), the managed mod by directory plus author, imports not detected, no disk read: Tasks 1 and 3.
- Three modes per tool, Protected default, saved, an unknown stored value reads as Protected: Tasks 1 and 2.
- One protection rule reads the policy; the override set covers both tools, is not persisted and a rescan clears it while the mode survives; explicit protection and protected folders still win: Task 4.
- Sort inside: anchor is the current top folder, sorter folders and already-prefixed paths and root mods are sorted normally, collision disambiguation sees the final path, Group by Creator handled, Heliosphere uses the same mechanism: Tasks 1 and 5.
- The managed mod: protected by default, outside the three modes, root option places it at the top level: Tasks 4 and 5.
- Heliosphere's mode persists across rescans (the contract change): Tasks 2 and 4; the old `SetHeliosphereProtection` is kept so its tests hold, and the UI stops calling it in Task 6.
- Protect tab dropdowns with counts, hidden while a tool has no mods, greyed while another operation runs, managed-mod checkbox, notes, ordering, help entries, dump line, diagnostic counts: Task 6.
- Template apply and Manual assign are unchanged: Task 5 touches only the private sort loop. Documented as out of scope in the spec.
- Prerequisite check against the UX pass, branch, baseline: Task 1 Step 0.
- Type consistency: `ToolPolicy(Heliosphere, Proteus, ProteusManagedAtRoot)` is used with those property names in Tasks 2, 4, 5 and 6; `ProteusRole` and `IsToolManaged` come from Task 3 and are used in Tasks 4 to 6; `ToolFolders.ApplyAnchor(currentPath, normalPath)` is called with that argument order in Task 5.
- Known limits: ImGui drawing (Task 6) is verified by compile and in game; the config attribute choice in Task 2 and the Plugin startup ordering in Task 6 depend on code the implementer must read, and both steps say what to look for. Your library has no Proteus mods, so detection and sort-inside are covered by fixtures until a real one exists.
