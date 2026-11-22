using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RaceReasonSummaryTests
{
    private static OrganizerModRow Row(RaceFolderResult? staged) =>
        new()
        {
            Identifier = Guid.NewGuid().ToString("N"), Name = "m", Author = "a", CurrentPath = "m", ProposedPath = "m",
            StagedRace = staged,
        };

    [Fact]
    public void NoStagedRaces_IsNull() => Assert.Null(RaceReasonSummary.Describe([Row(null), Row(null)]));

    [Fact]
    public void CountsEachReasonSeparately_SoADetectionFailureNeverLooksLikeMultiRace()
    {
        var rows = new[]
        {
            Row(new("Viera", RaceFolderReason.OneRace)),
            Row(new("Viera", RaceFolderReason.OneRace)),
            Row(new("Multi-race", RaceFolderReason.SeveralToMultiRace)),
            Row(new("Elezen", RaceFolderReason.PriorityPick)),
            Row(new("Multi-race", RaceFolderReason.NoEvidence)),
            Row(new("Multi-race", RaceFolderReason.Unreadable)),
            Row(new("Multi-race", RaceFolderReason.SeveralNoRankedRace)),
        };

        Assert.Equal(
            "Race split: 2 one race, 1 several races (Multi-race), 1 several races (priority pick), "
            + "1 several races (none ranked, Multi-race), 1 no race evidence, 1 unreadable evidence",
            RaceReasonSummary.Describe(rows));
    }
}
