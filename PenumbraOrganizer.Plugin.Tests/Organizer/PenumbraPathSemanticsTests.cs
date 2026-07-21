using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class PenumbraPathSemanticsTests
{
    // --- FixName (mirror of Luna's FileSystemUtility.FixName) ---

    [Theory]
    [InlineData("Foo", "Foo")]
    [InlineData(" Foo ", "Foo")]
    [InlineData("Vespucci ", "Vespucci")]
    [InlineData("", "<None>")]
    [InlineData("   ", "<None>")]
    [InlineData("Foo/Bar", "Foo\\Bar")]
    public void FixName_MirrorsLunaBehavior(string input, string expected)
    {
        Assert.Equal(expected, PenumbraPathSemantics.FixName(input));
    }

    // --- IsDuplicateName (mirror of Luna's FileSystemUtility.IsDuplicateName) ---

    [Theory]
    [InlineData("Foo (2)", true, "Foo")]
    [InlineData("Foo (34)", true, "Foo")]
    [InlineData("Foo (007)", true, "Foo")] // uint.TryParse accepts leading zeros - so does Luna
    [InlineData("Foo", false, "Foo")]
    [InlineData("Foo (x)", false, "Foo (x)")]     // non-numeric content
    [InlineData("Foo(2)", false, "Foo(2)")]       // no space before '('
    [InlineData("Foo ()", false, "Foo ()")]       // empty content
    [InlineData("(2)", false, "(2)")]             // '(' too early - base would be empty
    [InlineData("Foo (-2)", false, "Foo (-2)")]   // negative - uint parse fails
    public void IsDuplicateName_MirrorsLunaGrammar(string input, bool expected, string expectedBase)
    {
        var result = PenumbraPathSemantics.IsDuplicateName(input, out var baseName);

        Assert.Equal(expected, result);
        Assert.Equal(expectedBase, baseName);
    }

    // --- AreEquivalent: the churn-killing equivalence ---

    [Fact]
    public void AreEquivalent_IdenticalPaths_True()
    {
        Assert.True(PenumbraPathSemantics.AreEquivalent("Gear/Foo", "Gear/Foo", "Foo"));
    }

    [Fact]
    public void AreEquivalent_DuplicateMarkerOnCurrent_SameFolder_True()
    {
        // The core case: Penumbra dealt this mod " (2)" on load; proposing the plain name
        // is the same persisted location - moving it would be pure churn.
        Assert.True(PenumbraPathSemantics.AreEquivalent("Gear/Foo (2)", "Gear/Foo", "Foo"));
    }

    [Fact]
    public void AreEquivalent_DifferentDuplicateMarkers_SameFolder_True()
    {
        // Suffix reshuffling between reloads: (2) today, (3) tomorrow - same location.
        Assert.True(PenumbraPathSemantics.AreEquivalent("Gear/Foo (2)", "Gear/Foo (3)", "Foo"));
    }

    [Fact]
    public void AreEquivalent_DifferentFolder_False()
    {
        Assert.False(PenumbraPathSemantics.AreEquivalent("Old/Foo (2)", "Gear/Foo", "Foo"));
    }

    [Fact]
    public void AreEquivalent_SuffixOnCustomLeaf_StillReduced_True()
    {
        // Even for a mod named "Bar", a custom leaf "Foo (2)" persists as SortName "Foo" -
        // DataPath.UpdateByNode's duplicate branch strips the marker unconditionally
        // ("SortName = baseName"). So "Foo (2)" and "Foo" are the same persisted location.
        Assert.True(PenumbraPathSemantics.AreEquivalent("Gear/Foo (2)", "Gear/Foo", "Bar"));
        Assert.True(PenumbraPathSemantics.AreEquivalent("Gear/Foo (2)", "Gear/Foo (3)", "Bar"));
    }

    [Fact]
    public void AreEquivalent_NonMarkerParenthetical_False()
    {
        Assert.False(PenumbraPathSemantics.AreEquivalent("Gear/Foo (x)", "Gear/Foo", "Foo"));
    }

    [Fact]
    public void AreEquivalent_TrailingSpaceDisplayName_True()
    {
        // A mod named "Vespucci " (real library case): Penumbra trims the node name, so the
        // stored leaf is "Vespucci". A proposal built from the untrimmed name is the same place.
        Assert.True(PenumbraPathSemantics.AreEquivalent("Gear/Vespucci", "Gear/Vespucci ", "Vespucci "));
    }

    [Fact]
    public void AreEquivalent_RootLevelPaths_MarkerReduced_True()
    {
        Assert.True(PenumbraPathSemantics.AreEquivalent("Foo (2)", "Foo", "Foo"));
    }

    [Fact]
    public void AreEquivalent_FolderComparisonIsCaseInsensitive()
    {
        Assert.True(PenumbraPathSemantics.AreEquivalent("gear/Foo (2)", "Gear/Foo", "Foo"));
    }

    [Fact]
    public void AreEquivalent_ModWhoseRealNameEndsInMarker_TreatsThatLeafAsItsOwnName()
    {
        // A mod genuinely named "Foo (2)" by its creator: leaf "Foo (2)" IS its display name,
        // so it equals itself and is NOT equivalent to plain "Foo".
        Assert.True(PenumbraPathSemantics.AreEquivalent("Gear/Foo (2)", "Gear/Foo (2)", "Foo (2)"));
        Assert.False(PenumbraPathSemantics.AreEquivalent("Gear/Foo", "Gear/Foo (2)", "Foo (2)"));
    }
}
