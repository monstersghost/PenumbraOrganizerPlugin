using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class WorkbookExportGateTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    private static SortSelection Current(Configuration? config = null) =>
        SortSelectionFactory.FromConfig(config ?? new Configuration(), Labels);

    private static OrganizerState Scanned()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Old/A", ProposedPath = "Old/A" }],
            new HashSet<string>());
        return state;
    }

    [Fact]
    public void BeforeAnyScan_SaysScanFirst() =>
        Assert.Equal("Scan first.", WorkbookExportGate.SuggestedUnavailableReason(new OrganizerState(), Current()));

    [Fact]
    public void AfterAScanButNoSort_SaysPressSortFirst() =>
        Assert.Equal("Press Sort first.", WorkbookExportGate.SuggestedUnavailableReason(Scanned(), Current()));

    [Fact]
    public void AfterASortWithCurrentSettings_IsAvailable()
    {
        var state = Scanned();
        SortRunner.Run(state, Current(), name => name, scope: null);

        Assert.Null(WorkbookExportGate.SuggestedUnavailableReason(state, Current()));
    }

    [Fact]
    public void AfterTheSettingsChange_SaysSortAgain()
    {
        var state = Scanned();
        SortRunner.Run(state, Current(), name => name, scope: null);

        var changed = Current(new Configuration { SortSplitGear = true });

        Assert.Equal(
            "Sort choices changed since the last sort, press Sort again.",
            WorkbookExportGate.SuggestedUnavailableReason(state, changed));
    }

    [Fact]
    public void AfterAnotherWayOfStagingPaths_SaysThePathsChanged()
    {
        var state = Scanned();
        SortRunner.Run(state, Current(), name => name, scope: null);
        state.AssignManual("a", "Somewhere/A");

        Assert.Equal(
            "Staged paths changed since the last whole-library sort, press Sort again.",
            WorkbookExportGate.SuggestedUnavailableReason(state, Current()));
    }
}
