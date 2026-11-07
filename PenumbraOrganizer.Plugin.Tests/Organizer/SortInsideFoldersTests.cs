using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SortInsideFoldersTests
{
    private static OrganizerModRow Row(string id, string path) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = path, ProposedPath = path,
        Category = ModCategory.Gear, SubCategory = "Top",
    };

    private static ToolPolicy Policy(params string[] folders) =>
        ToolPolicy.From(0, 0, false, folders);

    private static OrganizerState SortedState(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, new HashSet<string>());
        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false, name => name);
        return state;
    }

    private static string PathOf(OrganizerState s, string id) =>
        s.Mods.Single(m => m.Identifier == id).ProposedPath;

    [Fact]
    public void AModInAChosenFolder_KeepsItsTopFolder_CaseInsensitively()
    {
        var state = SortedState(Policy(".chars"), Row("My Dress", ".Chars/Old Folder/My Dress"));
        Assert.Equal(".Chars/Gear/My Dress", PathOf(state, "My Dress"));
    }

    [Fact]
    public void WithAnEmptySet_TodaysBehaviourIsUnchanged()
    {
        var state = SortedState(Policy(), Row("My Dress", ".Chars/Old Folder/My Dress"));
        Assert.Equal("Gear/My Dress", PathOf(state, "My Dress"));
    }

    [Fact]
    public void ASorterFolderName_CanNeverBeChosen()
    {
        Assert.Null(Policy("Gear").SortInsideFolders);
        var state = SortedState(Policy("Gear"), Row("My Dress", "Gear/My Dress"));
        Assert.Equal("Gear/My Dress", PathOf(state, "My Dress"));
    }

    [Fact]
    public void AProtectedRow_IsNotMoved()
    {
        var state = new OrganizerState();
        state.SetToolPolicy(Policy(".Chars"));
        state.LoadScan([Row("My Dress", ".Chars/Old Folder/My Dress")], new HashSet<string>());
        state.SetProtected("My Dress", true);
        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false, name => name);
        Assert.Equal(".Chars/Old Folder/My Dress", PathOf(state, "My Dress"));
    }

    [Fact]
    public void BodiesAreNumberedPerAnchor()
    {
        static OrganizerModRow BodyRow(string id, string body, string path) => new()
        {
            Identifier = id, Name = id, Author = "A", CurrentPath = path, ProposedPath = path,
            Category = ModCategory.Body, BodyType = body,
        };

        var state = new OrganizerState();
        state.SetToolPolicy(Policy(".Chars", "halloween+"));
        state.LoadScan(
        [
            BodyRow("charsRue", "Rue+", ".Chars/charsRue"),
            BodyRow("charsYab", "YAB", ".Chars/charsYab"),
            BodyRow("halTbse", "TBSE", "halloween+/halTbse"),
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false,
            name => name, splitBodyMods: true);

        Assert.StartsWith(".Chars/Body/10. Rue+/", PathOf(state, "charsRue"));
        Assert.StartsWith(".Chars/Body/11. YAB/", PathOf(state, "charsYab"));
        Assert.StartsWith("halloween+/Body/10. TBSE/", PathOf(state, "halTbse"));
    }

    [Fact]
    public void From_DropsBlanksAndSorterFolders_AndDeduplicatesCaseInsensitively()
    {
        var policy = ToolPolicy.From(0, 0, false, ["  ", "Review", ".Chars", ".chars"]);
        Assert.Single(policy.SortInsideFolders!);
        Assert.Contains(".Chars", policy.SortInsideFolders!);
    }

    [Fact]
    public void Candidates_AreDistinctTopFolders_WithoutSorterFoldersChosenOnesOrRootMods()
    {
        var rows = new[] { Row("x", "A/x"), Row("y", "A/y"), Row("z", "B/z"), Row("q", "Gear/q"), Row("root", "root") };
        Assert.Equal(["A"], SortInsideFolderChoices.Candidates(rows, ["B"]));
        Assert.Equal(["A", "B"], SortInsideFolderChoices.Candidates(rows, []));
    }
}
