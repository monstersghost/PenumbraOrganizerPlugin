namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class ModNameNormalizerTests
{
    [Theory]
    [InlineData("Bibo+ Medieval (Penumbra)_1_1_0", "bibo+ medieval")]
    [InlineData("Bibo+  Medieval", "bibo+ medieval")]
    [InlineData("Bibo+ Medieval Redux", "bibo+ medieval redux")]
    [InlineData("Emperor's New Fists", "emperors new fists")]
    [InlineData("[WIP] Foo-Bar", "foo bar")]
    [InlineData("My Mod v2.1", "my mod")]
    [InlineData("Gear 2000", "gear 2000")]
    [InlineData("Café Outfit", "café outfit")]
    public void Normalize_SpecTable(string input, string expected)
    {
        Assert.Equal(expected, ModNameNormalizer.Normalize(input));
    }

    // The whole feature's correctness rests on these two NOT collapsing together.
    [Fact]
    public void Normalize_DistinctNames_DoNotCollide()
    {
        Assert.NotEqual(
            ModNameNormalizer.Normalize("Bibo+ Medieval"),
            ModNameNormalizer.Normalize("Bibo+ Medieval Redux"));
    }

    // A general "strip trailing digits" rule would wrongly turn this into "gear".
    [Fact]
    public void Normalize_TrailingDigitsWithoutSeparatorPrefix_ArePreserved()
    {
        Assert.Equal("gear 2000", ModNameNormalizer.Normalize("Gear 2000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("___")]
    public void Normalize_NothingSignificant_ReturnsEmpty(string input)
    {
        Assert.Equal("", ModNameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = ModNameNormalizer.Normalize("Bibo+ Medieval (Penumbra)_1_1_0");

        Assert.Equal(once, ModNameNormalizer.Normalize(once));
    }
}
