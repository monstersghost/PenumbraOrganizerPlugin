using PenumbraOrganizer.Plugin.Organizer.Collections;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Collections;

public class CollectionUsageTests
{
    private static Guid G(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Build_KeepsOnlyEnabledDirectories_PerCollection()
    {
        var names = new Dictionary<Guid, string> { [G(1)] = "Alice", [G(2)] = "Bob" };
        var settings = new Dictionary<Guid, IReadOnlyDictionary<string, bool>?>
        {
            [G(1)] = new Dictionary<string, bool> { ["a"] = true, ["b"] = false },
            [G(2)] = new Dictionary<string, bool> { ["b"] = true },
        };

        var set = CollectionUsageSet.Build(names, id => settings[id]);

        Assert.Equal(["Alice", "Bob"], set.Collections.Select(c => c.Name).ToArray());
        Assert.Equal(["a"], set.Collections[0].EnabledDirectories.ToArray());
        Assert.Equal(["b"], set.Collections[1].EnabledDirectories.ToArray());
        Assert.Empty(set.Unreadable);
    }

    [Fact]
    public void Build_ANullRead_IsListedUnreadable_AndOthersSurvive()
    {
        var names = new Dictionary<Guid, string> { [G(1)] = "Alice", [G(2)] = "Bob" };

        var set = CollectionUsageSet.Build(
            names, id => id == G(1) ? null : new Dictionary<string, bool> { ["x"] = true });

        Assert.Equal(["Bob"], set.Collections.Select(c => c.Name).ToArray());
        Assert.Equal(["Alice"], set.Unreadable);
    }

    [Fact]
    public void Build_AThrowingRead_IsUnreadable_NotAnException()
    {
        var names = new Dictionary<Guid, string> { [G(1)] = "Alice" };

        var set = CollectionUsageSet.Build(names, _ => throw new InvalidOperationException());

        Assert.Empty(set.Collections);
        Assert.Equal(["Alice"], set.Unreadable);
    }

    [Fact]
    public void UsedDirectories_UnionsAcrossCollections_CaseInsensitive()
    {
        var set = new CollectionUsageSet(
            [
                new CollectionUsage(G(1), "Alice", new HashSet<string> { "A" }),
                new CollectionUsage(G(2), "Bob", new HashSet<string> { "b" }),
            ], []);

        var used = set.UsedDirectories();

        Assert.Contains("a", used);
        Assert.Contains("B", used);
    }
}
