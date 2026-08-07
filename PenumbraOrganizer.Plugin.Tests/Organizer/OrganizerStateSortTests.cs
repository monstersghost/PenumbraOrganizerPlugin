using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateSortTests
{
    // The fixture is load-bearing. Each row below exists to make a specific pair of legacy
    // combinations produce DIFFERENT output; drop one and the theory passes while proving nothing.
    private static OrganizerState WithMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            // Separates gear-split on from off. The slot must be one GetFolder accepts:
            // Head/Top/Hands/Legs/Feet/Ears/Neck/Wrists/Rings.
            new OrganizerModRow { Identifier = "gear", Name = "Gear Mod", Author = "Ann",
                CurrentPath = "Gear Mod", ProposedPath = "Gear Mod",
                Category = ModCategory.Gear, SubCategory = "Feet" },

            // Separates NPC-split on from off. SubCategory MUST be set: it is nullable, and
            // GetFolder(NPC, null) already returns "NPC", so a null here makes every
            // Sort_NpcSplitOff assertion pass against a completely no-op flattener.
            new OrganizerModRow { Identifier = "npc", Name = "Npc Mod", Author = "Bob",
                CurrentPath = "Npc Mod", ProposedPath = "Npc Mod",
                Category = ModCategory.NPC, SubCategory = "Bosses" },

            // Separates the three strategies through the creator segment, and covers the
            // no-category fallback.
            new OrganizerModRow { Identifier = "unknown", Name = "Unknown Mod", Author = "Cy",
                CurrentPath = "Unknown Mod", ProposedPath = "Unknown Mod" },
        ], new HashSet<string>());
        return state;
    }

    private static string Canon(string s) => s;

    // The complete selection space: 4 strategies x splitGear x splitNpc. Seven of these sixteen are
    // what the old buttons produced, and these expectations were CAPTURED from the seven SortBy*
    // methods while they still existed rather than hand-written - hand-writing them invites getting
    // the order wrong and then "fixing" the expectations until green, which destroys the guarantee.
    // Mods is ordered by Name, not insertion order, and FinishProposals can rewrite paths after the
    // sort, so the order below is observed output.
    //
    // Rows read: gear (Gear/Feet, by Ann), npc (NPC/Bosses, by Bob), unknown (no category, by Cy).
    [Theory]
    [InlineData(SortStrategy.CreatorOnly, false, false, "Ann/Gear Mod", "Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.CreatorOnly, false, true, "Ann/Gear Mod", "Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.CreatorOnly, true, false, "Ann/Gear Mod", "Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.CreatorOnly, true, true, "Ann/Gear Mod", "Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.TypeOnly, false, false, "Gear/Gear Mod", "NPC/Npc Mod", "Review/Unknown Mod")]
    [InlineData(SortStrategy.TypeOnly, false, true, "Gear/Gear Mod", "NPC/Bosses/Npc Mod", "Review/Unknown Mod")]
    [InlineData(SortStrategy.TypeOnly, true, false, "Gear/Feet/Gear Mod", "NPC/Npc Mod", "Review/Unknown Mod")]
    [InlineData(SortStrategy.TypeOnly, true, true, "Gear/Feet/Gear Mod", "NPC/Bosses/Npc Mod", "Review/Unknown Mod")]
    [InlineData(SortStrategy.TypeThenCreator, false, false, "Gear/Ann/Gear Mod", "NPC/Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.TypeThenCreator, false, true, "Gear/Ann/Gear Mod", "NPC/Bosses/Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.TypeThenCreator, true, false, "Gear/Feet/Ann/Gear Mod", "NPC/Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.TypeThenCreator, true, true, "Gear/Feet/Ann/Gear Mod", "NPC/Bosses/Bob/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.CreatorThenType, false, false, "Ann/Gear/Gear Mod", "Bob/NPC/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.CreatorThenType, false, true, "Ann/Gear/Gear Mod", "Bob/NPC/Bosses/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.CreatorThenType, true, false, "Ann/Gear/Feet/Gear Mod", "Bob/NPC/Npc Mod", "Cy/Unknown Mod")]
    [InlineData(SortStrategy.CreatorThenType, true, true, "Ann/Gear/Feet/Gear Mod", "Bob/NPC/Bosses/Npc Mod", "Cy/Unknown Mod")]
    public void Sort_ProducesTheExpectedPaths(
        SortStrategy strategy, bool splitGear, bool splitNpc,
        string expectedGear, string expectedNpc, string expectedUnknown)
    {
        var state = WithMods();
        state.Sort(strategy, splitGear, splitNpc, Canon);

        Assert.Equal(
            [expectedGear, expectedNpc, expectedUnknown],
            state.Mods.Select(m => m.ProposedPath));
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, false)]
    [InlineData(SortStrategy.TypeOnly, true)]
    [InlineData(SortStrategy.TypeThenCreator, false)]
    [InlineData(SortStrategy.TypeThenCreator, true)]
    [InlineData(SortStrategy.CreatorThenType, false)]
    [InlineData(SortStrategy.CreatorThenType, true)]
    public void Sort_NpcSplitOff_ProducesNpcWithoutASubfolder(SortStrategy strategy, bool splitGear)
    {
        // The six new combinations. NPC-classified mods land in "NPC", never "NPC/Bosses".
        var state = WithMods();
        state.Sort(strategy, splitGear, splitNpc: false, Canon);

        var npcPaths = state.Mods
            .Where(m => m.Category == ModCategory.NPC)
            .Select(m => m.ProposedPath)
            .ToList();

        Assert.NotEmpty(npcPaths);
        Assert.All(npcPaths, p => Assert.DoesNotContain("NPC/NPCs", p));
        Assert.All(npcPaths, p => Assert.DoesNotContain("NPC/Bosses", p));
        Assert.All(npcPaths, p => Assert.DoesNotContain("NPC/Enemies", p));
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, false)]
    [InlineData(SortStrategy.TypeThenCreator, false)]
    [InlineData(SortStrategy.CreatorThenType, false)]
    public void Sort_NpcSplitOn_DoesProduceASubfolder_SoTheOffCaseProvesSomething(
        SortStrategy strategy, bool splitGear)
    {
        // Guards the fixture itself: if the NPC row ever stopped producing "NPC/Bosses" with the
        // split ON, every Sort_NpcSplitOff assertion above would pass vacuously.
        var state = WithMods();
        state.Sort(strategy, splitGear, splitNpc: true, Canon);

        var npcPath = state.Mods.Single(m => m.Category == ModCategory.NPC).ProposedPath;
        Assert.Contains("NPC/Bosses", npcPath);
    }

    [Fact]
    public void Sort_CreatorOnly_IgnoresBothSplits()
    {
        // By Creator never consults category, so neither split can change its output.
        var a = WithMods(); a.Sort(SortStrategy.CreatorOnly, false, false, Canon);
        var b = WithMods(); b.Sort(SortStrategy.CreatorOnly, true, true, Canon);

        Assert.Equal(a.Mods.Select(m => m.ProposedPath), b.Mods.Select(m => m.ProposedPath));
    }

    [Fact]
    public void Sort_LeavesProtectedModsAlone()
    {
        // Unchanged behaviour, asserted because the reparameterisation touches every path.
        var state = WithMods();
        state.SetProtected("npc", true);
        var before = state.Mods.Single(m => m.Identifier == "npc").ProposedPath;

        state.Sort(SortStrategy.TypeThenCreator, splitGear: true, splitNpc: true, Canon);

        Assert.Equal(before, state.Mods.Single(m => m.Identifier == "npc").ProposedPath);
    }

    [Fact]
    public void Sort_ReturnsTheNumberOfTouchedRows()
    {
        var state = WithMods();
        state.SetProtected("npc", true);

        Assert.Equal(2, state.Sort(SortStrategy.TypeOnly, false, true, Canon));
    }
}
