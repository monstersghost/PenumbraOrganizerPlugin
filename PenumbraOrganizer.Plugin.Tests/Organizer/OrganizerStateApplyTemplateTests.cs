namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class OrganizerStateApplyTemplateTests
{
    private static OrganizerModRow Row(
        string identifier, string name, ModCategory? category = ModCategory.Gear,
        string? subCategory = null, string author = "Tsar", string? currentPath = null) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = currentPath ?? name,
        ProposedPath = currentPath ?? name,
        Category = category,
        SubCategory = subCategory,
    };

    private static ValidatedOrganizationTemplate Template(
        TemplateFallbackStrategy strategy = TemplateFallbackStrategy.ModType,
        Dictionary<string, string>? entries = null,
        Dictionary<string, string>? labels = null) => new(
            "T", "A", null, strategy,
            labels ?? new Dictionary<string, string>(),
            [],
            entries ?? new Dictionary<string, string>());

    private static string Same(string value) => value;

    private static OrganizerState StateWith(params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static TemplateApplyReport Apply(
        OrganizerState state, ValidatedOrganizationTemplate template)
    {
        var plan = TemplatePlanner.Plan(template, state.Mods, Same);
        return state.ApplyTemplate(plan);
    }

    [Fact]
    public void ApplyTemplate_MatchedRow_ProposesTemplateFolderWithLocalName()
    {
        var state = StateWith(Row("id1", "Bibo+ Medieval"));

        Apply(state, Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }));

        Assert.Equal("Characters/Nyx/Bibo+ Medieval", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ApplyTemplate_UnmatchedRow_UsesFallbackStrategy()
    {
        var state = StateWith(Row("id1", "Unknown", ModCategory.Gear, "Head"));

        Apply(state, Template(TemplateFallbackStrategy.ModTypeDetailed));

        Assert.Equal("Gear/Head/Unknown", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ApplyTemplate_FolderLabels_ApplyToFallbackRows()
    {
        var state = StateWith(Row("id1", "Unknown", ModCategory.Gear, "Head"));

        Apply(state, Template(TemplateFallbackStrategy.ModTypeDetailed, labels: new() { ["Gear"] = "Equipment" }));

        Assert.Equal("Equipment/Head/Unknown", state.Mods.Single().ProposedPath);
    }

    // The leaf is the importer's own Name -- never the Identifier, and never the template's key.
    [Fact]
    public void ApplyTemplate_Leaf_IsLocalNameNotIdentifier()
    {
        var state = StateWith(Row("some_directory_name_1_0", "Pretty Display Name"));

        Apply(state, Template(entries: new() { ["pretty display name"] = "Gear" }));

        Assert.Equal("Gear/Pretty Display Name", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ApplyTemplate_ProtectedRow_IsNotMoved()
    {
        var state = StateWith(Row("id1", "Locked", currentPath: "Original/Locked"));
        state.SetProtected("id1", true);

        Apply(state, Template(entries: new() { ["locked"] = "Characters" }));

        Assert.Equal("Original/Locked", state.Mods.Single().ProposedPath);
    }

    // The whole reason apply goes through OrganizerState.Sort rather than writing proposals
    // directly: the shared tail disambiguates two rows landing on the same path.
    [Fact]
    public void ApplyTemplate_TwoRowsSameFolderAndLeaf_AreDisambiguated()
    {
        var state = StateWith(Row("id1", "Same Name"), Row("id2", "Same Name"));

        Apply(state, Template(entries: new() { ["same name"] = "Gear" }));

        var proposed = state.Mods.Select(m => m.ProposedPath).ToList();
        Assert.Equal(2, proposed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ApplyTemplate_ReturnsThePlansReport()
    {
        var state = StateWith(Row("id1", "Bibo+ Medieval"), Row("id2", "Other", ModCategory.Hair));
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Gear/Top" }), state.Mods, Same);

        var report = state.ApplyTemplate(plan);

        Assert.Equal(plan.Report, report);
        Assert.Equal(1, report.RowsMatchedByEntry);
        Assert.Equal(1, report.RowsPlacedByFallback);
    }

    // The result must equal the plan the user was shown, not a recomputation of it.
    [Fact]
    public void ApplyTemplate_Result_MatchesThePlannedFolderForEveryRow()
    {
        var state = StateWith(
            Row("id1", "Bibo+ Medieval"),
            Row("id2", "Other", ModCategory.Hair),
            Row("id3", "Mystery", category: null));
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }), state.Mods, Same);

        state.ApplyTemplate(plan);

        foreach (var row in state.Mods)
        {
            var plannedFolder = plan.DestinationFolders[row.Identifier];
            Assert.StartsWith(plannedFolder + "/", row.ProposedPath);
        }
    }

    [Fact]
    public void ApplyTemplate_RowMissingFromPlan_IsLeftAlone()
    {
        var state = StateWith(Row("id1", "A"), Row("id2", "B"));
        var plan = new TemplateApplicationPlan(
            new Dictionary<string, string> { ["id1"] = "Gear" },
            new Dictionary<string, int> { ["Gear"] = 1 },
            new TemplateApplyReport(2, 0, 0, 1, 0, 0, 0, 0),
            []);

        state.ApplyTemplate(plan);

        Assert.Equal("Gear/A", state.Mods.Single(m => m.Identifier == "id1").ProposedPath);
        Assert.Equal("B", state.Mods.Single(m => m.Identifier == "id2").ProposedPath);
    }
}
