using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateTests
{
    // The strategies that ignore the creator segment still take a canonicalizer. These tests were
    // written against SortByModType()/SortByModTypeDetailed(), which took none.
    private static string Identity(string s) => s;

    private static OrganizerModRow MakeRow(string id, string name, bool heliosphere = false, string? currentPath = null) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = currentPath ?? $"Unsorted/{name}",
        ProposedPath = currentPath ?? $"Unsorted/{name}",
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
    public void SetAllProtection_True_ProtectsEveryMod()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana")], new HashSet<string>());

        state.SetAllProtection(true);

        Assert.All(state.Mods, m => Assert.True(m.Protected));
    }

    [Fact]
    public void SetAllProtection_False_UnprotectsEveryMod()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana", heliosphere: true)], new HashSet<string>());
        state.SetAllProtection(true);

        state.SetAllProtection(false);

        Assert.All(state.Mods, m => Assert.False(m.Protected));
    }

    [Fact]
    public void SetAllProtection_EmptyLibrary_DoesNotThrow()
    {
        var state = new OrganizerState();
        state.LoadScan([], new HashSet<string>());

        state.SetAllProtection(true);

        Assert.Empty(state.Mods);
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

        var count = state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreator_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        var count = state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, name => name.ToUpperInvariant());

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

        state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, name => name);

        Assert.False(state.Validate().HasIssues);
    }

    [Fact]
    public void SortByModType_GroupsByCategoryFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

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

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal("Animation and VFX/Emotes/Cool Dance", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_UnknownCategory_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SortByCreator_UnknownOrWhitespaceCreator_GoesToReviewFolder(string canonicalized)
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var count = state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, _ => canonicalized);

        Assert.Equal(1, count);
        Assert.Equal("Review/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Guarded Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreator_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("Foo", "Foo"), MakeRow("Foo_2", "Foo")],
            new HashSet<string>());

        state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, name => name);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByCreator_CalledTwice_ProducesIdenticalPaths()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("Foo", "Foo"), MakeRow("Foo_2", "Foo")],
            new HashSet<string>());

        state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, name => name);
        var firstRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, name => name);
        var secondRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        Assert.Equal(firstRun, secondRun);
    }

    [Fact]
    public void SortByModType_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByModType_CalledTwice_ProducesIdenticalPaths()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);
        var firstRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);
        var secondRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        Assert.Equal(firstRun, secondRun);
    }

    [Fact]
    public void SortByTypeThenCreator_BothKnown_BuildsTypeSlashCreatorPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("Gear/SOMEAUTHOR/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_OnlyTypeKnown_UsesTypeAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, _ => "");

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_OnlyCreatorKnown_UsesCreatorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_NeitherKnown_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, _ => "");

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Guarded Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, name => name);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByCreatorThenType_BothKnown_BuildsCreatorSlashTypePath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_OnlyCreatorKnown_UsesCreatorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_OnlyTypeKnown_UsesTypeAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, _ => "");

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_NeitherKnown_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, _ => "");

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Guarded Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [
                MakeCategorizedRow("Foo", "Foo", ModCategory.Gear),
                MakeCategorizedRow("Foo_2", "Foo", ModCategory.Gear),
            ],
            new HashSet<string>());

        state.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, name => name);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByTypeThenCreatorAndSortByCreatorThenType_DifferOnlyInOrder()
    {
        var typeThenCreatorState = new OrganizerState();
        typeThenCreatorState.LoadScan(
            [MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());
        typeThenCreatorState.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, name => name.ToUpperInvariant());

        var creatorThenTypeState = new OrganizerState();
        creatorThenTypeState.LoadScan(
            [MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());
        creatorThenTypeState.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal("Gear/SOMEAUTHOR/Cool Jacket", typeThenCreatorState.Mods.Single().ProposedPath);
        Assert.Equal("SOMEAUTHOR/Gear/Cool Jacket", creatorThenTypeState.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreatorFlat_GearWithResolvedSlot_IgnoresSlotSubfolder()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeCategorizedRow("a", "Cool Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        var count = state.Sort(SortStrategy.TypeThenCreator, false, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("Gear/SOMEAUTHOR/Cool Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_GearWithResolvedSlot_UsesSlotSubfolder()
    {
        // Contrast case proving the detailed variant is unaffected by the flat helper's addition.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeCategorizedRow("a", "Cool Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        state.Sort(SortStrategy.TypeThenCreator, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal("Gear/Feet/SOMEAUTHOR/Cool Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenTypeFlat_GearWithResolvedSlot_IgnoresSlotSubfolder()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeCategorizedRow("a", "Cool Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        var count = state.Sort(SortStrategy.CreatorThenType, false, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Gear/Cool Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_GearWithResolvedSlot_UsesSlotSubfolder()
    {
        // Contrast case proving the detailed variant is unaffected by the flat helper's addition.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeCategorizedRow("a", "Cool Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        state.Sort(SortStrategy.CreatorThenType, true, true, splitHair: false, name => name.ToUpperInvariant());

        Assert.Equal("SOMEAUTHOR/Gear/Feet/Cool Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModTypeAndSortByModTypeDetailed_GearWithResolvedSlot_DifferOnlyInSlotSubfolder()
    {
        var flatState = new OrganizerState();
        flatState.LoadScan(
            [MakeCategorizedRow("a", "Cool Boots", ModCategory.Gear, "Feet")], new HashSet<string>());
        flatState.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        var detailedState = new OrganizerState();
        detailedState.LoadScan(
            [MakeCategorizedRow("a", "Cool Boots", ModCategory.Gear, "Feet")], new HashSet<string>());
        detailedState.Sort(SortStrategy.TypeOnly, true, true, splitHair: false, Identity);

        Assert.Equal("Gear/Cool Boots", flatState.Mods.Single().ProposedPath);
        Assert.Equal("Gear/Feet/Cool Boots", detailedState.Mods.Single().ProposedPath);
    }

    [Fact]
    public void HasScanned_FalseBeforeAnyScan()
    {
        var state = new OrganizerState();

        Assert.False(state.HasScanned);
    }

    [Fact]
    public void HasScanned_TrueAfterEmptyScan()
    {
        // The specific case Mods.Count == 0 can't distinguish: a scan that found zero mods.
        var state = new OrganizerState();

        state.LoadScan([], new HashSet<string>());

        Assert.True(state.HasScanned);
        Assert.Empty(state.Mods);
    }

    // --- Penumbra round-trip semantics: duplicate-suffix and FixName churn prevention ---

    private static OrganizerModRow MakePlacedRow(
        string id, string name, string currentPath, ModCategory? category = ModCategory.Gear) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = currentPath,
        ProposedPath = currentPath,
        HeliosphereManaged = false,
        Category = category,
    };

    [Fact]
    public void SortByModType_DuplicatesAlreadyInFolderWithReshuffledSuffixes_ProposeNoMoves()
    {
        // Penumbra discards " (N)" suffixes on save and re-deals them in arbitrary order on
        // every reload - so which install carries which suffix is not identity. Three duplicate
        // installs already sitting in Gear must all be recognized as in place, whatever
        // suffixes Penumbra dealt them today.
        var state = new OrganizerState();
        state.LoadScan(
        [
            MakePlacedRow("Foo", "Foo", "Gear/Foo (2)"),
            MakePlacedRow("Foo (2)", "Foo", "Gear/Foo (3)"),
            MakePlacedRow("Foo (3)", "Foo", "Gear/Foo"),
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.All(state.Mods, m => Assert.Equal(m.CurrentPath, m.ProposedPath));
    }

    [Fact]
    public void SortByModType_OneDuplicateElsewhere_MovesOnlyThatOne()
    {
        var state = new OrganizerState();
        var inPlace = MakePlacedRow("Foo", "Foo", "Gear/Foo (2)");
        var elsewhere = MakePlacedRow("Foo (2)", "Foo", "Unsorted/Foo");
        state.LoadScan([inPlace, elsewhere], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal("Gear/Foo (2)", inPlace.ProposedPath);          // pinned, no churn
        Assert.Equal("Gear/Foo", elsewhere.ProposedPath);            // genuinely moves
    }

    [Fact]
    public void SortByModType_TrailingSpaceModName_NoPhantomMove()
    {
        // Real-library case ("Vespucci "): Penumbra trims node names, so CurrentPath comes
        // back trimmed. A proposal built from the untrimmed Name must not diff forever.
        var state = new OrganizerState();
        var row = MakePlacedRow("Vespucci ", "Vespucci ", "Gear/Vespucci");
        state.LoadScan([row], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal("Gear/Vespucci", row.ProposedPath);
    }

    [Fact]
    public void SortByModType_ModNameContainingSlash_LeafUsesBackslashLikePenumbra()
    {
        // Luna's FixName converts '/' to '\' in names - a leaf with a raw '/' would split
        // into a bogus extra folder level on Penumbra's side.
        var state = new OrganizerState();
        var row = MakePlacedRow("a", "Foo/Bar", "Unsorted/FooBar");
        state.LoadScan([row], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal("Gear/Foo\\Bar", row.ProposedPath);
    }

    [Fact]
    public void SortByCreator_CreatorSegmentIsTrimmedLikePenumbra()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Apple");
        state.LoadScan([row], new HashSet<string>());

        state.Sort(SortStrategy.CreatorOnly, false, true, splitHair: false, _ => " Alice ");

        Assert.Equal("Alice/Apple", row.ProposedPath);
    }

    [Fact]
    public void SortByModType_GearWithSubCategory_GoesToFlatGearFolderNotSubfolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal("Gear/Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_NonGearWithSubCategory_KeepsSubfolder()
    {
        // Only Gear is flattened - every other category's subfolder behavior is unchanged.
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Wave", ModCategory.Animation, "Emotes")], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Identity);

        Assert.Equal("Animation and VFX/Emotes/Wave", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModTypeDetailed_GearWithSubCategory_UsesSubfolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, true, true, splitHair: false, Identity);

        Assert.Equal("Gear/Feet/Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModTypeDetailed_GearWithoutSubCategory_GoesToBareGearFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cloak", ModCategory.Gear, null)], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, true, true, splitHair: false, Identity);

        Assert.Equal("Gear/Cloak", state.Mods.Single().ProposedPath);
    }

    // --- Three-source protection model: individual / folder / Heliosphere ---

    // --- Folder-scoped bulk unprotect ---
    // Reported in-game: "Toggle protect all", then try to exclude one folder so its mods sort.
    // Protect All writes an explicit per-mod entry for every mod and never ticks a folder, so the
    // folder checkbox is already clear - there is nothing to untick - and every row under it stays
    // protected through the explicit-identifier clause. Sort then offers "apply to 0 mods". The
    // folder checkbox stays a pure folder-rule toggle (the two tests above depend on that); this
    // is the affirmative action that clears protection from every source for one folder's contents.

    [Fact]
    public void UnprotectFolderContents_ClearsExplicitModProtectionUnderFolder()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Boots", currentPath: "NewMods/Boots"), MakeRow("b", "Hat", currentPath: "Gear/Hat")],
            new HashSet<string>());

        state.SetAllProtection(true);
        state.UnprotectFolderContents("NewMods");

        Assert.False(state.Mods.Single(m => m.Identifier == "a").Protected);
        Assert.DoesNotContain("a", state.ProtectedModIdentifiers);
        Assert.True(state.Mods.Single(m => m.Identifier == "b").Protected);
    }

    [Fact]
    public void UnprotectFolderContents_ClearsNestedSubfolderMods()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "NewMods/Sub/Deeper/Boots")], new HashSet<string>());

        state.SetAllProtection(true);
        state.UnprotectFolderContents("NewMods");

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnprotectFolderContents_RemovesTheFolderRuleToo()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "NewMods/Boots")], new HashSet<string>());

        state.SetFolderProtected("NewMods", true);
        state.UnprotectFolderContents("NewMods");

        Assert.False(state.Mods.Single().Protected);
        Assert.DoesNotContain("NewMods", state.ProtectedFolders);
    }

    [Fact]
    public void UnprotectFolderContents_UnprotectsHeliosphereModsInsideTheFolder()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Boots", heliosphere: true, currentPath: "NewMods/Boots"),
             MakeRow("b", "Hat", heliosphere: true, currentPath: "Gear/Hat")],
            new HashSet<string>());

        state.UnprotectFolderContents("NewMods");

        Assert.False(state.Mods.Single(m => m.Identifier == "a").Protected);
        Assert.True(state.Mods.Single(m => m.Identifier == "b").Protected);
    }

    [Fact]
    public void UnprotectFolderContents_LeavesModsProtectedByAStillProtectedAncestorFolder()
    {
        // The ancestor is a separate, untouched instruction - clearing "Gear/Feet" must not quietly
        // escape a protected "Gear". The row stays protected, and the count reflects that.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots")], new HashSet<string>());

        state.SetFolderProtected("Gear", true);
        state.UnprotectFolderContents("Gear/Feet");

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnprotectFolderContents_ReturnsTheNumberOfModsItFreed()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Boots", currentPath: "NewMods/Boots"),
             MakeRow("b", "Hat", currentPath: "NewMods/Hat"),
             MakeRow("c", "Belt", currentPath: "Gear/Belt")],
            new HashSet<string>());

        state.SetAllProtection(true);

        Assert.Equal(2, state.UnprotectFolderContents("NewMods"));
    }

    [Fact]
    public void UnprotectFolderContents_UnknownFolder_DoesNothing()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "NewMods/Boots")], new HashSet<string>());

        state.SetAllProtection(true);

        Assert.Equal(0, state.UnprotectFolderContents("   "));
        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void BuildProtectedModCountsByFolder_ProtectedAncestor_LeavesNothingFreeableBelowIt()
    {
        // What the Protect tab needs to know before offering the action at all: with "Gear"
        // protected, no unprotect of "Gear/Feet" can free anything, so the tab must not offer one.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots")], new HashSet<string>());

        state.SetAllProtection(true);
        state.SetFolderProtected("Gear", true);

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(1, counts["Gear/Feet"].Protected);
        Assert.Equal(0, counts["Gear/Feet"].Freeable);
        // The ancestor's own row can free it: its rule is the only folder rule covering the mod.
        Assert.Equal(1, counts["Gear"].Freeable);
    }

    [Fact]
    public void BuildProtectedModCountsByFolder_TwoProtectedFoldersCoverARow_NeitherCanFreeIt()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots")], new HashSet<string>());

        state.SetFolderProtected("Gear", true);
        state.SetFolderProtected("Gear/Feet", true);

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(0, counts["Gear"].Freeable);
        Assert.Equal(0, counts["Gear/Feet"].Freeable);
    }

    [Fact]
    public void UnprotectFolderContents_ReturnsOnlyRowsItTookFromProtectedToUnprotected()
    {
        // Already-unprotected rows are not something this action freed. Counting them would
        // overstate what the caller can report - a folder of 10 mods with 2 protected returned 10.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Boots", currentPath: "NewMods/Boots"),
             MakeRow("b", "Hat", currentPath: "NewMods/Hat"),
             MakeRow("c", "Belt", currentPath: "NewMods/Belt")],
            new HashSet<string>());

        state.SetProtected("a", true);

        Assert.Equal(1, state.UnprotectFolderContents("NewMods"));
    }

    [Fact]
    public void UnprotectFolderContents_AncestorStillProtects_LeavesTheRowsUntouchedRatherThanHalfCleared()
    {
        // The row stays protected either way, so the tick must survive: otherwise unticking "Gear"
        // later would unprotect a mod whose own tick the user never knowingly dropped.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots")], new HashSet<string>());

        state.SetProtected("a", true);
        state.SetFolderProtected("Gear", true);

        Assert.Equal(0, state.UnprotectFolderContents("Gear/Feet"));
        Assert.Contains("a", state.ProtectedModIdentifiers);

        state.SetFolderProtected("Gear", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnprotectFolderContents_AncestorStillProtects_RecordsNoHeliosphereOverride()
    {
        // Same shape, for the source with no checkbox of its own. The override would be invisible
        // now and surface later as protection silently dropped.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", heliosphere: true, currentPath: "Gear/Feet/Boots")], new HashSet<string>());

        state.SetFolderProtected("Gear", true);
        state.UnprotectFolderContents("Gear/Feet");
        state.SetFolderProtected("Gear", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void BuildProtectedModCountsByFolder_CountsEverySourceAtEveryDepth()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Boots", currentPath: "NewMods/Boots"),
             MakeRow("b", "Hat", heliosphere: true, currentPath: "NewMods/Sub/Hat"),
             MakeRow("c", "Belt", currentPath: "NewMods/Belt"),
             MakeRow("d", "Ring", currentPath: "Gear/Ring")],
            new HashSet<string>());

        state.SetProtected("a", true);

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(2, counts["NewMods"].Protected);
        Assert.Equal(2, counts["NewMods"].Freeable);
        Assert.Equal(1, counts["NewMods/Sub"].Protected);
        // "Gear" now gets an entry so the Protect tab's tree can label it; what the old
        // assertion was really pinning is that an unprotected mod contributes nothing to
        // the protected counts, which this says directly.
        Assert.Equal(0, counts["Gear"].Protected);
        Assert.Equal(0, counts["Gear"].Freeable);
    }


    [Fact]
    public void SetFolderProtected_ProtectsCurrentlyScannedModsUnderFolder()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_ProtectsNestedSubfolderMods()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Sub/Boots");
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_UnprotectingAncestor_LeavesDescendantFolderProtectionIntact()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear", true);
        state.SetFolderProtected("Gear/Feet", true);
        state.SetFolderProtected("Gear", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_Unprotecting_DoesNotDisableIndividualProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        state.LoadScan([row], new HashSet<string>());

        state.SetProtected("a", true);
        state.SetFolderProtected("Gear/Feet", true);
        state.SetFolderProtected("Gear/Feet", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_Unprotecting_DoesNotDisableHeliosphereProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", heliosphere: true, currentPath: "Gear/Feet/Boots");
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);
        state.SetFolderProtected("Gear/Feet", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtected_DoesNotUndoAnExplicitHeliosphereUnprotect()
    {
        // Reported in-game: untick "Toggle Heliosphere protection", then tick ANY folder, and every
        // Heliosphere mod snaps back to protected. SetFolderProtected recomputes every row through
        // IsEffectivelyProtected, whose first clause is `row.HeliosphereManaged ||`, so the user's
        // explicit choice is silently overwritten by an unrelated action.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("helio", "Boots", heliosphere: true), MakeRow("plain", "Hat", currentPath: "Gear/Top/Hat")],
            new HashSet<string>());

        state.SetHeliosphereProtection(false);
        Assert.False(state.Mods.Single(m => m.Identifier == "helio").Protected);

        state.SetFolderProtected("Gear/Top", true);

        Assert.False(state.Mods.Single(m => m.Identifier == "helio").Protected);
    }

    [Fact]
    public void SetFolderProtected_Unprotecting_AlsoDoesNotUndoAnExplicitHeliosphereUnprotect()
    {
        // The other direction the report named. Both branches of SetFolderProtected run the same
        // full recompute, so unprotecting re-asserts Heliosphere protection just as protecting does.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("helio", "Boots", heliosphere: true)], new HashSet<string>());

        state.SetFolderProtected("Gear/Top", true);
        state.SetHeliosphereProtection(false);
        state.SetFolderProtected("Gear/Top", false);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void HeliosphereUnprotectOverride_IsClearedByTheNextScan()
    {
        // The documented contract: Heliosphere mods are re-protected on every scan no matter how
        // the toggle was last left, because Heliosphere owns their location. The override makes the
        // choice survive unrelated UI actions, NOT survive a scan.
        var state = new OrganizerState();
        var row = MakeRow("helio", "Boots", heliosphere: true);
        state.LoadScan([row], new HashSet<string>());
        state.SetHeliosphereProtection(false);

        state.LoadScan([MakeRow("helio", "Boots", heliosphere: true)], new HashSet<string>());

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void ProtectingAFolder_StillProtectsAnUnprotectedHeliosphereModInsideIt()
    {
        // Protecting a folder is a newer and more specific instruction than "unprotect Heliosphere",
        // so it wins for mods actually in that folder. The bug was that it also won for every
        // Heliosphere mod everywhere else.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("inside", "Boots", heliosphere: true, currentPath: "Gear/Feet/Boots"),
             MakeRow("outside", "Hat", heliosphere: true, currentPath: "Gear/Top/Hat")],
            new HashSet<string>());
        state.SetHeliosphereProtection(false);

        state.SetFolderProtected("Gear/Feet", true);

        Assert.True(state.Mods.Single(m => m.Identifier == "inside").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "outside").Protected);
    }

    [Fact]
    public void RetickingAHeliosphereMod_WithdrawsTheOverride()
    {
        // Without withdrawal the row would stay stuck unprotected: the override would outlive the
        // user's decision to re-protect it.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("helio", "Boots", heliosphere: true)], new HashSet<string>());

        state.SetProtected("helio", false);
        state.SetProtected("helio", true);
        state.SetFolderProtected("Unsorted", false);   // any unrelated full recompute

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void ToggleProtectAllOff_AlsoUnticksHeliosphereMods()
    {
        // "Toggle protect all" is an explicit instruction about every mod. Before the override
        // existed, the Heliosphere rows stayed visibly ticked and the button looked broken.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("helio", "Boots", heliosphere: true), MakeRow("plain", "Hat")],
            new HashSet<string>());

        state.SetAllProtection(false);

        Assert.All(state.Mods, m => Assert.False(m.Protected));
    }

    [Fact]
    public void FolderOnlyProtectedMod_NeverEntersProtectedModIdentifiers()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        state.LoadScan([row], new HashSet<string>());

        state.SetFolderProtected("Gear/Feet", true);

        Assert.DoesNotContain("a", state.ProtectedModIdentifiers);
    }

    [Fact]
    public void SetProtected_OnHeliosphereMod_PreservesTransientOverrideUntilNextScan()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple", heliosphere: true)], new HashSet<string>());
        Assert.True(state.Mods.Single().Protected);

        state.SetProtected("a", false);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetProtected_OnFolderProtectedMod_RecomputesImmediatelyBackToProtected()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        state.LoadScan([row], new HashSet<string>());
        state.SetFolderProtected("Gear/Feet", true);
        Assert.True(state.Mods.Single().Protected);

        state.SetProtected("a", false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetAllProtection_False_DoesNotDisableFolderProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        state.LoadScan([row], new HashSet<string>());
        state.SetFolderProtected("Gear/Feet", true);

        state.SetAllProtection(false);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void KnownFolders_DerivesDistinctParentsFromScannedMods()
    {
        var state = new OrganizerState();
        var a = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");
        var b = MakeRow("b", "Hat", currentPath: "Gear/Feet/Hat");
        var c = MakeRow("c", "Root", currentPath: "RootMod");
        state.LoadScan([a, b, c], new HashSet<string>());

        Assert.Equal(["Gear", "Gear/Feet"], state.KnownFolders);
    }

    [Fact]
    public void KnownFolders_IncludesEveryAncestorPrefixOfADeeplyNestedMod()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Sub/Boots");
        state.LoadScan([row], new HashSet<string>());

        Assert.Equal(["Gear", "Gear/Feet", "Gear/Feet/Sub"], state.KnownFolders);
    }

    [Fact]
    public void KnownFolders_ProtectingAnAncestorOfferedByThisExpansion_ProtectsTheDeepMod()
    {
        // End-to-end confirmation that the newly offered ancestor row actually works through the
        // existing (unchanged) recursive matching in SetFolderProtected/IsUnderAnyProtectedFolder.
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Sub/Boots");
        state.LoadScan([row], new HashSet<string>());
        Assert.Contains("Gear", state.KnownFolders);

        state.SetFolderProtected("Gear", true);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void KnownFolders_IsRecomputedOnEachLoadScan_NotStaleFromAPriorScan()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots")], new HashSet<string>());
        Assert.Equal(["Gear", "Gear/Feet"], state.KnownFolders);

        state.LoadScan([MakeRow("b", "Hat", currentPath: "Face/Hat")], new HashSet<string>());

        Assert.Equal(["Face"], state.KnownFolders);
    }

    [Fact]
    public void LoadScan_WithPersistedProtectedFolder_AppliesFolderProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");

        state.LoadScan([row], new HashSet<string>(), new Dictionary<string, bool> { ["Gear/Feet"] = true });

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_WithoutThirdArgument_StillCompilesAndAppliesNoFolderProtection()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots");

        state.LoadScan([row], new HashSet<string>());

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void AssignManualBatch_BlankFolder_ReportsAllFailedWithoutMutating()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var results = state.AssignManualBatch(new HashSet<string> { "a" }, "   ");

        Assert.All(results, r => Assert.False(r.Success));
        Assert.Equal("Unsorted/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void AssignManualBatch_AssignsEveryIdentifierUnderSameFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana")], new HashSet<string>());

        var results = state.AssignManualBatch(new HashSet<string> { "a", "b" }, "MyFolder");

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal("MyFolder/Apple", state.Mods.Single(m => m.Identifier == "a").ProposedPath);
        Assert.Equal("MyFolder/Banana", state.Mods.Single(m => m.Identifier == "b").ProposedPath);
    }

    [Fact]
    public void AssignManualBatch_UnknownIdentifier_ReportsFailedWithoutAffectingOthers()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        var results = state.AssignManualBatch(new HashSet<string> { "a", "missing" }, "MyFolder");

        Assert.True(results.Single(r => r.Identifier == "a").Success);
        Assert.False(results.Single(r => r.Identifier == "missing").Success);
    }

    [Fact]
    public void AssignManualBatch_ProtectedIdentifier_ReportsFailed()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        var results = state.AssignManualBatch(new HashSet<string> { "a" }, "MyFolder");

        Assert.False(results.Single().Success);
    }

    [Fact]
    public void AssignManualBatch_TrimsSlashesFromFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string>());

        state.AssignManualBatch(new HashSet<string> { "a" }, "/MyFolder/");

        Assert.Equal("MyFolder/Apple", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ReplaceScanAtomically_ThrowingDuringDerivation_LeavesPreviousStateIntact()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Gear/A", ProposedPath = "Gear/A" }],
            new HashSet<string>(StringComparer.Ordinal));

        // A row whose enumeration throws part-way: the first item is fine, the second blows up
        // during derivation, exactly like a malformed path would.
        IEnumerable<OrganizerModRow> Exploding()
        {
            yield return new OrganizerModRow { Identifier = "b", Name = "B", Author = "y", CurrentPath = "Gear/B", ProposedPath = "Gear/B" };
            throw new InvalidOperationException("derivation failed");
        }

        Assert.Throws<InvalidOperationException>(() =>
            state.ReplaceScanAtomically(Exploding(), new HashSet<string>(StringComparer.Ordinal)));

        // The previous scan must still be entirely present and uncontaminated.
        var mods = state.Mods;
        Assert.Single(mods);
        Assert.Equal("a", mods[0].Identifier);
        Assert.Contains("Gear", state.KnownFolders);
    }

    [Fact]
    public void ReplaceScanAtomically_OnSuccess_BehavesExactlyLikeLoadScan()
    {
        var viaLoadScan = new OrganizerState();
        var viaReplace = new OrganizerState();
        OrganizerModRow[] Rows() =>
        [
            new() { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Gear/Feet/A", ProposedPath = "somewhere/else" },
        ];
        var protectedIds = new HashSet<string>(["a"], StringComparer.Ordinal);

        viaLoadScan.LoadScan(Rows(), protectedIds);
        viaReplace.ReplaceScanAtomically(Rows(), protectedIds);

        Assert.Equal(viaLoadScan.Mods.Select(m => m.Identifier), viaReplace.Mods.Select(m => m.Identifier));
        Assert.Equal(viaLoadScan.Mods[0].Protected, viaReplace.Mods[0].Protected);
        Assert.Equal(viaLoadScan.Mods[0].ProposedPath, viaReplace.Mods[0].ProposedPath);
        Assert.Equal(viaLoadScan.KnownFolders, viaReplace.KnownFolders);
        Assert.True(viaReplace.HasScanned);
    }

    // --- Folder exclude rules: "protect haloween, except haloween/animation" ---

    [Fact]
    public void SetFolderProtectionRule_ExcludeUnderProtectedAncestor_FreesOnlyThatSubtree()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("root", "Mask", currentPath: "haloween/Mask"),
             MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance"),
             MakeRow("deep", "Wave", currentPath: "haloween/animation/sub/Wave"),
             // Under no rule at all. Without it, every row here sits under SOME rule, where
             // "the folder layer protects" and "the folder layer does not exclude" agree - and
             // a protection clause weakened from one to the other would pass this test.
             MakeRow("outside", "Boots", currentPath: "Gear/Boots")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.True(state.Mods.Single(m => m.Identifier == "root").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "anim").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "deep").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "outside").Protected);
    }

    [Fact]
    public void SetFolderProtectionRule_ProtectNestedUnderAnExclude_ReprotectsThatBranch()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance"),
             MakeRow("keep", "Keep", currentPath: "haloween/animation/keep/Keep")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);
        state.SetFolderProtectionRule("haloween/animation/keep", FolderProtectionRule.Protected);

        Assert.False(state.Mods.Single(m => m.Identifier == "anim").Protected);
        Assert.True(state.Mods.Single(m => m.Identifier == "keep").Protected);
    }

    [Fact]
    public void SetFolderProtectionRule_None_WithdrawsTheRuleAndInheritsAgain()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.None);

        Assert.True(state.Mods.Single().Protected);
        Assert.DoesNotContain("haloween/animation", state.FolderProtectionRules.Keys);
    }

    [Fact]
    public void SetFolderProtectionRule_Exclude_DoesNotFreeAHandTickedMod()
    {
        // An exclude cancels the FOLDER layer only. A tick is a per-mod choice the user made.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetProtected("anim", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void SetFolderProtectionRule_Exclude_DoesNotFreeAHeliosphereMod()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("anim", "Dance", heliosphere: true, currentPath: "haloween/animation/Dance")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void ProtectedFolders_ReportsOnlyProtectRules_NotExcludes()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.Contains("haloween", state.ProtectedFolders);
        Assert.DoesNotContain("haloween/animation", state.ProtectedFolders);
    }

    [Fact]
    public void ResolveFolderRule_ReportsOwnRuleAndItsSource()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);

        Assert.Equal(
            new OrganizerState.FolderRuleResolution(FolderProtectionRule.Protected, "haloween"),
            state.ResolveFolderRule("haloween"));
        // Inherited: the rule applies, and the source names the ancestor it came from.
        Assert.Equal(
            new OrganizerState.FolderRuleResolution(FolderProtectionRule.Protected, "haloween"),
            state.ResolveFolderRule("haloween/animation"));
        Assert.Equal(
            new OrganizerState.FolderRuleResolution(FolderProtectionRule.None, null),
            state.ResolveFolderRule("Gear"));
    }

    [Fact]
    public void ReplaceScanAtomically_CarriesExcludeRulesThroughARescan()
    {
        var state = new OrganizerState();
        var rules = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["haloween"] = true,
            ["haloween/animation"] = false,
        };

        state.LoadScan(
            [MakeRow("root", "Mask", currentPath: "haloween/Mask"),
             MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")],
            new HashSet<string>(),
            rules);

        Assert.True(state.Mods.Single(m => m.Identifier == "root").Protected);
        Assert.False(state.Mods.Single(m => m.Identifier == "anim").Protected);
        Assert.False(state.FolderProtectionRules["haloween/animation"]);
    }

    [Fact]
    public void BuildProtectedModCountsByFolder_ExcludedFolder_CountsOnlyTheStragglersAsFreeable()
    {
        // The exclude already freed the folder-protected rows. What is left is a hand-ticked mod,
        // which only "Unprotect N inside" can clear - so the tab must still offer it, for 1.
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("plain", "Dance", currentPath: "haloween/animation/Dance"),
             MakeRow("ticked", "Wave", currentPath: "haloween/animation/Wave")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetProtected("ticked", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(1, counts["haloween/animation"].Protected);
        Assert.Equal(1, counts["haloween/animation"].Freeable);
        Assert.Equal(2, counts["haloween/animation"].Total);
    }

    [Fact]
    public void BuildProtectedModCountsByFolder_ReportsTotalsForFoldersWithNothingProtected()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("a", "Dance", currentPath: "hair/01/Dance"),
             MakeRow("b", "Wave", currentPath: "hair/01/Wave")],
            new HashSet<string>());

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(2, counts["hair"].Total);
        Assert.Equal(2, counts["hair/01"].Total);
        Assert.Equal(0, counts["hair"].Protected);
    }

    [Fact]
    public void BuildProtectedModCountsByFolder_ProtectNestedUnderAnExclude_IsFreeableAtTheDeepestFolder()
    {
        // The masked ancestor is the trap: "haloween" protects, but the exclude below it means
        // withdrawing the "keep" rule leaves the row excluded, not protected - so the deepest
        // folder CAN free it and the tab must offer the action.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Keep", currentPath: "haloween/animation/keep/Keep")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);
        state.SetFolderProtectionRule("haloween/animation/keep", FolderProtectionRule.Protected);

        var counts = state.BuildProtectedModCountsByFolder();

        Assert.Equal(1, counts["haloween/animation/keep"].Protected);
        Assert.Equal(1, counts["haloween/animation/keep"].Freeable);
    }

    [Fact]
    public void UnprotectFolderContents_InsideAnExcludedBranch_ClearsTicksAndLeavesTheAncestorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("root", "Mask", currentPath: "haloween/Mask"),
             MakeRow("ticked", "Wave", currentPath: "haloween/animation/Wave")],
            new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetProtected("ticked", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);

        Assert.Equal(1, state.UnprotectFolderContents("haloween/animation"));
        Assert.False(state.Mods.Single(m => m.Identifier == "ticked").Protected);
        // The ancestor's rule is untouched, so its own mods stay protected.
        Assert.True(state.Mods.Single(m => m.Identifier == "root").Protected);
    }

    [Fact]
    public void UnprotectFolderContents_KeepsAnExcludeRule_SoItDoesNotReprotectWhatItJustFreed()
    {
        // The action withdraws a PROTECT rule. Withdrawing an exclude would hand the branch back
        // to the protected ancestor - it would re-protect every row the action is being asked to
        // free, which is the exact opposite of the button's promise.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("anim", "Dance", currentPath: "haloween/animation/Dance")], new HashSet<string>());

        state.SetFolderProtected("haloween", true);
        state.SetFolderProtectionRule("haloween/animation", FolderProtectionRule.Excluded);
        state.UnprotectFolderContents("haloween/animation");

        Assert.False(state.FolderProtectionRules["haloween/animation"]);
        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnprotectFolderContents_WithNoOwnRule_StillDoesNotEscapeAProtectedAncestor()
    {
        // The pinned rule from 0.6.1.2, restated against the new model: the button never overrides
        // an ancestor on its own. Escaping an ancestor is what the tri-state exclude is for, and
        // that takes a deliberate click on this folder's own box.
        var state = new OrganizerState();
        state.LoadScan([MakeRow("a", "Boots", currentPath: "Gear/Feet/Boots")], new HashSet<string>());

        state.SetFolderProtected("Gear", true);
        state.UnprotectFolderContents("Gear/Feet");

        Assert.True(state.Mods.Single().Protected);
        Assert.DoesNotContain("Gear/Feet", state.FolderProtectionRules.Keys);
    }
}
