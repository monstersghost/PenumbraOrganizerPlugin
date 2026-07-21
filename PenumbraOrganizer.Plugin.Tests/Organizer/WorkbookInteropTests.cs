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
