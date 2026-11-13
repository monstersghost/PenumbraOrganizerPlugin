using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

/// <summary>
/// Sort with a <see cref="SortScope"/>: only in-scope rows get a new proposal, every other unprotected
/// row is reset (Apply moves every changed unprotected row), numbering still comes from the whole
/// library, and untouched rows' current paths are reserved so a moved row never lands on one.
/// </summary>
public class OrganizerStateScopedSortTests
{
    private static readonly SortScope RootOnly = new(RootOnly: true);

    private static OrganizerModRow Row(
        string id, string path, string name, string? slot = "Top", string? body = null,
        ModCategory category = ModCategory.Gear) => new()
    {
        Identifier = id, Name = name, Author = "A", CurrentPath = path, ProposedPath = path,
        Category = category, SubCategory = slot, BodyType = body,
    };

    private static OrganizerState StateWith(params OrganizerModRow[] rows) => StateWith(ToolPolicy.Default, rows);

    private static OrganizerState StateWith(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static OrganizerModRow RowOf(OrganizerState state, string id) => state.Mods.Single(m => m.Identifier == id);

    private static string PathOf(OrganizerState state, string id) => RowOf(state, id).ProposedPath;

    private static int SortByType(OrganizerState state, SortScope? scope) =>
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, n => n, scope: scope);

    private static int SortByBody(OrganizerState state, SortScope? scope, BodyPriority? priority = null) =>
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, n => n,
            splitGearBody: true, splitBodyMods: true, bodyPriority: priority, scope: scope);

    [Fact]
    public void RootOnly_MovesOnlyTheRootRows()
    {
        var state = StateWith(Row("root", "Loose Top", "Loose Top"), Row("filed", "Old/Filed Top", "Filed Top"));

        var count = SortByType(state, RootOnly);

        Assert.Equal(1, count);
        Assert.Equal("Gear/Top/Loose Top", PathOf(state, "root"));
        Assert.Equal("Old/Filed Top", PathOf(state, "filed"));
    }

    [Fact]
    public void RootOnly_AfterAStagedFullSort_ResetsOutOfScopeRows()
    {
        string[] bodies = ["YAB", "Rue+"];
        var multi = new OrganizerModRow
        {
            Identifier = "multi", Name = "Succubus Lifestyle", Author = "A",
            CurrentPath = "Old/Succubus Lifestyle", ProposedPath = "Old/Succubus Lifestyle",
            Category = ModCategory.Gear, SubCategory = "Top", BodyType = "YAB",
            BodyCandidates = new BodyCandidates
            {
                Content = [new BodyCandidate("YAB", BodyEvidenceSource.Option),
                           new BodyCandidate("Rue+", BodyEvidenceSource.Option)],
            },
        };
        var state = StateWith(Row("root", "Loose Top", "Loose Top"), multi);
        var priority = BodyPriority.Normalize(["Rue+"], bodies);

        SortByBody(state, scope: null, priority);
        Assert.NotEqual("Old/Succubus Lifestyle", PathOf(state, "multi"));
        Assert.NotNull(RowOf(state, "multi").StagedBodyResolution);

        SortByBody(state, RootOnly, priority);

        Assert.Equal("Old/Succubus Lifestyle", PathOf(state, "multi"));
        Assert.Null(RowOf(state, "multi").StagedBodyResolution);
        Assert.StartsWith("Gear/", PathOf(state, "root"));
    }

    [Fact]
    public void RootOnly_LeavesAProtectedRootRowUntouched()
    {
        var state = StateWith(Row("root", "Loose Top", "Loose Top"), Row("kept", "Kept Top", "Kept Top"));
        state.SetProtected("kept", true);

        SortByType(state, RootOnly);

        Assert.Equal("Kept Top", PathOf(state, "kept"));
        Assert.Equal("Gear/Top/Loose Top", PathOf(state, "root"));
    }

    [Fact]
    public void RootOnly_LeavesRowsInAnExcludedFolderUntouched()
    {
        var state = StateWith(Row("root", "Loose Top", "Loose Top"), Row("anim", "Pack/Anim/Dance", "Dance"));
        state.SetFolderProtectionRule("Pack", FolderProtectionRule.Protected);
        state.SetFolderProtectionRule("Pack/Anim", FolderProtectionRule.Excluded);
        Assert.False(RowOf(state, "anim").Protected);

        SortByType(state, RootOnly);

        Assert.Equal("Pack/Anim/Dance", PathOf(state, "anim"));
    }

    [Fact]
    public void RootOnly_LeavesSortInsideFolderRowsUntouched()
    {
        var policy = ToolPolicy.Default with
        {
            SortInsideFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Chars" },
        };
        var state = StateWith(policy, Row("root", "Loose Top", "Loose Top"), Row("char", "Chars/Alice Top", "Alice Top"));

        SortByType(state, RootOnly);

        Assert.Equal("Chars/Alice Top", PathOf(state, "char"));
        Assert.Equal("Gear/Top/Loose Top", PathOf(state, "root"));
    }

    [Fact]
    public void RootOnly_NumbersFoldersExactlyAsAFullSortWould()
    {
        OrganizerModRow[] Rows() =>
        [
            Row("bibo", "Old/Bibo Dress", "Bibo Dress", body: "Bibo+"),
            Row("tbse", "TBSE Outfit", "TBSE Outfit", body: "TBSE"),
        ];
        var full = StateWith(Rows());
        var scoped = StateWith(Rows());

        SortByBody(full, scope: null);
        SortByBody(scoped, RootOnly);

        Assert.Equal(PathOf(full, "tbse"), PathOf(scoped, "tbse"));
        Assert.Equal("Old/Bibo Dress", PathOf(scoped, "bibo"));
    }

    [Fact]
    public void RootOnly_ARowProposedOntoAnUntouchedRowsPath_IsRenumbered_AndValidateIsClean()
    {
        var state = StateWith(Row("old", "Gear/Top/Foo", "Foo"), Row("new", "Foo", "Foo"));

        SortByType(state, RootOnly);

        Assert.Equal("Gear/Top/Foo", PathOf(state, "old"));
        Assert.Equal("Gear/Top/Foo (2)", PathOf(state, "new"));
        Assert.False(state.Validate().HasIssues);
    }

    [Fact]
    public void RootOnly_AProtectedRowsPathIsReservedToo()
    {
        var state = StateWith(Row("kept", "Gear/Top/Foo", "Foo"), Row("new", "Foo", "Foo"));
        state.SetProtected("kept", true);

        SortByType(state, RootOnly);

        Assert.Equal("Gear/Top/Foo (2)", PathOf(state, "new"));
        Assert.False(state.Validate().HasIssues);
    }

    [Fact]
    public void IdentifierScope_MovesOnlyTheNamedRootRows()
    {
        var state = StateWith(Row("a", "A Top", "A Top"), Row("b", "B Top", "B Top"));

        var count = SortByType(state, new SortScope(RootOnly: true, Identifiers: new HashSet<string> { "a" }));

        Assert.Equal(1, count);
        Assert.Equal("Gear/Top/A Top", PathOf(state, "a"));
        Assert.Equal("B Top", PathOf(state, "b"));
    }

    [Fact]
    public void Sort_RecordsTheSummary_AndAScanClearsIt()
    {
        var state = StateWith(
            Row("root", "Loose Top", "Loose Top"),
            Row("kept", "Kept Top", "Kept Top"),
            Row("filed", "Old/Filed Top", "Filed Top"));
        state.SetProtected("kept", true);
        Assert.Null(state.LastSortSummary);

        SortByType(state, RootOnly);

        Assert.Equal(new SortSummary(RootOnly, InScope: 1, Total: 3, ProtectedAtRoot: 1), state.LastSortSummary);

        state.LoadScan([Row("root", "Loose Top", "Loose Top")], new HashSet<string>());

        Assert.Null(state.LastSortSummary);
    }

    [Fact]
    public void NullScope_SortsEveryUnprotectedRow_AndRecordsAWholeLibrarySummary()
    {
        var state = StateWith(Row("root", "Loose Top", "Loose Top"), Row("filed", "Old/Filed Top", "Filed Top"));

        var count = SortByType(state, scope: null);

        Assert.Equal(2, count);
        Assert.Equal("Gear/Top/Loose Top", PathOf(state, "root"));
        Assert.Equal("Gear/Top/Filed Top", PathOf(state, "filed"));
        Assert.Equal(new SortSummary(SortScope.WholeLibrary, InScope: 2, Total: 2, ProtectedAtRoot: 0), state.LastSortSummary);
    }
}
