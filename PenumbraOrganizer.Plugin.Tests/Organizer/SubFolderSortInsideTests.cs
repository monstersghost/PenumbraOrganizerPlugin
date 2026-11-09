using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SubFolderSortInsideTests
{
    private static OrganizerModRow Row(string id, string path, bool helio = false) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = path, ProposedPath = path,
        Category = ModCategory.Gear, SubCategory = "Top", HeliosphereManaged = helio,
    };

    private static ToolPolicy Policy(params string[] folders) => ToolPolicy.From(0, 0, false, folders);

    private static OrganizerState Sorted(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, new HashSet<string>());
        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false, name => name);
        return state;
    }

    private static string P(OrganizerState s, string id) => s.Mods.Single(m => m.Identifier == id).ProposedPath;

    private static HashSet<string> Set(params string[] f) => new(f, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void DeepestChosenFolderWins()
    {
        var state = Sorted(Policy(".Chars", ".Chars/Alice"),
            Row("X", ".Chars/Alice/Old/X"), Row("Y", ".Chars/Bob/Y"));
        Assert.Equal(".Chars/Alice/Gear/X", P(state, "X"));
        Assert.Equal(".Chars/Gear/Y", P(state, "Y"));
    }

    [Fact]
    public void SubFolderOnly_SiblingSortsNormally()
    {
        var state = Sorted(Policy(".Chars/Alice"), Row("X", ".Chars/Alice/Old/X"), Row("Y", ".Chars/Bob/Y"));
        Assert.Equal(".Chars/Alice/Gear/X", P(state, "X"));
        Assert.Equal("Gear/Y", P(state, "Y"));
    }

    [Fact]
    public void CaseInsensitive_KeepsModsOwnSpelling()
    {
        var state = Sorted(Policy(".chars/ALICE"), Row("X", ".Chars/Alice/Old/X"));
        Assert.Equal(".Chars/Alice/Gear/X", P(state, "X"));
    }

    [Fact]
    public void DeepNesting_AnchorsAtChosenFolder()
    {
        Assert.Equal(".Chars/Alice", ToolFolders.DeepestAnchor(".Chars/Alice/Old/Deeper/X", Set(".Chars/Alice")));
    }

    [Fact]
    public void From_DropsSorterFirstSegment_AndNormalises()
    {
        Assert.Null(Policy("Gear/Foo").SortInsideFolders);
        Assert.Null(Policy(" / ").SortInsideFolders);
        Assert.Contains(".Chars/Alice", Policy(" .Chars / Alice ").SortInsideFolders!);
    }

    [Fact]
    public void ModUnderSorterFolder_NeverAnchored()
    {
        Assert.Null(ToolFolders.DeepestAnchor("Gear/Foo/X", Set("Gear/Foo")));
        Assert.Null(ToolFolders.DeepestAnchor("X", Set(".Chars")));
    }

    [Fact]
    public void ApplyAnchorTo_DoesNotDoublePrefix()
    {
        Assert.Equal(".Chars/Alice/Gear/X", ToolFolders.ApplyAnchorTo(".Chars/Alice", "Gear/X"));
        Assert.Equal(".chars/alice/Gear/X", ToolFolders.ApplyAnchorTo(".Chars/Alice", ".chars/alice/Gear/X"));
        Assert.Equal(".chars/alice", ToolFolders.ApplyAnchorTo(".Chars/Alice", ".chars/alice"));
        Assert.Equal(".Chars/Alice/.Chars/Bob", ToolFolders.ApplyAnchorTo(".Chars/Alice", ".Chars/Bob"));
    }

    [Fact]
    public void NumberingIsPerSubFolderAnchor()
    {
        var state = Sorted(Policy(".C/Alice", ".C/Bob"),
            Row("A1", ".C/Alice/A1"), Row("B1", ".C/Bob/B1"));
        Assert.Equal(".C/Alice/Gear/A1", P(state, "A1"));
        Assert.Equal(".C/Bob/Gear/B1", P(state, "B1"));
    }

    [Fact]
    public void HeliosphereRow_KeepsItsOwnToolMode()
    {
        var state = Sorted(Policy(".Chars/Alice"), Row("H", ".Chars/Alice/H", helio: true));
        Assert.Equal(".Chars/Alice/H", P(state, "H"));
    }

    [Fact]
    public void Candidates_ListSubFolders_ExcludeChosenToolOnlyAndSorter()
    {
        var rows = new[]
        {
            Row("a", ".Chars/Alice/Old/a"), Row("b", ".Chars/Bob/b"), Row("c", "Gear/Foo/c"),
            Row("d", "Tools/d", helio: true), Row("e", "Mixed/e", helio: true), Row("f", "Mixed/f"),
            Row("root", "root"),
        };
        var result = SortInsideFolderChoices.Candidates(rows, [" .chars / alice "]);
        Assert.Equal([".Chars", ".Chars/Alice/Old", ".Chars/Bob", "Mixed"], result);
    }

    [Fact]
    public void ProtectedParentWithExcludedSubFolder_SortsOnlyTheSubFolder()
    {
        var state = new OrganizerState();
        state.SetToolPolicy(Policy(".chars/Akako"));
        state.LoadScan([Row("X", ".chars/Akako/Old/X"), Row("Y", ".chars/Bob/Y")], new HashSet<string>());
        state.SetFolderProtectionRule(".chars", FolderProtectionRule.Protected);
        state.SetFolderProtectionRule(".chars/Akako", FolderProtectionRule.Excluded);
        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false, name => name);
        Assert.Equal(".chars/Akako/Gear/X", P(state, "X"));
        Assert.Equal(".chars/Bob/Y", P(state, "Y"));
    }

    [Fact]
    public void ResolvesToProtected_ReflectsTheDeepestRule()
    {
        var state = new OrganizerState();
        state.SetFolderProtectionRule(".chars", FolderProtectionRule.Protected);
        state.SetFolderProtectionRule(".chars/Akako", FolderProtectionRule.Excluded);
        Assert.True(SortInsideFolderChoices.ResolvesToProtected(state, ".chars/Bob"));
        Assert.False(SortInsideFolderChoices.ResolvesToProtected(state, ".chars/Akako"));
        Assert.False(SortInsideFolderChoices.ResolvesToProtected(state, "Other"));
    }
}
