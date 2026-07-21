using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerExportFormatterTests
{
    private static OrganizerModRow MakeRow(
        string id, string name, bool isProtected = false, bool heliosphere = false,
        ModCategory? category = null, string? subCategory = null,
        GearSlotDiagnostic gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = $"Unsorted/{name}",
        ProposedPath = $"Unsorted/{name}",
        Protected = isProtected,
        HeliosphereManaged = heliosphere,
        Category = category,
        SubCategory = subCategory,
        GearSlotDiagnostic = gearSlotDiagnostic,
    };

    [Fact]
    public void Format_EmptyInput_ProducesZeroCountsAndNoSections()
    {
        var result = OrganizerExportFormatter.Format([], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("Total mods: 0", result);
        Assert.Contains("Protected: 0", result);
        Assert.Contains("Collisions: 0", result);
        Assert.Contains("Protected violations: (none)", result);
        Assert.Contains("Path collisions: (none)", result);
    }

    [Fact]
    public void Format_FullyPopulatedMod_IncludesEveryField()
    {
        var row = MakeRow("a", "Cool Jacket", isProtected: true, heliosphere: true,
            category: ModCategory.Gear, subCategory: "Battle Animation");

        var result = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("Identifier: a", result);
        Assert.Contains("Name: Cool Jacket", result);
        Assert.Contains("Author: SomeAuthor", result);
        Assert.Contains("Category: Gear", result);
        Assert.Contains("SubCategory: Battle Animation", result);
        Assert.Contains("HeliosphereManaged: True", result);
        Assert.Contains("Protected: True", result);
        Assert.Contains("CurrentPath: Unsorted/Cool Jacket", result);
        Assert.Contains("ProposedPath: Unsorted/Cool Jacket", result);
    }

    [Fact]
    public void Format_NullCategoryAndSubCategory_RendersAsNone()
    {
        var row = MakeRow("a", "Mystery Mod");

        var result = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("Category: (none)", result);
        Assert.Contains("SubCategory: (none)", result);
    }

    [Fact]
    public void Format_ProtectedViolations_ListsIdentifiers()
    {
        var result = OrganizerExportFormatter.Format([], new ReviewResult(["a", "b"], new Dictionary<string, List<string>>()));

        Assert.Contains("Protected violations: a, b", result);
    }

    [Fact]
    public void Format_PathCollisions_ListsPathAndIdentifiers()
    {
        var collisions = new Dictionary<string, List<string>> { ["Shared/Same"] = ["a", "b"] };

        var result = OrganizerExportFormatter.Format([], new ReviewResult([], collisions));

        Assert.Contains("'Shared/Same': a, b", result);
    }

    [Fact]
    public void Format_NoGearMods_OmitsGearSlotDetectionSection()
    {
        var row = MakeRow("a", "Some Hair", category: ModCategory.Hair);

        var result = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.DoesNotContain("Gear slot detection:", result);
        Assert.DoesNotContain("GearSlotDiagnostic:", result);
    }

    [Fact]
    public void Format_GearMods_IncludesGearSlotDetectionSummaryWithCorrectCounts()
    {
        var rows = new[]
        {
            MakeRow("a", "Boots", category: ModCategory.Gear, gearSlotDiagnostic: GearSlotDiagnostic.Single),
            MakeRow("b", "Outfit", category: ModCategory.Gear, gearSlotDiagnostic: GearSlotDiagnostic.Ambiguous),
            MakeRow("c", "Mystery", category: ModCategory.Gear, gearSlotDiagnostic: GearSlotDiagnostic.ZeroEvidence),
            MakeRow("d", "Missing", category: ModCategory.Gear, gearSlotDiagnostic: GearSlotDiagnostic.DirectoryMissing),
            MakeRow("e", "Broken", category: ModCategory.Gear, gearSlotDiagnostic: GearSlotDiagnostic.ReadFailure),
        };

        var result = OrganizerExportFormatter.Format(rows, new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains(
            "Gear slot detection: 1 single-slot, 1 multi-slot (ambiguous), 1 zero-evidence, 1 directory-missing, 1 read/parse failures",
            result);
    }

    [Fact]
    public void Format_GearMod_IncludesPerModGearSlotDiagnosticLine()
    {
        var row = MakeRow("a", "Boots", category: ModCategory.Gear, gearSlotDiagnostic: GearSlotDiagnostic.ReadFailure);

        var result = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("GearSlotDiagnostic: ReadFailure", result);
    }

    [Fact]
    public void Format_CountsMatchInput()
    {
        var rows = new[]
        {
            MakeRow("a", "Apple", isProtected: true),
            MakeRow("b", "Banana"),
            MakeRow("c", "Cherry"),
        };
        var collisions = new Dictionary<string, List<string>> { ["Shared/Same"] = ["b", "c"] };

        var result = OrganizerExportFormatter.Format(rows, new ReviewResult([], collisions));

        Assert.Contains("Total mods: 3", result);
        Assert.Contains("Protected: 1", result);
        Assert.Contains("Collisions: 1", result);
    }
}
