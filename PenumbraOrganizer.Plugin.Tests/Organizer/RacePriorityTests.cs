using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RacePriorityTests
{
    [Fact]
    public void Normalize_KeepsKnownDistinctNamesInOrder() =>
        Assert.Equal(
            new[] { "Viera", "Miqo'te" },
            RacePriority.Normalize(["Viera", "Hyur", "Miqo'te", "Viera", "midlander"]).Ranked);

    [Fact]
    public void Normalize_NullIsEmpty() => Assert.Empty(RacePriority.Normalize(null).Ranked);

    [Fact]
    public void AddRemoveAndMove_ReturnNewInstancesAndNeverTouchTheOriginal()
    {
        var p = RacePriority.Empty.Add("Viera").Add("Elezen").Add("Viera");
        Assert.Equal(new[] { "Viera", "Elezen" }, p.Ranked);

        Assert.Equal(new[] { "Elezen", "Viera" }, p.MoveDown(0).Ranked);
        Assert.Equal(new[] { "Elezen", "Viera" }, p.MoveUp(1).Ranked);
        Assert.Equal(new[] { "Elezen" }, p.Remove("Viera").Ranked);
        Assert.Equal(new[] { "Viera", "Elezen" }, p.Ranked);
    }

    [Fact]
    public void MoveUpAtTopAndMoveDownAtBottom_ChangeNothing()
    {
        var p = RacePriority.Empty.Add("Viera").Add("Elezen");
        Assert.Equal(p.Ranked, p.MoveUp(0).Ranked);
        Assert.Equal(p.Ranked, p.MoveDown(1).Ranked);
    }

    [Fact]
    public void Add_IgnoresUnknownNames() => Assert.Empty(RacePriority.Empty.Add("Hyur").Ranked);

    [Fact]
    public void RankOf_IsZeroBasedAndMinusOneWhenUnranked()
    {
        var p = RacePriority.Empty.Add("Viera").Add("Elezen");
        Assert.Equal(0, p.RankOf("Viera"));
        Assert.Equal(1, p.RankOf("Elezen"));
        Assert.Equal(-1, p.RankOf("Lalafell"));
    }

    [Fact]
    public void Equality_IsBySequenceNotByReference()
    {
        var a = RacePriority.Empty.Add("Viera").Add("Elezen");
        var b = RacePriority.Normalize(["Viera", "Elezen"]);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b.MoveDown(0));
    }
}
