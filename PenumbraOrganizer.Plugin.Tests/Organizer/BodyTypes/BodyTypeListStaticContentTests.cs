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
        // The real contract: every shipped label must be accepted by ModTypeFolders.GetFolder and
        // produce exactly one level under Body. A label GetFolder rejects would throw mid-sort.
        Assert.All(Bundled().Bodies, b =>
        {
            var folder = ModTypeFolders.GetFolder(ModCategory.Body, b.Label);
            Assert.Equal($"Body/{b.Label}", folder);
            Assert.Equal(2, folder.Split('/').Length);
        });
    }

    [Fact]
    public void EverySex_IsFemaleOrMale()
    {
        // Metadata only - nothing builds a folder from it - but a typo here would still be wrong
        // in a file people read.
        Assert.All(Bundled().Bodies, b => Assert.Contains(b.Sex, (string[])["Female", "Male"]));
    }

    [Fact]
    public void EverySource_IsVerifiedOrReported() =>
        Assert.All(Bundled().Bodies, b => Assert.Contains(b.Source, (string[])["verified", "reported"]));

    [Fact]
    public void TheMaleBodies_AreTheThreeKeptSeparate()
    {
        // A deliberate decision, recorded here because it is easy to "tidy" TBSE-X and HRBody into
        // TBSE later: they are distinct bodies, not TBSE variants, and Proteus models them that way.
        var male = Bundled().Bodies.Where(b => b.Sex == "Male").Select(b => b.Label).OrderBy(l => l).ToList();

        Assert.Equal(["HRBody", "TBSE", "TBSE-X"], male);
    }

    [Theory]
    // The names that are NOT bodies, each for a confirmed reason. Re-adding any of them is a
    // regression: a size, a nail option, a shader generation or a garment pack would start
    // claiming a Body subfolder.
    [InlineData("uranus", "a chest size inside the Luxe garment pack")]
    [InlineData("stabbies", "a hand/nail option")]
    [InlineData("gen2", "a vanilla skin shader generation")]
    [InlineData("gen3", "a vanilla skin shader generation")]
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
