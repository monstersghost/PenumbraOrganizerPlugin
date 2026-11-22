using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SortFingerprintTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    private static OrganizerState StateWithOneMod()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Old/A", ProposedPath = "Old/A" }],
            new HashSet<string>());
        return state;
    }

    private static SortSelection Selection(Configuration? config = null) =>
        SortSelectionFactory.FromConfig(config ?? new Configuration(), Labels);

    [Fact]
    public void ANewState_HasNoFingerprint() =>
        Assert.Null(new OrganizerState().SortFingerprint);

    [Fact]
    public void SortRunnerMarksTheFingerprintOfTheSelectionItSortedWith()
    {
        var state = StateWithOneMod();
        var selection = Selection();

        SortRunner.Run(state, selection, name => name, scope: null);

        Assert.Equal(selection.Fingerprint(), state.SortFingerprint);
    }

    [Fact]
    public void APartialSort_LeavesNoFingerprint()
    {
        var state = StateWithOneMod();
        SortRunner.Run(state, Selection(), name => name, scope: null);

        SortRunner.Run(state, Selection(), name => name, new SortScope(RootOnly: true));

        Assert.Null(state.SortFingerprint);
    }

    [Fact]
    public void TheFingerprintIgnoresTheScopeFlag()
    {
        var selection = Selection();
        Assert.Equal(selection.Fingerprint(), (selection with { RootOnly = true }).Fingerprint());
    }

    [Fact]
    public void TheFingerprintSeesEverySettingThatCanChangeAFolder()
    {
        var baseline = Selection().Fingerprint();
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitGear = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitHair = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitEmotes = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSharedHandsFeet = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitGearBody = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { SortSplitBodyMods = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { MidlanderFallback = true }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { HairRaceCode = "1801" }).Fingerprint());
        Assert.NotEqual(baseline, Selection(new Configuration { BodyPriority = ["TBSE", "Rue+", "Bibo+"] }).Fingerprint());
        Assert.NotEqual(
            baseline,
            Selection(new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"] }).Fingerprint());
        Assert.NotEqual(
            Selection(new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"], RacePriority = ["Viera"] }).Fingerprint(),
            Selection(new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"], RacePriority = ["Elezen"] }).Fingerprint());
    }

    [Fact]
    public void AManualAssignmentClearsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        Assert.True(state.AssignManual("a", "Somewhere/A"));

        Assert.Null(state.SortFingerprint);
    }

    [Fact]
    public void AFailedAssignmentKeepsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        Assert.False(state.AssignManual("missing", "Somewhere/A"));

        Assert.Equal("x", state.SortFingerprint);
    }

    [Fact]
    public void StageMoveUnderClearsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        state.StageMoveUnder(["a"], "Unused");

        Assert.Null(state.SortFingerprint);
    }

    [Fact]
    public void AScanClearsTheFingerprint()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Old/A", ProposedPath = "Old/A" }],
            new HashSet<string>());

        Assert.Null(state.SortFingerprint);
    }

    [Fact]
    public void ProtectionChangesClearTheFingerprint()
    {
        void Check(Action<OrganizerState> change)
        {
            var state = StateWithOneMod();
            state.MarkSortedWith("x");
            change(state);
            Assert.Null(state.SortFingerprint);
        }

        Check(s => s.SetProtected("a", true));
        Check(s => s.SetAllProtection(true));
        Check(s => s.SetFolderProtectionRule("Old", FolderProtectionRule.Protected));
        Check(s => s.SetToolPolicy(ToolPolicy.From(0, 0, false, [])));
    }

    [Fact]
    public void InvalidateSortFingerprint_ClearsIt()
    {
        var state = StateWithOneMod();
        state.MarkSortedWith("x");

        state.InvalidateSortFingerprint();

        Assert.Null(state.SortFingerprint);
    }
}
