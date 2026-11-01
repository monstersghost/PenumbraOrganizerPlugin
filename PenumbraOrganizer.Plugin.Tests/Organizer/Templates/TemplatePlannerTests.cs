namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplatePlannerTests
{
    private static OrganizerModRow Row(
        string identifier, string name, ModCategory? category = ModCategory.Gear,
        string? subCategory = null, string author = "Tsar", bool isProtected = false) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = name,
        ProposedPath = name,
        Category = category,
        SubCategory = subCategory,
        Protected = isProtected,
    };

    // Defaults reproduce the old ModType strategy: type only, gear flattened, NPC split.
    private static ValidatedOrganizationTemplate Template(
        SortStrategy strategy = SortStrategy.TypeOnly,
        bool splitGear = false,
        bool splitNpc = true,
        Dictionary<string, string>? entries = null,
        Dictionary<string, string>? labels = null) => new(
            "T", "A", null, new TemplateFallback(strategy, splitGear, splitNpc),
            labels ?? new Dictionary<string, string>(),
            [],
            entries ?? new Dictionary<string, string>());

    private static string Same(string value) => value;

    [Fact]
    public void Plan_MatchedRow_UsesTemplateFolder()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }),
            [Row("id1", "Bibo+ Medieval (Penumbra)_1_1_0")],
            Same);

        Assert.Equal("Characters/Nyx", plan.DestinationFolders["id1"]);
        Assert.Equal(1, plan.Report.RowsMatchedByEntry);
        Assert.Equal(0, plan.Report.RowsPlacedByFallback);
        Assert.Equal(1, plan.Report.TemplateEntriesMatched);
    }

    [Fact]
    public void Plan_UnmatchedRow_UsesFallbackStrategy()
    {
        var plan = TemplatePlanner.Plan(
            Template(SortStrategy.TypeOnly, splitGear: true),
            [Row("id1", "Unknown Mod", ModCategory.Gear, "Head")],
            Same);

        Assert.Equal("Gear/Head", plan.DestinationFolders["id1"]);
        Assert.Equal(0, plan.Report.RowsMatchedByEntry);
        Assert.Equal(1, plan.Report.RowsPlacedByFallback);
    }

    [Fact]
    public void Plan_FolderLabels_ApplyToFallbackPlacement()
    {
        var plan = TemplatePlanner.Plan(
            Template(SortStrategy.TypeOnly, splitGear: true, labels: new() { ["Gear"] = "Equipment" }),
            [Row("id1", "Unknown Mod", ModCategory.Gear, "Head")],
            Same);

        Assert.Equal("Equipment/Head", plan.DestinationFolders["id1"]);
    }

    [Fact]
    public void Plan_ProtectedRow_IsExcludedAndCounted()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["locked mod"] = "Characters" }),
            [Row("id1", "Locked Mod", isProtected: true)],
            Same);

        Assert.Empty(plan.DestinationFolders);
        Assert.Equal(1, plan.Report.ProtectedRows);
        Assert.Equal(1, plan.Report.ConsideredRows);
    }

    // One entry deliberately matches every local row with that name: two installs of the same
    // mod should both land where the author put it.
    [Fact]
    public void Plan_OneEntryMatchingSeveralRows_PlacesAllOfThem()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Gear/Top" }),
            [Row("id1", "Bibo+ Medieval"), Row("id2", "Bibo+ Medieval_1_1_0")],
            Same);

        Assert.Equal("Gear/Top", plan.DestinationFolders["id1"]);
        Assert.Equal("Gear/Top", plan.DestinationFolders["id2"]);
        Assert.Equal(2, plan.Report.RowsMatchedByEntry);
        Assert.Equal(1, plan.Report.TemplateEntriesMatched);       // rows and entries differ
        Assert.Equal(1, plan.Report.AmbiguousLocalMatchGroups);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.AmbiguousLocalMatch, "bibo+ medieval"),
            plan.Warnings);
    }

    [Fact]
    public void Plan_EntryMatchingNothing_IsCountedAndWarned()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["mod i do not own"] = "Gear/Top" }),
            [Row("id1", "Something Else")],
            Same);

        Assert.Equal(1, plan.Report.TemplateEntriesUnmatched);
        Assert.Equal(0, plan.Report.TemplateEntriesMatched);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.UnmatchedTemplateEntry, "mod i do not own"),
            plan.Warnings);
    }

    [Fact]
    public void Plan_FolderCounts_CountRowsPerDestination()
    {
        var plan = TemplatePlanner.Plan(
            Template(),
            [
                Row("id1", "A", ModCategory.Gear),
                Row("id2", "B", ModCategory.Gear),
                Row("id3", "C", ModCategory.Hair),
            ],
            Same);

        Assert.Equal(2, plan.FolderCounts["Gear"]);
        Assert.Equal(1, plan.FolderCounts["Hair"]);
    }

    [Fact]
    public void Plan_UnclassifiedUnmatchedRow_FallsBackToReview()
    {
        var plan = TemplatePlanner.Plan(
            Template(),
            [Row("id1", "Mystery", category: null)],
            Same);

        Assert.Equal("Review", plan.DestinationFolders["id1"]);
    }

    [Fact]
    public void Plan_DecodeWarnings_AreCarriedThrough()
    {
        var decodeWarnings = new[] { new TemplateWarning(TemplateWarningCode.DuplicateEntry, "dup") };

        var plan = TemplatePlanner.Plan(Template(), [Row("id1", "A")], Same, decodeWarnings);

        Assert.Contains(decodeWarnings[0], plan.Warnings);
    }

    [Fact]
    public void Plan_IsPure_AndDoesNotMutateRows()
    {
        var row = Row("id1", "Bibo+ Medieval");
        var originalProposed = row.ProposedPath;

        TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }), [row], Same);

        Assert.Equal(originalProposed, row.ProposedPath);
    }
}
