using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateToolSortTests
{
    private static string Canon(string s) => s;

    private static OrganizerModRow Row(
        string id, string path, string name, string author = "A", bool hs = false,
        ProteusRole role = ProteusRole.None, ModCategory category = ModCategory.Gear) => new()
    {
        Identifier = id, Name = name, Author = author, CurrentPath = path, ProposedPath = path,
        Category = category, SubCategory = category == ModCategory.Gear ? "Top" : null, HeliosphereManaged = hs, ProteusRole = role,
    };

    private static OrganizerState StateWith(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static string PathOf(OrganizerState state, string id) =>
        state.Mods.Single(m => m.Identifier == id).ProposedPath;

    private static void SortByType(OrganizerState state) =>
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon);

    [Fact]
    public void SortInside_FilesAProteusModBeneathItsCurrentTopFolder()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Proteus Upscales/Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Proteus Upscales/Gear/Top/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_AModAlreadyInASorterFolder_IsSortedNormally()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Gear/Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Gear/Top/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_AModAtTheRoot_IsSortedNormally()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Gear/Top/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_AppliesToHeliosphereToo_UnderItsOwnMode()
    {
        var state = StateWith(new ToolPolicy(ToolMode.SortInside, ToolMode.Protected, false),
            Row("hs", "My Tools/[HS] Aeonium", "[HS] Aeonium", hs: true));

        SortByType(state);

        Assert.Equal("My Tools/Gear/Top/[HS] Aeonium", PathOf(state, "hs"));
    }

    [Fact]
    public void NormalMode_SortsToolModsLikeAnyOtherMod()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Normal, ToolMode.Normal, false),
            Row("pt", "Proteus Upscales/Cool Dress", "Cool Dress", role: ProteusRole.Made),
            Row("hs", "x/[HS] A", "[HS] A", hs: true));

        SortByType(state);

        Assert.Equal("Gear/Top/Cool Dress", PathOf(state, "pt"));
        Assert.Equal("Gear/Top/[HS] A", PathOf(state, "hs"));
    }

    [Fact]
    public void ProtectedMode_LeavesToolModsWhereTheyAre()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("pt", "Proteus Upscales/Cool Dress", "Cool Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.Equal("Proteus Upscales/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_WithGroupByCreator_DoesNotDoubleACreatorFolderTheModAlreadySitsIn()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("pt", "Solona/Cool Dress", "Cool Dress", author: "Solona", role: ProteusRole.Made));

        state.Sort(SortStrategy.CreatorOnly, splitGear: false, splitNpc: false, splitHair: false, c => c);

        Assert.Equal("Solona/Cool Dress", PathOf(state, "pt"));
    }

    [Fact]
    public void SortInside_TwoModsWithTheSameNameUnderTheSameFolder_AreDisambiguated()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false),
            Row("a", "Proteus Upscales/Dress", "Dress", role: ProteusRole.Made),
            Row("b", "Proteus Upscales/Dress (2)", "Dress", role: ProteusRole.Made));

        SortByType(state);

        Assert.NotEqual(PathOf(state, "a"), PathOf(state, "b"));
        Assert.StartsWith("Proteus Upscales/Gear/Top/Dress", PathOf(state, "a"));
        Assert.StartsWith("Proteus Upscales/Gear/Top/Dress", PathOf(state, "b"));
    }

    [Fact]
    public void TheManagedMod_AtRoot_IsProposedAtTheRootWhateverTheStrategy()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.Protected, true),
            Row("Proteus", "Proteus Upscales/Proteus", "Proteus", author: "Proteus",
                role: ProteusRole.Managed, category: ModCategory.Others));

        SortByType(state);

        Assert.Equal("Proteus", PathOf(state, "Proteus"));
    }

    [Fact]
    public void TheManagedMod_NotAtRoot_StaysProtectedAndUnmoved()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("Proteus", "Proteus Upscales/Proteus", "Proteus", author: "Proteus",
                role: ProteusRole.Managed, category: ModCategory.Others));

        SortByType(state);

        Assert.Equal("Proteus Upscales/Proteus", PathOf(state, "Proteus"));
    }
}
