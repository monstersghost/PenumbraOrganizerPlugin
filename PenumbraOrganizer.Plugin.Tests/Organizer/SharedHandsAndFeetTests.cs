using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SharedHandsAndFeetTests
{
    private static OrganizerModRow Gear(string id, string slot, string body) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
        Category = ModCategory.Gear, SubCategory = slot, BodyType = body,
    };

    private static string Folder(OrganizerModRow row, IEnumerable<OrganizerModRow> all,
        LayoutOptions options, bool splitGear = true, bool numbered = true)
    {
        var layout = numbered ? SortFolderSelectors.BuildGearLayout(all, splitGear, true, null, options) : null;
        return SortFolderSelectors.Select(SortStrategy.TypeOnly, splitGear, false, row,
            splitGearBody: true, gearLayout: layout, options: options).Primary!;
    }

    private static readonly OrganizerModRow[] Rows =
    [
        Gear("hands", "Hands", "Rue+"), Gear("top", "Top", "Rue+"), Gear("feet", "Feet", "YAB"),
    ];

    [Fact]
    public void Default_KeepsHandsAndFeetUnderTheirBody()
    {
        var o = new LayoutOptions();
        Assert.Equal("Gear/10. Rue+/03. Hands", Folder(Rows[0], Rows, o));
        Assert.Equal("Gear/11. YAB/05. Feet", Folder(Rows[2], Rows, o));
    }

    [Fact]
    public void Shared_PutsHandsAndFeetInThePlainSlotFolders()
    {
        var o = new LayoutOptions(SharedHandsAndFeet: true);
        Assert.Equal("Gear/03. Hands", Folder(Rows[0], Rows, o));
        Assert.Equal("Gear/05. Feet", Folder(Rows[2], Rows, o));
        Assert.Equal("Gear/10. Rue+/02. Top", Folder(Rows[1], Rows, o));
    }

    [Fact]
    public void Shared_ABodyWithOnlyHandsOrFeetTakesNoNumber()
    {
        var layout = SortFolderSelectors.BuildGearLayout(
            Rows, true, true, null, new LayoutOptions(SharedHandsAndFeet: true))!;
        Assert.True(layout.BodyNumbers.ContainsKey("Rue+"));
        Assert.False(layout.BodyNumbers.ContainsKey("YAB"));
    }

    [Fact]
    public void Shared_UnnumberedLayout_AlsoDropsTheBody()
    {
        var o = new LayoutOptions(SharedHandsAndFeet: true);
        Assert.Equal("Gear/Hands", Folder(Rows[0], Rows, o, numbered: false));
        Assert.Equal("Gear/Rue+/Top", Folder(Rows[1], Rows, o, numbered: false));
    }

    [Fact]
    public void Shared_DoesNothingWhileTheSlotSplitIsOff()
    {
        var o = new LayoutOptions(SharedHandsAndFeet: true);
        Assert.Equal("Gear/Rue+", Folder(Rows[0], Rows, o, splitGear: false, numbered: false));
    }
}
