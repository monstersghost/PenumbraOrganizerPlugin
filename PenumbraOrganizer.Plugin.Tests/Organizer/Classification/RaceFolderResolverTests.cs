using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class RaceFolderResolverTests
{
    private static RaceFolderResult R(RaceEvidence e, bool toMulti = true, RacePriority? p = null) =>
        RaceFolderResolver.Resolve(e, toMulti, p ?? RacePriority.Empty);

    [Fact]
    public void OneRace_FilesUnderThatRace() =>
        Assert.Equal(new RaceFolderResult("Viera", RaceFolderReason.OneRace), R(RaceEvidence.Of(["Viera"])));

    [Fact]
    public void SeveralRaces_ToggleOn_GoToMultiRace() =>
        Assert.Equal(
            new RaceFolderResult("Multi-race", RaceFolderReason.SeveralToMultiRace),
            R(RaceEvidence.Of(["Viera", "Miqo'te"])));

    [Fact]
    public void SeveralRaces_ToggleOff_PicksTheHighestRankedRace()
    {
        var p = RacePriority.Empty.Add("Viera");
        Assert.Equal(
            new RaceFolderResult("Viera", RaceFolderReason.PriorityPick),
            R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false, p));
    }

    [Fact]
    public void SeveralRaces_ToggleOff_PartialPriorityStillPicksARankedRaceAndUnrankedNeverWin()
    {
        var p = RacePriority.Empty.Add("Elezen").Add("Viera");
        Assert.Equal("Viera", R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false, p).Folder);
    }

    [Fact]
    public void SeveralRaces_ToggleOff_NoneRanked_FallsBackToMultiRace()
    {
        var empty = R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false);
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.SeveralNoRankedRace), empty);

        var other = RacePriority.Empty.Add("Elezen");
        Assert.Equal(
            RaceFolderReason.SeveralNoRankedRace,
            R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false, other).Reason);
    }

    [Fact]
    public void NoEvidence_IsMultiRaceWhateverTheToggle()
    {
        var p = RacePriority.Empty.Add("Viera");
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.NoEvidence), R(RaceEvidence.NoEvidence, true, p));
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.NoEvidence), R(RaceEvidence.NoEvidence, false, p));
    }

    [Fact]
    public void IncompleteEvidence_IsNeverPickedByPriorityAndNeverFilesAsASingleRace()
    {
        var p = RacePriority.Empty.Add("Viera");
        Assert.Equal(
            new RaceFolderResult("Multi-race", RaceFolderReason.Unreadable),
            R(RaceEvidence.Of(["Viera", "Miqo'te"], complete: false), toMulti: false, p));
        Assert.Equal(
            new RaceFolderResult("Multi-race", RaceFolderReason.Unreadable),
            R(RaceEvidence.Of(["Viera"], complete: false)));
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.Unreadable), R(RaceEvidence.Unreadable));
    }
}
