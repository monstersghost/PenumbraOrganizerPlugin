using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class WeaponJobsTests
{
    internal sealed class FakeLookup(Dictionary<string, string[]> items) : IItemJobLookup
    {
        public List<string> Asked { get; } = [];

        public IReadOnlySet<string>? JobsFor(string itemName)
        {
            Asked.Add(itemName);
            return items.TryGetValue(itemName, out var jobs) ? new HashSet<string>(jobs) : null;
        }
    }

    [Theory]
    [InlineData("GLA,PLD", "PLD")]
    [InlineData("PGL,MNK", "MNK")]
    [InlineData("MRD,WAR", "WAR")]
    [InlineData("LNC,DRG", "DRG")]
    [InlineData("ARC,BRD", "BRD")]
    [InlineData("CNJ,WHM", "WHM")]
    [InlineData("THM,BLM", "BLM")]
    [InlineData("ROG,NIN", "NIN")]
    [InlineData("ACN,SMN,SCH", "SCH,SMN")]
    [InlineData("SAM", "SAM")]
    [InlineData("CRP", "CRP")]
    [InlineData("ADV", "")]
    public void Fold_PutsEachClassUnderItsJob(string abbreviations, string expected) =>
        Assert.Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries),
            WeaponJobs.Fold(abbreviations.Split(',')).ToArray());

    [Fact]
    public void Resolve_OneJob_IsThatJob()
    {
        var lookup = new FakeLookup(new() { ["Kotetsu"] = ["SAM"], ["Kotetsu Replica"] = ["SAM"] });
        Assert.Equal("SAM", WeaponJobs.Resolve(["Kotetsu", "Kotetsu Replica"], lookup));
    }

    [Fact]
    public void Resolve_AClassWeapon_FilesUnderItsJob() =>
        Assert.Equal("PLD", WeaponJobs.Resolve(["Bronze Spatha"], new FakeLookup(new() { ["Bronze Spatha"] = ["GLA", "PLD"] })));

    [Fact]
    public void Resolve_AnArcanistBook_IsMulticlass() =>
        Assert.Equal(WeaponJobs.Multiclass,
            WeaponJobs.Resolve(["Grimoire"], new FakeLookup(new() { ["Grimoire"] = ["ACN", "SMN", "SCH"] })));

    [Fact]
    public void Resolve_ItemsThatDisagree_AreMulticlass()
    {
        var lookup = new FakeLookup(new() { ["Katana"] = ["SAM"], ["Daggers"] = ["ROG", "NIN"] });
        Assert.Equal(WeaponJobs.Multiclass, WeaponJobs.Resolve(["Katana", "Daggers"], lookup));
    }

    [Fact]
    public void Resolve_NoMatch_IsNoJob_AndUnmatchedNamesAreIgnored()
    {
        var lookup = new FakeLookup(new() { ["Katana"] = ["SAM"] });
        Assert.Null(WeaponJobs.Resolve(["Something Else"], lookup));
        Assert.Null(WeaponJobs.Resolve([], lookup));
        Assert.Equal("SAM", WeaponJobs.Resolve(["Something Else", "Katana"], lookup));
    }

    [Fact]
    public void Resolve_AnItemOnlyAdventurersShare_IsNoJob() =>
        Assert.Null(WeaponJobs.Resolve(["Toy"], new FakeLookup(new() { ["Toy"] = ["ADV"] })));

    [Fact]
    public void Resolve_TriesTheNameWithoutATrailingSuffix_OnlyWhenThePlainNameMisses()
    {
        var lookup = new FakeLookup(new() { ["Kotetsu"] = ["SAM"] });
        Assert.Equal("SAM", WeaponJobs.Resolve(["Kotetsu (Offhand)"], lookup));
        Assert.Equal(["Kotetsu (Offhand)", "Kotetsu"], lookup.Asked);

        var plain = new FakeLookup(new() { ["Kotetsu"] = ["SAM"] });
        WeaponJobs.Resolve(["Kotetsu"], plain);
        Assert.Equal(["Kotetsu"], plain.Asked);
    }
}
