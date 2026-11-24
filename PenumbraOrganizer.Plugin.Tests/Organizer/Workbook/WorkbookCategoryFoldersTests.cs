using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

/// <summary>The workbook follows renamed category folders (spec 1, section 5, "Workbook (decided)").</summary>
public class WorkbookCategoryFoldersTests
{
    private static readonly CategoryPlacement Renamed = CategoryPlacement.Create(
        new Dictionary<ModCategory, string>
        {
            [ModCategory.Gear] = "Equipment/Gear",
            [ModCategory.VFX] = "Effects",
            [ModCategory.Others] = "Misc",
        }, null)!;

    private static WorkbookService Service() => new(NullLogger<WorkbookService>.Instance);

    private static PenumbraInstallation Installation() => new(
        ConfigurationPath: "C:/Penumbra/Penumbra.json", ConfigDirectory: "C:/Penumbra", ModRoot: "C:/Penumbra/Mods",
        PluginAssemblyPath: null, PluginManifestPath: null, InstalledVersion: null,
        Confidence: DiscoveryConfidence.High, Evidence: [], Warnings: []);

    private static OrganizerModRow Row(string id, ModCategory? category, string currentPath) => new()
    {
        Identifier = id, Name = id, Author = "Thor", CurrentPath = currentPath, ProposedPath = currentPath,
        Category = category,
    };

    private static OrganizerState State(params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static async Task<string> Export(OrganizerState state, CategoryPlacement? placement)
    {
        var root = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "workbook.xlsx");
        var request = new WorkbookExportRequest(
            WorkbookAdapter.ToScanInventory(state, Installation()), WorkbookAdapter.ToProposals(state, false),
            WorkbookExportMode.AsIs, null, [], WorkbookAdapter.Leaves(state, false), placement);
        await Service().ExportAsync(request, path, CancellationToken.None);
        return path;
    }

    private static Task<WorkbookImportResult> Import(string path, OrganizerState state, CategoryPlacement? placement)
    {
        var rows = state.Mods.ToDictionary(m => m.Identifier);
        var context = new WorkbookImportContext(
            id => WorkbookTokens.For(rows[id], n => n, RaceSplitOptions.Off, placement),
            id => WorkbookAdapter.SplitPath(rows[id].CurrentPath).Leaf,
            placement);
        return Service().ImportAsync(path, WorkbookAdapter.ToScanInventory(state, Installation()), context, CancellationToken.None);
    }

    private static void Edit(string path, Action<XLWorkbook> change)
    {
        using var workbook = new XLWorkbook(path);
        change(workbook);
        workbook.Save();
    }

    private static void SetDestination(string path, string id, string destination) => Edit(path, workbook =>
    {
        var sheet = workbook.Worksheet("Edit Destinations");
        var row = sheet.RowsUsed().Single(r => r.Cell(8).GetString() == id);
        row.Cell(7).Value = destination;
    });

    private static void SetMeta(string path, string key, string value) => Edit(path, workbook =>
    {
        var meta = workbook.Worksheet("_Metadata");
        var last = meta.LastRowUsed()?.RowNumber() ?? 0;
        for (var r = 1; r <= last; r++)
            if (meta.Cell(r, 1).GetString() == key)
                meta.Cell(r, 2).Value = value;
    });

    [Theory]
    [InlineData(1, "Equipment/Gear")]
    [InlineData(13, "Effects")]
    [InlineData(16, "Misc")]
    [InlineData(4, "Hair")]
    [InlineData(15, "Animation and VFX")]
    public void BuiltInCodes_ResolveToTheConfiguredFolder(int code, string expected) =>
        Assert.Equal(expected, WorkbookCatalog.FolderFor(code, Renamed));

    [Fact]
    public void WithoutAPlacement_TheCatalogFoldersAreUnchanged()
    {
        foreach (var category in WorkbookCatalog.BuiltIn)
            Assert.Equal(category.Folder, WorkbookCatalog.FolderFor(category.Code, null));

        // R6: code 16 stays Review (the Sort's own Others folder is a different default).
        Assert.Equal("Review", WorkbookCatalog.FolderFor(16, null));
        Assert.Equal("Review", WorkbookTokens.TypeFolderFor(ModCategory.Others));
        Assert.Equal("Review", WorkbookTokens.TypeFolderFor(null));
    }

    [Fact]
    public void Vfx_KeepsItsFoldersUntilVfxItselfIsRenamed()
    {
        var animationOnly = CategoryPlacement.Create(
            new Dictionary<ModCategory, string> { [ModCategory.Animation] = "Motion" }, null);

        Assert.Equal("Animation and VFX/VFX", WorkbookCatalog.FolderFor(13, animationOnly));
        Assert.Equal("Motion", WorkbookCatalog.FolderFor(15, animationOnly));
        Assert.Equal("Animation and VFX", WorkbookTokens.TypeFolderFor(ModCategory.VFX, animationOnly));
        Assert.Equal("Motion", WorkbookTokens.TypeFolderFor(ModCategory.Animation, animationOnly));
        Assert.Equal("Effects", WorkbookTokens.TypeFolderFor(ModCategory.VFX, Renamed));
    }

    [Fact]
    public void TheTypeToken_IsTheConfiguredFolder_ButTheRuleTypeFieldKeepsTheBuiltInName()
    {
        var gear = Row("g", ModCategory.Gear, "Loose/g");
        Assert.Equal("Equipment/Gear", WorkbookTokens.For(gear, n => n, RaceSplitOptions.Off, Renamed).Type);
        Assert.Equal("Misc", WorkbookTokens.For(Row("u", null, "Loose/u"), n => n, RaceSplitOptions.Off, Renamed).Type);
        Assert.Equal("Hair", WorkbookTokens.For(Row("h", ModCategory.Hair, "Loose/h"), n => n, RaceSplitOptions.Off, Renamed).Type);
        Assert.Equal("Gear", WorkbookTokens.For(gear, n => n, RaceSplitOptions.Off).Type);

        Assert.Equal("Gear", RuleFields.For(gear, n => n, RaceSplitOptions.Off, null).Type);
    }

    [Fact]
    public void ARuleFolder_ExpandsTypeToTheConfiguredFolder_AndMatchesTypeByTheBuiltInName()
    {
        var state = State(Row("g", ModCategory.Gear, "Loose/g"));
        var rules = RuleSet.Compile([new RuleEntry("type = 'Gear' -> Mine/{type}", true)], RaceSplitOptions.Off);
        state.Sort(SortStrategy.TypeOnly, false, true, false, n => n, options: new LayoutOptions(Rules: rules, Placement: Renamed));
        Assert.Equal("Mine/Equipment/Gear/g", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public async Task TheMappingSheet_ShowsTheConfiguredFolders_InTheReadOnlyBuiltInRows()
    {
        var path = await Export(State(Row("g", ModCategory.Gear, "Loose/g")), Renamed);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Category Mapping");
        Assert.Equal("Equipment/Gear", sheet.Cell(6, 4).GetString());
        Assert.Equal("Effects", sheet.Cell(18, 4).GetString());
        Assert.Equal("Misc", sheet.Cell(21, 4).GetString());
        Assert.Equal("Hair", sheet.Cell(9, 4).GetString());
        Assert.Contains("Rows 1-16 are built in", sheet.Cell(4, 1).GetString());
    }

    [Fact]
    public async Task ARoundTrip_WithTheSameFolders_RaisesNoBuiltInWarning_AndCodesUseTheFolders()
    {
        var state = State(Row("g", ModCategory.Gear, "Loose/g"), Row("v", ModCategory.VFX, "Loose/v"));
        var path = await Export(state, Renamed);
        SetDestination(path, "g", "1/Thor");
        SetDestination(path, "v", "{type}/{creator}");

        var result = await Import(path, state, Renamed);

        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("built-in code", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("now files under", StringComparison.Ordinal));
        Assert.Equal("Equipment/Gear/Thor", result.Rows.Single(r => r.StableScanId == "g").ResolvedDestination);
        Assert.Equal("Effects/Thor", result.Rows.Single(r => r.StableScanId == "v").ResolvedDestination);
    }

    [Fact]
    public async Task ABuiltInNameAsTheFirstSegment_UsesTheConfiguredFolder()
    {
        var state = State(Row("g", ModCategory.Gear, "Loose/g"));
        var path = await Export(state, Renamed);
        SetDestination(path, "g", "gear/Shoes");

        var result = await Import(path, state, Renamed);
        Assert.Equal("Equipment/Gear/Shoes", result.Rows.Single().ResolvedDestination);
    }

    // R11: the hidden _exported column makes an untouched cell literal, so a rename after the export does not
    // re-read a folder that merely looks like a category.
    [Fact]
    public async Task AnUntouchedExportedFolder_StaysLiteral_AfterARename()
    {
        var state = State(Row("g", ModCategory.Gear, "Gear/Thor/g"));
        var path = await Export(state, placement: null);

        var result = await Import(path, state, Renamed);

        Assert.Empty(result.Errors);
        Assert.Equal("Gear/Thor", result.Rows.Single().ResolvedDestination);
    }

    [Fact]
    public async Task AnEditedShorthand_InAFileExportedBeforeTheRename_UsesTheRenamedFolder_AndSaysSo()
    {
        var state = State(Row("g", ModCategory.Gear, "Gear/Thor/g"));
        var path = await Export(state, placement: null);
        SetDestination(path, "g", "1/Thor");

        var result = await Import(path, state, Renamed);

        Assert.Empty(result.Errors);
        Assert.Equal("Equipment/Gear/Thor", result.Rows.Single().ResolvedDestination);
        Assert.Contains(result.Warnings, w => w.Contains(
            "code 1 now files under 'Equipment/Gear' (exported as 'Gear')", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains(
            "code 13 now files under 'Effects' (exported as 'Animation and VFX/VFX')", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("was edited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportingAfterTheFoldersChanged_UsesTodaysFolders_AndSaysTheCodeMoved()
    {
        var state = State(Row("g", ModCategory.Gear, "Loose/g"));
        var path = await Export(state, Renamed);
        SetDestination(path, "g", "1/Thor");

        var result = await Import(path, state, placement: null);

        Assert.Equal("Gear/Thor", result.Rows.Single().ResolvedDestination);
        Assert.Contains(result.Warnings, w => w.Contains(
            "code 1 now files under 'Gear' (exported as 'Equipment/Gear')", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("was edited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABuiltInRowTheUserEdited_StillWarnsAsAnEdit_AndUsesTheConfiguredFolder()
    {
        var state = State(Row("g", ModCategory.Gear, "Loose/g"));
        var path = await Export(state, Renamed);
        Edit(path, wb => wb.Worksheet("Category Mapping").Cell(6, 4).Value = "Elsewhere");
        SetDestination(path, "g", "1/Thor");

        var result = await Import(path, state, Renamed);

        Assert.Equal("Equipment/Gear/Thor", result.Rows.Single().ResolvedDestination);
        Assert.Contains(result.Warnings, w => w.Contains(
            "built-in code 1 was edited (folder 'Equipment/Gear' to 'Elsewhere')", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("now files under", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVersionTwoFile_KeepsItsLegacyResolver_AndIgnoresRenames()
    {
        var state = State(Row("g", ModCategory.Gear, "Loose/g"));
        var path = await Export(state, placement: null);
        SetMeta(path, "formatVersion", "2");
        SetDestination(path, "g", "1/Thor");

        var renamed = await Import(path, state, Renamed);
        var plain = await Import(path, state, placement: null);

        Assert.Empty(renamed.Errors);
        Assert.Equal(2, renamed.FormatVersion);
        Assert.Equal(plain.Rows.Single().ResolvedDestination, renamed.Rows.Single().ResolvedDestination);
        Assert.DoesNotContain("Equipment", renamed.Rows.Single().ResolvedDestination);
        Assert.DoesNotContain(renamed.Warnings, w => w.Contains("now files under", StringComparison.Ordinal));
    }
}
