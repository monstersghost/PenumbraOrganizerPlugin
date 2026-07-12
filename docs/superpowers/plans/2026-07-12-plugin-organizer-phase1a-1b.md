# Plugin Organizer Phase 1a/1b Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a live, in-game organize pipeline in `PenumbraOrganizer.Plugin` — Scan, Protect (incl. Heliosphere auto-protect), Start Manually + By Creator sort strategies, Review Changes — with Apply left disabled. No writes to Penumbra anywhere in this plan.

**Architecture:** Pure organizing logic (`OrganizerState`, `Configuration`, `HeliosphereDetector`) is separated from IPC-fetching and ImGui rendering so it's unit-testable without a running game. `PenumbraOrganizer.Plugin.Tests` (new xUnit project) covers the pure logic; IPC/UI code is verified manually in-game per the existing MVP's convention. Protection persists via Dalamud's native `IPluginConfiguration`. `ModCategory` and `CreatorCanonicalizer` are shared with the standalone app via MSBuild linked source files (same source compiled into both binaries, zero runtime coupling).

**Tech Stack:** C#, Dalamud.NET.Sdk 15.0.0 (net10.0-windows7.0), Penumbra.Api 5.15.1, Dalamud.Bindings.ImGui, xUnit 2.5.3.

## Global Constraints

- No call to `SetModPath` or any other Penumbra write IPC anywhere in this plan (per spec's Non-goals).
- No shared binary/package dependency between `PenumbraOrganizer.Plugin` and `PenumbraOrganizer.Core` — only MSBuild linked source files for the two artifacts named in the spec (`ModCategory` enum, `CreatorCanonicalizer` + `ICreatorCanonicalizer`).
- Protection state stored via `IPluginConfiguration`, fully separate from the app's `%LocalAppData%\PenumbraOrganizer\` data.
- Both repos (`C:\Repo\PenumbraOrganizer` and `C:\Repo\PenumbraOrganizer.Plugin`) remain sibling checkouts — linked file paths are relative across them.
- Phase 1c (By mod type / `GetChangedItems` parsing) is explicitly out of scope for this plan — see Task 13, which only runs the format-verification spike and stops there.

---

## Task 1: Extract `ModCategory` and `ICreatorCanonicalizer` into their own files in the app repo

The shared taxonomy enum currently lives in `ModClassificationModels.cs` alongside path-oriented
records that aren't reusable by the plugin. `ICreatorCanonicalizer` currently lives inside
`Interfaces/Services.cs`, a file packed with unrelated service interfaces that depend on
`PenumbraOrganizer.Core.Models` types — linking that whole file would drag in a large,
plugin-inappropriate dependency graph. MSBuild `<Compile Include>` links at file granularity, so
both need their own file before they can be linked cleanly.

**Files:**
- Create: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModCategory.cs`
- Modify: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModClassificationModels.cs`
- Create: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\ICreatorCanonicalizer.cs`
- Modify: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\Services.cs`

**Interfaces:**
- Produces: `PenumbraOrganizer.Core.Classification.ModCategory` and
  `PenumbraOrganizer.Core.Interfaces.ICreatorCanonicalizer` (unchanged values/namespace/members,
  moved files only).

- [ ] **Step 1: Extract the enum**

Create `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModCategory.cs`:

```csharp
namespace PenumbraOrganizer.Core.Classification;

public enum ModCategory
{
    Gear = 1,
    Weapon = 2,
    Face = 3,
    Hair = 4,
    Body = 5,
    Skin = 6,
    NPC = 7,
    Minion = 8,
    Mount = 9,
    Pet = 10,
    Ornament = 11,
    Furniture = 12,
    VFX = 13,
    Sound = 14,
    Animation = 15,
    Others = 16,
}
```

- [ ] **Step 2: Remove the enum from the old file**

In `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModClassificationModels.cs`,
delete the `public enum ModCategory { ... }` block (lines 3-21 as of this plan's writing). Leave
`CanonicalTargetKind`, `CanonicalGameTarget`, and `ModTargetClassification` untouched.

- [ ] **Step 3: Extract the interface**

Create `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\ICreatorCanonicalizer.cs`:

```csharp
namespace PenumbraOrganizer.Core.Interfaces;

public interface ICreatorCanonicalizer
{
    string Canonicalize(string creatorName);
}
```

- [ ] **Step 4: Remove the interface from `Services.cs`**

In `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\Services.cs`, delete lines 68-71
(the `public interface ICreatorCanonicalizer { string Canonicalize(string creatorName); }` block).
Leave every other interface in the file untouched.

- [ ] **Step 5: Verify the app still builds and its tests still pass**

Run: `cd C:\Repo\PenumbraOrganizer && dotnet build PenumbraOrganizer.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

Run: `cd C:\Repo\PenumbraOrganizer && dotnet test PenumbraOrganizer.sln`
Expected: all existing tests still pass (same pass count as before this change — this is a pure
file move, no behavior change).

- [ ] **Step 6: Commit in the app repo**

```bash
cd C:\Repo\PenumbraOrganizer
git add PenumbraOrganizer.Core/Classification/ModCategory.cs PenumbraOrganizer.Core/Classification/ModClassificationModels.cs PenumbraOrganizer.Core/Interfaces/ICreatorCanonicalizer.cs PenumbraOrganizer.Core/Interfaces/Services.cs
git commit -m "refactor: extract ModCategory and ICreatorCanonicalizer into their own files

Enables the Dalamud plugin repo to link these directly via MSBuild
Compile Include without also pulling in path-oriented classification
records or the dependency-heavy Services.cs interface bundle."
```

---

## Task 2: Create the plugin test project

**Files:**
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\PenumbraOrganizer.Plugin.Tests.csproj`
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\SmokeTests.cs`
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.sln`

**Interfaces:**
- Consumes: `PenumbraOrganizer.Plugin.csproj` (project reference).
- Produces: a working `dotnet test` command for every later task in this plan.

- [ ] **Step 1: Create the test project file**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\PenumbraOrganizer.Plugin.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows7.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write a smoke test**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\SmokeTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProjectRuns()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 3: Add the test project to the solution**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet sln PenumbraOrganizer.Plugin.sln add PenumbraOrganizer.Plugin.Tests\PenumbraOrganizer.Plugin.Tests.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 4: Run the smoke test**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin.Tests PenumbraOrganizer.Plugin.sln
git commit -m "test: add PenumbraOrganizer.Plugin.Tests project"
```

---

## Task 3: Link `ModCategory` and `CreatorCanonicalizer` from the app repo

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.csproj`
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\LinkedFilesTests.cs`

**Interfaces:**
- Produces: `PenumbraOrganizer.Core.Classification.ModCategory` and
  `PenumbraOrganizer.Core.Services.CreatorCanonicalizer` (implementing
  `PenumbraOrganizer.Core.Interfaces.ICreatorCanonicalizer`, method
  `string Canonicalize(string creatorName)`), both usable directly inside the plugin project.

- [ ] **Step 1: Write a failing test that exercises the linked types**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\LinkedFilesTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Services;

namespace PenumbraOrganizer.Plugin.Tests;

public class LinkedFilesTests
{
    [Fact]
    public void ModCategory_HasExpectedValue()
    {
        Assert.Equal(4, (int)ModCategory.Hair);
    }

    [Fact]
    public void CreatorCanonicalizer_MergesKnownAlias()
    {
        var canonicalizer = new CreatorCanonicalizer();
        Assert.Equal("Enni", canonicalizer.Canonicalize("enni"));
    }
}
```

- [ ] **Step 2: Run it to confirm it fails to compile (types not yet linked)**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build error, `CS0246: The type or namespace name 'ModCategory' could not be found` (or
equivalent for `CreatorCanonicalizer`).

- [ ] **Step 3: Link the source files into the plugin project**

In `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.csproj`, add
a new `ItemGroup` after the existing `PackageReference` group:

```xml
  <ItemGroup>
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModCategory.cs" Link="Linked\ModCategory.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\ICreatorCanonicalizer.cs" Link="Linked\ICreatorCanonicalizer.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Services\CreatorCanonicalizer.cs" Link="Linked\CreatorCanonicalizer.cs" />
  </ItemGroup>
```

(`ICreatorCanonicalizer.cs` is the file extracted in Task 1, Step 3 — it must run before this task.)

- [ ] **Step 4: Run the tests again to confirm they pass**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin.Tests/LinkedFilesTests.cs
git commit -m "build: link ModCategory and CreatorCanonicalizer from the app repo

Same source compiled into both binaries via MSBuild Compile Include —
zero runtime/build coupling, zero drift on these two shared artifacts."
```

---

## Task 4: `HeliosphereDetector`

**Files:**
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\HeliosphereDetector.cs`
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\HeliosphereDetectorTests.cs`

**Interfaces:**
- Produces: `PenumbraOrganizer.Plugin.Organizer.HeliosphereDetector.IsHeliosphereManaged(string directoryName, DirectoryInfo modPath) -> bool`

- [ ] **Step 1: Write the failing tests**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\HeliosphereDetectorTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class HeliosphereDetectorTests
{
    [Fact]
    public void DirectoryPrefix_IsDetected()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.True(HeliosphereDetector.IsHeliosphereManaged("hs-Nightingale-1.0", tempDir));
    }

    [Fact]
    public void MetaFile_IsDetected()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        tempDir.Create();
        File.WriteAllText(Path.Combine(tempDir.FullName, "heliosphere.json"), "{}");

        try
        {
            Assert.True(HeliosphereDetector.IsHeliosphereManaged("SomeOtherMod", tempDir));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void NeitherSignal_ReturnsFalse()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.False(HeliosphereDetector.IsHeliosphereManaged("RegularMod", tempDir));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `CS0246: The type or namespace name 'HeliosphereDetector' could not be found`

- [ ] **Step 3: Implement**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\HeliosphereDetector.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

public static class HeliosphereDetector
{
    private const string DirectoryPrefix = "hs-";
    private const string MetaFileName = "heliosphere.json";

    public static bool IsHeliosphereManaged(string directoryName, DirectoryInfo modPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryName)
            && directoryName.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        return modPath.Exists && File.Exists(Path.Combine(modPath.FullName, MetaFileName));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 6, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Organizer/HeliosphereDetector.cs PenumbraOrganizer.Plugin.Tests/Organizer/HeliosphereDetectorTests.cs
git commit -m "feat: add HeliosphereDetector"
```

---

## Task 5: `Configuration` (protection persistence)

**Files:**
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Configuration.cs`
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\ConfigurationTests.cs`

**Interfaces:**
- Produces: `PenumbraOrganizer.Plugin.Configuration` implementing `Dalamud.Configuration.IPluginConfiguration`
  (`int Version { get; set; }`), plus `HashSet<string> ProtectedModIdentifiers { get; set; }`.

- [ ] **Step 1: Write the failing test**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\ConfigurationTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests;

public class ConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_HasVersionOneAndEmptyProtectedSet()
    {
        var config = new Configuration();

        Assert.Equal(1, config.Version);
        Assert.Empty(config.ProtectedModIdentifiers);
    }

    [Fact]
    public void ProtectedModIdentifiers_IsMutable()
    {
        var config = new Configuration();

        config.ProtectedModIdentifiers.Add("hs-Nightingale-1.0");

        Assert.Contains("hs-Nightingale-1.0", config.ProtectedModIdentifiers);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `CS0246: The type or namespace name 'Configuration' could not be found`

- [ ] **Step 3: Implement**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Configuration.cs`:

```csharp
using Dalamud.Configuration;

namespace PenumbraOrganizer.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public HashSet<string> ProtectedModIdentifiers { get; set; } = [];
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 8, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Configuration.cs PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs
git commit -m "feat: add plugin Configuration for protection persistence"
```

---

## Task 6: `OrganizerModRow` and `OrganizerState.LoadScan`

**Files:**
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerModRow.cs`
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerState.cs`
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\OrganizerStateTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (this is the core data model).
- Produces:
  - `OrganizerModRow` — mutable class: `Identifier`, `Name`, `Author`, `CurrentPath` (all `required
    string`, `init`), `ProposedPath` (`string`, settable), `Protected` (`bool`, settable),
    `HeliosphereManaged` (`bool`, `init`).
  - `OrganizerState.Mods -> IReadOnlyList<OrganizerModRow>` (sorted by `Name`, ordinal
    case-insensitive).
  - `OrganizerState.LoadScan(IEnumerable<OrganizerModRow> scanned, IReadOnlySet<string> previouslyProtected) -> void`

- [ ] **Step 1: Write the failing tests**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\OrganizerStateTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateTests
{
    private static OrganizerModRow MakeRow(string id, string name, bool heliosphere = false) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = $"Unsorted/{name}",
        ProposedPath = $"Unsorted/{name}",
        HeliosphereManaged = heliosphere,
    };

    [Fact]
    public void LoadScan_SortsModsByName()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("b", "Zebra"), MakeRow("a", "Apple")], new HashSet<string>());

        Assert.Equal(["Apple", "Zebra"], state.Mods.Select(m => m.Name));
    }

    [Fact]
    public void LoadScan_AppliesPreviouslyProtectedFlag()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_AutoProtectsHeliosphereMods()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("a", "Apple", heliosphere: true)], new HashSet<string>());

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_ResetsProposedPathToCurrentPath()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Apple");
        row.ProposedPath = "SomewhereElse";

        state.LoadScan([row], new HashSet<string>());

        Assert.Equal(state.Mods.Single().CurrentPath, state.Mods.Single().ProposedPath);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `CS0246: The type or namespace name 'OrganizerModRow' could not be found`

- [ ] **Step 3: Implement `OrganizerModRow`**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerModRow.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerModRow
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string CurrentPath { get; init; }
    public required string ProposedPath { get; set; }
    public bool Protected { get; set; }
    public bool HeliosphereManaged { get; init; }
}
```

- [ ] **Step 4: Implement `OrganizerState.LoadScan`**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerState.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerState
{
    private readonly Dictionary<string, OrganizerModRow> _mods = new();

    public IReadOnlyList<OrganizerModRow> Mods =>
        _mods.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void LoadScan(IEnumerable<OrganizerModRow> scanned, IReadOnlySet<string> previouslyProtected)
    {
        _mods.Clear();
        foreach (var row in scanned)
        {
            row.Protected = row.HeliosphereManaged || previouslyProtected.Contains(row.Identifier);
            row.ProposedPath = row.CurrentPath;
            _mods[row.Identifier] = row;
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 12, Skipped: 0`

- [ ] **Step 6: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add OrganizerModRow and OrganizerState.LoadScan"
```

---

## Task 7: Protect / Unprotect + Heliosphere bulk toggle

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerState.cs`
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\OrganizerStateTests.cs`

**Interfaces:**
- Consumes: `OrganizerState.LoadScan` (Task 6), `OrganizerModRow.Protected`/`HeliosphereManaged`.
- Produces:
  - `OrganizerState.SetProtected(string identifier, bool value) -> void`
  - `OrganizerState.SetHeliosphereProtection(bool value) -> void`

- [ ] **Step 1: Add failing tests**

Append to `OrganizerStateTests.cs`:

```csharp
    [Fact]
    public void SetProtected_TogglesFlagForMatchingMod()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        state.SetProtected("a", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetProtected_UnknownIdentifier_DoesNothing()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        state.SetProtected("does-not-exist", true);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetHeliosphereProtection_OnlyAffectsHeliosphereMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Apple", heliosphere: true), MakeRow("b", "Banana")],
            new HashSet<string>());

        state.SetHeliosphereProtection(false);

        Assert.False(state.Mods.Single(m => m.Identifier == "a").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "b").Protected);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `CS1061: 'OrganizerState' does not contain a definition for 'SetProtected'`

- [ ] **Step 3: Implement**

Add to `OrganizerState.cs`, inside the class:

```csharp
    public void SetProtected(string identifier, bool value)
    {
        if (_mods.TryGetValue(identifier, out var row))
            row.Protected = value;
    }

    public void SetHeliosphereProtection(bool value)
    {
        foreach (var row in _mods.Values.Where(m => m.HeliosphereManaged))
            row.Protected = value;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 15, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add protect/unprotect and Heliosphere bulk toggle to OrganizerState"
```

---

## Task 8: Start Manually assignment

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerState.cs`
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\OrganizerStateTests.cs`

**Interfaces:**
- Produces: `OrganizerState.AssignManual(string identifier, string proposedPath) -> bool` (returns
  `false` and makes no change if the mod is unknown or protected, `true` on success).

- [ ] **Step 1: Add failing tests**

Append to `OrganizerStateTests.cs`:

```csharp
    [Fact]
    public void AssignManual_SetsProposedPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var result = state.AssignManual("a", "MyFolder/Apple");

        Assert.True(result);
        Assert.Equal("MyFolder/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void AssignManual_ProtectedMod_IsRejected()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        var result = state.AssignManual("a", "MyFolder/Apple");

        Assert.False(result);
        Assert.Equal("Unsorted/Apple", state.Mods.Single().ProposedPath);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `CS1061: 'OrganizerState' does not contain a definition for 'AssignManual'`

- [ ] **Step 3: Implement**

Add to `OrganizerState.cs`, inside the class:

```csharp
    public bool AssignManual(string identifier, string proposedPath)
    {
        if (!_mods.TryGetValue(identifier, out var row) || row.Protected)
            return false;

        row.ProposedPath = proposedPath;
        return true;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 17, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add Start Manually assignment to OrganizerState"
```

---

## Task 9: By Creator sort strategy

The proposed path is the full destination (folder + the mod's own display leaf), matching the
app's authoritative model (`sort_order.json` value = folder + display leaf) — not just the folder
name, otherwise every mod by the same author would collide on an identical path.

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerState.cs`
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\OrganizerStateTests.cs`

**Interfaces:**
- Consumes: `PenumbraOrganizer.Core.Services.CreatorCanonicalizer.Canonicalize(string) -> string`
  (linked in Task 3).
- Produces: `OrganizerState.SortByCreator(Func<string, string> canonicalizeCreator) -> int`
  (returns the count of mods reassigned; skips protected mods).

- [ ] **Step 1: Add failing tests**

Append to `OrganizerStateTests.cs`:

```csharp
    [Fact]
    public void SortByCreator_BuildsFolderPlusLeafPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var count = state.SortByCreator(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreator_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        var count = state.SortByCreator(name => name.ToUpperInvariant());

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Apple", state.Mods.Single().ProposedPath);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `CS1061: 'OrganizerState' does not contain a definition for 'SortByCreator'`

- [ ] **Step 3: Implement**

Add to `OrganizerState.cs`, inside the class:

```csharp
    public int SortByCreator(Func<string, string> canonicalizeCreator)
    {
        var count = 0;
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var folder = canonicalizeCreator(row.Author);
            row.ProposedPath = string.IsNullOrEmpty(folder) ? row.Name : $"{folder}/{row.Name}";
            count++;
        }

        return count;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 19, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add By Creator sort strategy to OrganizerState"
```

---

## Task 10: Review Changes validation

Collision here means two *different* mods (different `Identifier`) ending up with the exact same
`ProposedPath` string — a genuine name clash. Mods sharing a folder with different leaves is normal
and not flagged.

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Organizer\OrganizerState.cs`
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\Organizer\OrganizerStateTests.cs`

**Interfaces:**
- Produces:
  - `ReviewResult` record: `ProtectedViolations` (`IReadOnlyList<string>`, mod identifiers),
    `PathCollisions` (`IReadOnlyDictionary<string, List<string>>`, proposed path → colliding mod
    identifiers), `HasIssues` (`bool`).
  - `OrganizerState.Validate() -> ReviewResult`

- [ ] **Step 1: Add failing tests**

Append to `OrganizerStateTests.cs`:

```csharp
    [Fact]
    public void Validate_NoChanges_HasNoIssues()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var result = state.Validate();

        Assert.False(result.HasIssues);
    }

    [Fact]
    public void Validate_ProtectedModWithChangedPath_IsFlagged()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });
        // Bypass AssignManual's own protection check to exercise Validate in isolation.
        state.Mods.Single().ProposedPath = "SomewhereElse";

        var result = state.Validate();

        Assert.Contains("a", result.ProtectedViolations);
    }

    [Fact]
    public void Validate_TwoModsWithSameProposedPath_IsFlaggedAsCollision()
    {
        var state = new OrganizerState();
        var apple = MakeRow("a", "Apple");
        var banana = MakeRow("b", "Banana");
        state.LoadScan([apple, banana], new HashSet<string>());
        state.AssignManual("a", "Shared/Same");
        state.AssignManual("b", "Shared/Same");

        var result = state.Validate();

        Assert.True(result.PathCollisions.ContainsKey("Shared/Same"));
        Assert.Equal(2, result.PathCollisions["Shared/Same"].Count);
    }

    [Fact]
    public void Validate_ModsInSameFolderDifferentLeaf_IsNotACollision()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana")], new HashSet<string>());

        state.SortByCreator(name => name);

        Assert.False(state.Validate().HasIssues);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `CS1061: 'OrganizerState' does not contain a definition for 'Validate'`

- [ ] **Step 3: Implement**

Add to the bottom of `OrganizerState.cs`, inside the class, then the record after the class closes:

```csharp
    public ReviewResult Validate()
    {
        var protectedViolations = _mods.Values
            .Where(m => m.Protected && m.ProposedPath != m.CurrentPath)
            .Select(m => m.Identifier)
            .ToList();

        var collisions = _mods.Values
            .GroupBy(m => m.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Identifier).ToList());

        return new ReviewResult(protectedViolations, collisions);
    }
}

public sealed record ReviewResult(
    IReadOnlyList<string> ProtectedViolations,
    IReadOnlyDictionary<string, List<string>> PathCollisions)
{
    public bool HasIssues => ProtectedViolations.Count > 0 || PathCollisions.Count > 0;
}
```

(Note the extra closing brace before `public sealed record ReviewResult` — it closes the
`OrganizerState` class.)

- [ ] **Step 4: Run to verify it passes**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 23, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add Review Changes validation to OrganizerState"
```

---

## Task 11: Wire live IPC scan into `Plugin.cs`

This is the first task touching real Dalamud/Penumbra IPC types — not unit-testable without a
running game. Verify by build only; manual in-game verification happens in Task 14.

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Plugin.cs`

**Interfaces:**
- Consumes: `OrganizerState.LoadScan` (Task 6), `HeliosphereDetector.IsHeliosphereManaged` (Task 4),
  `Configuration.ProtectedModIdentifiers` (Task 5).
- Produces: `Plugin.OrganizerState` (public field, `Organizer.OrganizerState`), `Plugin.RunScan() -> void`.

- [ ] **Step 1: Replace the raw IPC fields with the adapter-based scan**

In `Plugin.cs`, replace:

```csharp
    internal readonly GetModList GetModListIpc;
    internal readonly GetModPath GetModPathIpc;
```

with:

```csharp
    internal readonly Penumbra.Api.IpcSubscribers.GetModListAdapter GetModListAdapterIpc;
    public readonly Organizer.OrganizerState OrganizerState = new();
    internal Configuration Config = null!;
```

- [ ] **Step 2: Update the constructor**

Replace:

```csharp
        GetModListIpc = new GetModList(PluginInterface);
        GetModPathIpc = new GetModPath(PluginInterface);
```

with:

```csharp
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GetModListAdapterIpc = new Penumbra.Api.IpcSubscribers.GetModListAdapter(PluginInterface);
```

- [ ] **Step 3: Add the scan method**

Add to the `Plugin` class:

```csharp
    public void RunScan()
    {
        using var modList = GetModListAdapterIpc.Invoke();

        var rows = modList.Select(mod => new Organizer.OrganizerModRow
        {
            Identifier = mod.Identifier,
            Name = mod.Name,
            Author = mod.Author,
            CurrentPath = mod.FullPath,
            ProposedPath = mod.FullPath,
            HeliosphereManaged = Organizer.HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath),
        }).ToList();

        OrganizerState.LoadScan(rows, Config.ProtectedModIdentifiers);
        SaveProtectionState();
    }

    internal void SaveProtectionState()
    {
        Config.ProtectedModIdentifiers = OrganizerState.Mods
            .Where(m => m.Protected)
            .Select(m => m.Identifier)
            .ToHashSet();
        PluginInterface.SavePluginConfig(Config);
    }
```

Add `using System.Linq;` at the top of the file if not already present (it already is, via
`GetModListIpc`'s removal — verify after editing).

- [ ] **Step 4: Remove the now-unused `GetModPath` usage in `MainWindow.RefreshMods`**

This will be replaced entirely in Task 13 when `MainWindow` is rewired to `OrganizerState`. For
this task, only confirm the project still builds — `MainWindow.cs` referencing the now-removed
`_plugin.GetModListIpc`/`GetModPathIpc` fields will cause a build error until Task 13. Temporarily
comment out the body of `MainWindow.RefreshMods()` (replace with `_lastError = "Scan moved to the
Sort tab — see Task 13.";`) so the build succeeds; Task 13 removes this window entirely.

- [ ] **Step 5: Build to verify**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: wire live scan (GetModListAdapter) into Plugin, replacing per-mod GetModPath calls

GetModListAdapter returns Author and FullPath (current virtual path) for
every mod in one call, superseding the MVP's per-mod GetModPath loop."
```

---

## Task 12: `PathTreeView` shared widget

Not unit-testable (ImGui requires a rendering context). Build-verify only; manual in-game
verification happens in Task 14.

**Files:**
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\PathTreeView.cs`

**Interfaces:**
- Consumes: `Organizer.OrganizerModRow` (Task 6).
- Produces: `PathTreeView.Draw(IReadOnlyList<OrganizerModRow> mods, bool showProposedColumn, Action<OrganizerModRow>? onRowSelected = null) -> void`

- [ ] **Step 1: Implement**

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\PathTreeView.cs`:

```csharp
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

public static class PathTreeView
{
    public static void Draw(IReadOnlyList<OrganizerModRow> mods, bool showProposedColumn, Action<OrganizerModRow>? onRowSelected = null)
    {
        var columnCount = showProposedColumn ? 4 : 3;
        using var table = ImRaii.Table("PathTreeView", columnCount,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new System.Numerics.Vector2(0, 300));
        if (!table)
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Author");
        ImGui.TableSetupColumn("Current Path");
        if (showProposedColumn)
            ImGui.TableSetupColumn("Proposed Path");
        ImGui.TableHeadersRow();

        foreach (var mod in mods)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (mod.Protected)
                ImGui.TextColored(ImGuiColors.DalamudYellow, mod.Name);
            else if (ImGui.Selectable(mod.Name))
                onRowSelected?.Invoke(mod);
            else
                ImGui.TextUnformatted(mod.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.Author);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.CurrentPath);

            if (showProposedColumn)
            {
                ImGui.TableNextColumn();
                var changed = mod.ProposedPath != mod.CurrentPath;
                if (changed)
                    ImGui.TextColored(ImGuiColors.HealerGreen, mod.ProposedPath);
                else
                    ImGui.TextUnformatted(mod.ProposedPath);
            }
        }
    }
}
```

Note: this draws both a "Selectable" and a fallback `TextUnformatted` on the same cell in the
non-protected branch above, which is a bug — `ImGui.Selectable` already draws the text, so the
`else` branch is dead code for non-protected rows. Fix before Step 2: replace the three-way
if/else-if/else with:

```csharp
            ImGui.TableNextColumn();
            if (mod.Protected)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, mod.Name);
            }
            else if (ImGui.Selectable(mod.Name))
            {
                onRowSelected?.Invoke(mod);
            }
```

- [ ] **Step 2: Build to verify**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Windows/PathTreeView.cs
git commit -m "feat: add shared PathTreeView widget for Scan and Review Changes"
```

---

## Task 13: Rewire `MainWindow` into Scan / Sort / Protect / Review tabs

Not unit-testable. Build-verify only; manual in-game verification happens in Task 14.

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.OrganizerState`, `Plugin.RunScan()`, `Plugin.SaveProtectionState()` (Task 11),
  `PathTreeView.Draw` (Task 12), `PenumbraOrganizer.Core.Services.CreatorCanonicalizer` (Task 3).

- [ ] **Step 1: Replace `MainWindow.cs` entirely**

Replace the full contents of `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\MainWindow.cs`:

```csharp
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Core.Services;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const int MaxEventLogLines = 200;

    private readonly Plugin _plugin;
    private readonly CreatorCanonicalizer _creatorCanonicalizer = new();
    private readonly List<string> _eventLog = [];
    private string? _lastError;
    private string _manualFolderInput = string.Empty;
    private string? _selectedManualModIdentifier;

    public MainWindow(Plugin plugin)
        : base("Penumbra Organizer###PenumbraOrganizerPluginMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        _plugin = plugin;
    }

    public void Dispose()
    {
    }

    internal void LogEvent(string message)
    {
        _eventLog.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
        if (_eventLog.Count > MaxEventLogLines)
            _eventLog.RemoveRange(MaxEventLogLines, _eventLog.Count - MaxEventLogLines);
    }

    public override void Draw()
    {
        if (_lastError != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, _lastError);

        using var tabBar = ImRaii.TabBar("MainTabs");
        if (!tabBar)
            return;

        DrawScanTab();
        DrawProtectTab();
        DrawSortTab();
        DrawReviewTab();
    }

    private void DrawScanTab()
    {
        using var tab = ImRaii.TabItem("Scan");
        if (!tab)
            return;

        if (ImGui.Button("Refresh mod list"))
            RunScan();

        ImGui.SameLine();
        ImGui.Text($"{_plugin.OrganizerState.Mods.Count} mods loaded");
        ImGui.Spacing();

        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: false);

        ImGui.Spacing();
        ImGui.Text("Live events (ModAdded / ModDeleted / ModMoved):");
        using (var child = ImRaii.Child("EventLog", new Vector2(0, 150), border: true))
        {
            if (child)
                foreach (var line in _eventLog)
                    ImGui.TextUnformatted(line);
        }
    }

    private void DrawProtectTab()
    {
        using var tab = ImRaii.TabItem("Protect");
        if (!tab)
            return;

        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            _plugin.SaveProtectionState();
        }

        ImGui.Spacing();

        foreach (var mod in _plugin.OrganizerState.Mods)
        {
            var isProtected = mod.Protected;
            if (ImGui.Checkbox($"{mod.Name}##protect-{mod.Identifier}", ref isProtected))
            {
                _plugin.OrganizerState.SetProtected(mod.Identifier, isProtected);
                _plugin.SaveProtectionState();
            }
        }
    }

    private void DrawSortTab()
    {
        using var tab = ImRaii.TabItem("Sort");
        if (!tab)
            return;

        if (ImGui.Button("By Creator"))
            _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: pick a mod below, type a folder, click Assign.");

        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        foreach (var mod in _plugin.OrganizerState.Mods.Where(m => !m.Protected))
        {
            if (ImGui.RadioButton(mod.Name, _selectedManualModIdentifier == mod.Identifier))
                _selectedManualModIdentifier = mod.Identifier;
        }

        if (ImGui.Button("Assign") && _selectedManualModIdentifier is not null && _manualFolderInput.Length > 0)
        {
            var mod = _plugin.OrganizerState.Mods.First(m => m.Identifier == _selectedManualModIdentifier);
            _plugin.OrganizerState.AssignManual(_selectedManualModIdentifier, $"{_manualFolderInput}/{mod.Name}");
        }
    }

    private void DrawReviewTab()
    {
        using var tab = ImRaii.TabItem("Review Changes");
        if (!tab)
            return;

        var result = _plugin.OrganizerState.Validate();

        if (!result.HasIssues)
            ImGui.TextColored(ImGuiColors.HealerGreen, "No issues found.");

        foreach (var identifier in result.ProtectedViolations)
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Protected mod changed: {identifier}");

        foreach (var (path, identifiers) in result.PathCollisions)
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Collision at '{path}': {string.Join(", ", identifiers)}");

        ImGui.Spacing();
        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: true);

        ImGui.Spacing();
        ImGui.BeginDisabled();
        ImGui.Button("Apply (disabled in Phase 1)");
        ImGui.EndDisabled();
    }

    private void RunScan()
    {
        try
        {
            _plugin.RunScan();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to reach Penumbra IPC: {ex.Message}";
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Run the full test suite one more time**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 23, Skipped: 0`

- [ ] **Step 4: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: rewire MainWindow into Scan/Protect/Sort/Review tabs

Apply stays disabled per the Phase 1 spec's non-goals."
```

---

## Task 14: Manual in-game verification (1a/1b)

Not automatable — this is the same manual dev-plugin verification convention the existing MVP
already uses (see `README.md`).

- [ ] **Step 1: Build and load**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`

Confirm the build output path is still registered under Dalamud Settings → Experimental → Dev
Plugin Locations (should already be set from the earlier MVP verification session). Reload the
plugin via `/xlplugins` → Dev Tools, or restart the game if needed.

- [ ] **Step 2: Verify Scan**

Open with `/porganizer`. Click "Refresh mod list" on the Scan tab. Confirm the mod count and table
match what's actually installed, and that Author/Current Path columns show real values (not
"(unresolved)" or blank).

- [ ] **Step 3: Verify Heliosphere auto-protect**

If any installed mods are Heliosphere-managed (directory starts `hs-`, or contain
`heliosphere.json`), confirm they show as protected (yellow name, checked in the Protect tab)
immediately after scan, with no manual action needed. Confirm "Toggle Heliosphere protection"
flips them.

- [ ] **Step 4: Verify manual protect/unprotect persists**

Protect an arbitrary non-Heliosphere mod via the Protect tab checkbox. Close and reopen the window
(or reload the plugin). Re-scan. Confirm the mod is still shown as protected — proving
`IPluginConfiguration` persistence round-trips.

- [ ] **Step 5: Verify Start Manually**

On the Sort tab, select an unprotected mod, type a destination folder, click Assign. Switch to
Review Changes, confirm the mod shows the new proposed path in green, and no issues are reported.

- [ ] **Step 6: Verify By Creator**

Click "By Creator" on the Sort tab. Switch to Review Changes. Confirm every unprotected mod's
proposed path is `{Author}/{ModName}` (or the canonicalized author name for known aliases, e.g. a
mod authored by `enni` should propose `Enni/{ModName}`), and that protected mods are unchanged.

- [ ] **Step 7: Verify protected-row violation detection**

This requires temporarily bypassing `AssignManual`'s own protection check to prove `Validate`
independently catches it (matching `Validate_ProtectedModWithChangedPath_IsFlagged` from Task 10) —
skip this step if there's no practical in-game way to force it; the unit test already covers this
case.

- [ ] **Step 8: Confirm Apply is inert**

Confirm the "Apply" button on Review Changes is visibly disabled and cannot be clicked.

- [ ] **Step 9: Record results**

No commit for this task — it's verification, not a code change. If any step fails, stop and treat
it as a bug against the relevant earlier task rather than proceeding to Task 15.

---

## Task 15: Phase 1c format-verification spike (data-gathering only)

This task deliberately does **not** implement any type-classification parsing logic — the spec
requires confirming `GetChangedItems`' key-string format empirically before writing a parser
against it. Output is a findings note, not shippable code.

**Files:**
- Create: `C:\Repo\PenumbraOrganizer.Plugin\docs\superpowers\specs\2026-07-12-changed-items-format-spike-findings.md`

- [ ] **Step 1: Add a temporary spike button**

In `MainWindow.DrawScanTab()`, temporarily add (to be removed after this task, not committed as
permanent UI):

```csharp
        if (ImGui.Button("[SPIKE] Log GetChangedItems for first 10 mods"))
            LogChangedItemsSpike();
```

Add the corresponding method to `MainWindow`:

```csharp
    private void LogChangedItemsSpike()
    {
        var ipc = new Penumbra.Api.IpcSubscribers.GetChangedItems(Plugin.PluginInterface);
        foreach (var mod in _plugin.OrganizerState.Mods.Take(10))
        {
            var items = ipc.Invoke(mod.Identifier, mod.Name);
            LogEvent($"[SPIKE] {mod.Name}: {string.Join(" | ", items.Keys)}");
        }
    }
```

(`Plugin.PluginInterface` must be accessible — it's currently `internal static` on `Plugin`; this
is acceptable for a throwaway spike, but do not carry this exact call site forward into any
permanent code.)

- [ ] **Step 2: Build, load in-game, run the spike**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`

In-game: scan, click the spike button, read the resulting lines in the event log (10 mods' worth
of raw `GetChangedItems` keys).

- [ ] **Step 3: Record findings**

Create `C:\Repo\PenumbraOrganizer.Plugin\docs\superpowers\specs\2026-07-12-changed-items-format-spike-findings.md`
with:
- The raw key strings observed, verbatim, for each of the 10 mods.
- Whether the `"{Slot}, {Item name}"` convention held for all of them, some, or none.
- The game client's UI language at time of testing (affects whether names are localized).
- A recommendation: proceed to a Phase 1c plan using this format, or the format doesn't hold and
  1c needs a different approach (to be brainstormed separately — do not improvise a fallback here).

- [ ] **Step 4: Remove the spike button and method**

Delete the button from `DrawScanTab()` and the `LogChangedItemsSpike` method from `MainWindow.cs`.
Confirm the build still succeeds.

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit the findings only**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add docs/superpowers/specs/2026-07-12-changed-items-format-spike-findings.md
git commit -m "docs: record GetChangedItems format spike findings

Gates whether Phase 1c (By mod type) proceeds as designed or needs a
different approach. No parsing logic implemented here."
```
