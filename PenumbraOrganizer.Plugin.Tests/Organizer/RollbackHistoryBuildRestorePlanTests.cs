using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class RollbackHistoryBuildRestorePlanTests
{
    private static RollbackSnapshot Snapshot(params (string Id, string Path)[] entries) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "n/a",
            entries.ToDictionary(e => e.Id, e => e.Path, StringComparer.Ordinal));

    [Fact]
    public void BuildRestorePlan_MatchingModDifferentPath_ProducesMove()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Gear/Mod A", move.CurrentPath);
        Assert.Equal("Creators/Alice/Mod A", move.TargetPath);
        Assert.Empty(plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MatchingModSamePath_IsUnchanged()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MatchingModPathDiffersOnlyByDuplicateMarker_IsUnchanged()
    {
        // Penumbra discards a transient " (N)" duplicate-marker suffix on save and re-deals it
        // arbitrarily on every load - the historical snapshot path and the live path here are
        // the SAME persisted location, so this must classify as Unchanged, not a proposed Move
        // (see PenumbraPathSemantics.AreEquivalent's doc comment for why raw string equality
        // is wrong here).
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A (2)", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInSnapshot_IsSkippedUninstalled()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod>();

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.SkippedUninstalledIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInCurrentState_MovesToRoot()
    {
        // Pre-existing, unchanged policy: a mod absent from the target snapshot is root-relocated.
        // This plan does not alter this behavior (see the plan's Revision Note) - kept here only
        // to pin the existing contract while Task 2 changes the protection-related classification
        // elsewhere in this method.
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Creators/Alice/Mod A", move.CurrentPath);
        Assert.Equal("Mod A", move.TargetPath);
        Assert.Equal(["mod-a"], plan.RootRelocatedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModOnlyInCurrentStateAlreadyAtRoot_IsUnchanged()
    {
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Empty(plan.Moves);
        Assert.Equal(["mod-a"], plan.UnchangedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_ModWithDifferentHistoricalPath_MovesRegardlessOfCallerProtectionState()
    {
        // API-shape test only: BuildRestorePlan no longer accepts a protected-identifiers set at
        // all, so there is nothing for a caller to pass that could block this move - this test
        // confirms the method's contract, not that Plugin.Restore's caller correctly refrains
        // from filtering before calling in. That end-to-end guarantee (Plugin.Restore actually
        // ignoring Config.ProtectedModIdentifiers/ProtectedFolderPaths/HeliosphereManaged) can't
        // be automated - Plugin.cs requires live Dalamud/Penumbra services and has no unit-test
        // coverage anywhere in this codebase (established convention). See this plan's Manual
        // Validation Matrix, "Exact Restore" section, for the real end-to-end check.
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"));
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Creators/Alice/Mod A", move.TargetPath);
    }

    [Fact]
    public void BuildRestorePlan_HeliosphereManagedModNotInSnapshot_MovesToRootLikeAnyOtherMod()
    {
        // Confirms the pre-existing root-relocation policy (see BuildRestorePlan_ModOnlyInCurrentState_MovesToRoot)
        // now applies uniformly regardless of HeliosphereManaged - previously this specific case
        // was diverted into "skipped protected" purely because HeliosphereManaged was true. This
        // does NOT introduce a new destructive policy: root-relocation for snapshot-absent mods
        // already existed before this plan: this test only confirms HeliosphereManaged no longer
        // special-cases it.
        var target = Snapshot();
        var current = new List<LiveMod> { new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: true) };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        var move = Assert.Single(plan.Moves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Mod A", move.TargetPath);
        Assert.Equal(["mod-a"], plan.RootRelocatedIdentifiers);
    }

    [Fact]
    public void BuildRestorePlan_MultipleMods_ClassifiesEachIndependently()
    {
        var target = Snapshot(("mod-a", "Creators/Alice/Mod A"), ("mod-c", "Gear/Mod C"));
        var current = new List<LiveMod>
        {
            new("mod-a", "Mod A", "Gear/Mod A", HeliosphereManaged: false), // move
            new("mod-b", "Mod B", "Gear/Mod B", HeliosphereManaged: false), // root-relocated
        };

        var plan = RollbackHistory.BuildRestorePlan(target, current);

        Assert.Equal(["mod-a", "mod-b"], plan.Moves.Select(m => m.Identifier).OrderBy(id => id));
        Assert.Equal(["mod-b"], plan.RootRelocatedIdentifiers);
        Assert.Equal(["mod-c"], plan.SkippedUninstalledIdentifiers);
    }
}
