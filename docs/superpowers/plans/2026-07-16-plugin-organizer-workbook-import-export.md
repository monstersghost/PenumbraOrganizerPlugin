# Workbook Import/Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The plugin can export a `.xlsx` workbook and import one back, schema- and behavior-compatible with the standalone app's existing workbook feature, by linking the standalone app's actual `WorkbookWorkflowService` rather than reimplementing the format.

**Architecture:** Extract a small `ScanIdentity` utility in the standalone app repo, link three files (models, service, identity) into the plugin project, and add a plugin-only `WorkbookAdapter` that translates between this plugin's `OrganizerModRow`/`OrganizerState` and the linked service's `ScanInventory`/`OrganizerModProposal`/`PenumbraInstallation` shapes. Everything runs synchronously on the same call, matching the rest of this plugin.

**Tech Stack:** C#, Dalamud.NET.Sdk 15.0.0 (plugin), .NET 8 (standalone app), xUnit, ClosedXML 0.104.2, Microsoft.Extensions.Logging.Abstractions 8.0.2.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-16-plugin-organizer-workbook-import-export-design.md` — every task below implements a specific part of it; read it if anything here is ambiguous.
- No async/background-thread execution model is being introduced to this plugin's own code. The linked `WorkbookWorkflowService.ExportAsync`/`ImportAsync` happen to use `Task.Run` internally (unchanged, linked code) — this plugin's own methods block on `.GetAwaiter().GetResult()`, matching every other synchronous button-handler in this codebase. Do not add `async`/`await` to any *plugin-authored* method in this plan.
- Do not add a CI pipeline, a shared library/NuGet package, or a Git submodule. See the spec's Non-goals.
- ClosedXML version must be exactly `0.104.2` and `Microsoft.Extensions.Logging.Abstractions` exactly `8.0.2` — matching the standalone app's own versions, avoiding a second dependency-resolution graph for the same functionality.
- All new pure logic (`WorkbookAdapter`'s functions) must be unit-tested. IPC/file-I/O glue (`Plugin.cs` additions, `MainWindow.cs` additions) is not — matching this repo's existing, established convention (see `ApplyPlanner`/`CollisionDisambiguator` vs. `Plugin.cs`'s `RunScan`/`ApplyChanges`).

---

### Task 1: Extract `ScanIdentity` in the standalone app repo

**Files:**
- Create: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Identity\ScanIdentity.cs`
- Modify: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Sessions\OrganizerSessionService.cs`
- Modify: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Exports\WorkbookWorkflowService.cs`

**Interfaces:**
- Produces: `PenumbraOrganizer.Core.Identity.ScanIdentity.BuildScanIdentity(ScanInventory)` → `string`; `PenumbraOrganizer.Core.Identity.ScanIdentity.BuildInstallationIdentity(PenumbraInstallation)` → `string`. Every later task that links `WorkbookWorkflowService.cs` into the plugin depends on this file existing and `WorkbookWorkflowService.cs` no longer referencing `OrganizerSessionService`.

This is why it's step 1: linking `WorkbookWorkflowService.cs` unmodified into the plugin would require also linking `OrganizerSessionService.cs`, which implements `IOrganizerSessionService` (session save/load file I/O the plugin has no use for) — a confirmed dependency cascade. This task breaks that cascade at the source, in the standalone app repo, before any plugin code exists. Only `WorkbookWorkflowService.cs`'s four call sites change; **do not** touch `RealInstallationValidationService.cs`, `PlanInvalidationService.cs`, `DryRunPlanner.cs`, `MainViewModel.cs`, or any test file — they keep calling `OrganizerSessionService.BuildScanIdentity`/`BuildInstallationIdentity`, which still exist on that class (now as thin delegating wrappers) with identical behavior.

- [ ] **Step 1: Create the new `ScanIdentity` class**

```csharp
namespace PenumbraOrganizer.Core.Identity;

using System.Security.Cryptography;
using System.Text;
using PenumbraOrganizer.Core.Models;

/// <summary>
/// Pure hash-building functions used to detect whether a scanned Penumbra installation or its mod
/// library has changed since a workbook/session was produced. Extracted from
/// <c>PenumbraOrganizer.Infrastructure.Sessions.OrganizerSessionService</c> so consumers with no need
/// for session save/load file I/O (e.g. a Dalamud plugin linking only the workbook export/import
/// logic) can depend on this alone.
/// </summary>
public static class ScanIdentity
{
    public static string BuildScanIdentity(ScanInventory inventory)
    {
        var input = string.Join('\n', inventory.Mods.OrderBy(mod => mod.StableScanId, StringComparer.Ordinal).Select(mod => $"{mod.StableScanId}|{mod.CurrentVirtualFolder}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static string BuildInstallationIdentity(PenumbraInstallation installation)
    {
        var input = $"{NormalizeForIdentity(installation.ConfigDirectory)}|{NormalizeForIdentity(installation.ModRoot)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string NormalizeForIdentity(string path)
        => path.Trim().Replace('\\', '/').ToUpperInvariant();
}
```

- [ ] **Step 2: Update `OrganizerSessionService.cs` to delegate to it**

In `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Sessions\OrganizerSessionService.cs`, add the using directive after the existing `using PenumbraOrganizer.Core.Interfaces;` line:

```csharp
using PenumbraOrganizer.Core.Identity;
```

Replace the two method bodies (find `public static string BuildScanIdentity(ScanInventory inventory)` through the end of `BuildInstallationIdentity`):

```csharp
    public static string BuildScanIdentity(ScanInventory inventory)
    {
        var input = string.Join('\n', inventory.Mods.OrderBy(mod => mod.StableScanId, StringComparer.Ordinal).Select(mod => $"{mod.StableScanId}|{mod.CurrentVirtualFolder}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static string BuildInstallationIdentity(PenumbraInstallation installation)
    {
        var input = $"{NormalizeForIdentity(installation.ConfigDirectory)}|{NormalizeForIdentity(installation.ModRoot)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
```

with:

```csharp
    public static string BuildScanIdentity(ScanInventory inventory)
        => ScanIdentity.BuildScanIdentity(inventory);

    public static string BuildInstallationIdentity(PenumbraInstallation installation)
        => ScanIdentity.BuildInstallationIdentity(installation);
```

Then remove the now-unused private helper at the bottom of the class (find and delete just this method, leaving the closing `}` of the class in place):

```csharp
    private static string NormalizeForIdentity(string path)
        => path.Trim().Replace('\\', '/').ToUpperInvariant();
```

Leave `System.Security.Cryptography`/`System.Text` usings in place — `BuildSessionIdentity`/`BuildProposalSnapshotIdentity` (unchanged, further down in the same file) still use `SHA256`/`Encoding`/`StringBuilder` directly.

- [ ] **Step 3: Update `WorkbookWorkflowService.cs`'s four call sites**

In `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Exports\WorkbookWorkflowService.cs`, replace the using block at the top:

```csharp
using ClosedXML.Excel;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Interfaces;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Infrastructure.Sessions;
```

with:

```csharp
using ClosedXML.Excel;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Identity;
using PenumbraOrganizer.Core.Interfaces;
using PenumbraOrganizer.Core.Models;
```

(Note: `using PenumbraOrganizer.Infrastructure.Sessions;` is removed — this is exactly what breaks the dependency cascade.)

In `ExportAsync`, replace:

```csharp
            var scanIdentity = OrganizerSessionService.BuildScanIdentity(inventory);
            var installationIdentity = OrganizerSessionService.BuildInstallationIdentity(inventory.Installation);
```

with:

```csharp
            var scanIdentity = ScanIdentity.BuildScanIdentity(inventory);
            var installationIdentity = ScanIdentity.BuildInstallationIdentity(inventory.Installation);
```

In `ValidateMetadata`, replace:

```csharp
        var currentInstallationIdentity = OrganizerSessionService.BuildInstallationIdentity(inventory.Installation);
```

with:

```csharp
        var currentInstallationIdentity = ScanIdentity.BuildInstallationIdentity(inventory.Installation);
```

and replace:

```csharp
        var currentScanIdentity = OrganizerSessionService.BuildScanIdentity(inventory);
```

with:

```csharp
        var currentScanIdentity = ScanIdentity.BuildScanIdentity(inventory);
```

- [ ] **Step 4: Run the standalone app's full test suite to confirm zero regressions**

Run: `cd C:\Repo\PenumbraOrganizer && dotnet test PenumbraOrganizer.Tests`
Expected: `Passed! - Failed: 0, Passed: 282, Skipped: 0, Total: 282` (same count as before this change — this is a pure relocation, no behavior change).

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer
git add PenumbraOrganizer.Core/Identity/ScanIdentity.cs PenumbraOrganizer.Infrastructure/Sessions/OrganizerSessionService.cs PenumbraOrganizer.Infrastructure/Exports/WorkbookWorkflowService.cs
git commit -m "refactor: extract ScanIdentity so it can be linked without OrganizerSessionService

Lets the Dalamud plugin repo link WorkbookWorkflowService.cs without
also needing IOrganizerSessionService and its session-file I/O.
OrganizerSessionService.BuildScanIdentity/BuildInstallationIdentity
keep their existing signatures and values via thin delegation - no
other call site changes.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: Link the workbook files and add dependencies to the plugin

**Files:**
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`

**Interfaces:**
- Consumes: `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Identity\ScanIdentity.cs` (Task 1).
- Produces: `PenumbraOrganizer.Core.Models.WorkbookExportResult`/`WorkbookImportRow`/`WorkbookImportResult`/`WorkbookCategoryCatalog`/`WorkbookCategoryDefinition` (from linked `WorkbookWorkflowModels.cs`); `PenumbraOrganizer.Infrastructure.Exports.WorkbookWorkflowService` (from linked `WorkbookWorkflowService.cs`) — all consumed by Tasks 7–10.

- [ ] **Step 1: Add the package references and linked files**

In `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`, replace:

```xml
  <ItemGroup>
    <PackageReference Include="Penumbra.Api" Version="5.15.1" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModCategory.cs" Link="Linked\ModCategory.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\ICreatorCanonicalizer.cs" Link="Linked\ICreatorCanonicalizer.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Services\CreatorCanonicalizer.cs" Link="Linked\CreatorCanonicalizer.cs" />
  </ItemGroup>
```

with:

```xml
  <ItemGroup>
    <PackageReference Include="Penumbra.Api" Version="5.15.1" />
    <PackageReference Include="ClosedXML" Version="0.104.2" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModCategory.cs" Link="Linked\ModCategory.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\ICreatorCanonicalizer.cs" Link="Linked\ICreatorCanonicalizer.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Services\CreatorCanonicalizer.cs" Link="Linked\CreatorCanonicalizer.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Models\WorkbookWorkflowModels.cs" Link="Linked\WorkbookWorkflowModels.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Identity\ScanIdentity.cs" Link="Linked\ScanIdentity.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Exports\WorkbookWorkflowService.cs" Link="Linked\WorkbookWorkflowService.cs" />
  </ItemGroup>
```

- [ ] **Step 2: Build to confirm the linked files compile cleanly**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin`
Expected: `Build succeeded.` with `0 Error(s)`. If `WorkbookWorkflowService.cs` fails to resolve `ScanIdentity`, confirm Task 1 was completed in the standalone app repo first (this task depends on that file existing there).

- [ ] **Step 3: Run the plugin test suite to confirm no regressions**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 187, Skipped: 0, Total: 187` (same as before this change — nothing new is tested yet, this task only adds linked source and dependencies).

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj
git commit -m "build: link WorkbookWorkflowService and add ClosedXML dependency

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: `PluginLogAdapter` — bridge `IPluginLog` to `ILogger<T>`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/PluginLogAdapter.cs`

**Interfaces:**
- Consumes: `Dalamud.Plugin.Services.IPluginLog` (already available via `Plugin.Log`).
- Produces: `PenumbraOrganizer.Plugin.Organizer.PluginLogAdapter<T>` implementing `Microsoft.Extensions.Logging.ILogger<T>` — consumed by Task 7's `WorkbookWorkflowService` construction.

The linked `WorkbookWorkflowService` constructor requires `ILogger<WorkbookWorkflowService>`. This plugin logs via Dalamud's `IPluginLog` everywhere else; this is the one place a call needs the `Microsoft.Extensions.Logging` shape. Confirmed via reflection against the real `Dalamud.dll`: `IPluginLog.Information(string, params object?[])`, `.Warning(string, params object?[])`, `.Error(string, params object?[])` all exist with this exact string-first signature.

No unit test for this class — it is IPC/logging glue that only forwards a formatted string, matching this repo's established convention that thin `Plugin.cs`-adjacent glue is exercised in-game rather than unit-tested (see `ApplyChanges`/`RunScan`, which are also untested).

- [ ] **Step 1: Write the adapter**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// Wraps Dalamud's IPluginLog behind ILogger&lt;T&gt; so the linked standalone-app
/// WorkbookWorkflowService (which takes ILogger&lt;T&gt;) logs through this plugin's own logging
/// pipeline instead of pulling in a full DI/logging framework this plugin doesn't otherwise use.
/// </summary>
public sealed class PluginLogAdapter<T> : ILogger<T>
{
    private readonly IPluginLog _log;

    public PluginLogAdapter(IPluginLog log) => _log = log;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Warning:
                _log.Warning(message);
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                _log.Error(message);
                break;
            default:
                _log.Information(message);
                break;
        }
    }
}
```

- [ ] **Step 2: Build to confirm it compiles against the real `IPluginLog`/`ILogger<T>`**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/PluginLogAdapter.cs
git commit -m "feat: add PluginLogAdapter bridging IPluginLog to ILogger<T>

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: `WorkbookAdapter.SplitPath`/`JoinPath`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterSplitJoinTests.cs`

**Interfaces:**
- Produces: `PenumbraOrganizer.Plugin.Organizer.WorkbookAdapter.SplitPath(string fullPath)` → `(string Folder, string Leaf)`; `WorkbookAdapter.JoinPath(string folder, string leaf)` → `string`. Consumed by Tasks 5 and 6.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Plugin.Organizer;

public class WorkbookAdapterSplitJoinTests
{
    [Fact]
    public void SplitPath_RootLevelPath_ReturnsEmptyFolderAndFullLeaf()
    {
        var (folder, leaf) = WorkbookAdapter.SplitPath("Bibo+ Medieval (Penumbra)_1_1_0");

        Assert.Equal("", folder);
        Assert.Equal("Bibo+ Medieval (Penumbra)_1_1_0", leaf);
    }

    [Fact]
    public void SplitPath_NestedPath_SplitsAtLastSeparator()
    {
        var (folder, leaf) = WorkbookAdapter.SplitPath("Tsar/Gear/Bibo+ Medieval (Penumbra)_1_1_0");

        Assert.Equal("Tsar/Gear", folder);
        Assert.Equal("Bibo+ Medieval (Penumbra)_1_1_0", leaf);
    }

    [Fact]
    public void JoinPath_EmptyFolder_ReturnsLeafAlone()
    {
        Assert.Equal("Foo", WorkbookAdapter.JoinPath("", "Foo"));
    }

    [Fact]
    public void JoinPath_NonEmptyFolder_JoinsWithSeparator()
    {
        Assert.Equal("Tsar/Gear/Foo", WorkbookAdapter.JoinPath("Tsar/Gear", "Foo"));
    }

    [Theory]
    [InlineData("Bibo+ Medieval (Penumbra)_1_1_0")]
    [InlineData("Tsar/Gear/Bibo+ Medieval (Penumbra)_1_1_0")]
    [InlineData("Gear/Galateah (2)")]
    public void SplitThenJoin_RoundTrips(string path)
    {
        var (folder, leaf) = WorkbookAdapter.SplitPath(path);

        Assert.Equal(path, WorkbookAdapter.JoinPath(folder, leaf));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests --filter WorkbookAdapterSplitJoinTests`
Expected: build FAIL — `WorkbookAdapter` does not exist yet.

- [ ] **Step 3: Write the minimal implementation**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

public static class WorkbookAdapter
{
    public static (string Folder, string Leaf) SplitPath(string fullPath)
    {
        var lastSeparator = fullPath.LastIndexOf('/');
        return lastSeparator < 0
            ? (string.Empty, fullPath)
            : (fullPath[..lastSeparator], fullPath[(lastSeparator + 1)..]);
    }

    public static string JoinPath(string folder, string leaf)
        => folder.Length == 0 ? leaf : $"{folder}/{leaf}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests --filter WorkbookAdapterSplitJoinTests`
Expected: `Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5`.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterSplitJoinTests.cs
git commit -m "feat: add WorkbookAdapter.SplitPath/JoinPath

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: `WorkbookAdapter.ToScanInventory`/`ToProposals`/`ToOrganizationPreferences`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterInventoryTests.cs`

**Interfaces:**
- Consumes: `WorkbookAdapter.SplitPath` (Task 4); `PenumbraOrganizer.Plugin.Organizer.OrganizerState`/`OrganizerModRow` (existing); `PenumbraOrganizer.Core.Models.ScanInventory`/`ModScanResult`/`OrganizerModProposal`/`PenumbraInstallation`/`OrganizationPreferences`/`OrganizationStrategy`/`DiscoveryConfidence` and `PenumbraOrganizer.Core.Classification.ModCategory` (linked, Task 2).
- Produces: `WorkbookAdapter.ToScanInventory(OrganizerState, PenumbraInstallation)` → `ScanInventory`; `WorkbookAdapter.ToProposals(OrganizerState)` → `IReadOnlyList<OrganizerModProposal>`; `WorkbookAdapter.ToOrganizationPreferences(OrganizationStrategy)` → `OrganizationPreferences`. Consumed by Task 7 and Task 9.

`BuildEditableSheet` (in the linked `WorkbookWorkflowService`) only ever reads `Name`, `Author`, `CurrentVirtualFolder`, `StableScanId`, `Protected`, `DetectedCategory` from `ModScanResult`, and only `Protected` from `OrganizerModProposal` — confirmed by reading the linked file in full (see spec's Context section). Every other field below is a harmless placeholder, not a guessed real value.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;

public class WorkbookAdapterInventoryTests
{
    private static OrganizerModRow MakeRow(string identifier, string name, string author, string currentPath, ModCategory? category = null) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = currentPath,
        ProposedPath = currentPath,
        Category = category,
    };

    private static PenumbraInstallation MakeInstallation() => new(
        ConfigurationPath: "C:/Penumbra/Penumbra.json",
        ConfigDirectory: "C:/Penumbra",
        ModRoot: "C:/Penumbra/Mods",
        PluginAssemblyPath: null,
        PluginManifestPath: null,
        InstalledVersion: null,
        Confidence: DiscoveryConfidence.High,
        Evidence: [],
        Warnings: []);

    [Fact]
    public void ToScanInventory_MapsIdentifierToStableScanId()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Gear/Foo Mod")], new HashSet<string>());

        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());

        var mod = Assert.Single(inventory.Mods);
        Assert.Equal("Foo", mod.StableScanId);
    }

    [Fact]
    public void ToScanInventory_CurrentVirtualFolderIsFolderOnlySplitOfCurrentPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Tsar/Gear/Foo Mod")], new HashSet<string>());

        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());

        Assert.Equal("Tsar/Gear", inventory.Mods.Single().CurrentVirtualFolder);
    }

    [Fact]
    public void ToScanInventory_NullCategoryMapsToOthers()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Foo Mod", category: null)], new HashSet<string>());

        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());

        Assert.Equal(ModCategory.Others, inventory.Mods.Single().DetectedCategory);
    }

    [Fact]
    public void ToProposals_CarriesStableScanIdAndProtected()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Gear/Foo Mod")], new HashSet<string> { "Foo" });

        var proposals = WorkbookAdapter.ToProposals(state);

        var proposal = Assert.Single(proposals);
        Assert.Equal("Foo", proposal.StableScanId);
        Assert.True(proposal.Protected);
    }

    [Theory]
    [InlineData(OrganizationStrategy.TypeOnly)]
    [InlineData(OrganizationStrategy.CreatorOnly)]
    [InlineData(OrganizationStrategy.TypeThenCreator)]
    [InlineData(OrganizationStrategy.CreatorThenType)]
    public void ToOrganizationPreferences_CarriesRequestedStrategy(OrganizationStrategy strategy)
    {
        var preferences = WorkbookAdapter.ToOrganizationPreferences(strategy);

        Assert.Equal(strategy, preferences.Strategy);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests --filter WorkbookAdapterInventoryTests`
Expected: build FAIL — `ToScanInventory`/`ToProposals`/`ToOrganizationPreferences` do not exist yet.

- [ ] **Step 3: Add the implementation**

Add to `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs` (inside the existing `WorkbookAdapter` class, after `JoinPath`), plus a `using` block at the top of the file:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;

public static class WorkbookAdapter
{
    public static (string Folder, string Leaf) SplitPath(string fullPath)
    {
        var lastSeparator = fullPath.LastIndexOf('/');
        return lastSeparator < 0
            ? (string.Empty, fullPath)
            : (fullPath[..lastSeparator], fullPath[(lastSeparator + 1)..]);
    }

    public static string JoinPath(string folder, string leaf)
        => folder.Length == 0 ? leaf : $"{folder}/{leaf}";

    public static ScanInventory ToScanInventory(OrganizerState state, PenumbraInstallation installation)
        => new()
        {
            Installation = installation,
            ScannedAtUtc = DateTimeOffset.UtcNow,
            Mods = state.Mods.Select(ToModScanResult).ToList(),
            CurrentFolderTree = [],
            Collections = [],
            Warnings = [],
        };

    private static ModScanResult ToModScanResult(OrganizerModRow row)
    {
        var (folder, _) = SplitPath(row.CurrentPath);
        return new ModScanResult
        {
            StableScanId = row.Identifier,
            PhysicalDirectory = string.Empty,
            PhysicalDirectoryName = row.Identifier,
            CurrentVirtualFolder = folder,
            Name = row.Name,
            Author = row.Author,
            Protected = row.Protected,
            DetectedCategory = row.Category ?? ModCategory.Others,
        };
    }

    public static IReadOnlyList<OrganizerModProposal> ToProposals(OrganizerState state)
        => state.Mods.Select(row =>
        {
            var (folder, _) = SplitPath(row.CurrentPath);
            return new OrganizerModProposal
            {
                StableScanId = row.Identifier,
                Name = row.Name,
                CurrentVirtualFolder = folder,
                ProposedVirtualFolder = folder,
                OriginalCreator = row.Author,
                Protected = row.Protected,
                OriginalProtected = row.Protected,
            };
        }).ToList();

    public static OrganizationPreferences ToOrganizationPreferences(OrganizationStrategy strategy)
        => OrganizationPreferences.DefaultManual with { Strategy = strategy };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests --filter WorkbookAdapterInventoryTests`
Expected: `Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7`.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterInventoryTests.cs
git commit -m "feat: add WorkbookAdapter.ToScanInventory/ToProposals/ToOrganizationPreferences

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: `WorkbookAdapter.ApplyImportResult`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterApplyImportResultTests.cs`

**Interfaces:**
- Consumes: `WorkbookAdapter.JoinPath` (Task 4); `OrganizerState.AssignManual`/`SetProtected`/`Mods` (existing); `PenumbraOrganizer.Core.Models.WorkbookImportResult`/`WorkbookImportRow` (linked, Task 2).
- Produces: `WorkbookAdapter.ApplyImportResult(OrganizerState, WorkbookImportResult)` → `void`. Consumed by Task 7 and Task 9.

Order matters: protection is applied *before* attempting the destination assignment, not after. `OrganizerState.AssignManual` rejects any row where `row.Protected` is currently `true` — if a workbook row both unprotects a mod *and* moves it in the same import, applying the move first would see the still-protected flag and silently drop the destination change. This mirrors the exact ordering reasoning already established in this codebase's Phase 2 Apply spec for `ProtectAndSkipBlockingMods`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;

public class WorkbookAdapterApplyImportResultTests
{
    private static OrganizerState MakeStateWithOneRow(string identifier, string name, string currentPath, bool initiallyProtected = false)
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = identifier, Name = name, Author = "Author", CurrentPath = currentPath, ProposedPath = currentPath }],
            initiallyProtected ? new HashSet<string> { identifier } : new HashSet<string>());
        return state;
    }

    private static WorkbookImportResult MakeResult(params WorkbookImportRow[] rows)
        => new("workbook.xlsx", "export-1", DateTimeOffset.UtcNow, "scan-1", "install-1", rows, [], [], "ok");

    [Fact]
    public void ResolvedDestination_RecombinesWithCurrentNameNotIdentifier()
    {
        var state = MakeStateWithOneRow("Bibo+ Medieval (Penumbra)_1_1_0", "Bibo+ Medieval Dress", "Gear/Bibo+ Medieval Dress");
        var result = MakeResult(new WorkbookImportRow(
            2, "Bibo+ Medieval (Penumbra)_1_1_0", "Bibo+ Medieval Dress", "Tsar", "Gear", "Gear", false, "Tsar/Gear", "Gear", "Tsar/Gear"));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal("Tsar/Gear/Bibo+ Medieval Dress", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void NullResolvedDestination_DoesNotChangeProposedPath()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod");
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", true, "", "Gear", null));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal("Gear/Foo Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ProtectedAppliedUnconditionally_EvenWithNullResolvedDestination()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod");
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", true, "", "Gear", null));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnresolvedProtectionFalse_UnprotectsAPreviouslyProtectedRow()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod", initiallyProtected: true);
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", false, "", "Gear", null));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnprotectAndMoveInSameRow_AppliesBothCorrectly()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod", initiallyProtected: true);
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", false, "Tsar/Gear", "Gear", "Tsar/Gear"));

        WorkbookAdapter.ApplyImportResult(state, result);

        var row = state.Mods.Single();
        Assert.False(row.Protected);
        Assert.Equal("Tsar/Gear/Foo Mod", row.ProposedPath);
    }

    [Fact]
    public void UnknownIdentifier_IsSkippedWithoutThrowing()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod");
        var result = MakeResult(new WorkbookImportRow(
            2, "DoesNotExist", "Ghost Mod", "Author", "Gear", "Gear", false, "Gear", "Gear", "Gear"));

        var exception = Record.Exception(() => WorkbookAdapter.ApplyImportResult(state, result));

        Assert.Null(exception);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests --filter WorkbookAdapterApplyImportResultTests`
Expected: build FAIL — `ApplyImportResult` does not exist yet.

- [ ] **Step 3: Add the implementation**

Add to `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`, inside the class, after `ToOrganizationPreferences`:

```csharp
    public static void ApplyImportResult(OrganizerState state, WorkbookImportResult result)
    {
        var rowsById = state.Mods.ToDictionary(row => row.Identifier, StringComparer.Ordinal);
        foreach (var importedRow in result.Rows)
        {
            if (!rowsById.TryGetValue(importedRow.StableScanId, out var row))
                continue;

            state.SetProtected(row.Identifier, importedRow.Protected);

            if (importedRow.ResolvedDestination is not null)
                state.AssignManual(row.Identifier, JoinPath(importedRow.ResolvedDestination, row.Name));
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests --filter WorkbookAdapterApplyImportResultTests`
Expected: `Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6`.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterApplyImportResultTests.cs
git commit -m "feat: add WorkbookAdapter.ApplyImportResult

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 7: Wire `ExportWorkbook`/`ImportWorkbook` into `Plugin.cs`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `WorkbookAdapter.ToScanInventory`/`ToProposals`/`ToOrganizationPreferences`/`ApplyImportResult` (Tasks 5–6); `PluginLogAdapter<T>` (Task 3); linked `WorkbookWorkflowService`/`WorkbookImportResult` (Task 2); `Penumbra.Api.IpcSubscribers.GetModDirectory` (new IPC call, confirmed present in the 5.15.1 surface, `Invoke()` → `string`, no args).
- Produces: `internal string ExportWorkbook(OrganizationStrategy strategy)`; `internal WorkbookImportResult ImportWorkbook(string workbookPath)` — consumed by Task 8's UI.

No unit test — this is IPC/file-I/O glue, matching the established convention for every other `Plugin.cs` method (`RunScan`, `ApplyChanges`, `CleanUpFolders`). Verified in-game per the spec's Testing section.

- [ ] **Step 1: Add the using directives**

At the top of `PenumbraOrganizer.Plugin/Plugin.cs`, add after the existing `using Penumbra.Api.IpcSubscribers;` line:

```csharp
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Infrastructure.Exports;
```

- [ ] **Step 2: Add the `_workbookService` field and construct it in the constructor**

Replace:

```csharp
    internal readonly Penumbra.Api.IpcSubscribers.GetModListAdapter GetModListAdapterIpc;
    internal readonly Penumbra.Api.IpcSubscribers.SetModPath SetModPathIpc;
    public readonly Organizer.OrganizerState OrganizerState = new();
    internal Configuration Config = null!;
```

with:

```csharp
    internal readonly Penumbra.Api.IpcSubscribers.GetModListAdapter GetModListAdapterIpc;
    internal readonly Penumbra.Api.IpcSubscribers.SetModPath SetModPathIpc;
    public readonly Organizer.OrganizerState OrganizerState = new();
    internal Configuration Config = null!;
    private readonly WorkbookWorkflowService _workbookService;
```

Replace:

```csharp
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GetModListAdapterIpc = new Penumbra.Api.IpcSubscribers.GetModListAdapter(PluginInterface);
        SetModPathIpc = new Penumbra.Api.IpcSubscribers.SetModPath(PluginInterface);
```

with:

```csharp
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GetModListAdapterIpc = new Penumbra.Api.IpcSubscribers.GetModListAdapter(PluginInterface);
        SetModPathIpc = new Penumbra.Api.IpcSubscribers.SetModPath(PluginInterface);
        _workbookService = new WorkbookWorkflowService(
            new CreatorCanonicalizer(), new Organizer.PluginLogAdapter<WorkbookWorkflowService>(Log));
```

- [ ] **Step 3: Add `ExportWorkbook`/`ImportWorkbook` and their shared installation-builder**

Add these methods to `Plugin.cs`, immediately after `SaveProtectionState()` (right before the existing `ExportReview()` method):

```csharp
    private PenumbraInstallation BuildInstallation() => new(
        ConfigurationPath: string.Empty,
        ConfigDirectory: PenumbraConfigDirectory,
        ModRoot: new Penumbra.Api.IpcSubscribers.GetModDirectory(PluginInterface).Invoke(),
        PluginAssemblyPath: null,
        PluginManifestPath: null,
        InstalledVersion: null,
        Confidence: DiscoveryConfidence.High,
        Evidence: [],
        Warnings: []);

    private string WorkbookFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-workbook.xlsx");

    internal string ExportWorkbook(OrganizationStrategy strategy)
    {
        var inventory = Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation());
        var proposals = Organizer.WorkbookAdapter.ToProposals(OrganizerState);
        var preferences = Organizer.WorkbookAdapter.ToOrganizationPreferences(strategy);

        var tempPath = WorkbookFilePath + ".tmp";
        var export = _workbookService.ExportAsync(inventory, proposals, preferences, tempPath, CancellationToken.None)
            .GetAwaiter().GetResult();
        File.Move(export.WorkbookPath, WorkbookFilePath, overwrite: true);
        return WorkbookFilePath;
    }

    internal WorkbookImportResult ImportWorkbook(string workbookPath)
    {
        var inventory = Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation());
        var result = _workbookService.ImportAsync(workbookPath, inventory, CancellationToken.None)
            .GetAwaiter().GetResult();
        Organizer.WorkbookAdapter.ApplyImportResult(OrganizerState, result);
        return result;
    }
```

- [ ] **Step 4: Build to confirm it compiles**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 5: Run the full plugin test suite to confirm no regressions**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 205, Skipped: 0, Total: 205` (187 baseline + 5 SplitJoin + 7 Inventory + 6 ApplyImportResult from Tasks 4–6 = 205 — if the actual number differs, treat the running total from the preceding tasks' own "Run tests" steps as the source of truth rather than this line).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: wire ExportWorkbook/ImportWorkbook into Plugin

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 8: UI — Export Workbook and Import Workbook buttons

**Files:**
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.ExportWorkbook(OrganizationStrategy)`/`Plugin.ImportWorkbook(string)` (Task 7).

`System.Windows.Forms.OpenFileDialog` needs `<UseWindowsForms>true</UseWindowsForms>` in the csproj — **confirmed to build cleanly** with the `Dalamud.NET.Sdk/15.0.0` SDK during planning (verified with a throwaway probe file before writing this plan; not a guess). No new NuGet package required.

- [ ] **Step 1: Enable Windows Forms in the csproj**

In `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`, replace:

```xml
    <IsPackable>false</IsPackable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
```

with:

```xml
    <IsPackable>false</IsPackable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
```

- [ ] **Step 2: Add the Export Workbook button and strategy dropdown to the Review Changes tab**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add these fields alongside the existing ones near the top of the class (after `private string? _lastExportPath;`):

```csharp
    private string? _lastWorkbookExportPath;
    private int _workbookStrategyIndex = 2; // "By Type Then Creator" default
    private Organizer.WorkbookImportResultView? _lastWorkbookImportResult;
```

(`Organizer.WorkbookImportResultView` doesn't exist yet — it's a small alias added in Step 4 below so `MainWindow.cs` doesn't need to reference the linked `PenumbraOrganizer.Infrastructure.Exports` namespace directly for a field declaration; see that step for why.)

Add this static readonly array near the top of the class, next to the fields:

```csharp
    private static readonly (string Label, PenumbraOrganizer.Core.Models.OrganizationStrategy Strategy)[] WorkbookStrategyOptions =
    [
        ("By Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorOnly),
        ("By Mod Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeOnly),
        ("By Type Then Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeThenCreator),
        ("By Creator Then Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorThenType),
    ];
```

In `DrawReviewTab()`, replace:

```csharp
        ImGui.Spacing();
        if (ImGui.Button("Export"))
            _lastExportPath = _plugin.ExportReview();

        if (_lastExportPath is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Exported to: {_lastExportPath}");
        }
```

with:

```csharp
        ImGui.Spacing();
        if (ImGui.Button("Export"))
            _lastExportPath = _plugin.ExportReview();

        if (_lastExportPath is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Exported to: {_lastExportPath}");
        }

        ImGui.Spacing();
        var strategyLabels = WorkbookStrategyOptions.Select(o => o.Label).ToArray();
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Workbook suggestion strategy", ref _workbookStrategyIndex, strategyLabels, strategyLabels.Length);

        ImGui.SameLine();
        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(WorkbookStrategyOptions[_workbookStrategyIndex].Strategy);

        if (_lastWorkbookExportPath is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Workbook exported to: {_lastWorkbookExportPath}");
        }
```

- [ ] **Step 3: Add the Import Workbook button to the Sort tab**

In `DrawSortTab()`, replace:

```csharp
        ImGui.SameLine();
        if (ImGui.Button("By Creator Then Type"))
            _plugin.OrganizerState.SortByCreatorThenType(_creatorCanonicalizer.Canonicalize);

        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: pick a mod below, type a folder, click Assign.");
```

with:

```csharp
        ImGui.SameLine();
        if (ImGui.Button("By Creator Then Type"))
            _plugin.OrganizerState.SortByCreatorThenType(_creatorCanonicalizer.Canonicalize);

        ImGui.SameLine();
        if (ImGui.Button("Import Workbook"))
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "Excel Workbook (*.xlsx)|*.xlsx" };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ImportWorkbook(dialog.FileName);
        }

        if (_lastWorkbookImportResult is not null)
        {
            ImGui.TextUnformatted(_lastWorkbookImportResult.Summary);
            foreach (var error in _lastWorkbookImportResult.Errors)
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  {error}");
            foreach (var warning in _lastWorkbookImportResult.Warnings)
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {warning}");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: pick a mod below, type a folder, click Assign.");
```

- [ ] **Step 4: Add the small display-only result view type**

Create `PenumbraOrganizer.Plugin/Organizer/WorkbookImportResultView.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Display-only projection of the linked WorkbookImportResult's summary fields, so MainWindow.cs
/// doesn't need a using directive into PenumbraOrganizer.Infrastructure.Exports (the linked file's
/// original namespace) just to declare a field.
/// </summary>
public sealed record WorkbookImportResultView(string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
```

- [ ] **Step 5: Add the try/catch wrapper methods, matching the existing `RunScan()`/`ApplyChanges()` convention**

Every other button that touches Penumbra IPC goes through a private `MainWindow` wrapper that catches exceptions into `_lastError` (see `RunScan()`/`ApplyChanges()`/`RollbackLastApply()` further down in this file) — calling `_plugin.ExportWorkbook`/`_plugin.ImportWorkbook` directly from the button handler would be the only IPC-touching action in this file that skips that pattern. Add these two methods near the existing `RunScan()`/`ApplyChanges()` private methods (same section of the file, after `RollbackLastApply()`):

```csharp
    private void ExportWorkbook(PenumbraOrganizer.Core.Models.OrganizationStrategy strategy)
    {
        try
        {
            _lastWorkbookExportPath = _plugin.ExportWorkbook(strategy);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Workbook export failed: {ex.Message}";
        }
    }

    private void ImportWorkbook(string workbookPath)
    {
        try
        {
            var result = _plugin.ImportWorkbook(workbookPath);
            _lastWorkbookImportResult = new Organizer.WorkbookImportResultView(result.Summary, result.Errors, result.Warnings);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Workbook import failed: {ex.Message}";
        }
    }
```

- [ ] **Step 6: Build to confirm it compiles**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 7: Run the full plugin test suite to confirm no regressions**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: same count as the end of Task 7 (this task adds no new tests — UI glue is verified in-game, matching the established convention).

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin/Organizer/WorkbookImportResultView.cs
git commit -m "feat: add Export Workbook and Import Workbook buttons

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 9: Fixture-based interop contract tests

**Files:**
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`

**Interfaces:**
- Consumes: `WorkbookAdapter.ToScanInventory`/`ToProposals`/`ToOrganizationPreferences`/`ApplyImportResult` (Tasks 5–6); linked `WorkbookWorkflowService` (Task 2).

These three tests exercise the actual linked `WorkbookWorkflowService.ExportAsync`/`ImportAsync` through the plugin adapter — the real interop boundary, not a re-derivation of validation branches `WorkbookWorkflowTests.cs` in the standalone app repo already covers for that same file (see spec's Testing section on why the fixture list was trimmed from an exhaustive matrix to these three).

- [ ] **Step 1: Write the tests**

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Infrastructure.Exports;
using PenumbraOrganizer.Plugin.Organizer;

public class WorkbookInteropTests
{
    private static WorkbookWorkflowService CreateService()
        => new(new CreatorCanonicalizer(), NullLogger<WorkbookWorkflowService>.Instance);

    private static PenumbraInstallation MakeInstallation() => new(
        ConfigurationPath: "C:/Penumbra/Penumbra.json",
        ConfigDirectory: "C:/Penumbra",
        ModRoot: "C:/Penumbra/Mods",
        PluginAssemblyPath: null,
        PluginManifestPath: null,
        InstalledVersion: null,
        Confidence: DiscoveryConfidence.High,
        Evidence: [],
        Warnings: []);

    private static string MakeWorkbookPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "workbook.xlsx");
    }

    [Fact]
    public async Task RootLevelMod_ExportEditImport_RecombinesDestinationWithCurrentName()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "Foo", Name = "Foo Mod", Author = "Author", CurrentPath = "Foo Mod", ProposedPath = "Foo Mod", Category = ModCategory.Gear }],
            new HashSet<string>());

        var service = CreateService();
        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());
        var export = await service.ExportAsync(
            inventory, WorkbookAdapter.ToProposals(state), WorkbookAdapter.ToOrganizationPreferences(OrganizationStrategy.TypeThenCreator),
            MakeWorkbookPath(), CancellationToken.None);

        using (var workbook = new XLWorkbook(export.WorkbookPath))
        {
            workbook.Worksheet("Edit Destinations").Cell(2, 7).Value = "Tsar/Gear";
            workbook.Save();
        }

        var imported = await service.ImportAsync(export.WorkbookPath, inventory, CancellationToken.None);
        WorkbookAdapter.ApplyImportResult(state, imported);

        Assert.Empty(imported.Errors);
        Assert.Equal("Tsar/Gear/Foo Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task NestedMod_ExportEditImport_RecombinesDestinationWithCurrentName()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "Bar", Name = "Bar Mod", Author = "Author", CurrentPath = "Old/Nested/Bar Mod", ProposedPath = "Old/Nested/Bar Mod", Category = ModCategory.Gear }],
            new HashSet<string>());

        var service = CreateService();
        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());
        var export = await service.ExportAsync(
            inventory, WorkbookAdapter.ToProposals(state), WorkbookAdapter.ToOrganizationPreferences(OrganizationStrategy.TypeThenCreator),
            MakeWorkbookPath(), CancellationToken.None);

        using (var workbook = new XLWorkbook(export.WorkbookPath))
        {
            workbook.Worksheet("Edit Destinations").Cell(2, 7).Value = "New/Nested/Home";
            workbook.Save();
        }

        var imported = await service.ImportAsync(export.WorkbookPath, inventory, CancellationToken.None);
        WorkbookAdapter.ApplyImportResult(state, imported);

        Assert.Empty(imported.Errors);
        Assert.Equal("New/Nested/Home/Bar Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task ProtectionOnlyEdit_BlankDestination_AppliesProtectionWithoutMoving()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "Baz", Name = "Baz Mod", Author = "Author", CurrentPath = "Gear/Baz Mod", ProposedPath = "Gear/Baz Mod", Category = ModCategory.Gear }],
            new HashSet<string>());

        var service = CreateService();
        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());
        // StartManually produces a blank destination column (BuildSuggestedDestination's default
        // case) -- the same convention the standalone app's own tests use to get a genuinely blank
        // (not merely same-as-current) destination cell.
        var export = await service.ExportAsync(
            inventory, WorkbookAdapter.ToProposals(state), WorkbookAdapter.ToOrganizationPreferences(OrganizationStrategy.StartManually),
            MakeWorkbookPath(), CancellationToken.None);

        using (var workbook = new XLWorkbook(export.WorkbookPath))
        {
            workbook.Worksheet("Edit Destinations").Cell(2, 6).Value = "TRUE";
            workbook.Save();
        }

        var imported = await service.ImportAsync(export.WorkbookPath, inventory, CancellationToken.None);
        WorkbookAdapter.ApplyImportResult(state, imported);

        Assert.Empty(imported.Errors);
        var row = state.Mods.Single();
        Assert.True(row.Protected);
        Assert.Equal("Gear/Baz Mod", row.ProposedPath);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests --filter WorkbookInteropTests`
Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3`.

If `RootLevelMod`/`NestedMod` fail with the destination resolving differently than expected, check the exact cell being written — `TypeThenCreator`'s `BuildSuggestedDestination` produces `"{code}/{creator}"` from the *inventory's own* creator/category, not the cell we overwrite; overwriting column 7 (destination) always wins over whatever was auto-suggested there, so this should not happen, but confirm against `WorkbookWorkflowService.TryResolveDestination`'s exact parsing rules if it does.

- [ ] **Step 3: Run the full plugin test suite one more time**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests pass, zero failures.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs
git commit -m "test: add fixture-based workbook interop contract tests

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 10: Final whole-branch review and handoff doc

**Files:**
- Modify: `docs/ROADMAP.md`
- Create: `docs/HANDOFF_WORKBOOK_IMPORT_EXPORT.md`

**Interfaces:**
- Consumes: nothing new — this task reviews and documents Tasks 1–9 as a whole.

Matches this repo's established convention (see every prior phase's handoff doc): a final review of the whole branch together, since per-task reviews can each pass clean while a cross-task integration bug still slips through (the Phase 2 Apply handoff doc's own process note gives a concrete example of exactly this).

- [ ] **Step 1: Review the whole diff across both repos together**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && git diff main --stat` and `cd C:\Repo\PenumbraOrganizer && git diff main --stat` (or whatever base branch each repo is on). Read every changed file in both repos as a whole, specifically checking:
- That `WorkbookAdapter.ApplyImportResult`'s protect-before-move ordering (Task 6) is actually exercised by a real end-to-end path when called from `Plugin.ImportWorkbook` (Task 7) — not just in isolation.
- That the Task 1 standalone-app change didn't miss a call site (re-run the grep from planning: `grep -rn "OrganizerSessionService\.BuildScanIdentity\|OrganizerSessionService\.BuildInstallationIdentity" C:\Repo\PenumbraOrganizer` and confirm every match is still valid — i.e., `OrganizerSessionService` still has these two public methods, just delegating).
- That `MainWindow.cs`'s `_workbookStrategyIndex` default (`2`, "By Type Then Creator") is a real, valid index into `WorkbookStrategyOptions`.

- [ ] **Step 2: Update `docs/ROADMAP.md`**

Find the `## Phase 3 (later, unscoped) — remaining parity features` section and its bullet:

```
- **Workbook import/export**, if there's ever a need to move an organizing scheme between the
  standalone app and the plugin (note: [[dalamud-plugin-decision]] rules out *sharing code* between
  them, not necessarily interoperable data formats).
```

Replace it with:

```
- **Workbook import/export — shipped.** Links the standalone app's actual `WorkbookWorkflowService`
  (plus a small extracted `ScanIdentity` utility) into the plugin via `<Compile Include>`, the same
  pattern already used for `ModCategory.cs`/`CreatorCanonicalizer.cs`. A plugin-only `WorkbookAdapter`
  bridges the one real schema gap (full-path `ProposedPath` vs. the standalone app's folder-only
  `destination`). Design: `docs/superpowers/specs/2026-07-16-plugin-organizer-workbook-import-export-design.md`.
  Plan: `docs/superpowers/plans/2026-07-16-plugin-organizer-workbook-import-export.md`.
```

- [ ] **Step 3: Write the handoff doc**

Create `docs/HANDOFF_WORKBOOK_IMPORT_EXPORT.md`:

```markdown
# Handoff: Workbook import/export

Merged to `main` (both repos). This note is for whoever picks up in-game verification or the next
phase of work.

## What's on `main` now

The plugin can export a `.xlsx` workbook and import one back, interoperable with the standalone app's
existing workbook feature, by linking the standalone app's actual `WorkbookWorkflowService` rather than
reimplementing the format.

- Standalone app repo: `PenumbraOrganizer.Core/Identity/ScanIdentity.cs` — extracted
  `BuildScanIdentity`/`BuildInstallationIdentity`/`NormalizeForIdentity` out of
  `OrganizerSessionService`, which now delegates to it. No behavior change to any existing caller.
- Plugin repo: links `WorkbookWorkflowModels.cs`, `WorkbookWorkflowService.cs`, and the new
  `ScanIdentity.cs` via `<Compile Include>` (`PenumbraOrganizer.Plugin.csproj`), extending the same
  pattern already used for `ModCategory.cs`/`CreatorCanonicalizer.cs`.
- `Organizer/WorkbookAdapter.cs` — pure, unit-tested translation between `OrganizerState`/`OrganizerModRow`
  and the linked service's `ScanInventory`/`OrganizerModProposal`/`PenumbraInstallation` shapes.
  `SplitPath`/`JoinPath` bridge the one real schema gap: this plugin's `ProposedPath`/`CurrentPath` are
  full paths including the mod's leaf name, while the standalone app's `CurrentVirtualFolder`/workbook
  `destination` are folder-only.
- `Organizer/PluginLogAdapter.cs` — bridges Dalamud's `IPluginLog` to the `ILogger<T>` the linked service
  requires.
- `Plugin.ExportWorkbook(OrganizationStrategy)`/`Plugin.ImportWorkbook(string)` — new methods, both
  synchronous (block on `.GetAwaiter().GetResult()` over the linked service's `Task.Run`-based methods;
  no async execution model was introduced to this plugin's own code).
- Review Changes tab: new "Export Workbook" button + a strategy dropdown (this plugin has no persisted
  "current sort strategy" concept, so the strategy is an explicit choice at export time).
- Sort tab: new "Import Workbook" button, using `System.Windows.Forms.OpenFileDialog`
  (`<UseWindowsForms>true</UseWindowsForms>`, confirmed to build cleanly under the Dalamud SDK).

Design: `docs/superpowers/specs/2026-07-16-plugin-organizer-workbook-import-export-design.md` — read
this first, it documents an external design review and what was/wasn't adopted (see its "Revision
notes" section).
Plan: `docs/superpowers/plans/2026-07-16-plugin-organizer-workbook-import-export.md`.

Plugin test count: [fill in actual count after Task 9 completes]. Standalone app test count: 282
(unchanged — the `ScanIdentity` extraction is a pure relocation).

## Key decisions, in case they need revisiting

- The leaf segment of a reconstructed `ProposedPath` always comes from the mod's current `Name`
  (matching every existing sort strategy's `BuildPath` convention), never `Identifier` and never
  whatever leaf happened to be in `CurrentPath` before the import. This was a real correction during
  design review — the first draft assumed `Identifier` based on one export sample where `Name`
  happened to coincidentally equal `Identifier`.
- `WorkbookAdapter.ApplyImportResult` applies `Protected` *before* attempting `AssignManual` for each
  row — reversed, a row that's both unprotected and moved in the same import would have its move
  silently dropped, since `AssignManual` rejects any currently-protected row.
- Export's suggested destinations come from an explicit `OrganizationStrategy` the user picks in the
  UI dropdown, not from this plugin's own already-computed `ProposedPath` values — the linked
  `WorkbookWorkflowService.BuildEditableSheet` never reads a proposal's destination at all, only
  `Protected`.

## What's NOT done yet

**Not yet in-game verified.** Per the design spec's Testing section: export a workbook from a real
library, confirm it opens correctly in Excel; open the same file in the standalone app and confirm
it's recognized as valid for that install; edit destinations, import back into the plugin, confirm
resolved `ProposedPath` values look correct and Apply behaves normally; separately, export from the
standalone app and import into the plugin to confirm the reverse direction; confirm
`installationIdentity` actually matches between the plugin's IPC-derived path and the standalone app's
file-system-discovered path for the same real install (see the design spec's Open risks #2 — this is
the one part of the identity story that couldn't be fully closed by code alone).

## Process note

[Fill in after execution: subagent-driven-development task count, whether the final whole-branch
review caught anything per-task reviews missed, any worktree-boundary notes.]
```

- [ ] **Step 4: Commit both repos**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add docs/ROADMAP.md docs/HANDOFF_WORKBOOK_IMPORT_EXPORT.md
git commit -m "docs: mark workbook import/export as shipped, add handoff doc

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

(The standalone app repo's Task 1 commit already covers its own changes — no additional commit needed there.)
