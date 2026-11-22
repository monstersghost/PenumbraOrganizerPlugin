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

    private static OrganizerModRow Row(string id, string name, string currentPath, string? proposedPath = null, bool prot = false) =>
        new()
        {
            Identifier = id, Name = name, Author = "Thor", CurrentPath = currentPath, ProposedPath = proposedPath ?? currentPath,
            Category = ModCategory.Hair, Protected = prot,
        };

    // LoadScan resets ProposedPath and keeps the same row objects: snapshot first, restore after.
    private static OrganizerState State(params OrganizerModRow[] rows)
    {
        var staged = rows.ToDictionary(r => r.Identifier, r => r.ProposedPath);
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>(rows.Where(r => r.Protected).Select(r => r.Identifier)));
        foreach (var row in state.Mods)
            row.ProposedPath = staged[row.Identifier];
        return state;
    }

    private static WorkbookImportContext Context(OrganizerState state) =>
        new(_ => new DestinationTokens("Thor", "Hair", "Viera"),
            id => WorkbookAdapter.SplitPath(state.Mods.Single(m => m.Identifier == id).CurrentPath).Leaf);

    private static async Task<string> Export(
        OrganizerState state, WorkbookExportMode mode = WorkbookExportMode.AsIs,
        IReadOnlyList<CustomCode>? custom = null)
    {
        var staged = mode == WorkbookExportMode.AsSuggestedBySort;
        var request = new WorkbookExportRequest(
            WorkbookAdapter.ToScanInventory(state, Installation()), WorkbookAdapter.ToProposals(state, staged), mode,
            staged ? "fp" : null, custom ?? [], WorkbookAdapter.Leaves(state, staged));
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

    private static void SetMeta(XLWorkbook wb, string key, string value)
    {
        var meta = wb.Worksheet("_Metadata");
        for (var r = 1; r <= 12; r++)
            if (meta.Cell(r, 1).GetString() == key)
                meta.Cell(r, 2).Value = value;
    }

    private static Task<WorkbookImportResult> Import(string path, OrganizerState state) =>
        CreateService().ImportAsync(
            path, WorkbookAdapter.ToScanInventory(state, Installation()), Context(state), CancellationToken.None);

    private static async Task<WorkbookImportResult> ImportAndApply(string path, OrganizerState state)
    {
        var result = await Import(path, state);
        WorkbookAdapter.ApplyImportResult(state, result);
        return result;
    }

    [Fact]
    public async Task ACustomCodeAddedInTheSheet_ExpandsWithTokens_AndIsOfferedAsTheSeed()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var map = wb.Worksheet("Category Mapping");
            map.Cell(22, 1).Value = 17;
            map.Cell(22, 4).Value = "Mine/{type}";
            wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "17/{creator}/{race}";
        });

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.FormatVersion);
        Assert.Equal("Mine/Hair/Thor/Viera/Alpha", state.Mods.Single().ProposedPath);
        Assert.Equal(new[] { new CustomCode(17, "Mine/{type}") }, result.CustomCodes);
        Assert.Equal(new WorkbookCodeUsage(17, "Mine/{type}", 1), Assert.Single(result.CodeUsage!));
    }

    [Fact]
    public async Task AnUnknownCode_FailsOnItsCellInPlainWords_AndTheRowIsNotMoved()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "99/Thor");

        var result = await ImportAndApply(path, state);

        Assert.Equal(
            "Edit Destinations!G2: invalid destination for a: code 99 is not in the Category Mapping sheet",
            Assert.Single(result.Errors));
        Assert.Equal("Hair/Alpha", state.Mods.Single().ProposedPath);
        Assert.Equal(1, result.Counts!.Blocked);
    }

    [Fact]
    public async Task ADuplicatedCustomCode_BlocksOnlyTheModsThatUseIt_AndNothingIsOfferedAsTheSeed()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"), Row("b", "Beta", "Hair/Beta"));
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

        var result = await ImportAndApply(path, state);

        Assert.Contains("Category Mapping!A22: code 17 is used twice.", result.Errors);
        Assert.Contains("Category Mapping!A23: code 17 is used twice.", result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("code 17 cannot be used: code 17 is defined more than once"));
        Assert.Equal("Hair/Alpha", state.Mods.Single(m => m.Identifier == "a").ProposedPath);
        Assert.Equal("Fine/x/Beta", state.Mods.Single(m => m.Identifier == "b").ProposedPath);
        Assert.Null(result.CustomCodes);
    }

    [Fact]
    public async Task AMappingFolderWithAnUnknownToken_IsAnErrorAndNeverTheSeed()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var map = wb.Worksheet("Category Mapping");
            map.Cell(22, 1).Value = 17;
            map.Cell(22, 4).Value = "Mine/{typo}";
        });

        var result = await Import(path, state);

        Assert.Contains(result.Errors, e => e.Contains("unknown token {typo}"));
        Assert.Null(result.CustomCodes);
    }

    [Fact]
    public async Task EditingABuiltInRow_WarnsAndTheBuiltInValueStillWins()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            wb.Worksheet("Category Mapping").Cell(6, 4).Value = "Stuff"; // code 1, folder Gear -> Stuff
            wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "1/x";
        });

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("built-in code 1") && w.Contains("Stuff"));
        Assert.Equal("Gear/x/Alpha", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task AClearedLastBuiltInRow_Warns()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var map = wb.Worksheet("Category Mapping");
            for (var c = 1; c <= 5; c++)
                map.Cell(21, c).Clear();
        });

        var result = await Import(path, state);

        Assert.Contains(result.Warnings, w => w.Contains("built-in code 16 was edited"));
    }

    [Fact]
    public async Task ACleanEmptyMappingTable_OffersAnEmptySeed()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state, custom: [new CustomCode(17, "Mine")]);
        Edit(path, wb =>
        {
            var map = wb.Worksheet("Category Mapping");
            for (var c = 1; c <= 5; c++)
                map.Cell(22, c).Clear();
        });

        var result = await Import(path, state);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.CustomCodes);
        Assert.Empty(result.CustomCodes!);
    }

    [Fact]
    public async Task AMissingMappingSheet_IsStructural_AndNothingFromTheFileIsUsed()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "Moved";
            wb.Worksheet("Category Mapping").Delete();
        });

        var result = await ImportAndApply(path, state);

        Assert.Contains(result.Errors, e => e.Contains("missing the Category Mapping sheet"));
        Assert.Empty(result.Rows);
        Assert.Equal("Hair/Alpha", state.Mods.Single().ProposedPath);
        Assert.Null(result.CustomCodes);
    }

    [Fact]
    public async Task TheStagedLeaf_IsCarriedToTheImportedRow_SoDuplicatesDoNotCollide()
    {
        var state = State(Row("b", "Beta", "Old/Beta", "Gear/Thor/Beta (2)"));
        var path = await Export(state, WorkbookExportMode.AsSuggestedBySort);

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal("Beta (2)", result.Rows.Single().Leaf);
        Assert.Equal("Gear/Thor/Beta (2)", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task AnUneditedAsIsExport_KeepsEveryRealPath_IncludingALeafThatIsNotTheName()
    {
        var state = State(Row("dress", "Dress", "Gear/My Dress"));
        var path = await Export(state);

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal("Gear/My Dress", state.Mods.Single().ProposedPath);
        Assert.Equal(0, result.Counts!.Ready);
    }

    [Fact]
    public async Task AnUneditedFolderNamedLikeACategory_IsTakenLiterally()
    {
        var state = State(Row("a", "Foo Mod", "VFX/Foo Mod"));
        var path = await Export(state);

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal("VFX/Foo Mod", state.Mods.Single().ProposedPath);
        Assert.Equal(0, result.Counts!.Ready);
    }

    [Fact]
    public async Task TheSameFolder_WhenEdited_GoesThroughTheGrammar()
    {
        var state = State(Row("a", "Foo Mod", "VFX/Foo Mod"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "VFX/y");

        await ImportAndApply(path, state);

        Assert.Equal("Animation and VFX/VFX/y/Foo Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task ACreatorFolderStoredAsOneBackslashSegment_SurvivesAnUneditedImport()
    {
        var state = State(Row("a", "Mod", "Hair/A\\B/Mod"));
        var path = await Export(state);

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal("Hair/A\\B/Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task AnUneditedLiteralFolderNamed17_IsNotCountedAsCode17()
    {
        var state = State(Row("a", "Alpha", "17/Alpha"), Row("b", "Beta", "Hair/Beta"));
        var path = await Export(state, custom: [new CustomCode(17, "Mine")]);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(3, 7).Value = "17/x");

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal(new WorkbookCodeUsage(17, "Mine", 1), Assert.Single(result.CodeUsage!));
        Assert.Equal("17/Alpha", state.Mods.Single(m => m.Identifier == "a").ProposedPath);
        Assert.Equal("Mine/x/Beta", state.Mods.Single(m => m.Identifier == "b").ProposedPath);
    }

    [Fact]
    public async Task AVersionTwoFile_ReadsUnderVersionTwoRules_BracesAreLiteral_AndOldNamesApply()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            SetMeta(wb, "formatVersion", "2");
            var sheet = wb.Worksheet("Edit Destinations");
            sheet.Cell(2, 7).Value = "15/{creator}";
            sheet.Cell(2, 9).Value = "Ignored";
        });

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.FormatVersion);
        Assert.Equal("Animation/{creator}/Alpha", state.Mods.Single().ProposedPath);
        Assert.Null(result.CustomCodes);
    }

    [Fact]
    public async Task AVersionTwoFile_HasNoLengthCap()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        var longPath = string.Join('/', Enumerable.Repeat(new string('a', 50), 6));
        Edit(path, wb =>
        {
            SetMeta(wb, "formatVersion", "2");
            wb.Worksheet("Edit Destinations").Cell(2, 7).Value = longPath;
        });

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.StartsWith(longPath, state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task AnInvalidModTypeCell_IsAWarningNotAnError()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb =>
        {
            var sheet = wb.Worksheet("Edit Destinations");
            sheet.Cell(2, 5).Value = "NotACategory";
            sheet.Cell(2, 7).Value = "Mine";
        });

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.StartsWith("Edit Destinations!E2:"));
        Assert.Equal("Mine/Alpha", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task ABlankDestination_LeavesTheModAlone()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "");

        var result = await ImportAndApply(path, state);

        Assert.Empty(result.Errors);
        Assert.Equal("Hair/Alpha", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task AFormulaInADestination_IsAnErrorNamingTheCell()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).FormulaA1 = "=\"Moved\"");

        var result = await ImportAndApply(path, state);

        Assert.Contains(result.Errors, e => e.StartsWith("Edit Destinations!G2:") && e.Contains("formula"));
        Assert.Equal("Hair/Alpha", state.Mods.Single().ProposedPath);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task AnInvalidLeaf_BlocksTheRow(string leaf)
    {
        var state = State(Row("b", "Beta", "Old/Beta", "Gear/Thor/Beta"));
        var path = await Export(state, WorkbookExportMode.AsSuggestedBySort);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 9).Value = leaf);

        var result = await ImportAndApply(path, state);

        Assert.Contains(result.Errors, e => e.StartsWith("Edit Destinations!I2:"));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task TheSummary_CountsEveryOutcome_AndNeverSaysSuccessWhenARowWasBlocked()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"), Row("b", "Beta", "Hair/Beta"));
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
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(2, 7).Value = "Moved");

        var result = await Import(path, state);

        Assert.StartsWith("Workbook imported:", result.Summary);
        Assert.DoesNotContain("blocked", result.Summary);
    }

    [Fact]
    public async Task AStaleRow_IsSkippedAndCounted()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        state.LoadScan([Row("a", "Alpha", "Moved/Alpha")], new HashSet<string>());

        var result = await Import(path, state);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Counts!.Stale);
    }

    [Fact]
    public async Task AFolderProtectedMod_FalseCannotLiftIt_AndTheMoveIsReportedNotStaged()
    {
        var state = State(Row("p", "Prot", "Gear/Prot"));
        state.SetFolderProtectionRule("Gear", FolderProtectionRule.Protected);
        Assert.True(state.Mods.Single().Protected);
        var path = await Export(state);
        Edit(path, wb =>
        {
            var sheet = wb.Worksheet("Edit Destinations");
            sheet.Cell(2, 6).Value = "FALSE";
            sheet.Cell(2, 7).Value = "Elsewhere";
        });

        var result = await Import(path, state);
        var applied = WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal(1, applied.NotStaged);
        Assert.Equal("Gear/Prot", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task AnUneditedProtectedImport_DoesNotTurnInheritedProtectionIntoAnExplicitRule()
    {
        var state = State(Row("p", "Prot", "Gear/Prot"));
        state.SetFolderProtectionRule("Gear", FolderProtectionRule.Protected);
        var path = await Export(state);

        await ImportAndApply(path, state);
        state.SetFolderProtectionRule("Gear", FolderProtectionRule.None);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public async Task ACorruptFile_GivesThePlainMessageNotAStackTrace()
    {
        var path = NewPath();
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Import(path, State(Row("a", "Alpha", "Hair/Alpha"))));

        Assert.Equal("This file could not be read as a Penumbra Organizer workbook.", ex.Message);
    }

    [Fact]
    public async Task ASparseSheetFarBeyondTheLibrary_IsRejectedBeforeItIsLoaded()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => wb.Worksheet("Edit Destinations").Cell(900000, 1).Value = "x");

        var ex = await Record.ExceptionAsync(() => Import(path, state));

        var invalid = Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains("more than", invalid.Message);
    }

    [Fact]
    public async Task AWorkbookFromAnotherLibrary_IsStillBlocked()
    {
        var state = State(Row("a", "Alpha", "Hair/Alpha"));
        var path = await Export(state);
        Edit(path, wb => SetMeta(wb, "installationIdentity", "someone-elses-library"));

        var result = await Import(path, state);

        Assert.Contains("This workbook belongs to a different Penumbra library.", result.Errors);
        Assert.Null(result.CustomCodes);
        Assert.Empty(result.Rows);
    }
}
