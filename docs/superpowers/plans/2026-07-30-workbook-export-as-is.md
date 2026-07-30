# As-Is Workbook Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user export the workbook with every Destination set to the mod's current folder, instead of a computed sorting suggestion.

**Architecture:** The shared `WorkbookWorkflowService` already implements this as `OrganizationStrategy.PreserveAndClean`. The only missing piece is that the plugin's export dropdown never offers it. This plan adds the dropdown entry and pins the round-trip behaviour with a test.

**Tech Stack:** C# / .NET 10, Dalamud plugin, ClosedXML for workbooks, xUnit for tests.

## Global Constraints

- **Do not modify `PenumbraOrganizer.Infrastructure/Exports/WorkbookWorkflowService.cs`.** It is a linked file shared with the standalone app repo (`../PenumbraOrganizer`). Editing it silently diverges the two repos. Everything in this plan happens on the plugin side.
- Append the new dropdown entry; do not insert it. `MainWindow._workbookStrategyIndex` defaults to `2` meaning "By Type Then Creator", and inserting would silently change the default.
- Comment density and style must match the surrounding file. `MainWindow.cs` and `WorkbookInteropTests.cs` both use full-sentence comments only where the reason for something is non-obvious.
- No em dashes in user-facing strings, docs, or release notes. This project uses commas or restructured sentences instead.

---

### Task 1: Offer "keep current folders" in the workbook export dropdown

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:74-80` (the `WorkbookStrategyOptions` array) and `:950` (the combo label)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs` (append one `[Fact]`)
- Modify: `docs/USER_GUIDE.md` (Review Changes section)
- Modify: `docs/RELEASE_NOTES_0.5.2.0.md` (new "Added" section)

**Interfaces:**
- Consumes: `PenumbraOrganizer.Core.Models.OrganizationStrategy.PreserveAndClean`; `WorkbookAdapter.ToScanInventory(OrganizerState, PenumbraInstallation)`, `WorkbookAdapter.ToProposals(OrganizerState)`, `WorkbookAdapter.ToOrganizationPreferences(OrganizationStrategy)`, `WorkbookAdapter.ApplyImportResult(OrganizerState, WorkbookImportResult)`; `WorkbookWorkflowService.ExportAsync/ImportAsync`.
- Produces: nothing other tasks depend on. This is the only task.

**Background the implementer needs:**

`WorkbookWorkflowService.BuildSuggestedDestination` maps `PreserveAndClean` to `mod.CurrentVirtualFolder`, and returns `string.Empty` for any mod where `mod.Protected` is true, under every strategy. A mod at the library root has an empty `CurrentVirtualFolder`, so its cell is blank too. Blank is not a failure: `TryResolveDestination` returns a null resolved destination for blank input, and `WorkbookAdapter.ApplyImportResult` skips `AssignManual` when the resolved destination is null. Blank therefore means "leave this mod where it is".

Rows in the "Edit Destinations" sheet are written in `OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)` order, not the order they were loaded. The test below relies on that: with mod names "Nested Mod", "Protected Mod", "Root Mod", the sheet rows are 2, 3 and 4 respectively. Row 1 is the header. Destination is column 7.

- [ ] **Step 1: Write the failing test**

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`, inside the existing `WorkbookInteropTests` class, after the last `[Fact]`:

```csharp
    [Fact]
    public async Task AsIsExport_ImportedUnedited_LeavesEveryProposedPathUnchanged()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                new OrganizerModRow { Identifier = "Nested", Name = "Nested Mod", Author = "Author", CurrentPath = "Gear/Nested Mod", ProposedPath = "Gear/Nested Mod", Category = ModCategory.Gear },
                new OrganizerModRow { Identifier = "Protected", Name = "Protected Mod", Author = "Author", CurrentPath = "Gear/Protected Mod", ProposedPath = "Gear/Protected Mod", Category = ModCategory.Gear },
                new OrganizerModRow { Identifier = "Root", Name = "Root Mod", Author = "Author", CurrentPath = "Root Mod", ProposedPath = "Root Mod", Category = ModCategory.Gear },
            ],
            new HashSet<string>(["Protected"]));

        var service = CreateService();
        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());
        var export = await service.ExportAsync(
            inventory, WorkbookAdapter.ToProposals(state), WorkbookAdapter.ToOrganizationPreferences(OrganizationStrategy.PreserveAndClean),
            MakeWorkbookPath(), CancellationToken.None);

        // Rows are written in name order, so 2/3/4 are Nested/Protected/Root. Asserting the
        // nested mod's cell holds its own folder is what distinguishes as-is from a sorting
        // strategy: without it, this test would still pass if the option were wired to the
        // wrong strategy, because an unedited round-trip is internally consistent either way.
        using (var workbook = new XLWorkbook(export.WorkbookPath))
        {
            var sheet = workbook.Worksheet("Edit Destinations");
            Assert.Equal("Gear", sheet.Cell(2, 7).GetString());
            Assert.Equal(string.Empty, sheet.Cell(3, 7).GetString());
            Assert.Equal(string.Empty, sheet.Cell(4, 7).GetString());
        }

        var imported = await service.ImportAsync(export.WorkbookPath, inventory, CancellationToken.None);
        WorkbookAdapter.ApplyImportResult(state, imported);

        Assert.Empty(imported.Errors);
        Assert.All(state.Mods, row => Assert.Equal(row.CurrentPath, row.ProposedPath));
    }
```

Also append, in the same file, a test that the dropdown actually offers the strategy. Put it in a new file `PenumbraOrganizer.Plugin.Tests/Windows/WorkbookStrategyOptionsTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Windows;

using System.Reflection;
using PenumbraOrganizer.Core.Models;

public class WorkbookStrategyOptionsTests
{
    // The dropdown array is private static and MainWindow cannot be constructed without a live
    // Dalamud, so this reads the field reflectively rather than instantiating the window.
    private static (string Label, OrganizationStrategy Strategy)[] ReadOptions()
    {
        var field = typeof(PenumbraOrganizer.Plugin.Windows.MainWindow)
            .GetField("WorkbookStrategyOptions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WorkbookStrategyOptions not found.");
        return ((string, OrganizationStrategy)[])field.GetValue(null)!;
    }

    [Fact]
    public void Options_OfferKeepCurrentFolders()
    {
        var options = ReadOptions();
        var asIs = Assert.Single(options, option => option.Strategy == OrganizationStrategy.PreserveAndClean);
        Assert.Equal("Keep current folders (as-is)", asIs.Label);
    }

    [Fact]
    public void DefaultIndex_StillSelectsTypeThenCreator()
    {
        // The as-is entry must be appended, not inserted: _workbookStrategyIndex defaults to 2.
        Assert.Equal(OrganizationStrategy.TypeThenCreator, ReadOptions()[2].Strategy);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "AsIsExport_ImportedUnedited_LeavesEveryProposedPathUnchanged|WorkbookStrategyOptionsTests"
```

Expected: `AsIsExport_...` FAILS on the `Assert.Equal("Gear", ...)` line only if wired wrong; it should actually PASS already, because it drives the service directly and the service already supports `PreserveAndClean`. `Options_OfferKeepCurrentFolders` FAILS with "The collection did not contain any matching elements" because the dropdown does not offer the strategy yet.

If `AsIsExport_...` passes at this step, that is expected and correct. It is a characterization test protecting behaviour the shared service already has. Do not weaken it to force a red run.

- [ ] **Step 3: Add the dropdown entry**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, change the `WorkbookStrategyOptions` array (currently lines 74-80) to:

```csharp
    private static readonly (string Label, PenumbraOrganizer.Core.Models.OrganizationStrategy Strategy)[] WorkbookStrategyOptions =
    [
        ("By Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorOnly),
        ("By Mod Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeOnly),
        ("By Type Then Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeThenCreator),
        ("By Creator Then Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorThenType),
        // Appended deliberately: _workbookStrategyIndex defaults to 2, so inserting this earlier
        // would silently change which strategy a fresh session exports with.
        ("Keep current folders (as-is)", PenumbraOrganizer.Core.Models.OrganizationStrategy.PreserveAndClean),
    ];
```

- [ ] **Step 4: Relabel the combo**

In the same file, at the `ImGui.Combo` call (currently line 950), change:

```csharp
        ImGui.Combo("Workbook suggestion strategy", ref _workbookStrategyIndex, strategyLabels, strategyLabels.Length);
```

to:

```csharp
        ImGui.Combo("Workbook destinations", ref _workbookStrategyIndex, strategyLabels, strategyLabels.Length);
```

The old label described every option as a suggestion, which the as-is entry is not.

- [ ] **Step 5: Run the full test suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: all tests pass. The baseline before this task is 883 passing, so expect 886.

- [ ] **Step 6: Update the user guide**

In `docs/USER_GUIDE.md`, find the Review Changes section's description of workbook export. Add, after the existing description of the export:

```markdown
The Workbook destinations dropdown decides what goes in the workbook's Destination column. The four
sorting choices fill it with suggested folders. Keep current folders (as-is) fills it with each
mod's folder as it stands right now, which is what you want when you intend to write the layout
yourself in Excel rather than start from a suggestion. Protected mods and mods sitting at the top
level of your library get a blank Destination, which means "leave this one alone" on import.
```

If the Review Changes section does not currently mention the dropdown at all, add the paragraph immediately after the sentence describing Export Workbook.

- [ ] **Step 7: Update the release notes**

In `docs/RELEASE_NOTES_0.5.2.0.md`, insert a new section immediately after the `## Changes since v0.5.1.1` paragraph and before the first `### Fixed:` heading:

```markdown
### Added: export the workbook exactly as your library stands

Workbook export always filled the Destination column with a suggested folder, based on whichever
sorting strategy you picked. There was no way to get a workbook that simply described your library
as it is, so anyone wanting to write the layout by hand had to pick a strategy they did not want and
then clear every cell it produced.

The dropdown now offers Keep current folders (as-is). Every mod's Destination is the folder it is
in right now, and importing that workbook back without editing it changes nothing.
```

Then change the release notes' opening paragraph. It is currently wrapped across two lines in the file, exactly:

```markdown
This release is about one thing: the plugin no longer freezes the game while it works on your mod
library.
```

Replace both of those lines with:

```markdown
This release is mostly about one thing: the plugin no longer freezes the game while it works on your
mod library. There is also one small addition to workbook export.
```

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs PenumbraOrganizer.Plugin.Tests/Windows/WorkbookStrategyOptionsTests.cs docs/USER_GUIDE.md docs/RELEASE_NOTES_0.5.2.0.md
git commit -m "feat: offer an as-is option for workbook export"
```

---

## Self-review notes

- **Spec coverage:** the dropdown entry, the append-not-insert constraint, the relabel, the blank-destination behaviour for protected and root-level mods, the round-trip test with its anti-false-pass assertion, the user guide line and the release notes entry are all covered by Task 1. The spec's "Known wrinkle" section is informational and needs no task.
- **No shared-file edit:** confirmed in Global Constraints and repeated in the task background.
- **Step 2 honesty:** the round-trip test is a characterization test, not a red-green test, because the behaviour it covers already exists in the shared service. The plan says so explicitly rather than pretending it will fail. The genuinely red test is `Options_OfferKeepCurrentFolders`.
