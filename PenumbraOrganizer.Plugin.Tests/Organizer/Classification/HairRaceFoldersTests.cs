using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class HairRaceFoldersTests
{
    private static IReadOnlyDictionary<string, IReadOnlySet<int>> By(params (string Code, int[] Numbers)[] e) =>
        e.ToDictionary(x => x.Code, x => (IReadOnlySet<int>)x.Numbers.ToHashSet());

    private static string Tail(IReadOnlyDictionary<string, IReadOnlySet<int>>? by, HairPriority? p = null,
        string? stored = null, bool byNumber = true)
    {
        var flat = by?.Values.SelectMany(v => v).ToHashSet();
        return HairRaceFolders.Tail(flat, by, p, stored, byNumber);
    }

    [Fact]
    public void OneRaceOneNumber_FilesUnderRaceAndNumber() =>
        Assert.Equal("Viera/115", Tail(By(("1801", [115]))));

    [Fact]
    public void BothGendersOfOneRace_CountAsOneRace() =>
        Assert.Equal("Viera/115", Tail(By(("1701", [115]), ("1801", [115]))));

    [Fact]
    public void OneRaceSeveralNumbers_FilesUnderTheRaceAlone() =>
        Assert.Equal("Viera", Tail(By(("1801", [115, 120]))));

    [Fact]
    public void SeveralRaces_GoToMultiRace_NumberedByTheStoredNumber() =>
        Assert.Equal("Multi-race/115", Tail(By(("1801", [115]), ("0801", [115])), stored: "115"));

    [Fact]
    public void SeveralRaces_UseThePriorityRace()
    {
        var by = By(("1801", [115]), ("0801", [105]));
        Assert.Equal("Multi-race/105", Tail(by, new HairPriority("0801", false)));
    }

    [Fact]
    public void SeveralRaces_UseTheMidlanderFallback() =>
        Assert.Equal("Multi-race/004", Tail(By(("0101", [4]), ("0801", [9])), new HairPriority(null, true)));

    [Fact]
    public void PriorityRaceNeverMovesASingleRaceMod() =>
        Assert.Equal("Viera/115", Tail(By(("1801", [115])), new HairPriority("0801", true)));

    [Fact]
    public void NoRaceEvidence_IsMultiRace()
    {
        Assert.Equal("Multi-race", Tail(null));
        Assert.Equal("Multi-race/007", Tail(null, stored: "7"));
    }

    [Fact]
    public void UnknownRaceCodesOnly_IsMultiRace() =>
        Assert.Equal("Multi-race", Tail(By(("9999", [115]))));

    [Fact]
    public void NumberSplitOff_UsesOnlyTheRaceFolder()
    {
        Assert.Equal("Viera", Tail(By(("1801", [115])), byNumber: false));
        Assert.Equal("Multi-race", Tail(By(("1801", [115]), ("0801", [115])), stored: "115", byNumber: false));
    }

    [Fact]
    public void Select_AppliesTheTemplateRenameToHairOnly()
    {
        var row = new OrganizerModRow
        {
            Identifier = "h", Name = "h", Author = "A", CurrentPath = "h", ProposedPath = "h",
            Category = ModCategory.Hair, SubCategory = "115",
            HairFlat = new HashSet<int> { 115 }, HairByRaceCode = By(("1801", [115])),
        };
        var folder = SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row,
            renameFolder: f => f == "Hair" ? "Hairstyles" : f, splitHair: true,
            options: new LayoutOptions(SplitHairByRace: true)).Primary;
        Assert.Equal("Hairstyles/Viera/115", folder);
    }

    [Fact]
    public void Select_DefaultOptionsKeepTheFlatNumberFolder()
    {
        var row = new OrganizerModRow
        {
            Identifier = "h", Name = "h", Author = "A", CurrentPath = "h", ProposedPath = "h",
            Category = ModCategory.Hair, SubCategory = "115",
        };
        Assert.Equal("Hair/115",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row, splitHair: true).Primary);
    }
}
