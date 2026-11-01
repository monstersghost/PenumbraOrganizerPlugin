using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class HairRaceCodesTests
{
    [Theory]
    [InlineData("0101", "Midlander", "Male")]
    [InlineData("0201", "Midlander", "Female")]
    [InlineData("0301", "Highlander", "Male")]
    [InlineData("0401", "Highlander", "Female")]
    [InlineData("0501", "Elezen", "Male")]
    [InlineData("0601", "Elezen", "Female")]
    [InlineData("0701", "Miqo'te", "Male")]
    [InlineData("0801", "Miqo'te", "Female")]
    [InlineData("0901", "Roegadyn", "Male")]
    [InlineData("1001", "Roegadyn", "Female")]
    [InlineData("1101", "Lalafell", "Male")]
    [InlineData("1201", "Lalafell", "Female")]
    [InlineData("1301", "Au Ra", "Male")]
    [InlineData("1401", "Au Ra", "Female")]
    [InlineData("1501", "Hrothgar", "Male")]
    [InlineData("1601", "Hrothgar", "Female")]
    [InlineData("1701", "Viera", "Male")]
    [InlineData("1801", "Viera", "Female")]
    public void EveryConfirmedCode_MapsToItsRaceAndGender(string code, string race, string gender)
    {
        // One row per confirmed code. A wrong row here is a wrong folder for a real user, so each
        // entry is pinned on its own rather than by counting the table.
        Assert.True(HairRaceCodes.TryGetByCode(code, out var found));
        Assert.Equal(new HairRace(code, race, gender), found);
    }

    [Fact]
    public void TheTableHasExactlyEighteenEntries()
    {
        Assert.Equal(18, HairRaceCodes.All.Count);
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("1102")]
    [InlineData("9999")]
    public void AnUnknownCode_IsNotFound(string code)
    {
        Assert.False(HairRaceCodes.TryGetByCode(code, out _));
    }
}
