# Workbook Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the workbook the way to hand-edit the Sort tab's result in Excel: export follows Sort (two modes), users can define their own category codes (17 and up) in the mapping sheet, and destination cells accept `{type}`, `{creator}` and `{race}` tokens.

**Architecture:** The plugin takes ownership of the workbook code (a copy of the standalone app's service and catalog, link dropped) and splits it into units with one job each: a pure destination resolver (codes, tokens, collapse, validation), a pure mapping-sheet parser, a token provider fed by the plugin's own state, and the xlsx file layer in the service. Export reads the Sort tab's staged paths, guarded by a sort fingerprint kept in `OrganizerState`. Import reads the mapping sheet first, resolves every destination through the resolver, and saves a clean custom-code table as the next export's seed.

**Tech Stack:** C# / .NET (Dalamud plugin), ClosedXML 0.104.2 (already referenced), xUnit, ImGui.

Spec: `docs/superpowers/specs/2026-11-22-workbook-rework-design.md` (read it first; this plan implements it). It depends on the race-split work (`RaceSplitRouting`, `RaceFolderResolver`, `RaceSplitOptions`), which is on branch `feat/race-split` (PR 6, not merged when this plan was written).

> **Review amendments:** the section "Review amendments (binding)" at the end of this file came from an Opus pass and a GPT pass over this plan. Where it conflicts with a task above, the amendment wins. Read it before starting Task 1.

## Global Constraints

- Branch: create `feat/workbook-rework` from `feat/race-split`. If PR 6 has merged by then, branch from `main` instead.
- Format version moves from 2 to 3. Import accepts 2 and 3. A version 2 file is read under version 2 rules (no tokens, v2 code meanings), never as a version 3 file with an empty custom table.
- Built-in codes 1-16 are locked. In version 3 their folders follow the Sort tab: codes 1-12 and 14 are the category name, **13 = `Animation and VFX/VFX`, 15 = `Animation and VFX`, 16 = `Review`**. Version 2 keeps `VFX`, `Animation`, `Others`.
- Custom codes are whole numbers 17 to 9999, at most **200** rows, folder path at most **260** characters. A destination path is also at most 260 characters.
- Tokens are exactly `{type}`, `{creator}`, `{race}` (letters only, case-insensitive; an unknown `{name}` is an error). Only **version 3** files expand tokens. A token with no value collapses its segment; a partly empty segment keeps its literal text (`By-{creator}` becomes `By-`). Token values are never expanded again.
- Only the **first** destination segment is read as a code, and only a **positive** whole number is a code (`0` and `-16` stay literal folder names, as today).
- Import limits: file at most **25 MB** (`MaxWorkbookBytes`), at most (library mod count + 100) data rows, any parse failure becomes exactly `This file could not be read as a Penumbra Organizer workbook.` (never a stack trace).
- The sheet always wins at import. The config only seeds the next export. The saved list is replaced only by a clean mapping table (no mapping-sheet error) from an accepted import.
- The workbook stays tied to one library (the existing installation-identity check still blocks another library's file).
- Plain ASCII in every string this plan adds. Memory: characters in anything the user copies out of chat get mangled (`_` and `*` were stripped from a pasted script once), so hand the user files, not pasted scripts.
- Test command (repo root `C:\Repo\PenumbraOrganizer.Plugin`): `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~<Name>"`. Build: `dotnet build`. Plugin source: `PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\`; tests: `PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\` (paths below are relative to the repo root). Baseline before this work: 2,365 passing tests on `feat/race-split`.
- No `InternalsVisibleTo`: anything a test touches must be `public`.
- Existing files use CRLF in places and LF in others; the Edit tool copes. When appending to a CRLF test file with a shell, write CRLF.

## File Structure

New, plugin project (`PenumbraOrganizer.Plugin/`), all in namespace `PenumbraOrganizer.Plugin.Organizer.Workbook` unless stated:
- `Organizer/Workbook/WorkbookModels.cs`: copied result records plus the new request, context, counts and usage records.
- `Organizer/Workbook/WorkbookService.cs`: the copied service, then reworked (export modes, version 3 sheets, import).
- `Organizer/Workbook/WorkbookCatalog.cs`: the version 3 built-in table (code, name, description, Sort folder).
- `Organizer/Workbook/DestinationResolver.cs`: pure grammar, tokens, collapse, validation.
- `Organizer/Workbook/MappingSheet.cs`: pure parser for the Category Mapping rows, plus the seed diff.
- `Organizer/Workbook/WorkbookTokens.cs`: builds `DestinationTokens` for a mod from plugin state.
- `Windows/WorkbookExportGate.cs` (namespace `PenumbraOrganizer.Plugin.Windows`): why "As suggested by Sort" is unavailable.

Modified: `PenumbraOrganizer.Plugin.csproj`, `Plugin.cs`, `Configuration.cs`, `Organizer/WorkbookAdapter.cs`, `Organizer/WorkbookImportResultView.cs`, `Organizer/RaceSplitRouting.cs`, `Organizer/OrganizerState.cs`, `Windows/SortPanel.cs` (`SortSelection.Fingerprint`), `Windows/SortSelectionFactory.cs` (`SortRunner`), `Windows/MainWindow.cs`, `Windows/MainWindow.ReviewTab.cs`, `Resources/help-content.json`, `docs/USER_GUIDE.md`.

Deleted: `Interfaces/IWorkbookWorkflowService.cs`, and two `Compile Include` links in the csproj.

New tests (`PenumbraOrganizer.Plugin.Tests/`, namespace `PenumbraOrganizer.Plugin.Tests.Organizer.Workbook` unless stated): `Organizer/Workbook/DestinationResolverTests.cs`, `WorkbookCatalogTests.cs`, `MappingSheetTests.cs`, `WorkbookTokensTests.cs`, `WorkbookExportTests.cs`, `WorkbookImportTests.cs`; `Organizer/SortFingerprintTests.cs`; `Windows/WorkbookExportGateTests.cs`. Existing `WorkbookInteropTests.cs`, `WorkbookAdapterApplyImportResultTests.cs` and `WorkbookAdapterInventoryTests.cs` are updated.

Docs: `docs/testing/2026-11-22-workbook-rework-checklist.md` (new), `docs/USER_GUIDE.md`.

---

### Task 1: Take ownership of the workbook code (no behavior change)

Before: the plugin compiles `WorkbookWorkflowService.cs` and `WorkbookWorkflowModels.cs` straight out of the standalone app's repo. After: the plugin owns copies; nothing else changes and every existing test still passes. This is the move that the later tasks edit.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookModels.cs`, `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs`
- Delete: `PenumbraOrganizer.Plugin/Interfaces/IWorkbookWorkflowService.cs`
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`, `PenumbraOrganizer.Plugin/Plugin.cs`, `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`, `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`, `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterApplyImportResultTests.cs`

**Interfaces:**
- Consumes: the standalone repo at `../PenumbraOrganizer` (siblings of this repo).
- Produces: namespace `PenumbraOrganizer.Plugin.Organizer.Workbook` with `WorkbookService` (constructor `(ICreatorCanonicalizer, ILogger<WorkbookService>)` for now, `ExportAsync(inventory, proposals, preferences, path, ct)`, `ImportAsync(path, inventory, ct)`) and the records `WorkbookCategoryDefinition`, `WorkbookExportResult`, `WorkbookImportRow`, `WorkbookImportResult`, static `WorkbookCategoryCatalog`, exactly as before.

- [ ] **Step 1: Branch**

```bash
git switch feat/race-split && git switch -c feat/workbook-rework
```

- [ ] **Step 2: Copy the two files and retarget their namespaces**

```bash
mkdir -p PenumbraOrganizer.Plugin/Organizer/Workbook
cp ../PenumbraOrganizer/PenumbraOrganizer.Core/Models/WorkbookWorkflowModels.cs PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookModels.cs
cp ../PenumbraOrganizer/PenumbraOrganizer.Infrastructure/Exports/WorkbookWorkflowService.cs PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs
sed -i 's/^namespace PenumbraOrganizer\.Core\.Models;/namespace PenumbraOrganizer.Plugin.Organizer.Workbook;/' PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookModels.cs
sed -i 's/^namespace PenumbraOrganizer\.Infrastructure\.Exports;/namespace PenumbraOrganizer.Plugin.Organizer.Workbook;/' PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs
sed -i 's/public sealed class WorkbookWorkflowService : IWorkbookWorkflowService/public sealed class WorkbookService/; s/WorkbookWorkflowService(/WorkbookService(/; s/ILogger<WorkbookWorkflowService>/ILogger<WorkbookService>/g' PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs
grep -n "WorkbookWorkflowService\|IWorkbookWorkflowService" PenumbraOrganizer.Plugin/Organizer/Workbook/*.cs || echo "no leftovers"
```

Expected: `no leftovers`.

In `WorkbookModels.cs` add this line under `using PenumbraOrganizer.Core.Classification;` (the `Detect` method uses `ModScanResult`, which stays in the linked `Core.Models`):

```csharp
using PenumbraOrganizer.Core.Models;
```

- [ ] **Step 3: Drop the two links and the hand-written interface**

In `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj` delete exactly these two lines (the other `Compile Include` links stay):

```xml
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Models\WorkbookWorkflowModels.cs" Link="Linked\WorkbookWorkflowModels.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Exports\WorkbookWorkflowService.cs" Link="Linked\WorkbookWorkflowService.cs" />
```

```bash
git rm PenumbraOrganizer.Plugin/Interfaces/IWorkbookWorkflowService.cs
```

- [ ] **Step 4: Fix the references**

In `PenumbraOrganizer.Plugin/Plugin.cs`:
- replace `using PenumbraOrganizer.Infrastructure.Exports;` with `using PenumbraOrganizer.Plugin.Organizer.Workbook;`
- `private readonly WorkbookWorkflowService _workbookService;` becomes `private readonly WorkbookService _workbookService;`
- the construction (about line 157) becomes:

```csharp
        _workbookService = new WorkbookService(
            new CreatorCanonicalizer(), new Organizer.PluginLogAdapter<WorkbookService>(Log));
```

In `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs` add `using PenumbraOrganizer.Plugin.Organizer.Workbook;` after the existing usings (it uses `WorkbookImportResult`).

In `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`: replace `using PenumbraOrganizer.Infrastructure.Exports;` with `using PenumbraOrganizer.Plugin.Organizer.Workbook;`, and replace the two `WorkbookWorkflowService` mentions in `CreateService()` with `WorkbookService`:

```csharp
    private static WorkbookService CreateService()
        => new(new CreatorCanonicalizer(), NullLogger<WorkbookService>.Instance);
```

In `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterApplyImportResultTests.cs` add `using PenumbraOrganizer.Plugin.Organizer.Workbook;` after the existing usings.

- [ ] **Step 5: Build and run everything**

Run: `dotnet build` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds; `Passed! ... 2365`. If the build names another file that mentions a moved type, add the `using` above to it (the moved types are `WorkbookCategoryDefinition`, `WorkbookExportResult`, `WorkbookImportRow`, `WorkbookImportResult`, `WorkbookCategoryCatalog`, `WorkbookService`).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: the plugin owns the workbook service and catalog, link dropped"
```

---

### Task 2: The destination resolver (pure)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Workbook/DestinationResolver.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/DestinationResolverTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (all `public`, namespace `PenumbraOrganizer.Plugin.Organizer.Workbook`):
  - `DestinationTokens(string Creator, string Type, string Race)` with `DestinationTokens.None`.
  - `DestinationRules(bool TokensEnabled, Func<int, string?> CodeFolder, Func<string, string?> NameFolder)`.
  - `DestinationResult(string? Destination, string? Error)` with `Leave`, `Ok(string)`, `Fail(string)`.
  - `DestinationResolver.Resolve(string raw, DestinationRules rules, DestinationTokens tokens) -> DestinationResult`, `NormalizePath(string)`, `StructureError(string path) -> string?`, `FirstSegmentCode(string raw) -> int?`, `MaxPathLength` (260).

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/DestinationResolverTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class DestinationResolverTests
{
    private static DestinationRules Rules(bool tokens = true, Dictionary<int, string>? custom = null) =>
        new(
            tokens,
            code => code switch
            {
                1 => "Gear",
                4 => "Hair",
                15 => "Animation and VFX",
                _ => custom is not null && custom.TryGetValue(code, out var folder) ? folder : null,
            },
            name => name.ToLowerInvariant() switch
            {
                "gear" => "Gear",
                "animation" => "Animation and VFX",
                _ => null,
            });

    private static readonly DestinationTokens Thor = new("Thor", "Hair", "Viera");

    private static DestinationResult R(string raw, DestinationRules? rules = null, DestinationTokens? tokens = null) =>
        DestinationResolver.Resolve(raw, rules ?? Rules(), tokens ?? Thor);

    [Fact]
    public void Blank_LeavesTheModAlone()
    {
        Assert.Equal(DestinationResult.Leave, R(""));
        Assert.Equal(DestinationResult.Leave, R("   "));
    }

    [Fact]
    public void ACode_ExpandsToItsFolder() =>
        Assert.Equal("Gear/Thor", R("1/Thor").Destination);

    [Fact]
    public void ACustomCode_ExpandsToItsFolderPath()
    {
        var rules = Rules(custom: new() { [17] = "Face/Sculpts" });
        Assert.Equal("Face/Sculpts/Thor", R("17/{creator}", rules).Destination);
    }

    [Fact]
    public void ACodeAlone_IsJustItsFolder() =>
        Assert.Equal("Animation and VFX", R("15").Destination);

    [Fact]
    public void AnUnknownCode_FailsInPlainWords()
    {
        var result = R("99/Thor");
        Assert.Null(result.Destination);
        Assert.Equal("code 99 is not in the Category Mapping sheet", result.Error);
    }

    [Theory]
    [InlineData("0/Thor", "0/Thor")]
    [InlineData("-16/Creator", "-16/Creator")]
    public void ZeroAndNegativeFirstSegments_StayLiteral(string raw, string expected) =>
        Assert.Equal(expected, R(raw).Destination);

    [Fact]
    public void ANumberInALaterSegment_IsAFolderName() =>
        Assert.Equal("Mine/17", R("Mine/17").Destination);

    [Theory]
    [InlineData("gear/Thor", "Gear/Thor")]
    [InlineData("GEAR/Thor", "Gear/Thor")]
    [InlineData("Animation/Emotes", "Animation and VFX/Emotes")]
    public void ABuiltInCategoryName_ResolvesToItsFolder(string raw, string expected) =>
        Assert.Equal(expected, R(raw).Destination);

    [Fact]
    public void AnythingElseInTheFirstSegment_IsALiteralFolder() =>
        Assert.Equal("Mine/Stuff", R("Mine/Stuff").Destination);

    [Fact]
    public void Tokens_ExpandAnywhereInThePath() =>
        Assert.Equal("Mine/Hair/Thor/test", R("Mine/{type}/{creator}/test").Destination);

    [Fact]
    public void ARaceToken_UsesTheSuppliedRaceFolder() =>
        Assert.Equal("Mine/Viera", R("Mine/{race}").Destination);

    [Fact]
    public void TokenNames_AreCaseInsensitive() =>
        Assert.Equal("Mine/Thor", R("Mine/{CREATOR}").Destination);

    [Fact]
    public void AnEmptyToken_CollapsesItsWholeSegment()
    {
        var noAuthor = new DestinationTokens("", "Hair", "");
        Assert.Equal("Mine/Hair/test", R("Mine/{type}/{creator}/test", tokens: noAuthor).Destination);
        Assert.Equal("Mine/test", R("Mine/{creator}{race}/test", tokens: noAuthor).Destination);
    }

    [Fact]
    public void APartlyEmptySegment_KeepsItsLiteralText()
    {
        var noAuthor = new DestinationTokens("", "Hair", "");
        Assert.Equal("Mine/By-", R("Mine/By-{creator}", tokens: noAuthor).Destination);
    }

    [Fact]
    public void WhenEveryLevelCollapses_TheModIsLeftAlone() =>
        Assert.Equal(DestinationResult.Leave, R("{creator}", tokens: DestinationTokens.None));

    [Fact]
    public void AnUnknownToken_FailsNamingIt()
    {
        var result = R("Mine/{foo}");
        Assert.Equal("unknown token {foo}", result.Error);
    }

    [Fact]
    public void TokenValues_AreNeverExpandedAgain()
    {
        var tricky = new DestinationTokens("{type}", "Hair", "");
        Assert.Equal("Mine/{type}", R("Mine/{creator}", tokens: tricky).Destination);
    }

    [Fact]
    public void WithTokensOff_BracesAreOrdinaryText() =>
        Assert.Equal("Mine/{creator}", R("Mine/{creator}", Rules(tokens: false)).Destination);

    [Theory]
    [InlineData("Mine//x")]
    [InlineData("../x")]
    [InlineData("Mine/./x")]
    [InlineData("Mine/..")]
    public void EmptyDotAndDotDotSegments_Fail(string raw)
    {
        var result = R(raw);
        Assert.Null(result.Destination);
        Assert.Equal("Destination cannot contain empty segments, . or ..", result.Error);
    }

    [Theory]
    [InlineData("C:/x")]
    [InlineData("C:\\Mods\\x")]
    [InlineData("Mine/a:b")]
    public void RootedAndColonPaths_Fail(string raw) =>
        Assert.Equal("Use a relative Penumbra folder path.", R(raw).Error);

    [Fact]
    public void ABackslashPath_IsNormalizedToForwardSlashes() =>
        Assert.Equal("Mine/Sub/x", R("Mine\\Sub\\x").Destination);

    [Fact]
    public void ALongFinalPath_Fails()
    {
        var long1 = string.Join('/', Enumerable.Repeat(new string('a', 50), 6)); // 6 * 50 + 5 = 305
        Assert.Equal($"Destination is longer than {DestinationResolver.MaxPathLength} characters.", R(long1).Error);
    }

    [Fact]
    public void FirstSegmentCode_ReadsOnlyAPositiveWholeNumber()
    {
        Assert.Equal(17, DestinationResolver.FirstSegmentCode("17/{creator}"));
        Assert.Equal(17, DestinationResolver.FirstSegmentCode("  17  "));
        Assert.Null(DestinationResolver.FirstSegmentCode("0/x"));
        Assert.Null(DestinationResolver.FirstSegmentCode("-16/x"));
        Assert.Null(DestinationResolver.FirstSegmentCode("Mine/17"));
        Assert.Null(DestinationResolver.FirstSegmentCode(""));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~DestinationResolverTests"`
Expected: build FAIL (`DestinationResolver` does not exist).

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Workbook/DestinationResolver.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

/// <summary>Token values for one mod. An empty string means "no value", so its folder level collapses.</summary>
public sealed record DestinationTokens(string Creator, string Type, string Race)
{
    public static readonly DestinationTokens None = new(string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// How a destination cell is read. <paramref name="CodeFolder"/> maps a positive code to its folder path
/// (null when no usable row defines it); <paramref name="NameFolder"/> maps a built-in category name, any
/// case, to its folder path (null when the name is not a built-in). <paramref name="TokensEnabled"/> is
/// true for version 3 files only.
/// </summary>
public sealed record DestinationRules(
    bool TokensEnabled,
    Func<int, string?> CodeFolder,
    Func<string, string?> NameFolder);

public sealed record DestinationResult(string? Destination, string? Error)
{
    /// <summary>A blank destination: leave the mod where it is.</summary>
    public static readonly DestinationResult Leave = new(null, null);

    public static DestinationResult Ok(string destination) => new(destination, null);

    public static DestinationResult Fail(string error) => new(null, error);
}

/// <summary>
/// The one place a destination cell becomes a folder path (workbook spec, section 4): code or built-in
/// name or literal first segment, then tokens expanded one segment at a time, then collapse, then
/// validation. Pure: no file access.
/// </summary>
public static class DestinationResolver
{
    public const int MaxPathLength = 260;

    private static readonly Regex TokenPattern = new(
        @"\{([A-Za-z]{1,40})\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizePath(string path) => path.Trim().Replace('\\', '/').Trim('/');

    /// <summary>The rule every folder path obeys, whether it came from a cell or from the mapping sheet.</summary>
    public static string? StructureError(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Any(char.IsControl))
            return "Use a relative Penumbra folder path.";

        var parts = path.Split('/');
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
            return "Destination cannot contain empty segments, . or ..";

        if (path.Length > MaxPathLength)
            return $"Destination is longer than {MaxPathLength} characters.";

        return null;
    }

    /// <summary>The code a destination's first segment names, or null. Only a positive whole number is a code.</summary>
    public static int? FirstSegmentCode(string raw)
    {
        var first = NormalizePath(raw ?? string.Empty).Split('/')[0];
        return TryParseCode(first, out var code) ? code : null;
    }

    private static bool TryParseCode(string text, out int code) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0;

    public static DestinationResult Resolve(string raw, DestinationRules rules, DestinationTokens tokens)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DestinationResult.Leave;

        var normalized = NormalizePath(raw);
        if (normalized.Length == 0)
            return DestinationResult.Fail("Destination is empty.");
        if (StructureError(normalized) is { } structure)
            return DestinationResult.Fail(structure);

        var parts = normalized.Split('/');
        string[] head;
        if (TryParseCode(parts[0], out var code))
        {
            var folder = rules.CodeFolder(code);
            if (folder is null)
                return DestinationResult.Fail($"code {code} is not in the Category Mapping sheet");
            head = folder.Split('/');
        }
        else if (rules.NameFolder(parts[0]) is { } named)
        {
            head = named.Split('/');
        }
        else
        {
            head = [parts[0]];
        }

        var segments = head.Concat(parts.Skip(1)).ToList();

        if (rules.TokensEnabled)
        {
            var expanded = new List<string>();
            foreach (var segment in segments)
            {
                string? unknown = null;
                var text = TokenPattern.Replace(segment, match =>
                {
                    var value = ValueOf(match.Groups[1].Value, tokens);
                    if (value is null)
                    {
                        unknown ??= match.Groups[1].Value;
                        return match.Value;
                    }

                    return value;
                });

                if (unknown is not null)
                    return DestinationResult.Fail($"unknown token {{{unknown}}}");

                // The whole segment expanded to nothing: its level collapses. A partly empty one keeps
                // its literal text.
                if (text.Trim().Length == 0)
                    continue;

                expanded.Add(text);
            }

            segments = expanded;
        }

        if (segments.Count == 0)
            return DestinationResult.Leave;

        var final = string.Join('/', segments);
        return StructureError(final) is { } finalError
            ? DestinationResult.Fail(finalError)
            : DestinationResult.Ok(final);
    }

    private static string? ValueOf(string name, DestinationTokens tokens) => name.ToLowerInvariant() switch
    {
        "creator" => tokens.Creator,
        "type" => tokens.Type,
        "race" => tokens.Race,
        _ => null,
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~DestinationResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Workbook/DestinationResolver.cs PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/DestinationResolverTests.cs
git commit -m "feat: the workbook destination resolver (codes, tokens, collapse, validation)"
```

---

### Task 3: The built-in catalog, the mapping-sheet parser, and the saved list

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookCatalog.cs`, `PenumbraOrganizer.Plugin/Organizer/Workbook/MappingSheet.cs`
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookCatalogTests.cs`, `MappingSheetTests.cs`

**Interfaces:**
- Consumes: `DestinationResolver.NormalizePath`, `DestinationResolver.StructureError` (Task 2).
- Produces:
  - `BuiltInCategory(int Code, string Name, string Description, string Folder)`; `WorkbookCatalog.FormatVersion` (3), `WorkbookCatalog.BuiltIn` (16 rows), `WorkbookCatalog.FolderFor(int)`, `WorkbookCatalog.TryGetByName(string, out BuiltInCategory)`.
  - `MappingSheetRow(int RowNumber, string CodeText, string Name, string Folder, bool HasFormula)`; `CustomCode(int Code, string Folder)`; `MappingParseResult(IReadOnlyList<CustomCode> Custom, IReadOnlyDictionary<int,string> Unusable, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)` with `HasErrors` and `FolderFor(int)`; `MappingSheet.Parse(IReadOnlyList<MappingSheetRow>)`, `MappingSheet.SheetName` ("Category Mapping"), constants `FirstCustomCode` 17, `LastCustomCode` 9999, `MaxCustomRows` 200.
  - `WorkbookSeed.Describe(IReadOnlyList<CustomCode> before, IReadOnlyList<CustomCode> after) -> string?`.
  - `Configuration.WorkbookCustomCodes` (`List<WorkbookCustomCode>`), class `WorkbookCustomCode { int Code; string Folder }`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookCatalogTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class WorkbookCatalogTests
{
    [Fact]
    public void BuiltIn_HasSixteenCodesInOrder() =>
        Assert.Equal(Enumerable.Range(1, 16), WorkbookCatalog.BuiltIn.Select(c => c.Code));

    [Theory]
    [InlineData(1, "Gear")]
    [InlineData(2, "Weapon")]
    [InlineData(3, "Face")]
    [InlineData(4, "Hair")]
    [InlineData(5, "Body")]
    [InlineData(6, "Skin")]
    [InlineData(7, "NPC")]
    [InlineData(8, "Minion")]
    [InlineData(9, "Mount")]
    [InlineData(10, "Pet")]
    [InlineData(11, "Ornament")]
    [InlineData(12, "Furniture")]
    [InlineData(13, "Animation and VFX/VFX")]
    [InlineData(14, "Sound")]
    [InlineData(15, "Animation and VFX")]
    [InlineData(16, "Review")]
    public void VersionThreeFolders_FollowTheSortTab(int code, string folder) =>
        Assert.Equal(folder, WorkbookCatalog.FolderFor(code));

    // The catalog and the sorter must agree on where things go, so these tie the table to the sorter.
    [Fact]
    public void TheAnimationAndVfxFolders_MatchWhatSortFilesThemUnder()
    {
        Assert.StartsWith(
            WorkbookCatalog.FolderFor(15) + "/", ModTypeFolders.GetFolder(ModCategory.Animation, "Emotes"));
        Assert.Equal(WorkbookCatalog.FolderFor(13), ModTypeFolders.GetFolder(ModCategory.VFX, "VFX"));
    }

    [Fact]
    public void TheReviewFolder_MatchesTheSortersFallback() =>
        Assert.Equal(WorkbookCatalog.FolderFor(16), SortFolderSelectors.FlattenToFolder(null, null));

    [Fact]
    public void TheSingleSegmentCategories_AreTheCategoryNameTheSorterUses()
    {
        foreach (var code in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14 })
            Assert.Equal(((ModCategory)code).ToString(), WorkbookCatalog.FolderFor(code));
    }

    [Theory]
    [InlineData("gear", true, 1)]
    [InlineData("VFX", true, 13)]
    [InlineData("others", true, 16)]
    [InlineData("Mine", false, 0)]
    public void TryGetByName_IsCaseInsensitive(string name, bool found, int code)
    {
        Assert.Equal(found, WorkbookCatalog.TryGetByName(name, out var category));
        if (found)
            Assert.Equal(code, category.Code);
    }
}
```

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/MappingSheetTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class MappingSheetTests
{
    private static MappingSheetRow Row(int row, string code, string folder, string name = "", bool formula = false) =>
        new(row, code, name, folder, formula);

    private static MappingParseResult Parse(params MappingSheetRow[] rows) => MappingSheet.Parse(rows);

    [Fact]
    public void ACleanCustomRow_IsUsable()
    {
        var result = Parse(Row(22, "17", "Face/Sculpts"));
        Assert.False(result.HasErrors);
        Assert.Equal(new[] { new CustomCode(17, "Face/Sculpts") }, result.Custom);
        Assert.Equal("Face/Sculpts", result.FolderFor(17));
    }

    [Theory]
    [InlineData("17")]
    [InlineData("0017")]
    [InlineData(" 17 ")]
    [InlineData("17.0")]
    public void ACodeTypedDifferentWays_IsTheSameCode(string text) =>
        Assert.Equal(17, Parse(Row(22, text, "Mine")).Custom.Single().Code);

    [Theory]
    [InlineData("17.5")]
    [InlineData("-17")]
    [InlineData("10000")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("TRUE")]
    public void AnInvalidCode_IsAnErrorOnItsCell(string text)
    {
        var result = Parse(Row(22, text, "Mine"));
        Assert.True(result.HasErrors);
        Assert.Empty(result.Custom);
        Assert.StartsWith("Category Mapping!A22:", result.Errors.Single());
    }

    [Fact]
    public void ADuplicatedCode_IsAnErrorOnEveryRowAndUnusable()
    {
        var result = Parse(Row(22, "17", "A"), Row(23, "17", "B"));
        Assert.Equal(
            new[] { "Category Mapping!A22: code 17 is used twice.", "Category Mapping!A23: code 17 is used twice." },
            result.Errors);
        Assert.Empty(result.Custom);
        Assert.Equal("code 17 is defined more than once", result.Unusable[17]);
    }

    [Fact]
    public void ABlankFolder_IsAnErrorOnTheFolderCellAndTheCodeIsUnusable()
    {
        var result = Parse(Row(22, "17", ""));
        Assert.Equal("Category Mapping!D22: the folder is empty for code 17.", result.Errors.Single());
        Assert.Contains(17, result.Unusable.Keys);
    }

    [Fact]
    public void ABadFolderPath_IsAnErrorOnTheFolderCell()
    {
        var result = Parse(Row(22, "17", "Mine/../x"));
        Assert.Equal(
            "Category Mapping!D22: Destination cannot contain empty segments, . or ..", result.Errors.Single());
    }

    [Fact]
    public void AFolderMayContainTokens() =>
        Assert.Equal("Mine/{type}", Parse(Row(22, "17", "Mine/{type}")).Custom.Single().Folder);

    [Fact]
    public void AFormula_IsAnError()
    {
        var result = Parse(Row(22, "17", "Mine", formula: true));
        Assert.StartsWith("Category Mapping!A22: this row contains a formula", result.Errors.Single());
    }

    [Fact]
    public void OneBadRowDoesNotStopTheOthers()
    {
        var result = Parse(Row(22, "17", "Face/Sculpts"), Row(23, "18", ""));
        Assert.Equal("Face/Sculpts", result.FolderFor(17));
        Assert.Null(result.FolderFor(18));
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void BlankRows_AreIgnored() =>
        Assert.Empty(Parse(Row(22, "", ""), Row(23, "  ", "  ")).Custom);

    [Fact]
    public void EditingABuiltInRow_WarnsWithBothValuesAndChangesNothing()
    {
        var result = Parse(Row(10, "5", "Bodies", name: "Body"));
        Assert.False(result.HasErrors);
        Assert.Empty(result.Custom);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("built-in code 5", warning);
        Assert.Contains("Body", warning);
        Assert.Contains("Bodies", warning);
    }

    [Fact]
    public void AnUneditedBuiltInRow_WarnsNothing() =>
        Assert.Empty(Parse(Row(6, "1", "Gear", name: "Gear")).Warnings);

    [Fact]
    public void MoreThanTwoHundredCustomRows_IsAnError()
    {
        var rows = Enumerable.Range(17, 201).Select(code => Row(code + 6, code.ToString(), "F" + code)).ToArray();
        var result = Parse(rows);
        Assert.Contains(result.Errors, e => e.StartsWith("Category Mapping: more than 200 custom categories"));
    }
}

public class WorkbookSeedTests
{
    private static CustomCode C(int code, string folder) => new(code, folder);

    [Fact]
    public void NoChange_IsNull() =>
        Assert.Null(WorkbookSeed.Describe([C(17, "A")], [C(17, "A")]));

    [Fact]
    public void ReportsAddedChangedAndRemoved() =>
        Assert.Equal(
            "Saved 2 custom categories: 1 added, 1 changed, 1 removed.",
            WorkbookSeed.Describe([C(17, "A"), C(19, "Z")], [C(17, "B"), C(18, "N")]));

    [Fact]
    public void ClearingEverything_StillSaysSo() =>
        Assert.Equal(
            "Saved 0 custom categories: 0 added, 0 changed, 2 removed.",
            WorkbookSeed.Describe([C(17, "A"), C(18, "B")], []));

    [Fact]
    public void OneCategory_UsesTheSingularWord() =>
        Assert.Equal(
            "Saved 1 custom category: 1 added, 0 changed, 0 removed.",
            WorkbookSeed.Describe([], [C(17, "A")]));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookCatalogTests|FullyQualifiedName~MappingSheetTests|FullyQualifiedName~WorkbookSeedTests"`
Expected: build FAIL (`WorkbookCatalog`, `MappingSheet`, `WorkbookSeed` do not exist).

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookCatalog.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

public sealed record BuiltInCategory(int Code, string Name, string Description, string Folder);

/// <summary>
/// The version 3 built-in category table. Each folder is where the Sort tab files that category, so a sheet
/// that mixes codes and {type} builds one tree. Version 2 files keep the older table in
/// <see cref="WorkbookCategoryCatalog"/>.
/// </summary>
public static class WorkbookCatalog
{
    public const int FormatVersion = 3;

    public static readonly IReadOnlyList<BuiltInCategory> BuiltIn =
    [
        new(1, "Gear", "Equipment and accessories: head, body, hands, legs, feet, ears, neck, wrists, and rings.", "Gear"),
        new(2, "Weapon", "Weapon mods.", "Weapon"),
        new(3, "Face", "Face mesh and texture replacements.", "Face"),
        new(4, "Hair", "Hair mesh and texture replacements.", "Hair"),
        new(5, "Body", "Body mesh replacements, plus tail and ear meshes.", "Body"),
        new(6, "Skin", "Body texture-only retextures, no mesh replacement.", "Skin"),
        new(7, "NPC", "Mods that reskin or reshape a specific NPC rather than a playable character.", "NPC"),
        new(8, "Minion", "Minion companion mods.", "Minion"),
        new(9, "Mount", "Mount mods.", "Mount"),
        new(10, "Pet", "Battle pet mods: fairies, egis, turrets, and similar.", "Pet"),
        new(11, "Ornament", "Worn fashion accessories such as wings and halos.", "Ornament"),
        new(12, "Furniture", "Housing furniture and fixtures.", "Furniture"),
        new(13, "VFX", "Visual effects.", "Animation and VFX/VFX"),
        new(14, "Sound", "Sound replacements.", "Sound"),
        new(15, "Animation", "Animation and emote replacements.", "Animation and VFX"),
        new(16, "Others", "Anything that doesn't match a specific category, or needs manual review.", "Review"),
    ];

    public static string FolderFor(int code) => BuiltIn.First(c => c.Code == code).Folder;

    public static bool TryGetByName(string name, out BuiltInCategory category)
    {
        var found = BuiltIn.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        category = found ?? BuiltIn[^1];
        return found is not null;
    }
}
```

Create `PenumbraOrganizer.Plugin/Organizer/Workbook/MappingSheet.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

/// <summary>One data row of the Category Mapping sheet, as text. The file layer produces these.</summary>
public sealed record MappingSheetRow(int RowNumber, string CodeText, string Name, string Folder, bool HasFormula);

public sealed record CustomCode(int Code, string Folder);

/// <summary>
/// The whole mapping table, validated. <see cref="Custom"/> holds only codes with exactly one valid
/// definition; <see cref="Unusable"/> says why a code that appeared cannot be used.
/// </summary>
public sealed record MappingParseResult(
    IReadOnlyList<CustomCode> Custom,
    IReadOnlyDictionary<int, string> Unusable,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool HasErrors => Errors.Count > 0;

    public string? FolderFor(int code) => Custom.FirstOrDefault(c => c.Code == code)?.Folder;
}

/// <summary>Pure parser for the Category Mapping sheet (workbook spec, section 3).</summary>
public static class MappingSheet
{
    public const string SheetName = "Category Mapping";
    public const int FirstCustomCode = 17;
    public const int LastCustomCode = 9999;
    public const int MaxCustomRows = 200;

    // A whole number as Excel may have stored it: "17", "0017", "17.0", with surrounding spaces.
    private static readonly Regex CodePattern = new(
        @"^\s*([0-9]{1,5})(\.0+)?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static MappingParseResult Parse(IReadOnlyList<MappingSheetRow> rows)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var definitions = new Dictionary<int, List<(int Row, string? Folder)>>();
        var invalidReasons = new Dictionary<int, string>();
        var customRows = 0;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.CodeText) && string.IsNullOrWhiteSpace(row.Folder))
                continue;

            if (row.HasFormula)
            {
                errors.Add($"{SheetName}!A{row.RowNumber}: this row contains a formula. Replace it with plain text before importing.");
                continue;
            }

            var match = CodePattern.Match(row.CodeText ?? string.Empty);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                errors.Add($"{SheetName}!A{row.RowNumber}: \"{row.CodeText}\" is not a whole number from {FirstCustomCode} to {LastCustomCode}.");
                continue;
            }

            if (code is >= 1 and < FirstCustomCode)
            {
                WarnIfBuiltInEdited(row, code, warnings);
                continue;
            }

            if (code is < FirstCustomCode or > LastCustomCode)
            {
                errors.Add($"{SheetName}!A{row.RowNumber}: code {code} is outside {FirstCustomCode} to {LastCustomCode}.");
                continue;
            }

            customRows++;
            if (!definitions.TryGetValue(code, out var list))
                definitions[code] = list = [];

            var normalized = DestinationResolver.NormalizePath(row.Folder ?? string.Empty);
            if (normalized.Length == 0)
            {
                var message = $"the folder is empty for code {code}.";
                errors.Add($"{SheetName}!D{row.RowNumber}: {message}");
                invalidReasons.TryAdd(code, $"code {code} has an empty folder");
                list.Add((row.RowNumber, null));
            }
            else if (DestinationResolver.StructureError(normalized) is { } structure)
            {
                errors.Add($"{SheetName}!D{row.RowNumber}: {structure}");
                invalidReasons.TryAdd(code, $"code {code} has an invalid folder: {structure}");
                list.Add((row.RowNumber, null));
            }
            else
            {
                list.Add((row.RowNumber, normalized));
            }
        }

        if (customRows > MaxCustomRows)
            errors.Add($"{SheetName}: more than {MaxCustomRows} custom categories (found {customRows}).");

        var custom = new List<CustomCode>();
        var unusable = new Dictionary<int, string>();
        foreach (var (code, defs) in definitions.OrderBy(pair => pair.Key))
        {
            if (defs.Count > 1)
            {
                unusable[code] = $"code {code} is defined more than once";
                foreach (var def in defs)
                    errors.Add($"{SheetName}!A{def.Row}: code {code} is used twice.");
                continue;
            }

            if (defs[0].Folder is null)
            {
                unusable[code] = invalidReasons.TryGetValue(code, out var reason) ? reason : $"code {code} has an invalid folder";
                continue;
            }

            custom.Add(new CustomCode(code, defs[0].Folder!));
        }

        return new MappingParseResult(custom, unusable, errors, warnings);
    }

    private static void WarnIfBuiltInEdited(MappingSheetRow row, int code, List<string> warnings)
    {
        var builtIn = WorkbookCatalog.BuiltIn[code - 1];
        var changes = new List<string>();

        var name = (row.Name ?? string.Empty).Trim();
        if (name.Length > 0 && !name.Equals(builtIn.Name, StringComparison.OrdinalIgnoreCase))
            changes.Add($"name '{builtIn.Name}' to '{name}'");

        var folder = DestinationResolver.NormalizePath(row.Folder ?? string.Empty);
        if (folder.Length > 0 && !folder.Equals(builtIn.Folder, StringComparison.OrdinalIgnoreCase))
            changes.Add($"folder '{builtIn.Folder}' to '{folder}'");

        if (changes.Count > 0)
            warnings.Add(
                $"{SheetName} row {row.RowNumber}: built-in code {code} was edited ({string.Join(", ", changes)}); the built-in value was used.");
    }
}

/// <summary>Describes how an accepted import changed the saved custom categories.</summary>
public static class WorkbookSeed
{
    public static string? Describe(IReadOnlyList<CustomCode> before, IReadOnlyList<CustomCode> after)
    {
        var oldByCode = before.ToDictionary(c => c.Code, c => c.Folder);
        var newByCode = after.ToDictionary(c => c.Code, c => c.Folder);

        var added = newByCode.Keys.Count(code => !oldByCode.ContainsKey(code));
        var removed = oldByCode.Keys.Count(code => !newByCode.ContainsKey(code));
        var changed = newByCode.Count(pair => oldByCode.TryGetValue(pair.Key, out var folder)
            && !string.Equals(folder, pair.Value, StringComparison.Ordinal));

        if (added == 0 && removed == 0 && changed == 0)
            return null;

        var noun = after.Count == 1 ? "custom category" : "custom categories";
        return $"Saved {after.Count} {noun}: {added} added, {changed} changed, {removed} removed.";
    }
}
```

In `PenumbraOrganizer.Plugin/Configuration.cs`, add inside `Configuration` (next to the other workbook-unrelated settings, for example just before `HeliosphereMode`):

```csharp
    /// <summary>
    /// The custom workbook categories (code 17 and up) from the last accepted import. Only a seed for the next
    /// export: the sheet always wins at import and this is never consulted then.
    /// </summary>
    public List<WorkbookCustomCode> WorkbookCustomCodes { get; set; } = [];
```

and add this class at the bottom of the file (after the `Configuration` class):

```csharp
/// <summary>One saved custom workbook category. A plain class so any config serializer can read it.</summary>
public sealed class WorkbookCustomCode
{
    public int Code { get; set; }

    public string Folder { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookCatalogTests|FullyQualifiedName~MappingSheetTests|FullyQualifiedName~WorkbookSeedTests"`
Expected: PASS. If the Animation or Review catalog tests fail, the sorter's folder names differ from the table: fix the table (the sorter is the source of truth), not the test.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: version 3 built-in catalog, mapping sheet parser and saved custom codes"
```

---

### Task 4: Race applicability and the token provider

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/RaceSplitRouting.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookTokens.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookTokensTests.cs`

**Interfaces:**
- Consumes: `RaceSplitRouting.EvidenceFor`, `RaceFolderResolver.Resolve`, `RaceSplitOptions` (race-split work); `SortFolderSelectors.KnownSegment`; `DestinationTokens` (Task 2).
- Produces: `RaceSplitRouting.ApplicableRow(OrganizerModRow) -> RaceRows?`; `RaceSplitRouting.ResolveForToken(OrganizerModRow, RaceSplitOptions) -> RaceFolderResult?`; `WorkbookTokens.For(OrganizerModRow row, Func<string,string> canonicalizeCreator, RaceSplitOptions race) -> DestinationTokens`; `WorkbookTokens.TypeFolderFor(ModCategory?) -> string`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookTokensTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class WorkbookTokensTests
{
    private static ISet<string> S(params string[] v) => new HashSet<string>(v, StringComparer.Ordinal);

    private static OrganizerModRow Row(
        ModCategory? category, string author = "Thor", RowRaceData? keys = null,
        BodyPart part = BodyPart.Replacer, bool scaling = false, RspEvidence? rsp = null,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? hair = null) =>
        new()
        {
            Identifier = "m", Name = "Mod", Author = author, CurrentPath = "Mod", ProposedPath = "Mod",
            Category = category, KeyRaces = keys, BodyPart = part, IsRacialScaling = scaling, Rsp = rsp,
            HairByRaceCode = hair,
        };

    private static RowRaceData Keys(string[]? face = null, string[]? skin = null, string[]? tail = null) =>
        new(S(face ?? []).ToHashSet(), new HashSet<string>(), S(skin ?? []).ToHashSet(),
            S(tail ?? []).ToHashSet(), false);

    private static readonly RaceSplitOptions MasterOff = RaceSplitOptions.Off;

    private static DestinationTokens Tokens(OrganizerModRow row, RaceSplitOptions? race = null) =>
        WorkbookTokens.For(row, name => name, race ?? MasterOff);

    [Fact]
    public void Creator_IsTheCanonicalAuthorAsASortSegment()
    {
        var row = Row(ModCategory.Hair, author: "  Thor  ");
        var tokens = WorkbookTokens.For(row, name => name.Trim().ToUpperInvariant(), MasterOff);
        Assert.Equal("THOR", tokens.Creator);
    }

    [Fact]
    public void ANoAuthorMod_HasNoCreatorValue() =>
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Hair, author: "")).Creator);

    [Theory]
    [InlineData(ModCategory.Gear, "Gear")]
    [InlineData(ModCategory.Hair, "Hair")]
    [InlineData(ModCategory.Face, "Face")]
    [InlineData(ModCategory.Animation, "Animation and VFX")]
    [InlineData(ModCategory.VFX, "Animation and VFX")]
    [InlineData(ModCategory.NPC, "NPC")]
    public void Type_IsTheCategoryFolderAsSortNamesIt(ModCategory category, string expected) =>
        Assert.Equal(expected, Tokens(Row(category)).Type);

    [Fact]
    public void AnUnclassifiedMod_IsTypeReview() =>
        Assert.Equal("Review", Tokens(Row(null)).Type);

    [Fact]
    public void Race_ForHairWithOneRace_IsThatRace_EvenWithTheMasterOff()
    {
        var hair = new Dictionary<string, IReadOnlySet<int>> { ["1801"] = new HashSet<int> { 115 } };
        Assert.Equal("Viera", Tokens(Row(ModCategory.Hair, hair: hair)).Race);
    }

    [Fact]
    public void Race_ForFaceUsesItsKeys() =>
        Assert.Equal("Elezen", Tokens(Row(ModCategory.Face, keys: Keys(face: ["Elezen"]))).Race);

    [Fact]
    public void Race_ForSkinAndTailsUsesTheirKeys()
    {
        Assert.Equal("Miqo'te", Tokens(Row(ModCategory.Skin, keys: Keys(skin: ["Miqo'te"]))).Race);
        Assert.Equal(
            "Au Ra",
            Tokens(Row(ModCategory.Body, keys: Keys(tail: ["Au Ra"]), part: BodyPart.TailsEarsHorns)).Race);
    }

    [Fact]
    public void Race_ForAModWithNoRaceRow_IsEmptySoItCollapses()
    {
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Gear)).Race);
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Weapon)).Race);
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Body, part: BodyPart.Replacer)).Race);
    }

    [Fact]
    public void Race_ForAnApplicableModWithNoEvidence_IsMultiRace() =>
        Assert.Equal("Multi-race", Tokens(Row(ModCategory.Face, keys: null)).Race);

    [Fact]
    public void Race_ForRacialScaling_UsesTheRspRaces()
    {
        var one = new RspEvidence(S("Miqo'te").ToHashSet(), true, true);
        var two = new RspEvidence(S("Miqo'te", "Viera").ToHashSet(), true, true);
        Assert.Equal("Miqo'te", Tokens(Row(ModCategory.Gear, scaling: true, rsp: one)).Race);
        Assert.Equal("Multi-race", Tokens(Row(ModCategory.Gear, scaling: true, rsp: two)).Race);
    }

    [Fact]
    public void Race_FollowsTheMultiRaceToggleAndThePriority()
    {
        var two = new RspEvidence(S("Miqo'te", "Viera").ToHashSet(), true, true);
        var row = Row(ModCategory.Gear, scaling: true, rsp: two);
        var toggleOff = new RaceSplitOptions(false, RaceRows.All, false, RacePriority.Empty.Add("Viera"));
        Assert.Equal("Viera", Tokens(row, toggleOff).Race);
    }

    [Fact]
    public void ApplicableRow_IsIndependentOfEverySwitch()
    {
        Assert.Equal(RaceRows.Hair, RaceSplitRouting.ApplicableRow(Row(ModCategory.Hair)));
        Assert.Equal(RaceRows.Face, RaceSplitRouting.ApplicableRow(Row(ModCategory.Face, keys: Keys())));
        Assert.Equal(RaceRows.Skin, RaceSplitRouting.ApplicableRow(Row(ModCategory.Skin)));
        Assert.Equal(
            RaceRows.TailsEarsHorns,
            RaceSplitRouting.ApplicableRow(Row(ModCategory.Body, part: BodyPart.TailsEarsHorns)));
        Assert.Equal(RaceRows.RacialScaling, RaceSplitRouting.ApplicableRow(Row(ModCategory.Gear, scaling: true)));
        Assert.Null(RaceSplitRouting.ApplicableRow(Row(ModCategory.Gear)));
    }

    [Fact]
    public void ApplicableRow_ForAnIrisOnlyFaceMod_IsEyes()
    {
        var iris = new RowRaceData(
            new HashSet<string>(), new HashSet<string> { "Viera" }, new HashSet<string>(), new HashSet<string>(), true);
        Assert.Equal(RaceRows.Eyes, RaceSplitRouting.ApplicableRow(Row(ModCategory.Face, keys: iris)));
        Assert.Equal("Viera", Tokens(Row(ModCategory.Face, keys: iris)).Race);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookTokensTests"`
Expected: build FAIL (`WorkbookTokens`, `ApplicableRow`, `ResolveForToken` do not exist).

- [ ] **Step 3: Write the implementation**

In `PenumbraOrganizer.Plugin/Organizer/RaceSplitRouting.cs`, add these two methods inside the static class (for example right after `Governing`):

```csharp
    /// <summary>
    /// The row a race-applicable mod belongs to, whatever the switches say (race spec, section 2). Null for a
    /// mod with no race row at all (gear, weapons, a body replacer).
    /// </summary>
    public static RaceRows? ApplicableRow(OrganizerModRow row)
    {
        if (row.IsRacialScaling)
            return RaceRows.RacialScaling;

        return row.Category switch
        {
            ModCategory.Hair => RaceRows.Hair,
            ModCategory.Face => row.KeyRaces is { IrisOnly: true } ? RaceRows.Eyes : RaceRows.Face,
            ModCategory.Skin => RaceRows.Skin,
            ModCategory.Body when row.BodyPart == BodyPart.TailsEarsHorns => RaceRows.TailsEarsHorns,
            _ => null,
        };
    }

    /// <summary>
    /// The race folder the workbook's {race} token uses: the same rule Sort applies, but for any applicable
    /// mod whether or not its row or the master switch is on, because the user typed the token on purpose.
    /// Null when the mod is not race-applicable.
    /// </summary>
    public static RaceFolderResult? ResolveForToken(OrganizerModRow row, RaceSplitOptions options)
    {
        if (ApplicableRow(row) is not { } applicable)
            return null;

        return RaceFolderResolver.Resolve(
            EvidenceFor(row, applicable), options.MultiRaceToMultiRace, options.Priority);
    }
```

Create `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookTokens.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

/// <summary>Builds the values of {creator}, {type} and {race} for one mod from the plugin's own state.</summary>
public static class WorkbookTokens
{
    public static DestinationTokens For(
        OrganizerModRow row, Func<string, string> canonicalizeCreator, RaceSplitOptions race)
    {
        var creator = SortFolderSelectors.KnownSegment(canonicalizeCreator(row.Author)) ?? string.Empty;
        var raceFolder = RaceSplitRouting.ResolveForToken(row, race)?.Folder ?? string.Empty;
        return new DestinationTokens(creator, TypeFolderFor(row.Category), raceFolder);
    }

    /// <summary>
    /// The category's folder as the Sort tab names it, and only the category (never a sub-folder or a
    /// numbered tail). Animation and VFX share one parent; an unclassified mod goes to Review.
    /// </summary>
    public static string TypeFolderFor(ModCategory? category) => category switch
    {
        null => "Review",
        ModCategory.Animation or ModCategory.VFX => "Animation and VFX",
        _ => category.Value.ToString(),
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookTokensTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: workbook token values and race applicability independent of the switches"
```

---

### Task 5: The sort fingerprint and the export gate

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` (`SortSelection`), `PenumbraOrganizer.Plugin/Windows/SortSelectionFactory.cs` (`SortRunner`), `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/WorkbookExportGate.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/SortFingerprintTests.cs`, `PenumbraOrganizer.Plugin.Tests/Windows/WorkbookExportGateTests.cs`

**Interfaces:**
- Consumes: `SortSelection`, `SortSelectionFactory.FromConfig`, `SortRunner.Run`, `OrganizerState`.
- Produces: `SortSelection.Fingerprint() -> string`; `OrganizerState.SortFingerprint` (`string?`) and `OrganizerState.MarkSortedWith(string)`; `WorkbookExportGate.SuggestedUnavailableReason(OrganizerState, SortSelection current) -> string?` (null means available). `SortRunner.Run` marks the fingerprint after every sort.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/SortFingerprintTests.cs`:

```csharp
using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SortFingerprintTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    private static OrganizerState StateWithOneMod()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Old/A", ProposedPath = "Old/A" }],
            new HashSet<string>());
        return state;
    }

    private static SortSelection Selection(Configuration? config = null) =>
        SortSelectionFactory.FromConfig(config ?? new Configuration(), Labels);

    [Fact]
    public void ANewState_HasNoFingerprint() =>
        Assert.Null(new OrganizerState().SortFingerprint);

    [Fact]
    public void SortRunnerMarksTheFingerprintOfTheSelectionItSortedWith()
    {
        var state = StateWithOneMod();
        var selection = Selection();

        SortRunner.Run(state, selection, name => name, scope: null);

        Assert.Equal(selection.Fingerprint(), state.SortFingerprint);
    }

    [Fact]
    public void TheFingerprintIgnoresTheScopeFlag()
    {
        var selection = Selection();
        Assert.Equal(selection.Fingerprint(), (selection with { RootOnly = true }).Fingerprint());
    }

    [Fact]
    public void TheFingerprintSeesEverySettingThatCanChangeAFolder()
    {
        var baseline = Selection().Fingerprint();
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitGear = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitHair = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitEmotes = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSharedHandsFeet = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { BodyPriority = ["TBSE", "Rue+", "Bibo+"] }).Fingerprint());
        Assert.NotEqual(
            baseline,
            Selection(new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"] }).Fingerprint());
        Assert.NotEqual(
            Selection(new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"], RacePriority = ["Viera"] }).Fingerprint(),
            Selection(new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"], RacePriority = ["Elezen"] }).Fingerprint());
    }

    [Fact]
    public void AManualAssignmentClearsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        Assert.True(state.AssignManual("a", "Somewhere/A"));

        Assert.Null(state.SortFingerprint);
    }

    [Fact]
    public void AFailedAssignmentKeepsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        Assert.False(state.AssignManual("missing", "Somewhere/A"));

        Assert.Equal("x", state.SortFingerprint);
    }

    [Fact]
    public void StageMoveUnderClearsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        state.StageMoveUnder(["a"], "Unused");

        Assert.Null(state.SortFingerprint);
    }

    [Fact]
    public void AScanClearsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Old/A", ProposedPath = "Old/A" }],
            new HashSet<string>());

        Assert.Null(state.SortFingerprint);
    }
}
```

Create `PenumbraOrganizer.Plugin.Tests/Windows/WorkbookExportGateTests.cs`:

```csharp
using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class WorkbookExportGateTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    private static SortSelection Current(Configuration? config = null) =>
        SortSelectionFactory.FromConfig(config ?? new Configuration(), Labels);

    private static OrganizerState Scanned()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Old/A", ProposedPath = "Old/A" }],
            new HashSet<string>());
        return state;
    }

    [Fact]
    public void BeforeAnyScan_SaysScanFirst() =>
        Assert.Equal("Scan first.", WorkbookExportGate.SuggestedUnavailableReason(new OrganizerState(), Current()));

    [Fact]
    public void AfterAScanButNoSort_SaysPressSortFirst() =>
        Assert.Equal("Press Sort first.", WorkbookExportGate.SuggestedUnavailableReason(Scanned(), Current()));

    [Fact]
    public void AfterASortWithCurrentSettings_IsAvailable()
    {
        var state = Scanned();
        SortRunner.Run(state, Current(), name => name, scope: null);

        Assert.Null(WorkbookExportGate.SuggestedUnavailableReason(state, Current()));
    }

    [Fact]
    public void AfterTheSettingsChange_SaysSortAgain()
    {
        var state = Scanned();
        SortRunner.Run(state, Current(), name => name, scope: null);

        var changed = Current(new Configuration { SortSplitGear = true });

        Assert.Equal(
            "Sort settings changed, press Sort again.",
            WorkbookExportGate.SuggestedUnavailableReason(state, changed));
    }

    [Fact]
    public void AfterAnotherWayOfStagingPaths_SaysThePathsChanged()
    {
        var state = Scanned();
        SortRunner.Run(state, Current(), name => name, scope: null);
        state.AssignManual("a", "Somewhere/A");

        Assert.Equal(
            "Staged paths changed since the last sort, press Sort again.",
            WorkbookExportGate.SuggestedUnavailableReason(state, Current()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~SortFingerprintTests|FullyQualifiedName~WorkbookExportGateTests"`
Expected: build FAIL (`Fingerprint`, `SortFingerprint`, `MarkSortedWith`, `WorkbookExportGate` do not exist).

- [ ] **Step 3: Implement**

In `PenumbraOrganizer.Plugin/Windows/SortPanel.cs`, inside the `SortSelection` record struct (after the `HairPriority` property), add:

```csharp
    /// <summary>
    /// A stable text for "what produced the staged paths": every setting that can change a folder and nothing
    /// else (RootOnly is a scope, not a setting). The workbook export compares it to the current settings to
    /// decide whether the staged sort is still current.
    /// </summary>
    public string Fingerprint()
    {
        var race = Layout.Race;
        var raceText = race is null
            ? "-"
            : $"{race.Enabled}:{(int)race.Rows}:{race.MultiRaceToMultiRace}:{string.Join(',', race.Priority.Ranked)}";
        IEnumerable<string> bodyLabels = BodyPriority?.Labels ?? [];

        return string.Join(
            '|',
            Strategy, SplitGear, SplitNpc, SplitHair, HairRaceCode ?? "-", MidlanderFallback,
            SplitGearBody, SplitBodyMods, string.Join(',', bodyLabels),
            Layout.SharedHandsAndFeet, Layout.SplitHairByRace, Layout.SplitEmotes, raceText);
    }
```

In `PenumbraOrganizer.Plugin/Windows/SortSelectionFactory.cs`, replace the body of `SortRunner.Run` with:

```csharp
    public static int Run(
        OrganizerState state, SortSelection selection, Func<string, string> canonicalizeCreator, SortScope? scope)
    {
        var count = state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
            canonicalizeCreator, selection.HairPriority, selection.SplitGearBody, selection.SplitBodyMods,
            selection.BodyPriority, selection.Layout, scope);
        state.MarkSortedWith(selection.Fingerprint());
        return count;
    }
```

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`:

1. Next to `public SortSummary? LastSortSummary { get; private set; }`, add:

```csharp
    /// <summary>
    /// The fingerprint of the Sort settings that produced the staged paths, or null when the staged paths are
    /// not (or no longer) a pure Sort result. Anything that stages a path another way clears it, and so does a
    /// scan; the workbook's "As suggested by Sort" export is available only while it is set and current.
    /// </summary>
    public string? SortFingerprint { get; private set; }

    public void MarkSortedWith(string fingerprint) => SortFingerprint = fingerprint;
```

2. In `ReplaceScanAtomically`, right after `LastSortSummary = null;` add `SortFingerprint = null;`.
3. In `AssignManual`, in the success path after `row.StagedRace = null;` add `SortFingerprint = null;` (before `return true;`).
4. In `ApplyTemplate`, after the `foreach` loop and before `FinishProposals(touched);`, add `SortFingerprint = null;`.
5. In `StageMoveUnder`, after the `if (touched.Count == 0) return 0;` guard and before the `touchedSet` line, add `SortFingerprint = null;`.

Create `PenumbraOrganizer.Plugin/Windows/WorkbookExportGate.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>Why the workbook's "As suggested by Sort" export is unavailable right now, or null when it is.</summary>
public static class WorkbookExportGate
{
    public static string? SuggestedUnavailableReason(OrganizerState state, SortSelection current)
    {
        if (!state.HasScanned)
            return "Scan first.";

        if (state.SortFingerprint is null)
            return state.LastSortSummary is null
                ? "Press Sort first."
                : "Staged paths changed since the last sort, press Sort again.";

        return string.Equals(state.SortFingerprint, current.Fingerprint(), StringComparison.Ordinal)
            ? null
            : "Sort settings changed, press Sort again.";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass, then the whole suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~SortFingerprintTests|FullyQualifiedName~WorkbookExportGateTests"` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, then every pre-existing test still passes.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: sort fingerprint and the export gate for the staged sort"
```

---

### Task 6: Export, version 3 (modes, mapping sheet, `_leaf`, metadata)

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookModels.cs`, `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs`, `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`, `PenumbraOrganizer.Plugin/Plugin.cs`
- Modify tests: `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`, `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterInventoryTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookExportTests.cs`

**Interfaces:**
- Consumes: `CustomCode`, `WorkbookCatalog`, `MappingSheet.SheetName` (Task 3); `OrganizerState` rows.
- Produces:
  - `enum WorkbookExportMode { AsIs, AsSuggestedBySort }`; `WorkbookExportRequest(ScanInventory Inventory, IReadOnlyList<OrganizerModProposal> Proposals, WorkbookExportMode Mode, string? SortFingerprint, IReadOnlyList<CustomCode> CustomCodes, IReadOnlyDictionary<string,string> StagedLeaves)`.
  - `WorkbookService(ILogger<WorkbookService>)` (the canonicalizer parameter is gone) with `ExportAsync(WorkbookExportRequest, string workbookPath, CancellationToken)`; the old five-argument export is removed.
  - `WorkbookAdapter.ToProposals(OrganizerState, bool staged)` and `WorkbookAdapter.StagedLeaves(OrganizerState)`; `WorkbookAdapter.ToOrganizationPreferences` is removed.
  - Sheet layout (version 3): `Edit Destinations` headers `#, mod name, author, current folder, mod type, protected, destination, _internal_key, _leaf` (columns 8 and 9 hidden); `Category Mapping` rows 1-3 text, row 4 red banner, row 5 header `code, name, description, folder, example`, rows 6-21 the built-ins, custom rows from row 22; hidden `_Metadata` gains `exportMode` and `sortFingerprint`, and `formatVersion` is `3`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookExportTests.cs`:

```csharp
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class WorkbookExportTests
{
    private static WorkbookService CreateService() => new(NullLogger<WorkbookService>.Instance);

    private static PenumbraInstallation Installation() => new(
        ConfigurationPath: "C:/Penumbra/Penumbra.json", ConfigDirectory: "C:/Penumbra", ModRoot: "C:/Penumbra/Mods",
        PluginAssemblyPath: null, PluginManifestPath: null, InstalledVersion: null,
        Confidence: DiscoveryConfidence.High, Evidence: [], Warnings: []);

    private static string NewPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "workbook.xlsx");
    }

    private static OrganizerState State(params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>(rows.Where(r => r.Protected).Select(r => r.Identifier)));
        return state;
    }

    private static OrganizerModRow Row(string id, string name, string currentPath, string proposedPath, bool prot = false) =>
        new()
        {
            Identifier = id, Name = name, Author = "Author", CurrentPath = currentPath, ProposedPath = proposedPath,
            Category = ModCategory.Gear, Protected = prot,
        };

    private static async Task<string> Export(
        OrganizerState state, WorkbookExportMode mode, string? fingerprint = null,
        IReadOnlyList<CustomCode>? custom = null)
    {
        var staged = mode == WorkbookExportMode.AsSuggestedBySort;
        var request = new WorkbookExportRequest(
            WorkbookAdapter.ToScanInventory(state, Installation()),
            WorkbookAdapter.ToProposals(state, staged),
            mode,
            fingerprint,
            custom ?? [],
            staged ? WorkbookAdapter.StagedLeaves(state) : new Dictionary<string, string>());
        var path = NewPath();
        await CreateService().ExportAsync(request, path, CancellationToken.None);
        return path;
    }

    [Fact]
    public async Task AsIs_WritesTheCurrentFolder_AndBlanksProtectedAndTopLevelMods()
    {
        var state = State(
            Row("nested", "Nested", "Gear/Nested", "Gear/Nested"),
            Row("prot", "Prot", "Gear/Prot", "Gear/Prot", prot: true),
            Row("root", "Root", "Root", "Root"));

        var path = await Export(state, WorkbookExportMode.AsIs);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Edit Destinations");
        Assert.Equal("Gear", sheet.Cell(2, 7).GetString());
        Assert.Equal(string.Empty, sheet.Cell(3, 7).GetString());
        Assert.Equal(string.Empty, sheet.Cell(4, 7).GetString());
        Assert.Equal(string.Empty, sheet.Cell(2, 9).GetString()); // _leaf is only for the staged sort
    }

    [Fact]
    public async Task AsSuggested_WritesTheStagedFolderAndLeaf_TopLevelMovesIncluded()
    {
        var state = State(
            Row("a", "Alpha", "Alpha", "Gear/Thor/Alpha"),
            Row("b", "Beta", "Old/Beta", "Gear/Thor/Beta (2)"),
            Row("p", "Prot", "Gear/Prot", "Gear/Prot", prot: true));

        var path = await Export(state, WorkbookExportMode.AsSuggestedBySort, fingerprint: "fp");

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Edit Destinations");
        Assert.Equal("Gear/Thor", sheet.Cell(2, 7).GetString());
        Assert.Equal("Alpha", sheet.Cell(2, 9).GetString());
        Assert.Equal("Gear/Thor", sheet.Cell(3, 7).GetString());
        Assert.Equal("Beta (2)", sheet.Cell(3, 9).GetString());
        Assert.Equal(string.Empty, sheet.Cell(4, 7).GetString());
    }

    [Fact]
    public async Task TheStagedLeafColumnAndTheInternalKeyAreHidden()
    {
        var path = await Export(State(Row("a", "Alpha", "Gear/Alpha", "Gear/Alpha")), WorkbookExportMode.AsIs);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Edit Destinations");
        Assert.Equal("_internal_key", sheet.Cell(1, 8).GetString());
        Assert.Equal("_leaf", sheet.Cell(1, 9).GetString());
        Assert.True(sheet.Column(8).IsHidden);
        Assert.True(sheet.Column(9).IsHidden);
    }

    [Fact]
    public async Task TheMetadataRecordsTheVersionTheModeAndTheFingerprint()
    {
        var path = await Export(
            State(Row("a", "Alpha", "Gear/Alpha", "Gear/Alpha")), WorkbookExportMode.AsSuggestedBySort, "fp-123");

        using var workbook = new XLWorkbook(path);
        var meta = workbook.Worksheet("_Metadata");
        var values = Enumerable.Range(1, 12)
            .Select(r => (Key: meta.Cell(r, 1).GetString(), Value: meta.Cell(r, 2).GetString()))
            .Where(p => p.Key.Length > 0)
            .ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("3", values["formatVersion"]);
        Assert.Equal("AsSuggestedBySort", values["exportMode"]);
        Assert.Equal("fp-123", values["sortFingerprint"]);
    }

    [Fact]
    public async Task TheMappingSheet_HasTheBannerTheBuiltInsAndTheSavedCustomRows()
    {
        var path = await Export(
            State(Row("a", "Alpha", "Gear/Alpha", "Gear/Alpha")), WorkbookExportMode.AsIs,
            custom: [new CustomCode(17, "Face/Sculpts"), new CustomCode(20, "Mine/{type}")]);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Category Mapping");
        Assert.Contains("Rows 1-16 are built in", sheet.Cell(4, 1).GetString());
        Assert.Equal(new[] { "code", "name", "description", "folder", "example" },
            Enumerable.Range(1, 5).Select(c => sheet.Cell(5, c).GetString()));

        // rows 6-21 are codes 1-16 with the Sort tab's folders
        Assert.Equal("1", sheet.Cell(6, 1).GetString());
        Assert.Equal("Gear", sheet.Cell(6, 4).GetString());
        Assert.Equal("13", sheet.Cell(18, 1).GetString());
        Assert.Equal("Animation and VFX/VFX", sheet.Cell(18, 4).GetString());
        Assert.Equal("Animation and VFX", sheet.Cell(20, 4).GetString());
        Assert.Equal("16", sheet.Cell(21, 1).GetString());
        Assert.Equal("Review", sheet.Cell(21, 4).GetString());

        // the saved custom rows follow, ascending
        Assert.Equal("17", sheet.Cell(22, 1).GetString());
        Assert.Equal("Face/Sculpts", sheet.Cell(22, 4).GetString());
        Assert.Equal("20", sheet.Cell(23, 1).GetString());
        Assert.Equal("Mine/{type}", sheet.Cell(23, 4).GetString());
    }

    [Fact]
    public async Task TheResultNamesTheMode()
    {
        var state = State(Row("a", "Alpha", "Gear/Alpha", "Gear/Alpha"));
        var request = new WorkbookExportRequest(
            WorkbookAdapter.ToScanInventory(state, Installation()), WorkbookAdapter.ToProposals(state, false),
            WorkbookExportMode.AsIs, null, [], new Dictionary<string, string>());

        var result = await CreateService().ExportAsync(request, NewPath(), CancellationToken.None);

        Assert.Equal("current folders", result.StrategyLabel);
        Assert.Contains("1 mod row(s)", result.Summary);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookExportTests"`
Expected: build FAIL (`WorkbookExportRequest`, `WorkbookExportMode`, the new constructor and `ToProposals(state, bool)` do not exist).

- [ ] **Step 3: Models and adapter**

Append to `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookModels.cs` (inside the namespace, after the existing records, before `WorkbookCategoryCatalog`):

```csharp
public enum WorkbookExportMode
{
    /// <summary>Each mod's current folder.</summary>
    AsIs,

    /// <summary>The folders the last Sort staged.</summary>
    AsSuggestedBySort,
}

/// <summary>Everything one export needs. The service never reads the plugin's state itself.</summary>
public sealed record WorkbookExportRequest(
    ScanInventory Inventory,
    IReadOnlyList<OrganizerModProposal> Proposals,
    WorkbookExportMode Mode,
    string? SortFingerprint,
    IReadOnlyList<CustomCode> CustomCodes,
    IReadOnlyDictionary<string, string> StagedLeaves);
```

In the same file update the two rule texts in `WorkbookCategoryCatalog` (`ReviewRule` mentions the old `16/Others`):

```csharp
    public const string ReviewRule = "Use mod type Others or destination 16 when a row still needs manual attention (it files under Review).";
```

In `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`, replace `ToProposals` and `ToOrganizationPreferences` with:

```csharp
    /// <summary>
    /// The proposals the export reads. With <paramref name="staged"/> each proposal's folder is the folder part
    /// of the row's staged path (what the last Sort proposed); otherwise it is the current folder.
    /// </summary>
    public static IReadOnlyList<OrganizerModProposal> ToProposals(OrganizerState state, bool staged = false)
        => state.Mods.Select(row =>
        {
            var (folder, _) = SplitPath(row.CurrentPath);
            var proposed = staged ? SplitPath(row.ProposedPath).Folder : folder;
            return new OrganizerModProposal
            {
                StableScanId = row.Identifier,
                Name = row.Name,
                CurrentVirtualFolder = folder,
                ProposedVirtualFolder = proposed,
                OriginalCreator = row.Author,
                Protected = row.Protected,
                OriginalProtected = row.Protected,
            };
        }).ToList();

    /// <summary>Each unprotected row's staged leaf (it can differ from the name: a duplicate install is "Name (2)").</summary>
    public static IReadOnlyDictionary<string, string> StagedLeaves(OrganizerState state)
        => state.Mods
            .Where(row => !row.Protected)
            .ToDictionary(row => row.Identifier, row => SplitPath(row.ProposedPath).Leaf, StringComparer.Ordinal);
```

Delete `ToOrganizationPreferences` and, in `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterInventoryTests.cs`, delete the test method that calls it (`ToOrganizationPreferences_CarriesRequestedStrategy`, with its `[Theory]` and `[InlineData]` attributes).

- [ ] **Step 4: The service export**

Apply these edits to `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs`. Names are as after Task 1.

(a) Replace the fields and constructor (the block from `public sealed class WorkbookService` through the closing brace of the constructor) with:

```csharp
public sealed class WorkbookService
{
    private const string EditableSheetName = "Edit Destinations";
    private const string CategorySheetName = MappingSheet.SheetName;
    private const string MetadataSheetName = "_Metadata";
    private readonly ILogger<WorkbookService> _logger;

    public WorkbookService(ILogger<WorkbookService> logger)
    {
        _logger = logger;
    }
```

(b) Replace the whole `ExportAsync` method (from `public Task<WorkbookExportResult> ExportAsync(` to the `}, cancellationToken);` that ends it) with:

```csharp
    public Task<WorkbookExportResult> ExportAsync(
        WorkbookExportRequest request,
        string workbookPath,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workbookDirectory = Path.GetDirectoryName(workbookPath);
            if (string.IsNullOrWhiteSpace(workbookDirectory))
                throw new InvalidOperationException("The workbook export path is invalid.");
            Directory.CreateDirectory(workbookDirectory);

            var inventory = request.Inventory;
            var generatedAtUtc = DateTimeOffset.UtcNow;
            var sourceExportId = $"workbook-{generatedAtUtc:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
            var scanIdentity = ScanIdentity.BuildScanIdentity(inventory);
            var installationIdentity = ScanIdentity.BuildInstallationIdentity(inventory.Installation);

            using var workbook = new XLWorkbook();
            BuildEditableSheet(workbook, request);
            BuildCategorySheet(workbook, request.CustomCodes);
            BuildMetadataSheet(workbook, sourceExportId, generatedAtUtc, scanIdentity, installationIdentity, request);
            workbook.SaveAs(workbookPath);

            var label = request.Mode == WorkbookExportMode.AsIs ? "current folders" : "the staged Sort";
            var summary = $"Workbook exported with {inventory.Mods.Count} mod row(s) using {label}.";
            _logger.LogInformation("Exported workbook workflow file to {WorkbookPath}", workbookPath);
            return new WorkbookExportResult(
                workbookPath,
                sourceExportId,
                generatedAtUtc,
                scanIdentity,
                installationIdentity,
                label,
                inventory.Mods.Count,
                summary);
        }, cancellationToken);
```

(c) Replace the whole `BuildEditableSheet` method with:

```csharp
    private void BuildEditableSheet(XLWorkbook workbook, WorkbookExportRequest request)
    {
        var inventory = request.Inventory;
        var liveProtected = request.Proposals.ToDictionary(p => p.StableScanId, p => p.Protected, StringComparer.Ordinal);
        var stagedFolders = request.Proposals.ToDictionary(p => p.StableScanId, p => p.ProposedVirtualFolder, StringComparer.Ordinal);

        var sheet = workbook.Worksheets.Add(EditableSheetName);
        var headers = new[] { "#", "mod name", "author", "current folder", "mod type", "protected", "destination", "_internal_key", "_leaf" };
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(1, index + 1).Value = headers[index];

        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E9EEF4");
        for (var editableColumn = 5; editableColumn <= 7; editableColumn++)
            sheet.Cell(1, editableColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#DFF3E4");
        sheet.SheetView.FreezeRows(1);

        var row = 2;
        var displayNumber = 1;
        foreach (var mod in inventory.Mods.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var category = WorkbookCategoryCatalog.Detect(mod);
            var isProtected = liveProtected.TryGetValue(mod.StableScanId, out var liveValue) ? liveValue : mod.Protected;

            // A protected mod is always blank ("leave alone"). As is writes the current folder (a top-level
            // mod's is empty, so it is blank too). The staged Sort writes the staged folder for every
            // unprotected mod, top-level moves included, plus the staged leaf so a duplicate install
            // ("Name (2)") does not collide again on import.
            var destination = string.Empty;
            var leaf = string.Empty;
            if (!isProtected)
            {
                if (request.Mode == WorkbookExportMode.AsIs)
                {
                    destination = mod.CurrentVirtualFolder;
                }
                else if (stagedFolders.TryGetValue(mod.StableScanId, out var stagedFolder))
                {
                    destination = stagedFolder;
                    leaf = request.StagedLeaves.TryGetValue(mod.StableScanId, out var stagedLeaf) ? stagedLeaf : string.Empty;
                }
            }

            sheet.Cell(row, 1).Value = displayNumber++;
            sheet.Cell(row, 2).Value = mod.Name;
            sheet.Cell(row, 3).Value = mod.Author;
            sheet.Cell(row, 4).Value = mod.CurrentVirtualFolder;
            sheet.Cell(row, 5).Value = category.Name;
            sheet.Cell(row, 6).Value = isProtected ? "TRUE" : "FALSE";
            sheet.Cell(row, 7).Value = destination;
            sheet.Cell(row, 8).Value = mod.StableScanId;
            sheet.Cell(row, 9).Value = leaf;
            row++;
        }

        var range = sheet.Range(1, 1, Math.Max(row - 1, 1), headers.Length);
        range.CreateTable();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, headers.Length).Style.NumberFormat.Format = "@";
        sheet.Column(8).Hide();
        sheet.Column(9).Hide();
        sheet.Column(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(2, 1, Math.Max(row - 1, 2), 4).Style.Protection.SetLocked(true);
        sheet.Range(2, 8, Math.Max(row - 1, 2), 9).Style.Protection.SetLocked(true);
        sheet.Range(2, 5, Math.Max(row - 1, 2), 7).Style.Protection.SetLocked(false);
        sheet.Protect("PenumbraOrganizer");
        sheet.Columns().AdjustToContents();
    }
```

(d) Replace the whole `BuildCategorySheet` method with:

```csharp
    private static void BuildCategorySheet(XLWorkbook workbook, IReadOnlyList<CustomCode> customCodes)
    {
        var sheet = workbook.Worksheets.Add(CategorySheetName);
        sheet.Cell(1, 1).Value = "This sheet explains category shorthand and lets you add your own categories (code 17 and up).";
        sheet.Cell(2, 1).Value = WorkbookCategoryCatalog.BlankDestinationRule;
        sheet.Cell(3, 1).Value = WorkbookCategoryCatalog.ReviewRule;
        sheet.Cell(4, 1).Value = "Rows 1-16 are built in. Do not edit them: edits are ignored.";
        var banner = sheet.Range(4, 1, 4, 5);
        banner.Style.Font.Bold = true;
        banner.Style.Font.FontColor = XLColor.White;
        banner.Style.Fill.BackgroundColor = XLColor.FromHtml("#C0392B");

        sheet.Cell(5, 1).Value = "code";
        sheet.Cell(5, 2).Value = "name";
        sheet.Cell(5, 3).Value = "description";
        sheet.Cell(5, 4).Value = "folder";
        sheet.Cell(5, 5).Value = "example";

        var row = 6;
        foreach (var category in WorkbookCatalog.BuiltIn)
        {
            sheet.Cell(row, 1).Value = category.Code;
            sheet.Cell(row, 2).Value = category.Name;
            sheet.Cell(row, 3).Value = category.Description;
            sheet.Cell(row, 4).Value = category.Folder;
            sheet.Cell(row, 5).Value = $"{category.Code}/{{creator}}";
            row++;
        }

        foreach (var custom in customCodes.OrderBy(c => c.Code))
        {
            sheet.Cell(row, 1).Value = custom.Code;
            sheet.Cell(row, 2).Value = "custom";
            sheet.Cell(row, 3).Value = "Your own category. Only code and folder are used.";
            sheet.Cell(row, 4).Value = custom.Folder;
            sheet.Cell(row, 5).Value = $"{custom.Code}/{{creator}}";
            row++;
        }

        sheet.Columns().AdjustToContents();
    }
```

(e) Replace `BuildMetadataSheet` (signature and the dictionary) so it takes the request:

```csharp
    private static void BuildMetadataSheet(
        XLWorkbook workbook,
        string sourceExportId,
        DateTimeOffset generatedAtUtc,
        string scanIdentity,
        string installationIdentity,
        WorkbookExportRequest request)
    {
        var sheet = workbook.Worksheets.Add(MetadataSheetName);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["formatVersion"] = WorkbookCatalog.FormatVersion.ToString(),
            ["sourceExportId"] = sourceExportId,
            ["generatedAtUtc"] = generatedAtUtc.ToString("O"),
            ["scanIdentity"] = scanIdentity,
            ["installationIdentity"] = installationIdentity,
            ["strategy"] = request.Mode.ToString(),
            ["exportMode"] = request.Mode.ToString(),
            ["sortFingerprint"] = request.SortFingerprint ?? string.Empty,
            ["blankDestinationRule"] = WorkbookCategoryCatalog.BlankDestinationRule,
            ["reviewRule"] = WorkbookCategoryCatalog.ReviewRule,
        };

        var row = 1;
        foreach (var pair in values)
        {
            sheet.Cell(row, 1).Value = pair.Key;
            sheet.Cell(row, 2).Value = pair.Value;
            row++;
        }

        sheet.Visibility = XLWorksheetVisibility.VeryHidden;
    }
```

(f) Delete these methods, which nothing uses any more: `BuildSuggestedDestination`, `BuildCreatorFolder`, `StrategyLabel`. Remove the now-unused `using PenumbraOrganizer.Core.Interfaces;` only if the build says it is unused (the import code in Task 7 still uses `ICreatorCanonicalizer` nowhere, so it can go).

(g) `ValidateMetadata` still compares `parsedVersion != WorkbookCategoryCatalog.CurrentFormatVersion`; replace that comparison so version 2 and 3 are both accepted:

```csharp
        if (!meta.TryGetValue("formatVersion", out var formatVersion) ||
            !int.TryParse(formatVersion, out var parsedVersion) ||
            parsedVersion is not (2 or 3))
        {
            errors.Add("The workbook format version is unsupported.");
        }
```

and delete the now-unused `CurrentFormatVersion` constant from `WorkbookCategoryCatalog` in `WorkbookModels.cs`.

(h) The old import code still calls the old `BuildImportResult`/rules; leave the import unchanged in this task (it is replaced in Task 7). It must still compile: it references `WorkbookCategoryCatalog` only, which still exists.

- [ ] **Step 5: Plugin call site and the old interop tests**

In `PenumbraOrganizer.Plugin/Plugin.cs`:
- the construction becomes `_workbookService = new WorkbookService(new Organizer.PluginLogAdapter<WorkbookService>(Log));`
- replace the whole `ExportWorkbook` method with:

```csharp
    /// <summary>Why "As suggested by Sort" cannot be exported right now, or null when it can.</summary>
    internal string? SuggestedWorkbookUnavailableReason()
    {
        var labels = OrganizerState.BodyLabels ?? BundledBodyLabels;
        return Windows.WorkbookExportGate.SuggestedUnavailableReason(
            OrganizerState, Windows.SortSelectionFactory.FromConfig(Config, labels));
    }

    internal string ExportWorkbook(Organizer.Workbook.WorkbookExportMode mode, string destinationPath)
    {
        var staged = mode == Organizer.Workbook.WorkbookExportMode.AsSuggestedBySort;
        if (staged && SuggestedWorkbookUnavailableReason() is { } reason)
            throw new InvalidOperationException(reason);

        var request = new Organizer.Workbook.WorkbookExportRequest(
            Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation()),
            Organizer.WorkbookAdapter.ToProposals(OrganizerState, staged),
            mode,
            staged ? OrganizerState.SortFingerprint : null,
            Config.WorkbookCustomCodes.Select(c => new Organizer.Workbook.CustomCode(c.Code, c.Folder)).ToList(),
            staged ? Organizer.WorkbookAdapter.StagedLeaves(OrganizerState) : new Dictionary<string, string>());

        // ClosedXML's SaveAs validates the file extension and rejects anything but
        // .xlsx/.xlsm/.xltx/.xltm, so the temp name must keep .xlsx as its actual extension -
        // "organizer-workbook.xlsx.tmp" fails, "organizer-workbook.tmp.xlsx" doesn't.
        var tempPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $"{Path.GetFileNameWithoutExtension(destinationPath)}.tmp{Path.GetExtension(destinationPath)}");
        var export = _workbookService.ExportAsync(request, tempPath, CancellationToken.None)
            .GetAwaiter().GetResult();
        try
        {
            File.Move(export.WorkbookPath, destinationPath, overwrite: true);
        }
        catch (IOException)
        {
            File.Delete(export.WorkbookPath);
            throw new IOException("The workbook could not be replaced; another program may be using it.");
        }

        return destinationPath;
    }
```

`MainWindow` still calls `_plugin.ExportWorkbook(strategy, path)`; Task 9 changes that call. To keep the build green in this task, change `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` temporarily: in the private `ExportWorkbook(PenumbraOrganizer.Core.Models.OrganizationStrategy strategy)` method replace the call `_plugin.ExportWorkbook(strategy, path)` with `_plugin.ExportWorkbook(PenumbraOrganizer.Plugin.Organizer.Workbook.WorkbookExportMode.AsIs, path)` (the unused `strategy` parameter is removed in Task 9).

Update `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`: the four tests build the export with the old five-argument call. Replace `CreateService()` with `private static WorkbookService CreateService() => new(NullLogger<WorkbookService>.Instance);` and add this helper at the top of the class:

```csharp
    private static WorkbookExportRequest AsIsRequest(OrganizerState state) => new(
        WorkbookAdapter.ToScanInventory(state, MakeInstallation()),
        WorkbookAdapter.ToProposals(state, staged: false),
        WorkbookExportMode.AsIs, null, [], new Dictionary<string, string>());
```

then in each of the four tests replace the multi-line `service.ExportAsync(inventory, WorkbookAdapter.ToProposals(state), WorkbookAdapter.ToOrganizationPreferences(...), MakeWorkbookPath(), CancellationToken.None)` call with `service.ExportAsync(AsIsRequest(state), MakeWorkbookPath(), CancellationToken.None)`. Keep each test's `inventory` variable (the import still takes it). In `ProtectionOnlyEdit_BlankDestination_AppliesProtectionWithoutMoving` the exported destination is now `Gear` (the current folder) instead of blank; the assertions (protected, proposed path `Gear/Baz Mod`) still hold. Also change that test's comment to say so. In `AsIsExport_ImportedUnedited_LeavesEveryProposedPathUnchanged` the three destination assertions already match the as-is behavior.

- [ ] **Step 6: Run the new tests, then the whole suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookExportTests|FullyQualifiedName~WorkbookInteropTests"` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS. The interop round trips prove an exported v3 file still imports through the (not yet reworked) import: the metadata check accepts version 3, and the old import ignores the new column and the new mapping layout.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: workbook export modes, version 3 sheets, leaf column and mapping rows"
```

---

### Task 7: Import, version 3 (mapping, resolver, tokens, report)

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookModels.cs`, `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs`, `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`, `PenumbraOrganizer.Plugin/Plugin.cs`, `PenumbraOrganizer.Plugin/Organizer/WorkbookImportResultView.cs`, `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`
- Modify tests: `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookImportTests.cs`, extend `WorkbookAdapterApplyImportResultTests.cs`

**Interfaces:**
- Consumes: `DestinationResolver`, `DestinationRules`, `DestinationTokens` (Task 2); `WorkbookCatalog`, `MappingSheet`, `MappingParseResult`, `CustomCode`, `WorkbookSeed` (Task 3); `WorkbookTokens` (Task 4); the export of Task 6.
- Produces:
  - `WorkbookImportContext(Func<string, DestinationTokens> TokensFor)` with `WorkbookImportContext.Empty`; `WorkbookCodeUsage(int Code, string Folder, int ModCount)`; `WorkbookImportCounts(int Ready, int Unchanged, int Stale, int Blocked, int Warnings)`.
  - `WorkbookImportRow` gains a last positional parameter `string? Leaf = null`; `WorkbookImportResult` gains `int FormatVersion = 2`, `IReadOnlyList<CustomCode>? CustomCodes = null`, `IReadOnlyList<WorkbookCodeUsage>? CodeUsage = null`, `WorkbookImportCounts? Counts = null` (all with defaults, so existing constructions compile).
  - `WorkbookService.ImportAsync(string workbookPath, ScanInventory inventory, WorkbookImportContext context, CancellationToken ct)`.
  - `WorkbookAdapter.ApplyImportResult` uses the row's `Leaf` when present.
  - Plugin: `ImportWorkbook(string path)` builds the token context, applies the result, saves an accepted clean mapping table as the seed, and appends the seed message to the summary.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Workbook/WorkbookImportTests.cs`:

```csharp
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class WorkbookImportTests
{
    private static WorkbookService CreateService() => new(NullLogger<WorkbookService>.Instance);

    private static PenumbraInstallation Installation() => new(
        ConfigurationPath: "C:/Penumbra/Penumbra.json", ConfigDirectory: "C:/Penumbra", ModRoot: "C:/Penumbra/Mods",
        PluginAssemblyPath: null, PluginManifestPath: null, InstalledVersion: null,
        Confidence: DiscoveryConfidence.High, Evidence: [], Warnings: []);

    private static string NewPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "workbook.xlsx");
    }

    private static OrganizerModRow Row(string id, string name, string currentPath, string proposedPath, bool prot = false) =>
        new()
        {
            Identifier = id, Name = name, Author = "Thor", CurrentPath = currentPath, ProposedPath = proposedPath,
            Category = ModCategory.Hair, Protected = prot,
        };

    private static OrganizerState State(params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>(rows.Where(r => r.Protected).Select(r => r.Identifier)));
        return state;
    }

    private static readonly WorkbookImportContext ThorHairViera =
        new(_ => new DestinationTokens("Thor", "Hair", "Viera"));

    private static async Task<string> Export(OrganizerState state, WorkbookExportMode mode = WorkbookExportMode.AsIs,
        IReadOnlyList<CustomCode>? custom = null)
    {
        var staged = mode == WorkbookExportMode.AsSuggestedBySort;
        var request = new WorkbookExportRequest(
            WorkbookAdapter.ToScanInventory(state, Installation()), WorkbookAdapter.ToProposals(state, staged), mode,
            staged ? "fp" : null, custom ?? [],
            staged ? WorkbookAdapter.StagedLeaves(state) : new Dictionary<string, string>());
        var path = NewPath();
        await CreateService().ExportAsync(request, path, CancellationToken.None);
        return path;
    }

    private static void Edit(string path, Action<XLWorkbook> change)
    {
        using var workbook = new XLWorkbook(path);
        change(workbook);
        workbook.Save();
    }

    private static Task<WorkbookImportResult> Import(
        string path, OrganizerState state, WorkbookImportContext? context = null) =>
        CreateService().ImportAsync(
            path, WorkbookAdapter.ToScanInventory(state, Installation()), context ?? ThorHairViera, CancellationToken.None);

    [Fact]
    public async Task ACustomCodeAddedInTheSheet_ExpandsWithTokens_AndIsOfferedAsTheSeed()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var map = wb.Worksheet("Category Mapping");
            map.Cell(22, 1).Value = 17;
            map.Cell(22, 4).Value = "Mine/{type}";
            wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "17/{creator}/{race}";
        });

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.FormatVersion);
        Assert.Equal("Mine/Hair/Thor/Viera/Alpha", state.Mods.Single().ProposedPath);
        Assert.Equal(new[] { new CustomCode(17, "Mine/{type}") }, result.CustomCodes);
        var usage = Assert.Single(result.CodeUsage!);
        Assert.Equal(new WorkbookCodeUsage(17, "Mine/{type}", 1), usage);
    }

    [Fact]
    public async Task AnUnknownCode_FailsOnItsCellInPlainWords_AndTheRowIsNotMoved()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "99/Thor");

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        var error = Assert.Single(result.Errors);
        Assert.Equal("Edit Destinations!G2: invalid destination for a: code 99 is not in the Category Mapping sheet", error);
        Assert.Equal("Hair/Alpha", state.Mods.Single().ProposedPath);
        Assert.Equal(1, result.Counts!.Blocked);
    }

    [Fact]
    public async Task ADuplicatedCustomCode_BlocksOnlyTheModsThatUseIt_AndNothingIsOfferedAsTheSeed()
    {
        var state = State(
            Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"),
            Row("b", "Beta", "Hair/Beta", "Hair/Beta"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var map = wb.Worksheet("Category Mapping");
            map.Cell(22, 1).Value = 17;
            map.Cell(22, 4).Value = "One";
            map.Cell(23, 1).Value = 17;
            map.Cell(23, 4).Value = "Two";
            var sheet = wb.Worksheet("Edit Destinations");
            sheet.Cell(2, 7).Value = "17/x";   // Alpha uses the broken code
            sheet.Cell(3, 7).Value = "Fine/x"; // Beta does not
        });

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Contains("Category Mapping!A22: code 17 is used twice.", result.Errors);
        Assert.Contains("Category Mapping!A23: code 17 is used twice.", result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("code 17 cannot be used: code 17 is defined more than once"));
        Assert.Equal("Hair/Alpha", state.Mods.Single(m => m.Identifier == "a").ProposedPath);
        Assert.Equal("Fine/x/Beta", state.Mods.Single(m => m.Identifier == "b").ProposedPath);
        Assert.Null(result.CustomCodes);
    }

    [Fact]
    public async Task EditingABuiltInRow_WarnsAndTheBuiltInValueStillWins()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            wb.Worksheet("Category Mapping").Cell(6, 4).Value = "Stuff"; // code 1, folder Gear -> Stuff
            wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "1/x";
        });

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("built-in code 1") && w.Contains("Stuff"));
        Assert.Equal("Gear/x/Alpha", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task ACleanEmptyMappingTable_OffersAnEmptySeed()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state, custom: [new CustomCode(17, "Mine")]);
        Edit(path, wb =>
        {
            var map = wb.Worksheet("Category Mapping");
            map.Cell(22, 1).Clear();
            map.Cell(22, 2).Clear();
            map.Cell(22, 3).Clear();
            map.Cell(22, 4).Clear();
            map.Cell(22, 5).Clear();
        });

        var result = await Import(path, state);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.CustomCodes);
        Assert.Empty(result.CustomCodes!);
    }

    [Fact]
    public async Task TheStagedLeaf_IsCarriedToTheImportedRow_SoDuplicatesDoNotCollide()
    {
        var state = State(Row("b", "Beta", "Old/Beta", "Gear/Thor/Beta (2)"));
        var path = await Export(state, WorkbookExportMode.AsSuggestedBySort);

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Empty(result.Errors);
        Assert.Equal("Beta (2)", result.Rows.Single().Leaf);
        Assert.Equal("Gear/Thor/Beta (2)", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task AVersionTwoFile_ReadsUnderVersionTwoRules_BracesAreLiteral_AndOldNamesApply()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var meta = wb.Worksheet("_Metadata");
            for (var r = 1; r <= 12; r++)
                if (meta.Cell(r, 1).GetString() == "formatVersion")
                    meta.Cell(r, 2).Value = "2";
            wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "15/{creator}";
        });

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.FormatVersion);
        Assert.Equal("Animation/{creator}/Alpha", state.Mods.Single().ProposedPath);
        Assert.Null(result.CustomCodes);
    }

    [Fact]
    public async Task AnInvalidModTypeCell_IsAWarningNotAnError()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var sheet = wb.Worksheet("Edit Destinations");
            sheet.Cell(2, 5).Value = "NotACategory";
            sheet.Cell(2, 7).Value = "Mine";
        });

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.StartsWith("Edit Destinations!E2:"));
        Assert.Equal("Mine/Alpha", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task ABlankDestination_LeavesTheModAlone()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "");

        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Empty(result.Errors);
        Assert.Equal("Hair/Alpha", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task TheSummary_CountsEveryOutcome_AndNeverSaysSuccessWhenARowWasBlocked()
    {
        var state = State(
            Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"),
            Row("b", "Beta", "Hair/Beta", "Hair/Beta"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var sheet = wb.Worksheet("Edit Destinations");
            sheet.Cell(2, 7).Value = "Moved";
            sheet.Cell(3, 7).Value = "99/x";
        });

        var result = await Import(path, state);

        Assert.Equal(new WorkbookImportCounts(Ready: 1, Unchanged: 0, Stale: 0, Blocked: 1, Warnings: 0), result.Counts);
        Assert.StartsWith("Workbook imported with problems:", result.Summary);
        Assert.Contains("1 move(s) ready for review", result.Summary);
        Assert.Contains("1 blocked by errors", result.Summary);
        Assert.DoesNotContain("success", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACleanImport_SaysSoWithoutABlockedCount()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "Moved");

        var result = await Import(path, state);

        Assert.StartsWith("Workbook imported:", result.Summary);
        Assert.DoesNotContain("blocked", result.Summary);
    }

    [Fact]
    public async Task AStaleRow_IsSkippedAndCounted()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        state.LoadScan([Row("a", "Alpha", "Moved/Alpha", "Moved/Alpha")], new HashSet<string>());

        var result = await Import(path, state);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Counts!.Stale);
    }

    [Fact]
    public async Task ACorruptFile_GivesThePlainMessageNotAStackTrace()
    {
        var path = NewPath();
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Import(path, State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"))));

        Assert.Equal("This file could not be read as a Penumbra Organizer workbook.", ex.Message);
    }

    [Fact]
    public async Task AWorkbookFromAnotherLibrary_IsStillBlocked()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var meta = wb.Worksheet("_Metadata");
            for (var r = 1; r <= 12; r++)
                if (meta.Cell(r, 1).GetString() == "installationIdentity")
                    meta.Cell(r, 2).Value = "someone-elses-library";
        });

        var result = await Import(path, state);

        Assert.Contains("This workbook belongs to a different Penumbra library.", result.Errors);
        Assert.Null(result.CustomCodes);
    }
}
```

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookAdapterApplyImportResultTests.cs` (inside the class; the file's existing `MakeResult(params WorkbookImportRow[] rows)` helper and its usings are reused; add `using PenumbraOrganizer.Plugin.Organizer.Workbook;` if Task 1 did not already):

```csharp
    [Fact]
    public void AnImportedLeaf_IsUsedInsteadOfTheModName()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "id", Name = "Mod", Author = "a", CurrentPath = "Old/Mod", ProposedPath = "Old/Mod" }],
            new HashSet<string>());
        var result = MakeResult(new WorkbookImportRow(
            2, "id", "Mod", "a", "Old", "Gear", false, "New", "Gear", "New", Leaf: "Mod (2)"));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal("New/Mod (2)", state.Mods.Single().ProposedPath);
    }
```

If the file's existing tests construct `WorkbookImportRow` differently from the ten positional arguments above, copy the shape they use and add `Leaf: "Mod (2)"`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookImportTests|FullyQualifiedName~WorkbookAdapterApplyImportResultTests"`
Expected: build FAIL (`WorkbookImportContext`, `WorkbookCodeUsage`, `WorkbookImportCounts`, the new `ImportAsync` overload, `Leaf`, `Counts` do not exist).

- [ ] **Step 3: Models**

In `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookModels.cs`:

1. Replace the `WorkbookImportRow` and `WorkbookImportResult` records with:

```csharp
public sealed record WorkbookImportRow(
    int RowNumber,
    string StableScanId,
    string ModName,
    string Author,
    string CurrentVirtualFolder,
    string ModType,
    bool Protected,
    string Destination,
    string ResolvedModType,
    string? ResolvedDestination,
    string? Leaf = null);

/// <summary>What one custom code expands to and how many mods in the file use it.</summary>
public sealed record WorkbookCodeUsage(int Code, string Folder, int ModCount);

/// <summary>The import report's separate counts (workbook spec, section 5).</summary>
public sealed record WorkbookImportCounts(int Ready, int Unchanged, int Stale, int Blocked, int Warnings);

/// <summary>Supplies the token values for one mod, by its stable id. Import never reads the plugin's state itself.</summary>
public sealed record WorkbookImportContext(Func<string, DestinationTokens> TokensFor)
{
    public static readonly WorkbookImportContext Empty = new(_ => DestinationTokens.None);
}

public sealed record WorkbookImportResult(
    string WorkbookPath,
    string SourceExportId,
    DateTimeOffset GeneratedAtUtc,
    string ScanIdentity,
    string InstallationIdentity,
    IReadOnlyList<WorkbookImportRow> Rows,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string Summary,
    int FormatVersion = 2,
    IReadOnlyList<CustomCode>? CustomCodes = null,
    IReadOnlyList<WorkbookCodeUsage>? CodeUsage = null,
    WorkbookImportCounts? Counts = null);
```

2. In `PenumbraOrganizer.Plugin/Organizer/WorkbookAdapter.cs`, in `ApplyImportResult`, replace the line `state.AssignManual(row.Identifier, JoinPath(importedRow.ResolvedDestination, row.Name));` with:

```csharp
                state.AssignManual(
                    row.Identifier,
                    JoinPath(importedRow.ResolvedDestination, importedRow.Leaf is { Length: > 0 } leaf ? leaf : row.Name));
```

- [ ] **Step 4: The service import**

Apply these edits to `PenumbraOrganizer.Plugin/Organizer/Workbook/WorkbookService.cs`.

(a) Replace the whole `ImportAsync` method with this wrapper, and add the new members right after it:

```csharp
    private const long MaxWorkbookBytes = 25L * 1024 * 1024;
    private const int RowMargin = 100;
    private const string UnreadableMessage = "This file could not be read as a Penumbra Organizer workbook.";

    private sealed class ImportTally
    {
        public int Stale;
        public HashSet<int> BlockedRows { get; } = [];
    }

    private static readonly MappingParseResult NoMapping =
        new([], new Dictionary<int, string>(), [], []);

    public Task<WorkbookImportResult> ImportAsync(
        string workbookPath,
        ScanInventory inventory,
        WorkbookImportContext context,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(workbookPath))
                throw new InvalidOperationException("The selected workbook could not be found.");
            if (!string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only .xlsx workbooks are supported for import.");
            if (new FileInfo(workbookPath).Length > MaxWorkbookBytes)
                throw new InvalidOperationException(
                    $"This file is larger than {MaxWorkbookBytes / (1024 * 1024)} MB, which is more than a workbook of any library needs.");

            try
            {
                return ReadWorkbook(workbookPath, inventory, context, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A corrupt, truncated or hostile file never surfaces its internals.
                _logger.LogWarning(ex, "Workbook import could not parse {WorkbookPath}", workbookPath);
                throw new InvalidOperationException(UnreadableMessage);
            }
        }, cancellationToken);
```

(b) Add `ReadWorkbook` (the old import body, reworked), `ReadMappingSheet`, and the rule builders:

```csharp
    private WorkbookImportResult ReadWorkbook(
        string workbookPath, ScanInventory inventory, WorkbookImportContext context, CancellationToken cancellationToken)
    {
        ValidateWorkbookPackage(workbookPath);

        using var workbook = new XLWorkbook(workbookPath);
        var editable = workbook.Worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, EditableSheetName, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException("The workbook is missing the Edit Destinations sheet.");
        var metadata = workbook.Worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, MetadataSheetName, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException("The workbook is missing its metadata sheet.");

        var meta = ReadMetadata(metadata);
        var rows = new List<WorkbookImportRow>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var tally = new ImportTally();

        ValidateMetadata(meta, inventory, errors, warnings);
        var formatVersion = FormatVersionOf(meta);
        if (errors.Count > 0)
            return BuildImportResult(workbookPath, meta, rows, errors, warnings, formatVersion, null, tally);

        var inventoryById = inventory.Mods.ToDictionary(mod => mod.StableScanId, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var headerMap = ReadHeaderRow(editable, 1);
        var requiredHeaders = new[] { "#", "mod name", "author", "current folder", "mod type", "protected", "destination", "_internal_key" };
        foreach (var header in requiredHeaders)
        {
            if (!headerMap.ContainsKey(header))
                errors.Add($"The workbook is missing the required column '{header}'.");
        }

        if (errors.Count > 0)
            return BuildImportResult(workbookPath, meta, rows, errors, warnings, formatVersion, null, tally);

        var lastRow = editable.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow - 1 > inventory.Mods.Count + RowMargin)
        {
            errors.Add(
                $"{EditableSheetName} has {lastRow - 1} rows, more than the {inventory.Mods.Count} mods in this library allow for.");
            return BuildImportResult(workbookPath, meta, rows, errors, warnings, formatVersion, null, tally);
        }

        // Version 3 reads the whole mapping table first; version 2 never does (it has no custom rows).
        var mapping = formatVersion >= 3 ? ReadMappingSheet(workbook, errors, warnings) : null;
        var rules = formatVersion >= 3 ? BuildVersionThreeRules(mapping ?? NoMapping) : BuildVersionTwoRules();
        var leafColumn = headerMap.TryGetValue("_leaf", out var leafIndex) ? leafIndex : 0;

        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            void Block(string message)
            {
                errors.Add(message);
                tally.BlockedRows.Add(rowNumber);
            }

            foreach (var header in requiredHeaders)
            {
                if (editable.Cell(rowNumber, headerMap[header]).HasFormula)
                    Block($"{EditableSheetName} row {rowNumber} contains a formula in '{header}'. Replace formulas with plain text before importing.");
            }

            var stableScanId = editable.Cell(rowNumber, headerMap["_internal_key"]).GetString().Trim();
            var modName = editable.Cell(rowNumber, headerMap["mod name"]).GetString().Trim();
            var author = editable.Cell(rowNumber, headerMap["author"]).GetString().Trim();
            var currentFolder = editable.Cell(rowNumber, headerMap["current folder"]).GetString();
            var modType = editable.Cell(rowNumber, headerMap["mod type"]).GetString().Trim();
            var protectedRaw = editable.Cell(rowNumber, headerMap["protected"]).GetString().Trim();
            var destination = editable.Cell(rowNumber, headerMap["destination"]).GetString().Trim();
            var leaf = leafColumn > 0 ? editable.Cell(rowNumber, leafColumn).GetString().Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(stableScanId) &&
                string.IsNullOrWhiteSpace(modName) &&
                string.IsNullOrWhiteSpace(author) &&
                string.IsNullOrWhiteSpace(currentFolder) &&
                string.IsNullOrWhiteSpace(modType) &&
                string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            if (tally.BlockedRows.Contains(rowNumber))
                continue;

            if (string.IsNullOrWhiteSpace(stableScanId))
            {
                Block($"{EditableSheetName} row {rowNumber} is missing the stable internal id.");
                continue;
            }

            if (!seenIds.Add(stableScanId))
            {
                Block($"{EditableSheetName} row {rowNumber} contains a duplicate id: {stableScanId}.");
                continue;
            }

            if (!inventoryById.TryGetValue(stableScanId, out var mod))
            {
                Block($"{EditableSheetName} row {rowNumber} references an unknown mod id: {stableScanId}.");
                continue;
            }

            if (!string.Equals(currentFolder, mod.CurrentVirtualFolder, StringComparison.Ordinal))
            {
                tally.Stale++;
                warnings.Add($"Row {rowNumber} no longer matches the exported current folder for {stableScanId} and was skipped. Scan and export a new workbook to include its edits.");
                continue;
            }

            if (!TryParseProtected(protectedRaw, mod.Protected, out var protectedValue))
            {
                Block($"{EditableSheetName}!F{rowNumber}: invalid protected value for {stableScanId}. Use TRUE or FALSE.");
                continue;
            }

            // The mod-type column has no effect on the import, so a value it cannot read is a warning, not a
            // reason to block the row.
            if (!TryResolveModType(modType, WorkbookCategoryCatalog.Detect(mod).Name, out var resolvedModType, out var modTypeError))
                warnings.Add($"{EditableSheetName}!E{rowNumber}: mod type \"{modType}\" was not recognised and was ignored ({modTypeError}).");

            var resolved = DestinationResolver.Resolve(destination, rules, context.TokensFor(stableScanId));
            if (resolved.Error is { } destinationError)
            {
                Block($"{EditableSheetName}!G{rowNumber}: invalid destination for {stableScanId}: {ExplainCodeError(destinationError, destination, mapping)}");
                continue;
            }

            var resolvedDestination = resolved.Destination;
            if (protectedValue && resolvedDestination is not null && !string.Equals(resolvedDestination, mod.CurrentVirtualFolder, StringComparison.Ordinal))
            {
                Block($"{EditableSheetName}!G{rowNumber}: tries to move protected mod {stableScanId} to a different destination.");
                continue;
            }

            if (leaf.Length > 0 && (leaf.Contains('/') || leaf.Contains('\\') || leaf.Any(char.IsControl)))
            {
                warnings.Add($"{EditableSheetName}!I{rowNumber}: the staged name was not a valid folder name and was ignored.");
                leaf = string.Empty;
            }

            rows.Add(new WorkbookImportRow(
                rowNumber,
                stableScanId,
                string.IsNullOrWhiteSpace(modName) ? mod.Name : modName,
                string.IsNullOrWhiteSpace(author) ? mod.Author : author,
                currentFolder,
                string.IsNullOrWhiteSpace(modType) ? WorkbookCategoryCatalog.Detect(mod).Name : modType,
                protectedValue,
                destination,
                resolvedModType,
                resolvedDestination,
                leaf.Length > 0 ? leaf : null));
        }

        return BuildImportResult(workbookPath, meta, rows, errors, warnings, formatVersion, mapping, tally);
    }

    private static DestinationRules BuildVersionThreeRules(MappingParseResult mapping) => new(
        TokensEnabled: true,
        CodeFolder: code => code is >= 1 and <= 16 ? WorkbookCatalog.FolderFor(code) : mapping.FolderFor(code),
        NameFolder: name => WorkbookCatalog.TryGetByName(name, out var category) ? category.Folder : null);

    private static DestinationRules BuildVersionTwoRules() => new(
        TokensEnabled: false,
        CodeFolder: code => WorkbookCategoryCatalog.TryGetByCode(code, out var category) ? category.Name : null,
        NameFolder: name => WorkbookCategoryCatalog.TryGetByName(name, out var category) ? category.Name : null);

    // A code that appeared in the mapping sheet but cannot be used gets its own reason, not "not in the sheet".
    private static string ExplainCodeError(string error, string destination, MappingParseResult? mapping)
    {
        if (mapping is not null
            && DestinationResolver.FirstSegmentCode(destination) is { } code
            && mapping.Unusable.TryGetValue(code, out var reason))
            return $"code {code} cannot be used: {reason}";

        return error;
    }

    private static MappingParseResult? ReadMappingSheet(XLWorkbook workbook, List<string> errors, List<string> warnings)
    {
        var sheet = workbook.Worksheets.FirstOrDefault(s => string.Equals(s.Name, CategorySheetName, StringComparison.Ordinal));
        if (sheet is null)
        {
            errors.Add($"The workbook is missing the {CategorySheetName} sheet.");
            return null;
        }

        var header = ReadHeaderRow(sheet, 5);
        if (!header.TryGetValue("code", out var codeColumn) || !header.TryGetValue("folder", out var folderColumn))
        {
            errors.Add($"{CategorySheetName} is missing its 'code' or 'folder' column in row 5.");
            return null;
        }

        var nameColumn = header.TryGetValue("name", out var found) ? found : 0;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 5;
        var list = new List<MappingSheetRow>();
        for (var row = 6; row <= lastRow; row++)
        {
            var codeCell = sheet.Cell(row, codeColumn);
            var folderCell = sheet.Cell(row, folderColumn);
            list.Add(new MappingSheetRow(
                row,
                codeCell.GetString(),
                nameColumn > 0 ? sheet.Cell(row, nameColumn).GetString() : string.Empty,
                folderCell.GetString(),
                codeCell.HasFormula || folderCell.HasFormula));
        }

        var parsed = MappingSheet.Parse(list);
        errors.AddRange(parsed.Errors);
        warnings.AddRange(parsed.Warnings);
        return parsed;
    }

    private static int FormatVersionOf(Dictionary<string, string> meta) =>
        meta.TryGetValue("formatVersion", out var text) && int.TryParse(text, out var version) ? version : 2;
```

(c) Replace `ReadHeaderMap(IXLWorksheet sheet)` with a row-aware version and keep the old name working:

```csharp
    private static Dictionary<string, int> ReadHeaderRow(IXLWorksheet sheet, int row)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastColumn = sheet.Row(row).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var name = sheet.Cell(row, column).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(name))
                map[name] = column;
        }

        return map;
    }
```

and delete the old `ReadHeaderMap` method (the new code calls `ReadHeaderRow(editable, 1)`).

(d) Replace `BuildImportResult` with:

```csharp
    private WorkbookImportResult BuildImportResult(
        string workbookPath,
        Dictionary<string, string> meta,
        IReadOnlyList<WorkbookImportRow> rows,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings,
        int formatVersion,
        MappingParseResult? mapping,
        ImportTally tally)
    {
        meta.TryGetValue("sourceExportId", out var sourceExportId);
        meta.TryGetValue("scanIdentity", out var scanIdentity);
        meta.TryGetValue("installationIdentity", out var installationIdentity);
        meta.TryGetValue("generatedAtUtc", out var generatedAtUtcRaw);
        DateTimeOffset.TryParse(generatedAtUtcRaw, out var generatedAtUtc);

        var ready = rows.Count(row => row.ResolvedDestination is not null && !string.Equals(row.ResolvedDestination, row.CurrentVirtualFolder, StringComparison.Ordinal));
        var unchanged = rows.Count - ready;
        var blocked = tally.BlockedRows.Count;
        var counts = new WorkbookImportCounts(ready, unchanged, tally.Stale, blocked, warnings.Count);

        var summary = rows.Count == 0 && errors.Count > 0 && blocked == 0
            ? $"Workbook import blocked. {errors.Count} validation issue(s) were found."
            : blocked > 0 || errors.Count > 0
                ? $"Workbook imported with problems: {ready} move(s) ready for review, {unchanged} unchanged or blank, {tally.Stale} skipped as stale, {blocked} blocked by errors, {warnings.Count} warning(s)."
                : $"Workbook imported: {ready} move(s) ready for review, {unchanged} unchanged or blank, {tally.Stale} skipped as stale, {warnings.Count} warning(s).";

        // The saved list is replaced only by a CLEAN mapping table (no mapping-sheet error); a blocked
        // import, a version 2 file and a table with errors all leave it alone.
        IReadOnlyList<CustomCode>? customCodes = mapping is { HasErrors: false } ? mapping.Custom : null;
        var usage = mapping is null ? [] : CodeUsageOf(rows, mapping);

        return new WorkbookImportResult(
            workbookPath,
            sourceExportId ?? string.Empty,
            generatedAtUtc,
            scanIdentity ?? string.Empty,
            installationIdentity ?? string.Empty,
            rows,
            errors,
            warnings,
            summary,
            formatVersion,
            customCodes,
            usage,
            counts);
    }

    private static IReadOnlyList<WorkbookCodeUsage> CodeUsageOf(IReadOnlyList<WorkbookImportRow> rows, MappingParseResult mapping)
    {
        var counts = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            if (row.ResolvedDestination is null || DestinationResolver.FirstSegmentCode(row.Destination) is not { } code)
                continue;
            if (mapping.FolderFor(code) is null)
                continue;
            counts[code] = counts.GetValueOrDefault(code) + 1;
        }

        return counts
            .OrderBy(pair => pair.Key)
            .Select(pair => new WorkbookCodeUsage(pair.Key, mapping.FolderFor(pair.Key)!, pair.Value))
            .ToList();
    }
```

(e) Delete the now-unused `TryResolveDestination` and `NormalizeRelativePath` methods (the resolver replaced them). Keep `TryResolveModType`, `TryParseProtected`, `ValidateWorkbookPackage`, `ReadMetadata`, `ValidateMetadata`.

- [ ] **Step 5: Plugin import and the seed**

In `PenumbraOrganizer.Plugin/Plugin.cs`:

1. Add a field next to the other private fields: `private static readonly CreatorCanonicalizer WorkbookCanonicalizer = new();`
2. Replace the whole `ImportWorkbook` method with:

```csharp
    internal Organizer.Workbook.WorkbookImportResult ImportWorkbook(string workbookPath)
    {
        var inventory = Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation());
        var race = Organizer.RaceSplitOptions.FromConfig(Config);
        var rowsById = OrganizerState.Mods.ToDictionary(r => r.Identifier, StringComparer.Ordinal);
        var context = new Organizer.Workbook.WorkbookImportContext(id =>
            rowsById.TryGetValue(id, out var row)
                ? Organizer.Workbook.WorkbookTokens.For(row, WorkbookCanonicalizer.Canonicalize, race)
                : Organizer.Workbook.DestinationTokens.None);

        var result = _workbookService.ImportAsync(workbookPath, inventory, context, CancellationToken.None)
            .GetAwaiter().GetResult();
        Organizer.WorkbookAdapter.ApplyImportResult(OrganizerState, result);
        return SaveCustomCodes(result);
    }

    // An accepted import with a clean mapping table becomes the seed for the next export. The sheet still wins at
    // import; this is only so a re-export does not wipe the user's rows. Said in the result line.
    private Organizer.Workbook.WorkbookImportResult SaveCustomCodes(Organizer.Workbook.WorkbookImportResult result)
    {
        if (result.CustomCodes is null)
            return result;

        var before = Config.WorkbookCustomCodes
            .Select(c => new Organizer.Workbook.CustomCode(c.Code, c.Folder)).ToList();
        var message = Organizer.Workbook.WorkbookSeed.Describe(before, result.CustomCodes);
        if (message is null)
            return result;

        Config.WorkbookCustomCodes = result.CustomCodes
            .Select(c => new WorkbookCustomCode { Code = c.Code, Folder = c.Folder }).ToList();
        SaveConfig();
        return result with { Summary = $"{result.Summary} {message}" };
    }
```

3. In `PenumbraOrganizer.Plugin/Organizer/WorkbookImportResultView.cs` replace the record with:

```csharp
public sealed record WorkbookImportResultView(
    string Summary,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Lines = null);
```

(keep the file's namespace and its summary comment, but change its mention of the linked `WorkbookImportResult` to say it is the workbook import result), and in `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` replace the construction in `ImportWorkbook` (about line 750) with:

```csharp
            var lines = (result.CodeUsage ?? [])
                .Select(u => $"Code {u.Code} -> {u.Folder}: {u.ModCount} mod(s)")
                .ToList();
            _lastWorkbookImportResult = new Organizer.WorkbookImportResultView(
                result.Summary, result.Errors, result.Warnings, lines);
```

- [ ] **Step 6: Fix the old interop tests, then run everything**

In `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs` each test calls `service.ImportAsync(export.WorkbookPath, inventory, CancellationToken.None)`; change each to `service.ImportAsync(export.WorkbookPath, inventory, WorkbookImportContext.Empty, CancellationToken.None)`.

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~WorkbookImportTests|FullyQualifiedName~WorkbookAdapterApplyImportResultTests|FullyQualifiedName~WorkbookInteropTests"` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS. If `AVersionTwoFile_...` fails because the exported file's metadata row for `formatVersion` is not within rows 1-12, widen that loop (the metadata sheet has 10 keys).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: workbook import reads the mapping sheet, resolves tokens, and reports separate counts"
```

---

### Task 8: The Review tab: export modes, the gate, and the import report

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, `PenumbraOrganizer.Plugin/Windows/MainWindow.ReviewTab.cs`, `PenumbraOrganizer.Plugin/Resources/help-content.json`

**Interfaces:**
- Consumes: `Plugin.SuggestedWorkbookUnavailableReason()`, `Plugin.ExportWorkbook(WorkbookExportMode, string)`, `WorkbookImportResultView.Lines` (Tasks 6 and 7).
- Produces: the Workbook group with an **Export mode** combo (As is, As suggested by Sort), the Export button disabled with the gate's reason, and the import report lines. No new pure logic, so no new unit test; the existing `HelpContentTests` guard the help text.

- [ ] **Step 1: MainWindow state and the export handler**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`:

1. Replace the field `private int _workbookStrategyIndex = 2; // "By Type Then Creator" default` with:

```csharp
    private int _workbookModeIndex;          // 0 As is, 1 As suggested by Sort; As is is the safe default
```

2. Replace the `WorkbookStrategyOptions` array and its comment block (the whole `private static readonly (string Label, ...)[] WorkbookStrategyOptions = [...]` declaration) with:

```csharp
    private static readonly string[] WorkbookModeLabels = ["As is (current folders)", "As suggested by Sort"];
```

3. Replace the private `ExportWorkbook` method's signature and body line so it takes the mode:

```csharp
    private void ExportWorkbook(PenumbraOrganizer.Plugin.Organizer.Workbook.WorkbookExportMode mode)
    {
        _fileDialogManager.SaveFileDialog(
            "Save Workbook",
            ".xlsx",
            Plugin.DefaultWorkbookFileName,
            ".xlsx",
            (success, path) =>
            {
                if (!success)
                    return;
                try
                {
                    _lastWorkbookExportPath = _plugin.ExportWorkbook(mode, path);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _lastError = $"Workbook export failed: {ex.Message}";
                }
            },
            Path.GetDirectoryName(_plugin.DefaultWorkbookFilePath));
    }
```

- [ ] **Step 2: The Workbook group**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.ReviewTab.cs`, in `DrawWorkbookGroup`, replace everything from `var strategyLabels = ...` through the `Help.Tooltip(HelpTopics.ReviewExportWorkbook);` line that follows the Export button with:

```csharp
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Export mode", ref _workbookModeIndex, WorkbookModeLabels, WorkbookModeLabels.Length);
        Help.Tooltip(HelpTopics.ReviewWorkbookDestinations);

        var mode = _workbookModeIndex == 1
            ? PenumbraOrganizer.Plugin.Organizer.Workbook.WorkbookExportMode.AsSuggestedBySort
            : PenumbraOrganizer.Plugin.Organizer.Workbook.WorkbookExportMode.AsIs;
        var unavailable = mode == PenumbraOrganizer.Plugin.Organizer.Workbook.WorkbookExportMode.AsSuggestedBySort
            ? _plugin.SuggestedWorkbookUnavailableReason()
            : null;

        ImGui.BeginDisabled(unavailable is not null);
        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(mode);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.ReviewExportWorkbook, unavailable);
        if (unavailable is not null)
            ImGui.TextDisabled(unavailable);
```

(Leave the `ImGui.SameLine();` and the Import button block that follow exactly as they are.)

In the same method, in the `if (_lastWorkbookImportResult is not null)` block, after the loop that prints warnings add:

```csharp
            if (_lastWorkbookImportResult.Lines is { Count: > 0 } lines)
            {
                foreach (var line in lines)
                    ImGui.TextWrapped($"  {line}");
            }
```

and update the comment above `DrawWorkbookGroup` so it says: "sort, export a workbook (as is, or the staged Sort), tune it in Excel, import it, then review".

- [ ] **Step 3: Help text**

In `PenumbraOrganizer.Plugin/Resources/help-content.json` replace the `review.workbook-destinations` entry with the following (every `short` must be 200 characters or fewer; check with the measuring command in Step 4):

```json
    {
      "id": "review.workbook-destinations",
      "title": "Export mode",
      "short": "As is writes each mod's current folder. As suggested by Sort writes the folders the last Sort staged; it is greyed until you Sort, and again whenever the settings or staged paths change."
    },
```

and replace the `review.export-workbook` entry's `short` with:

```json
      "short": "Writes every mod and its destination to a spreadsheet you can edit outside the game. Add your own categories (code 17 and up) on the Category Mapping sheet, then bring it back with Import Workbook."
```

and the `sort.import-workbook` entry's `short` with:

```json
      "short": "Reads your folder choices and any custom categories back from an exported workbook and stages them for review. Codes and {type}, {creator}, {race} in a destination are filled in for you."
```

- [ ] **Step 4: Build, measure the help text, run the suite**

```powershell
$j = Get-Content -Raw -LiteralPath 'PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Resources\help-content.json' | ConvertFrom-Json
@($j.topics ?? $j) | Where-Object { $_.id -in 'review.workbook-destinations','review.export-workbook','sort.import-workbook' } | ForEach-Object { "{0,-28} {1} chars" -f $_.id, $_.short.Length }
```

Windows PowerShell 5.1 has no `??`; if that line errors use `$topics = if ($j.topics) { $j.topics } else { $j }` first. Every length must be 200 or fewer.

Run: `dotnet build` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds; all tests pass (the help-content tests check the length cap and that every topic is still used by a control).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Review tab workbook export modes, the staged-sort gate and the import report"
```

---

### Task 9: Docs and the in-game checklist

**Files:**
- Modify: `docs/USER_GUIDE.md`
- Create: `docs/testing/2026-11-22-workbook-rework-checklist.md`

- [ ] **Step 1: User guide**

In `docs/USER_GUIDE.md`, in the `#### Workbook` section, replace the **Group workbook by** bullet and the **Export Workbook** bullet (the two bullets that mention "Group workbook by" and "interoperable with the standalone app") with:

```markdown
- **Export mode** decides what goes in the workbook's Destination column. **As is** writes each mod's
  current folder, which is what you want when you intend to write the layout yourself. **As suggested by
  Sort** writes the folders the last Sort staged, including every Sort setting (splits, numbered folders,
  race folders); it is greyed out until you press Sort, and again whenever your Sort settings change or
  you stage paths another way (a template, a manual move, an import). Protected mods always get a blank
  Destination, which means "leave this one alone" on import, and so do top-level mods in As is.
- **Export Workbook** writes `organizer-workbook.xlsx` for this library. **Open Workbook** opens the file
  you just wrote. The file belongs to your library: a workbook from another Penumbra library is refused.
```

Immediately after the Workbook section's last bullet (the **Import Workbook** one) add:

```markdown
##### Your own categories and tokens

The **Category Mapping** sheet lists the sixteen built-in codes (1 to 16) under a red banner: do not edit
them, edits are ignored with a warning. Under them, add your own rows from **17** up: a number in the
`code` column and a folder in the `folder` column, for example `17` and `Face/Sculpts`. Then type `17` in
a mod's Destination instead of the whole path, and `17/Thor` becomes `Face/Sculpts/Thor`. At most 200 custom
rows. A code used twice, a folder with `..` in it, or a formula in these cells is an error that names the
cell, and only the mods that use that code are held back. After an import with a clean mapping table the
plugin remembers your rows and writes them into the next export, so a re-export does not lose them.

A destination (or a mapping folder) may use three tokens, in any segment: `{type}` is the mod's category
folder (`Hair`, `Animation and VFX`, or `Review` for an unclassified mod), `{creator}` is the creator
folder the Sort tab uses, and `{race}` is the race folder (`Viera`, `Multi-race`, and so on) for a mod that
has one. For example `Mine/{type}/{creator}/test` becomes `Mine/Hair/Thor/test`. A token with nothing to
fill in drops its folder level: a mod with no creator gets `Mine/Hair/test`. Text around a token stays, so
`By-{creator}` becomes `By-` for a mod with no creator. Tokens work in new-format workbooks only.

In the new format the built-in folders follow the Sort tab: code 13 is `Animation and VFX/VFX`, 15 is
`Animation and VFX` and 16 is `Review` (older workbooks keep their old meaning for those three).
```

In the same guide, find any sentence that says the workbook is "interoperable with the standalone app" and delete that claim (the standalone app cannot read the new format). Run `Grep "standalone" docs/USER_GUIDE.md` to find them.

- [ ] **Step 2: The in-game checklist**

Create `docs/testing/2026-11-22-workbook-rework-checklist.md`:

```markdown
# Workbook rework: in-game checklist (2026-11-22)

Spec: docs/superpowers/specs/2026-11-22-workbook-rework-design.md

Nothing here has been run against a real library until you tick it.

- [ ] Review tab, Workbook group: the **Group workbook by** box is gone; **Export mode** offers As is and
      As suggested by Sort.
- [ ] As suggested by Sort is greyed with "Scan first." before a scan, "Press Sort first." after a scan,
      and becomes available after Sort.
- [ ] After changing any Sort setting, it greys again with "Sort settings changed, press Sort again."
- [ ] After a manual move, a template apply or a workbook import it greys with "Staged paths changed since
      the last sort, press Sort again."
- [ ] Export as suggested, open the file: Destination holds the staged folders (top-level moves included);
      protected mods are blank; Category Mapping has a red banner, 16 built-in rows, and code 13 reads
      `Animation and VFX/VFX`, 15 reads `Animation and VFX`, 16 reads `Review`.
- [ ] Add row 17 on Category Mapping (code 17, folder `Mine/{type}`), put `17/{creator}/{race}` in one
      mod's Destination, save, Import: the Review tab shows the expected path, the report names code 17
      with its mod count, and the result line says "Saved 1 custom category".
- [ ] Export again: row 17 is still there.
- [ ] Put `99/x` in a Destination: the error reads `Edit Destinations!G<row>: invalid destination for <id>:
      code 99 is not in the Category Mapping sheet`, and only that mod is held back.
- [ ] Use code 17 twice: both rows report `used twice`, mods using 17 are held back, the others import,
      and the saved list is not changed.
- [ ] Edit built-in row 5 on Category Mapping: a warning shows both values and the built-in is used.
- [ ] A duplicate install staged as `Name (2)`: export as suggested, import: the staged path keeps `(2)`
      and Review shows no new collision.
- [ ] An old (version 2) workbook still imports; `{creator}` in it stays literal text.
- [ ] A file that is not a workbook (rename a .txt to .xlsx) gives "This file could not be read as a
      Penumbra Organizer workbook."
- [ ] Close the workbook in Excel, hold it open, export over it: the message says another program may be
      using it, and the previous file is intact.
```

- [ ] **Step 3: Verify and commit**

Run: `dotnet build` and `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS (docs only).

```bash
git add docs
git commit -m "docs: workbook export modes, custom categories and tokens, with an in-game checklist"
```

---

## Self-Review

**Spec coverage (spec section to task):**
- Problem and goal: Tasks 6 and 7 (export follows Sort, custom categories) and the gate (Task 5).
- Decisions: dropdown removed (Task 8); two export modes (Tasks 6, 8); built-ins 1-16 locked with a warning (Tasks 3, 7); custom 17+ read at import with the sheet winning (Tasks 3, 7); hybrid storage (Tasks 3, 7: `WorkbookCustomCodes`, seed saved after an accepted clean import); three tokens and collapse (Tasks 2, 4); keyword rules out of scope (not planned).
- 1 Approach: Task 1 (own the code, link and interface dropped, v2 interop tests move first and pass); the three units: file layer in `WorkbookService` (Tasks 6, 7), pure resolver (Task 2), orchestration through `Plugin` (Tasks 6, 7); one catalog (Task 3, tied to the sorter by tests). Fallback wrapper is not built, by decision.
- 2 Export: As is and As suggested (Task 6); availability, the fingerprint, clearing by non-sort staging and scans, the three reasons (Task 5); blank rules (protected always; top-level blank in As is; staged path for every unprotected mod in Sort mode) (Task 6); `_leaf` (Tasks 6, 7); metadata mode, fingerprint and scan identity (Task 6; the scan identity already exists); version 2 and 3 import (Task 7); safe file replacement with a neutral message (Task 6, `Plugin.ExportWorkbook` keeps temp-then-move and catches `IOException`).
- 3 Mapping sheet: layout, banner, built-in folders follow Sort, custom rows seeded, whole-table parse, warnings with both values, duplicates, strict code parsing, 200 rows, 260 chars, formulas (Tasks 3, 6, 7).
- 4 Destinations and tokens: grammar, version 2 rules kept, per-segment expansion, collapse, validation, the three tokens, `{race}` via the shared function and applicability (Tasks 2, 4, 7).
- 5 Storage and report: seed rules (clean table only, blocked import saves nothing), friend's workbook still blocked (the existing identity check, covered by `AWorkbookFromAnotherLibrary_IsStillBlocked`), separate counts, per-code usage, "never success when blocked" (Task 7), shown in the UI (Task 8).
- 6 Limits and quirks: package checks kept, size cap, row cap, plain parse failure, mod-type column warning (Task 7); neutral lock message (Task 6).
- 8 Testing and docs: each listed group has a test in Tasks 2 to 7; docs and checklist in Task 9. Not verified until run: real Excel round trip and a real library (the checklist).
- Known gap, deliberate: Excel's `AdjustToContents` and sheet protection are untested beyond the round trips; the size cap is covered by the constant, not a 25 MB test file.

**Placeholder scan:** no TBD or TODO. Task 6 and 7 give the service as exact replacement methods against the Task 1 copy, so the engineer works from real file text; the only conditional is Task 8 Step 4's PowerShell 5.1 note, which names the exact fix.

**Type consistency:** `DestinationRules(TokensEnabled, CodeFolder, NameFolder)`, `DestinationResult(Destination, Error)`, `DestinationTokens(Creator, Type, Race)` are the same in Tasks 2, 4, 7. `CustomCode(Code, Folder)` and `MappingParseResult(Custom, Unusable, Errors, Warnings)` are the same in Tasks 3, 6, 7. `WorkbookExportRequest(Inventory, Proposals, Mode, SortFingerprint, CustomCodes, StagedLeaves)` is the same in Tasks 6 and 7 tests and in `Plugin.ExportWorkbook`. `WorkbookImportContext(TokensFor)`, `WorkbookImportCounts(Ready, Unchanged, Stale, Blocked, Warnings)`, `WorkbookCodeUsage(Code, Folder, ModCount)` match between the models, the service and the tests. `WorkbookService` has the one-parameter constructor from Task 6 onward and the tests use it.

---

## Review amendments (binding)

Sources: Opus review (checked against the real source) and GPT review (plan text only). Each item says which task it changes. "Rejected" items say why.

**R1. Test helpers lose staged paths (Tasks 6, 7; Opus, definite).** `OrganizerState.LoadScan` resets `ProposedPath` to `CurrentPath` (OrganizerState.cs ReplaceScanAtomically). In `WorkbookExportTests` and `WorkbookImportTests` replace the `State(...)` helper with:

```csharp
    private static OrganizerState State(params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>(rows.Where(r => r.Protected).Select(r => r.Identifier)));
        foreach (var input in rows)
            state.Mods.Single(m => m.Identifier == input.Identifier).ProposedPath = input.ProposedPath;
        return state;
    }
```

Also add to `WorkbookExportTests` one test that runs `SortRunner.Run(state, SortSelectionFactory.FromConfig(new Configuration(), labels), n => n, scope: null)` and then exports As suggested, asserting the destination equals the folder part of each row's `ProposedPath` (real Sort output, spec section 8). In `WorkbookImportTests.TheStagedLeaf_...` the helper fix above makes the leaf test meaningful. In `WorkbookExportTests.TheResultNamesTheMode` and the interop tests the same helper rule applies where staged paths matter.

**R2. Existing test file breaks (Task 8; Opus, definite).** `PenumbraOrganizer.Plugin.Tests/Windows/WorkbookStrategyOptionsTests.cs` reads `WorkbookStrategyOptions` by reflection. Task 8 deletes that field. Add that file to Task 8's Files as "Delete" and `git rm` it; replace with a two-line test asserting `MainWindow.WorkbookModeLabels` is `["As is (current folders)", "As suggested by Sort"]` (make the array `internal`/`public static` readonly so the test can read it).

**R3. Unedited round trip must not move mods (Tasks 6, 7; Opus, logic).** Version 3 maps a first segment that is a built-in name through the catalog, so an untouched As is cell `VFX/x` returns as `Animation and VFX/VFX/x`, and an author folder named `17` or `{type}` would be re-read as code or token. Fix: export a hidden column `_exported` (10th, hidden, locked) holding exactly the Destination text written. On import, when the Destination cell equals `_exported` (ordinal) and is non-blank, take it literally: `DestinationResolver.Literal(raw)` (add it: `NormalizePath`, then `StructureError`, no code, no name, no tokens) instead of `Resolve`. Add `DestinationResolverTests` for `Literal`, and a `WorkbookImportTests` test: a mod whose current folder is `VFX` exports and re-imports unedited with an unchanged path; the same mod edited to `VFX/y` goes through the grammar. `_exported` is optional on import (absent in version 2 files and treated as no match).

**R4. Fingerprint completeness (Task 5; Opus + GPT).** (a) Include `HairPriority` in `SortSelection.Fingerprint()`: append its text form (if it is a record struct use `HairPriority.ToString()`, otherwise join its fields); add a test changing only it. (b) Clear `SortFingerprint` in every mutator that can change what Sort would produce or who is protected: `SetToolPolicy`, `ChangeToolPolicy`, `SetProtected`, `SetAllProtection` and the folder-rule mutators; grep `OrganizerState.cs` for each and add `SortFingerprint = null;` where it changes protection or tool policy. One test per group. (c) Add `OrganizerState.InvalidateSortFingerprint()` and call it from `Plugin.ImportWorkbook` after `ApplyImportResult` (an import with only protection edits must invalidate); tests: workbook import clears it, `ApplyTemplate` clears it.

**R5. Leaf handling (Task 7; Opus + GPT).** Sort's `FixName` turns `/` in a mod name into `\`, so the leaf check must reject only `/`, control characters, `.` and `..` (not `\`). Reject (error, row blocked) rather than silently falling back. Validate the joined `folder/leaf` with `DestinationResolver.StructureError`. For a protected mod compare the whole proposed path (folder plus leaf) with the current path, not only the folder. The fallback in `WorkbookAdapter.ApplyImportResult` when there is no leaf becomes `PenumbraPathSemantics.FixName(row.Name)` (use the existing helper's real name; grep `PenumbraPathSemantics.cs`).

**R6. Missing structure is fatal (Task 7; GPT).** In `ReadWorkbook`, if `ReadMappingSheet` returns null (missing sheet or headers) return `BuildImportResult(...)` immediately with no rows. Custom-row errors stay per-row.

**R7. Built-in rows are locked by position (Task 3; GPT).** In `MappingSheet.Parse`, rows 6 to 21 are the built-in rows: for those physical rows ignore the code cell's value for definitions, compare name and folder to `WorkbookCatalog.BuiltIn[row - 6]`, warn for every changed or cleared field, and never treat them as custom. Custom rows start at row 22. Add tests: row 6 code changed to 17 gives a warning and no custom 17; a cleared built-in folder warns.

**R8. Formulas everywhere (Tasks 3, 7; both).** Check `HasFormula` on every consumed cell: mapping name, `_leaf`, and `_exported`, and check it before the blank-row skip. A formula row is an error and is skipped.

**R9. Tasks 6 and 7 ship together (ordering; GPT).** Task 6 turns on version 3 export while the old importer still has version 2 meanings. Do not release or merge between them; execute Task 6 and Task 7 back to back on the branch and run the full suite after Task 7 only. Task 6's interop tests stay green because they use only built-in single-segment folders.

**R10. Version 2 fidelity (Task 7; Opus + GPT).** Use `NumberStyles.Integer` for the version 2 first-segment code only (old behavior accepted `+16` and padded values). Add a committed fixture: build a version 2 workbook with the Task 1 service before any other task (run a small test once, copy the file to `PenumbraOrganizer.Plugin.Tests/Fixtures/Workbook/v2-export.xlsx`, add `<None Update ... CopyToOutputDirectory>` for it) and import it unchanged in a test. Keep the metadata-edit test too.

**R11. Small fixes.** (a) `WorkbookTokens.TypeFolderFor`: add `ModCategory.Others => "Review"` before the default arm, and derive other single-segment names from `WorkbookCatalog.FolderFor(code).Split('/')[0]`; test all `ModCategory` values. (b) Import wrapper: define `private sealed class WorkbookUserException : InvalidOperationException` for the plugin's own messages and rethrow only that; any other exception, `InvalidOperationException` included, becomes `UnreadableMessage`. (c) Task 6 Step 5 says "Task 9"; it is Task 8. (d) Task 8: draw the unavailable-reason text after the Import button block, not between the export button and `SameLine()`. (e) `HelpTooltip` two-argument overload: check `Help.Tooltip`'s signature first; if it takes one argument keep `Help.Tooltip(HelpTopics.ReviewExportWorkbook)`. (f) Move accounting (Task 7 `BuildImportResult`): count a row as ready when the final path (folder plus leaf) differs from the row's current path, not only the folder. (g) Seed safety: `WorkbookSeed.Describe` and the export must tolerate duplicate codes in a corrupt config (group by code, take the first, never `ToDictionary` blindly); export writes at most 200 rows and skips any row failing `StructureError`. (h) Package safety: extend `ValidateWorkbookPackage` with a total uncompressed size cap (100 MB) and a per-sheet row cap read from the sheet dimension before iterating.

**Round 2 amendments (GPT-6.2 Astra re-review, checked against the race-split source; these win over R1-R11 where they differ):**

**R12. As is must keep the real leaf (Tasks 6, 7; High).** A mod "Dress" at `Gear/My Dress` has a blank `_leaf` in As is, so an untouched import stages `Gear/Dress`. Fix: in BOTH export modes write `_leaf` as the current leaf (As is) or the staged leaf (Sort mode), taken from the row's real path (`SplitPath(...).Leaf`), and pass the live current path by stable id into the import inventory so import, validation, counting and staging all use one final path (folder plus leaf). Add the exact round-trip test: a mod named `Dress` at `Gear/My Dress` exports As is and re-imports unedited with an unchanged path.

**R13. `Literal` keeps backslashes (Task 2; High).** Sort stores a creator `A/B` as one folder segment `A\B`. `NormalizePath` would turn it into two folders. In the `_exported`-match path compare the raw cell strings ordinally (no trim) and validate with `/` as the only separator; do not call `NormalizePath`. Test an untouched suggested export under author `A/B`.

**R14. Protection and the move count (Task 7; High).** Importing `FALSE` for a folder-protected mod can leave it effectively protected (`AssignManual` returns false) while the report already counted a ready move; an unchanged `TRUE` calls `SetProtected` and turns inherited protection into an explicit rule. Fix: skip protection writes when the cell equals the row's effective protection; count a move as ready only when staging succeeded (`AssignManual` true), so counting moves into `ApplyImportResult`'s return value rather than the service.

**R15. Version dispatch (Tasks 2, 7; High).** Dispatch on the validated format version. Version 2 uses the original resolver rules exactly (no 260-character cap, `NumberStyles.Integer` codes) and ignores `_leaf` and `_exported` even if present. Version 3 only reads them. Tests: a long valid v2 path imports; a v2 file with extra v3-named columns ignores them.

**R16. Row caps before parsing (Task 7; High).** Do not trust the optional worksheet dimension. Open the file once as a stream, count actual rows per sheet against the caps (and reject a missing or understated dimension rather than trusting it) before constructing `XLWorkbook`, then rewind and load the same stream. Tests: missing dimension, understated dimension, size boundary.

**R17. R1's helper snapshot (Tests; High).** `LoadScan` mutates the supplied row objects and keeps those instances, so reading `input.ProposedPath` afterward reads the reset value. In the `State(...)` helper snapshot `Identifier -> ProposedPath` into a dictionary BEFORE `LoadScan`, then restore from the snapshot after it.

**R18. Invalidate only after an accepted import (Plugin; Medium).** `InvalidateSortFingerprint()` runs only when at least one row was applied (including protection-only edits), not for a wrong-library or structurally blocked workbook. Test: a wrong-library import keeps a valid fingerprint.

**R19. Code usage from resolution (Task 7; Medium).** `CodeUsageOf` must not reparse the raw destination. Have `DestinationResult` carry `CodeUsed` (the code actually looked up, or null) and aggregate that, so an unchanged literal folder `17` is not counted as code 17. Test a literal `17` row beside an edited `17/x` row.

**R20. Custom definitions (Tasks 2, 3, 7; Medium).** Enforce the 200-definition cap BEFORE exposing the lookup (definition 201 is unusable); validate token names in mapping folders before the table counts as clean (`Mine/{typo}` is an error and never enters the saved seed); drop the regex's 40-letter cutoff so any `{name}` is checked.

**R21. Sheet diagnostics (Tasks 3, 7; Medium).** Always inspect physical rows 6 to 21 even when `LastRowUsed` stops earlier (a cleared last built-in row must warn); reject duplicate consumed headers; discover columns by header everywhere and report the real cell address, formulas included (no hard-coded A/D/E/F/G/I).

**R22. Test corrections (Low).** R2: make `MainWindow.WorkbookModeLabels` `public`, not `internal` (no `InternalsVisibleTo`). R4: `HairPriority` is a getter computed from `HairRaceCode` and `MidlanderFallback`, both already fingerprinted; drop the `ToString()` addition and test each input independently.

**Rejected:** GPT item 13 (a shared category-folder source between Sort, catalog and tokens) is covered by the three tie tests plus R11(a); extracting a shared table is a refactor of Sort that this change should not carry. GPT item 14 (unique temp file name) is accepted only as `.tmp.xlsx` plus a `try/finally` delete in `Plugin.ExportWorkbook`; the existing replace behavior is kept. GPT item 10 (leading or trailing `/` silently accepted) is intended: the old importer normalized the same way.
