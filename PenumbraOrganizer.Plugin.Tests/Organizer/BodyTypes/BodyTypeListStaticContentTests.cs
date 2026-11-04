namespace PenumbraOrganizer.Plugin.Tests.Organizer.BodyTypes;

using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Core.Classification;

/// <summary>
/// Guards the bundled list itself, not the code that reads it. Every assertion here is a property
/// the data must hold for the feature to behave, and that a careless edit to the JSON could break
/// without any code change.
/// </summary>
public class BodyTypeListStaticContentTests
{
    private static BodyTypeListDocument Bundled()
    {
        var parse = BodyTypeListCodec.Parse(BodyTypeListStore.ReadEmbeddedStaticList());
        Assert.Equal(BodyTypeListParseStatus.Ok, parse.Status);
        return parse.Data!;
    }

    [Fact]
    public void EmbeddedResource_IsPresentAndParses() => Assert.NotEmpty(Bundled().Bodies);

    [Fact]
    public void EmbeddedResource_HasNoByteOrderMark()
    {
        // BodyTypeListCodec.Parse never throws, so a BOM would not fail loudly - it would leave
        // every scan with no body types at all. NpcNameListStore carries the same warning.
        var raw = BodyTypeListStore.ReadEmbeddedStaticList();
        Assert.False(raw.StartsWith('﻿'), "body-type-list-static.json must be saved without a UTF-8 BOM.");
    }

    [Fact]
    public void EveryLabel_IsUnique()
    {
        var labels = Bundled().Bodies.Select(b => b.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryAlias_IsUniqueAcrossTheWholeList()
    {
        // Two bodies claiming one alias is resolvable but arbitrary, so the shipped list must never
        // contain one. The codec leaves such a collision in place precisely so this can catch it.
        var aliases = Bundled().Bodies.SelectMany(b => b.Aliases).ToList();
        var duplicates = aliases.GroupBy(a => a, StringComparer.Ordinal)
                                .Where(g => g.Count() > 1)
                                .Select(g => g.Key)
                                .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryBody_HasAtLeastOneAlias() =>
        Assert.All(Bundled().Bodies, b => Assert.NotEmpty(b.Aliases));

    [Fact]
    public void EveryAlias_IsLowercaseAndTrimmed()
    {
        // The matcher lower-cases the MOD NAME and compares ordinally, so an alias that is not
        // already lowercase could never match anything.
        Assert.All(Bundled().Bodies, b => Assert.All(b.Aliases, a =>
        {
            Assert.Equal(a.ToLowerInvariant(), a);
            Assert.Equal(a.Trim(), a);
        }));
    }

    [Fact]
    public void EveryLabel_SurvivesGetFolder()
    {
        // The real contract: every shipped label must be accepted as a body type and produce
        // exactly one segment. A label GetFolder rejects would throw mid-sort.
        Assert.All(Bundled().Bodies, b =>
        {
            Assert.Equal($"Gear/{b.Label}/Top", ModTypeFolders.GetFolder(ModCategory.Gear, "Top", b.Label));
            Assert.Equal($"Gear/{b.Label}", ModTypeFolders.GetFolder(ModCategory.Gear, null, b.Label));
            Assert.Equal($"Body/{b.Label}", ModTypeFolders.GetFolder(ModCategory.Body, null, b.Label));
        });
    }

    [Fact]
    public void EverySex_IsRecognised()
    {
        // Metadata only - nothing builds a folder from it - but a typo here would still be wrong
        // in a file people read. Unisex is real: Otopop and Re[de]fined Lalafell are Lalafell bodies.
        Assert.All(Bundled().Bodies,
            b => Assert.Contains(b.Sex, (string[])["Female", "Male", "Unisex"]));
    }

    [Fact]
    public void OnlyTheLalafellBodies_CarryARace()
    {
        // A race-specific body is the exception, so an empty field is the norm and a stray value
        // in it would be a mistake worth catching.
        var raced = Bundled().Bodies.Where(b => b.Race.Length > 0).Select(b => b.Label).OrderBy(l => l);

        Assert.Equal(["Otopop", "Re[de]fined Lalafell"], raced);
        Assert.All(Bundled().Bodies.Where(b => b.Race.Length > 0), b => Assert.Equal("Lalafell", b.Race));
    }

    [Fact]
    public void EverySource_IsVerifiedOrReported() =>
        Assert.All(Bundled().Bodies, b => Assert.Contains(b.Source, (string[])["verified", "reported"]));

    [Fact]
    public void TheMaleBodies_AreTbseAndHrBodyOnly()
    {
        // A deliberate decision from the plugin author, recorded here because Proteus models it
        // differently and it is easy to "correct" back: every TBSE flavour, TBSE-X included, is one
        // TBSE folder, while HRBody is a genuinely separate body.
        var male = Bundled().Bodies.Where(b => b.Sex == "Male").Select(b => b.Label).OrderBy(l => l);

        Assert.Equal(["TBSE", "The hrBody"], male);
    }

    [Fact]
    public void TbseAbsorbsItsVariants()
    {
        // TBSE-X must NOT be its own label, and its spellings must still be detected - the two
        // halves of "fold the variant in" that a careless edit could get half-right.
        Assert.DoesNotContain("TBSE-X", Bundled().Bodies.Select(b => b.Label));

        var tbse = Bundled().Bodies.Single(b => b.Label == "TBSE");
        Assert.Contains("tbse-x", tbse.Aliases);
        Assert.Contains("tbsex", tbse.Aliases);
    }

    [Fact]
    public void BimboResolvesToRox_TheRebrandedName()
    {
        // Bimbo+ was renamed Rox+ by its author. Mods released under the old name are still out
        // there in quantity, so both spellings must land on the new label - dropping either half
        // breaks it: the old name stops being detected, or the new one does.
        var rox = Bundled().Bodies.Single(b => b.Label == "Rox+");

        Assert.Contains("bimbo+", rox.Aliases);
        Assert.Contains("rox+", rox.Aliases);
        Assert.DoesNotContain("Bimbo+", Bundled().Bodies.Select(b => b.Label));
        Assert.NotEqual(string.Empty, rox.Note);
    }

    [Fact]
    public void TightAndFirm_CarriesItsAbbreviations()
    {
        // TnF and TF3 are how the community writes it, and gear titles use them far more than the
        // full name. Missing these was the single largest detection gap in the first list.
        var tnf = Bundled().Bodies.Single(b => b.Label == "Tight&Firm").Aliases;

        Assert.Contains("tnf", tnf);
        Assert.Contains("tf3", tnf);
    }

    [Fact]
    public void LaRueBelongsToLavaBod_NotRue()
    {
        // LaRue is LavaBod in a Rue shape, so a LaRue garment is cut for LavaBod. Easy to "fix" the
        // wrong way on the strength of the name alone, hence the test.
        Assert.Contains("larue", Bundled().Bodies.Single(b => b.Label == "LavaBod+").Aliases);
        Assert.DoesNotContain("larue", Bundled().Bodies.Single(b => b.Label == "Rue+").Aliases);
    }

    [Theory]
    // The names that are NOT bodies, each for a confirmed reason. Re-adding any of them is a
    // regression: a size, a nail option, a shader generation or a garment pack would start
    // claiming a Body subfolder.
    [InlineData("uranus", "a chest size inside the Luxe garment pack")]
    [InlineData("stabbies", "a hand/nail option")]
    [InlineData("gen2", "a texture standard (vanilla-based mirrored textures), not a body")]
    [InlineData("gen3", "a texture standard, not a body - Gen3 bodies are named individually")]
    [InlineData("lithe", "a substring of Neolithe")]
    [InlineData("luxe", "a garment pack")]
    [InlineData("darling", "a garment pack")]
    [InlineData("almond", "a Neolithe size")]
    [InlineData("coconut", "a Rosaline chest size")]
    [InlineData("yiggle", "a YAS physics variant")]
    [InlineData("squish", "a NIRAE physics variant")]
    public void KnownNonBodies_AreNotAliases(string alias, string reason)
    {
        var aliases = Bundled().Bodies.SelectMany(b => b.Aliases).ToHashSet(StringComparer.Ordinal);

        Assert.False(aliases.Contains(alias), $"'{alias}' is {reason}, not a body type.");
    }
}
