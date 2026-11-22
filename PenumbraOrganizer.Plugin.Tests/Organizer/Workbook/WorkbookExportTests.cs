using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;
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

    // LoadScan resets every row's ProposedPath to its CurrentPath and keeps the same row objects, so the
    // staged paths are snapshotted BEFORE the scan and restored after it.
    private static OrganizerState State(params OrganizerModRow[] rows)
    {
        var staged = rows.ToDictionary(r => r.Identifier, r => r.ProposedPath);
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>(rows.Where(r => r.Protected).Select(r => r.Identifier)));
        foreach (var row in state.Mods)
            row.ProposedPath = staged[row.Identifier];
        return state;
    }

    private static OrganizerModRow Row(string id, string name, string currentPath, string proposedPath, bool prot = false) =>
        new()
        {
            Identifier = id, Name = name, Author = "Author", CurrentPath = currentPath, ProposedPath = proposedPath,
            Category = ModCategory.Gear, Protected = prot,
        };

    private static WorkbookExportRequest Request(
        OrganizerState state, WorkbookExportMode mode, string? fingerprint = null,
        IReadOnlyList<CustomCode>? custom = null)
    {
        var staged = mode == WorkbookExportMode.AsSuggestedBySort;
        return new WorkbookExportRequest(
            WorkbookAdapter.ToScanInventory(state, Installation()),
            WorkbookAdapter.ToProposals(state, staged),
            mode,
            fingerprint,
            custom ?? [],
            WorkbookAdapter.Leaves(state, staged));
    }

    private static async Task<string> Export(
        OrganizerState state, WorkbookExportMode mode, string? fingerprint = null,
        IReadOnlyList<CustomCode>? custom = null)
    {
        var path = NewPath();
        await CreateService().ExportAsync(Request(state, mode, fingerprint, custom), path, CancellationToken.None);
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
    }

    [Fact]
    public async Task AsIs_WritesTheRealCurrentLeaf_AndTheExportedDestinationForLiteralRoundTrips()
    {
        var state = State(Row("dress", "Dress", "Gear/My Dress", "Gear/My Dress"));

        var path = await Export(state, WorkbookExportMode.AsIs);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Edit Destinations");
        Assert.Equal("My Dress", sheet.Cell(2, 9).GetString());
        Assert.Equal("Gear", sheet.Cell(2, 10).GetString());
        Assert.Equal(sheet.Cell(2, 7).GetString(), sheet.Cell(2, 10).GetString());
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
    public async Task AsSuggested_AfterARealSort_WritesTheFolderPartOfEveryStagedPath()
    {
        var state = State(
            Row("a", "Alpha", "Alpha", "Alpha"),
            Row("b", "Beta", "Old/Beta", "Old/Beta"));
        var selection = SortSelectionFactory.FromConfig(new Configuration(), ["Bibo+"]);
        SortRunner.Run(state, selection, name => name, scope: null);

        var path = await Export(state, WorkbookExportMode.AsSuggestedBySort, state.SortFingerprint);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Edit Destinations");
        for (var i = 0; i < state.Mods.Count; i++)
        {
            var mod = state.Mods[i];
            Assert.Equal(WorkbookAdapter.SplitPath(mod.ProposedPath).Folder, sheet.Cell(i + 2, 7).GetString());
            Assert.Equal(WorkbookAdapter.SplitPath(mod.ProposedPath).Leaf, sheet.Cell(i + 2, 9).GetString());
        }
    }

    [Fact]
    public async Task TheLeafExportedAndInternalKeyColumnsAreHidden()
    {
        var path = await Export(State(Row("a", "Alpha", "Gear/Alpha", "Gear/Alpha")), WorkbookExportMode.AsIs);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Edit Destinations");
        Assert.Equal("_internal_key", sheet.Cell(1, 8).GetString());
        Assert.Equal("_leaf", sheet.Cell(1, 9).GetString());
        Assert.Equal("_exported", sheet.Cell(1, 10).GetString());
        Assert.True(sheet.Column(8).IsHidden);
        Assert.True(sheet.Column(9).IsHidden);
        Assert.True(sheet.Column(10).IsHidden);
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
            custom: [new CustomCode(20, "Mine/{type}"), new CustomCode(17, "Face/Sculpts")]);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Category Mapping");
        Assert.Contains("Rows 1-16 are built in", sheet.Cell(4, 1).GetString());
        Assert.Equal(new[] { "code", "name", "description", "folder", "example" },
            Enumerable.Range(1, 5).Select(c => sheet.Cell(5, c).GetString()));

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
    public async Task ACorruptSavedList_IsCleanedOnTheWayOut()
    {
        var path = await Export(
            State(Row("a", "Alpha", "Gear/Alpha", "Gear/Alpha")), WorkbookExportMode.AsIs,
            custom:
            [
                new CustomCode(17, "Good"), new CustomCode(17, "Duplicate"), new CustomCode(18, "Bad/../Path"),
                new CustomCode(19, "Mine/{typo}"), new CustomCode(5, "BuiltInCode"),
            ]);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Category Mapping");
        Assert.Equal("17", sheet.Cell(22, 1).GetString());
        Assert.Equal("Good", sheet.Cell(22, 4).GetString());
        Assert.Equal(string.Empty, sheet.Cell(23, 1).GetString());
    }

    [Fact]
    public async Task TheResultNamesTheMode()
    {
        var state = State(Row("a", "Alpha", "Gear/Alpha", "Gear/Alpha"));

        var result = await CreateService().ExportAsync(
            Request(state, WorkbookExportMode.AsIs), NewPath(), CancellationToken.None);

        Assert.Equal("current folders", result.StrategyLabel);
        Assert.Contains("1 mod row(s)", result.Summary);
    }
}
