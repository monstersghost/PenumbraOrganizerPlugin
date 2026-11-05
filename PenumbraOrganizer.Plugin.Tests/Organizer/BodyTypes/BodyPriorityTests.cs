using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.BodyTypes;

public class BodyPriorityTests
{
    private static readonly string[] All = ["Bibo+", "Rue+", "TBSE", "YAB"];

    [Fact]
    public void Default_IsTheBodyListOrder() =>
        Assert.Equal(All, BodyPriority.Default(All).Labels);

    [Fact]
    public void Repair_NullSaved_IsTheDefaultAndNeedsNoSave()
    {
        var result = BodyPriority.Repair(null, All, out var needsSave);

        Assert.Equal(All, result.Labels);
        Assert.False(needsSave);
    }

    [Fact]
    public void Normalize_NullOrEmptySaved_FallsBackToDefault()
    {
        Assert.Equal(All, BodyPriority.Normalize(null, All).Labels);
        Assert.Equal(All, BodyPriority.Normalize([], All).Labels);
    }

    [Fact]
    public void Normalize_KeepsSavedOrder_AppendsMissingBodiesAtTheEnd()
    {
        var p = BodyPriority.Normalize(["YAB", "Bibo+"], All);

        Assert.Equal(["YAB", "Bibo+", "Rue+", "TBSE"], p.Labels);
    }

    [Fact]
    public void Normalize_DropsStaleLabels_IgnoresCase_UsesCanonicalSpelling()
    {
        var p = BodyPriority.Normalize(["yab", "GoneBody", "RUE+"], All);

        Assert.Equal(["YAB", "Rue+", "Bibo+", "TBSE"], p.Labels);
    }

    [Fact]
    public void Normalize_DuplicatesKeepTheFirstOccurrence_BlankEntriesSkipped()
    {
        var p = BodyPriority.Normalize(["TBSE", "", "  ", "tbse", "TBSE", null!], All);

        Assert.Equal(["TBSE", "Bibo+", "Rue+", "YAB"], p.Labels);
    }

    [Fact]
    public void Normalize_DuplicateLabelsInTheBodyList_AreCollapsed()
    {
        var p = BodyPriority.Default(["Bibo+", "bibo+", "Rue+"]);

        Assert.Equal(["Bibo+", "Rue+"], p.Labels);
    }

    [Fact]
    public void Normalize_IsNotCappedAtTheUserBodyLimit()
    {
        var many = Enumerable.Range(0, BodyTypeListStore.MaxUserBodyCount + 30).Select(i => $"Body{i}").ToArray();

        Assert.Equal(many.Length, BodyPriority.Default(many).Labels.Count);
    }

    [Fact]
    public void Rank_IsPositionInTheList_UnknownIsLast()
    {
        var p = BodyPriority.Normalize(["YAB"], All);

        Assert.Equal(0, p.Rank("yab"));
        Assert.Equal(1, p.Rank("Bibo+"));
        Assert.Equal(int.MaxValue, p.Rank("Nope"));
    }

    [Fact]
    public void Equality_IsByContent_NotByReference()
    {
        var a = BodyPriority.Normalize(["YAB"], All);
        var b = BodyPriority.Normalize(["yab"], All);

        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != a.MoveDown(0));
    }

    [Fact]
    public void Labels_CannotBeUsedToMutateTheSnapshot()
    {
        var p = BodyPriority.Default(All);
        var before = p.GetHashCode();

        Assert.False(p.Labels is string[]);
        if (p.Labels is IList<string> list)
            Assert.Throws<NotSupportedException>(() => list[0] = "Hacked");

        Assert.Equal(All, p.Labels);
        Assert.Equal(before, p.GetHashCode());
        Assert.Equal(0, p.Rank("Bibo+"));
    }

    [Fact]
    public void Repair_NeedsSaveOnlyWhenANonEmptySavedListWasChanged()
    {
        Assert.False(RepairNeedsSave([], All));                         // empty = default, never rewritten
        Assert.False(RepairNeedsSave(["YAB", "Bibo+", "Rue+", "TBSE"], All)); // already normalized
        Assert.True(RepairNeedsSave(["YAB", "Gone"], All));             // stale label dropped
        Assert.True(RepairNeedsSave(["YAB"], All));                     // missing bodies appended
        Assert.True(RepairNeedsSave(["yab", "Bibo+", "Rue+", "TBSE"], All)); // spelling canonicalized
    }

    private static bool RepairNeedsSave(string[] saved, string[] all)
    {
        BodyPriority.Repair(saved, all, out var needsSave);
        return needsSave;
    }

    [Fact]
    public void Repair_RemoveThenReintroduce_DoesNotRestoreTheOldRank()
    {
        // Saved with Gone in first place; Gone is removed from the list, the repaired list is
        // WRITTEN BACK, then Gone returns. It must come back at the end, not at rank 0.
        var withGone = new[] { "Gone", "YAB", "Rue+", "Bibo+", "TBSE" };
        var without = All;

        var repaired = BodyPriority.Repair(withGone, without, out var needsSave);
        Assert.True(needsSave);
        var persisted = repaired.Labels.ToArray(); // what the panel writes back to config

        var reintroduced = BodyPriority.Repair(persisted, [.. without, "Gone"], out _);

        Assert.Equal("Gone", reintroduced.Labels[^1]);
        Assert.Equal(["YAB", "Rue+", "Bibo+", "TBSE", "Gone"], reintroduced.Labels);
    }

    [Fact]
    public void Move_ReordersWithoutMutating_AndEdgesAreNoOps()
    {
        var p = BodyPriority.Default(All);

        Assert.Equal(["Rue+", "Bibo+", "TBSE", "YAB"], p.MoveDown(0).Labels);
        Assert.Equal(["Rue+", "Bibo+", "TBSE", "YAB"], p.MoveUp(1).Labels);
        Assert.Same(p, p.MoveUp(0));
        Assert.Same(p, p.MoveDown(3));
        Assert.Equal(All, p.Labels);
    }

    [Fact]
    public void ResetToDefault_RestoresEqualityWithTheDefault()
    {
        var moved = BodyPriority.Default(All).MoveDown(0);

        Assert.True(moved != BodyPriority.Default(All));
        Assert.True(BodyPriority.Normalize(null, All) == BodyPriority.Default(All));
    }
}
