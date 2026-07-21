using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateTests
{
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
    public void SortByModType_UnknownCategory_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByModType();

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

        var count = state.SortByCreator(_ => canonicalized);

        Assert.Equal(1, count);
        Assert.Equal("Review/Apple", state.Mods.Single().ProposedPath);
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

    [Fact]
    public void SortByCreator_DuplicateInstallsWithSameName_AreDisambiguated()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeRow("Foo", "Foo"), MakeRow("Foo_2", "Foo")],
            new HashSet<string>());

        state.SortByCreator(name => name);

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

        state.SortByCreator(name => name);
        var firstRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        state.SortByCreator(name => name);
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

        state.SortByModType();

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

        state.SortByModType();
        var firstRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        state.SortByModType();
        var secondRun = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        Assert.Equal(firstRun, secondRun);
    }

    [Fact]
    public void SortByTypeThenCreator_BothKnown_BuildsTypeSlashCreatorPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("Gear/SOMEAUTHOR/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_OnlyTypeKnown_UsesTypeAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_OnlyCreatorKnown_UsesCreatorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_NeitherKnown_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByTypeThenCreator(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByTypeThenCreator_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.SortByTypeThenCreator(name => name.ToUpperInvariant());

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

        state.SortByTypeThenCreator(name => name);

        Assert.False(state.Validate().HasIssues);
        var paths = state.Mods.Select(m => m.ProposedPath).ToHashSet();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SortByCreatorThenType_BothKnown_BuildsCreatorSlashTypePath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByCreatorThenType(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_OnlyCreatorKnown_UsesCreatorAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByCreatorThenType(name => name.ToUpperInvariant());

        Assert.Equal(1, count);
        Assert.Equal("SOMEAUTHOR/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_OnlyTypeKnown_UsesTypeAlone()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByCreatorThenType(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_NeitherKnown_GoesToReviewFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByCreatorThenType(_ => "");

        Assert.Equal(1, count);
        Assert.Equal("Review/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByCreatorThenType_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.SortByCreatorThenType(name => name.ToUpperInvariant());

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

        state.SortByCreatorThenType(name => name);

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
        typeThenCreatorState.SortByTypeThenCreator(name => name.ToUpperInvariant());

        var creatorThenTypeState = new OrganizerState();
        creatorThenTypeState.LoadScan(
            [MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());
        creatorThenTypeState.SortByCreatorThenType(name => name.ToUpperInvariant());

        Assert.Equal("Gear/SOMEAUTHOR/Cool Jacket", typeThenCreatorState.Mods.Single().ProposedPath);
        Assert.Equal("SOMEAUTHOR/Gear/Cool Jacket", creatorThenTypeState.Mods.Single().ProposedPath);
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

        state.SortByModType();

        Assert.All(state.Mods, m => Assert.Equal(m.CurrentPath, m.ProposedPath));
    }

    [Fact]
    public void SortByModType_OneDuplicateElsewhere_MovesOnlyThatOne()
    {
        var state = new OrganizerState();
        var inPlace = MakePlacedRow("Foo", "Foo", "Gear/Foo (2)");
        var elsewhere = MakePlacedRow("Foo (2)", "Foo", "Unsorted/Foo");
        state.LoadScan([inPlace, elsewhere], new HashSet<string>());

        state.SortByModType();

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

        state.SortByModType();

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

        state.SortByModType();

        Assert.Equal("Gear/Foo\\Bar", row.ProposedPath);
    }

    [Fact]
    public void SortByCreator_CreatorSegmentIsTrimmedLikePenumbra()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Apple");
        state.LoadScan([row], new HashSet<string>());

        state.SortByCreator(_ => " Alice ");

        Assert.Equal("Alice/Apple", row.ProposedPath);
    }

    [Fact]
    public void SortByModType_GearWithSubCategory_GoesToFlatGearFolderNotSubfolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        state.SortByModType();

        Assert.Equal("Gear/Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_NonGearWithSubCategory_KeepsSubfolder()
    {
        // Only Gear is flattened - every other category's subfolder behavior is unchanged.
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Wave", ModCategory.Animation, "Emotes")], new HashSet<string>());

        state.SortByModType();

        Assert.Equal("Animation and VFX/Emotes/Wave", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModTypeDetailed_GearWithSubCategory_UsesSubfolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

        state.SortByModTypeDetailed();

        Assert.Equal("Gear/Feet/Boots", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModTypeDetailed_GearWithoutSubCategory_GoesToBareGearFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cloak", ModCategory.Gear, null)], new HashSet<string>());

        state.SortByModTypeDetailed();

        Assert.Equal("Gear/Cloak", state.Mods.Single().ProposedPath);
    }

    // --- Three-source protection model: individual / folder / Heliosphere ---

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

        state.LoadScan([row], new HashSet<string>(), new HashSet<string> { "Gear/Feet" });

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
}
