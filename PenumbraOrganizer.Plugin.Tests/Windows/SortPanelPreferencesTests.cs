using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

/// <summary>
/// The pure decisions inside SortPanel.Draw, pulled out so they can be tested without ImGui.
/// </summary>
public class SortPanelPreferencesTests
{
    // ---- hair race code <-> combo index ----

    [Fact]
    public void HairRaceIndexFor_NullCode_IsNone()
    {
        Assert.Equal(0, SortPanel.HairRaceIndexFor(null));
    }

    [Fact]
    public void HairRaceIndexFor_UnknownOrStaleCode_IsNone()
    {
        // A code saved by a build whose race table has since changed must not throw or point at a
        // random row: it falls back to None.
        Assert.Equal(0, SortPanel.HairRaceIndexFor("9999"));
        Assert.Equal(0, SortPanel.HairRaceIndexFor(""));
    }

    [Fact]
    public void HairRaceIndexFor_KnownCode_IsItsTablePositionPlusOne()
    {
        var all = HairRaceCodes.All.ToArray();

        Assert.Equal(1, SortPanel.HairRaceIndexFor(all[0].Code));
        Assert.Equal(all.Length, SortPanel.HairRaceIndexFor(all[^1].Code));
    }

    [Fact]
    public void HairRaceIndexFor_And_HairRaceCodeAt_RoundTripForEveryRace()
    {
        foreach (var race in HairRaceCodes.All)
            Assert.Equal(race.Code, SortPanel.HairRaceCodeAt(SortPanel.HairRaceIndexFor(race.Code)));

        Assert.Null(SortPanel.HairRaceCodeAt(0));
    }

    // ---- body priority re-repair decision ----

    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    [Fact]
    public void RefreshBodyPriority_EmptyLabels_DoesNothing_AndNeverWipesASavedList()
    {
        // The guard the controller cared about: Repair against an empty list would drop every saved
        // entry and report needsSave.
        var result = SortPanel.RefreshBodyPriority([], labelsSeen: null, saved: ["TBSE", "Rue+"], out var needsSave);

        Assert.Null(result);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_SameLabelsInstanceAsBefore_DoesNothing()
    {
        IReadOnlyList<string> labels = Labels;

        var result = SortPanel.RefreshBodyPriority(labels, labelsSeen: labels, saved: ["TBSE"], out var needsSave);

        Assert.Null(result);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_ANewButEqualInstance_RepairsAgain()
    {
        // The reference check is the whole point: a rescan publishes a new instance, and that is
        // when a saved list must be re-validated against the (possibly changed) body list.
        IReadOnlyList<string> seen = [.. Labels];
        IReadOnlyList<string> published = [.. Labels];

        var result = SortPanel.RefreshBodyPriority(published, seen, saved: ["TBSE", "Rue+", "Bibo+"], out var needsSave);

        Assert.NotNull(result);
        Assert.Equal(["TBSE", "Rue+", "Bibo+"], result!.Labels);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_FirstFrame_EmptySavedList_IsDefault_AndIsNotRewritten()
    {
        var result = SortPanel.RefreshBodyPriority(Labels, labelsSeen: null, saved: [], out var needsSave);

        Assert.Equal(Labels, result!.Labels);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_StaleSavedLabel_IsDroppedAndMustBeSaved()
    {
        var result = SortPanel.RefreshBodyPriority(Labels, labelsSeen: null, saved: ["Gone", "TBSE"], out var needsSave);

        Assert.Equal(["TBSE", "Bibo+", "Rue+"], result!.Labels);
        Assert.True(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_NullSavedList_IsTheDefault_AndDoesNotThrow()
    {
        // A hand-edited config can leave the property null.
        var result = SortPanel.RefreshBodyPriority(Labels, labelsSeen: null, saved: null, out var needsSave);

        Assert.Equal(Labels, result!.Labels);
        Assert.False(needsSave);
    }
}
