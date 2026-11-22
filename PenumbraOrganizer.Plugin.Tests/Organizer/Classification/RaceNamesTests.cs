using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class RaceNamesTests
{
    [Fact]
    public void All_IsExactlyTheNineNamesInOrder() =>
        Assert.Equal(
            new[] { "Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera" },
            RaceNames.All);

    [Fact]
    public void All_MatchesTheRacesHairRaceCodesKnows() =>
        Assert.Equal(
            RaceNames.All.OrderBy(n => n, StringComparer.Ordinal),
            HairRaceCodes.All.Select(r => r.Race).Distinct().OrderBy(n => n, StringComparer.Ordinal));

    [Fact]
    public void MultiRace_IsTheExistingFolderName() => Assert.Equal("Multi-race", RaceNames.MultiRace);

    [Theory]
    [InlineData("Midlander", true)]
    [InlineData("Miqo'te", true)]
    [InlineData("Hyur", false)]
    [InlineData("midlander", false)]
    [InlineData(null, false)]
    public void IsKnown_IsExactAndCaseSensitive(string? name, bool expected) =>
        Assert.Equal(expected, RaceNames.IsKnown(name));

    [Theory]
    [InlineData("Midlander", "Midlander")]
    [InlineData("Highlander", "Highlander")]
    [InlineData("Wildwood", "Elezen")]
    [InlineData("Duskwight", "Elezen")]
    [InlineData("Plainsfolk", "Lalafell")]
    [InlineData("Dunesfolk", "Lalafell")]
    [InlineData("SeekerOfTheSun", "Miqo'te")]
    [InlineData("KeeperOfTheMoon", "Miqo'te")]
    [InlineData("Seawolf", "Roegadyn")]
    [InlineData("Hellsguard", "Roegadyn")]
    [InlineData("Raen", "Au Ra")]
    [InlineData("Xaela", "Au Ra")]
    [InlineData("Helion", "Hrothgar")]
    [InlineData("Lost", "Hrothgar")]
    [InlineData("Rava", "Viera")]
    [InlineData("Veena", "Viera")]
    public void FromSubRaceEnumName_FoldsAllSixteenClans(string clan, string expected) =>
        Assert.Equal(expected, RaceNames.FromSubRaceEnumName(clan));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("7")]
    [InlineData("Seeker of the Sun")]
    [InlineData("")]
    [InlineData(null)]
    public void FromSubRaceEnumName_RejectsUnknownNumericDisplayNamesAndEmpty(string? clan) =>
        Assert.Null(RaceNames.FromSubRaceEnumName(clan));
}
