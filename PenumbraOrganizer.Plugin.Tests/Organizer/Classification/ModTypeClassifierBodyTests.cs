namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

public class ModTypeClassifierBodyTests
{
    private static readonly ClassificationResult BodyBase =
        new(ModCategory.Body, null, ClassificationSource.Structural);

    private static BodyTypeMatch Match(string label = "Bibo+") => new(label, label.ToLowerInvariant());

    // ── EnrichBodySubCategory: both signals required ──

    [Fact]
    public void Enrich_StructuralAndNameAgree_SetsTheBodyType()
    {
        var result = ModTypeClassifier.EnrichBodySubCategory(BodyBase, isBodyReplacer: true, Match());

        Assert.Equal(ModCategory.Body, result.Category);
        Assert.Equal("Bibo+", result.SubCategory);
    }

    [Fact]
    public void Enrich_NameMatchesButFilesSayItIsNotABody_LeavesItAlone()
    {
        // THE case the whole design exists for: "Bibo+ Medieval Dress" names a body and is a dress.
        var result = ModTypeClassifier.EnrichBodySubCategory(BodyBase, isBodyReplacer: false, Match());

        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void Enrich_ReadFailure_LeavesItAlone()
    {
        // Null is "no evidence", not "yes" - a mod is only filed under a body on positive evidence.
        var result = ModTypeClassifier.EnrichBodySubCategory(BodyBase, isBodyReplacer: null, Match());

        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void Enrich_IsABodyButNameUnrecognised_LeavesItAlone()
    {
        var result = ModTypeClassifier.EnrichBodySubCategory(BodyBase, isBodyReplacer: true, nameMatch: null);

        Assert.Null(result.SubCategory);
    }

    [Theory]
    [InlineData(ModCategory.Gear)]
    [InlineData(ModCategory.Hair)]
    [InlineData(ModCategory.Skin)]
    [InlineData(ModCategory.Face)]
    [InlineData(ModCategory.NPC)]
    public void Enrich_NonBodyCategory_IsUntouched(ModCategory category)
    {
        // Same guard shape as EnrichGearSubCategory and EnrichHairSubCategory: this is a second
        // pass over a category the structural rules already settled, never a reclassification.
        var baseResult = new ClassificationResult(category, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichBodySubCategory(baseResult, isBodyReplacer: true, Match());

        Assert.Equal(category, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void Enrich_PreservesTheClassificationSource()
    {
        // A body type is a subcategory refinement, so it must not turn a structural result into a
        // name-heuristic one even though a name is what supplied the label.
        var result = ModTypeClassifier.EnrichBodySubCategory(BodyBase, isBodyReplacer: true, Match());

        Assert.Equal(ClassificationSource.Structural, result.Source);
    }

    // ── ModTypeFolders: the Body arm ──

    [Theory]
    [InlineData("Bibo+", "Body/Bibo+")]
    [InlineData("YAB Buff", "Body/YAB Buff")]
    [InlineData("Tight&Firm", "Body/Tight&Firm")]
    [InlineData("TBSE-X", "Body/TBSE-X")]
    [InlineData("FreyjaAF", "Body/FreyjaAF")]
    public void GetFolder_ValidBodyLabel_NestsOneLevel(string label, string expected) =>
        Assert.Equal(expected, ModTypeFolders.GetFolder(ModCategory.Body, label));

    [Fact]
    public void GetFolder_BodyWithNoSubCategory_IsFlatBody() =>
        Assert.Equal("Body", ModTypeFolders.GetFolder(ModCategory.Body, null));

    [Theory]
    [InlineData("Body/Nested")]      // would escape the flat-folder guarantee
    [InlineData("Body\\Nested")]
    [InlineData(" Leading")]         // untrimmed
    [InlineData("Trailing ")]
    [InlineData("")]
    [InlineData("Bad*Char")]
    [InlineData("Bad:Char")]
    public void GetFolder_MalformedBodyLabel_Throws(string label) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTypeFolders.GetFolder(ModCategory.Body, label));

    [Fact]
    public void GetFolder_OverLongBodyLabel_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModTypeFolders.GetFolder(ModCategory.Body, new string('x', 41)));

    [Fact]
    public void GetFolder_BodyWithAnArbitraryWord_IsAcceptedByShape()
    {
        // Pins the LIMIT of this arm rather than pretending it is stricter than it is. Hair's
        // digits-only check rejects "Bosses"; body's shape check cannot, because a real body label
        // is an arbitrary word. Membership is guaranteed upstream instead - only a match against
        // the loaded list ever sets this subcategory - and that is what
        // BodyTypeListStaticContentTests covers from the data side.
        Assert.Equal("Body/Bosses", ModTypeFolders.GetFolder(ModCategory.Body, "Bosses"));
    }
}
