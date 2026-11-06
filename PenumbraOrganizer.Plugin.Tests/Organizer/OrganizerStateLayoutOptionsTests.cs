using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateLayoutOptionsTests
{
    private static OrganizerState WithMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "gloves", Name = "Gloves", Author = "A", CurrentPath = "Gloves",
                ProposedPath = "Gloves", Category = ModCategory.Gear, SubCategory = "Hands", BodyType = "Rue+" },
            new OrganizerModRow { Identifier = "top", Name = "Top", Author = "A", CurrentPath = "Top",
                ProposedPath = "Top", Category = ModCategory.Gear, SubCategory = "Top", BodyType = "Rue+" },
            new OrganizerModRow { Identifier = "hair", Name = "Hair", Author = "A", CurrentPath = "Hair",
                ProposedPath = "Hair", Category = ModCategory.Hair, SubCategory = "115",
                HairFlat = new HashSet<int> { 115 },
                HairByRaceCode = new Dictionary<string, IReadOnlySet<int>> { ["1801"] = new HashSet<int> { 115 } } },
        ], new HashSet<string>());
        return state;
    }

    private static string PathOf(OrganizerState s, string id) => s.Mods.Single(m => m.Identifier == id).ProposedPath;

    [Fact]
    public void BothOn_SortsHandsToTheSharedFolderAndHairByRace()
    {
        var state = WithMods();
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: true, n => n,
            splitGearBody: true, options: new LayoutOptions(true, true));

        Assert.Equal("Gear/03. Hands/Gloves", PathOf(state, "gloves"));
        Assert.Equal("Gear/10. Rue+/02. Top/Top", PathOf(state, "top"));
        Assert.Equal("Hair/Viera/115/Hair", PathOf(state, "hair"));
    }

    [Fact]
    public void BothOff_IsTheExistingLayout()
    {
        var state = WithMods();
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: true, n => n,
            splitGearBody: true);

        Assert.Equal("Gear/10. Rue+/03. Hands/Gloves", PathOf(state, "gloves"));
        Assert.Equal("Hair/115/Hair", PathOf(state, "hair"));
    }

    [Fact]
    public void SharedHandsAndFeet_UnderAToolFolder_IsStable_AndLeavesProtectedRowsAlone()
    {
        static OrganizerModRow Hs(string id, string slot, string path) => new()
        {
            Identifier = id, Name = id, Author = "A", CurrentPath = path, ProposedPath = path,
            Category = ModCategory.Gear, SubCategory = slot, BodyType = "Rue+", HeliosphereManaged = true,
        };

        var state = new OrganizerState();
        state.SetToolPolicy(new ToolPolicy(ToolMode.SortInside, ToolMode.Protected, false));
        state.LoadScan(
        [
            Hs("gloves", "Hands", "Heliosphere/gloves"),
            Hs("top", "Top", "Heliosphere/top"),
            Hs("kept", "Hands", "Kept/kept"),
        ], new HashSet<string>());
        state.SetProtected("kept", true);

        void Sort() => state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false,
            n => n, splitGearBody: true, options: new LayoutOptions(SharedHandsAndFeet: true));

        Sort();
        var first = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);
        Assert.Equal("Heliosphere/Gear/03. Hands/gloves", first["gloves"]);
        Assert.Equal("Heliosphere/Gear/10. Rue+/02. Top/top", first["top"]);
        Assert.Equal("Kept/kept", first["kept"]);

        Sort();
        Assert.Equal(first, state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath));
    }
}
