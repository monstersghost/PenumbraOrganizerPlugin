using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateTests
{
    private static OrganizerModRow MakeRow(string id, string name, bool heliosphere = false) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = $"Unsorted/{name}",
        ProposedPath = $"Unsorted/{name}",
        HeliosphereManaged = heliosphere,
    };

    private static OrganizerModRow MakeCategorizedRow(
        string id, string name, ModCategory? category, string? subCategory = null, bool isProtected = false) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = $"Unsorted/{name}",
        ProposedPath = $"Unsorted/{name}",
        HeliosphereManaged = false,
        Category = category,
        SubCategory = subCategory,
        Protected = isProtected,
    };

    [Fact]
    public void LoadScan_SortsModsByName()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("b", "Zebra"), MakeRow("a", "Apple")], new HashSet<string>());

        Assert.Equal(["Apple", "Zebra"], state.Mods.Select(m => m.Name));
    }

    [Fact]
    public void LoadScan_AppliesPreviouslyProtectedFlag()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_AutoProtectsHeliosphereMods()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("a", "Apple", heliosphere: true)], new HashSet<string>());

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_ResetsProposedPathToCurrentPath()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Apple");
        row.ProposedPath = "SomewhereElse";

        state.LoadScan([row], new HashSet<string>());

        Assert.Equal(state.Mods.Single().CurrentPath, state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SetProtected_TogglesFlagForMatchingMod()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        state.SetProtected("a", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetProtected_UnknownIdentifier_DoesNothing()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        state.SetProtected("does-not-exist", true);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetHeliosphereProtection_OnlyAffectsHeliosphereMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Apple", heliosphere: true), MakeRow("b", "Banana")],
            new HashSet<string>());
        state.SetProtected("b", true);

        state.SetHeliosphereProtection(false);

        Assert.False(state.Mods.Single(m => m.Identifier == "a").Protected);
        Assert.True(state.Mods.Single(m => m.Identifier == "b").Protected);
    }

    [Fact]
    public void AssignManual_SetsProposedPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var result = state.AssignManual("a", "MyFolder/Apple");

        Assert.True(result);
        Assert.Equal("MyFolder/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void AssignManual_ProtectedMod_IsRejected()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        var result = state.AssignManual("a", "MyFolder/Apple");

        Assert.False(result);
        Assert.Equal("Unsorted/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreator_BuildsFolderPlusLeafPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var count = state.SortByCreator(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreator_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        var count = state.SortByCreator(name => name.ToUpperInvariant());

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void Validate_NoChanges_HasNoIssues()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var result = state.Validate();

        Assert.False(result.HasIssues);
    }

    [Fact]
    public void Validate_ProtectedModWithChangedPath_IsFlagged()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });
        // Bypass AssignManual's own protection check to exercise Validate in isolation.
        state.Mods.Single().ProposedPath = "SomewhereElse";

        var result = state.Validate();

        Assert.Contains("a", result.ProtectedViolations);
    }

    [Fact]
    public void Validate_TwoModsWithSameProposedPath_IsFlaggedAsCollision()
    {
        var state = new OrganizerState();
        var apple = MakeRow("a", "Apple");
        var banana = MakeRow("b", "Banana");
        state.LoadScan([apple, banana], new HashSet<string>());
        state.AssignManual("a", "Shared/Same");
        state.AssignManual("b", "Shared/Same");

        var result = state.Validate();

        Assert.True(result.PathCollisions.ContainsKey("Shared/Same"));
        Assert.Equal(2, result.PathCollisions["Shared/Same"].Count);
    }

    [Fact]
    public void Validate_ModsInSameFolderDifferentLeaf_IsNotACollision()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana")], new HashSet<string>());

        state.SortByCreator(name => name);

        Assert.False(state.Validate().HasIssues);
    }

    [Fact]
    public void SortByModType_GroupsByCategoryFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByModType();

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_UsesSubCategoryAsSecondLevel()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeCategorizedRow("a", "Cool Dance", ModCategory.Animation, "Emotes")],
            new HashSet<string>());

        state.SortByModType();

        Assert.Equal("Animation and VFX/Emotes/Cool Dance", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_SkipsUnknownCategory()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByModType();

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.SortByModType();

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Guarded Mod", state.Mods.Single().ProposedPath);
    }
}
