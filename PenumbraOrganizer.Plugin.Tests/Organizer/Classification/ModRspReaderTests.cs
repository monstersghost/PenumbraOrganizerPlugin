using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModRspReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("race-rsp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ModContent? Read(string defaultModJson)
    {
        File.WriteAllText(Path.Combine(_dir, "default_mod.json"), defaultModJson);
        return ModContentReader.Read(new DirectoryInfo(_dir));
    }

    private static string Rsp(string subRace, string attribute = "MaleMinSize") =>
        $"{{\"Type\":\"Rsp\",\"Manipulation\":{{\"SubRace\":\"{subRace}\",\"Attribute\":\"{attribute}\",\"Entry\":1.0}}}}";

    [Fact]
    public void PureMiqoteScalingPack_IsOneRacePureAndComplete()
    {
        var json = "{\"Files\":{},\"Manipulations\":["
            + Rsp("SeekerOfTheSun") + "," + Rsp("KeeperOfTheMoon", "FemaleMaxSize") + "]}";

        var rsp = Read(json)!.Rsp;

        Assert.NotNull(rsp);
        Assert.Equal(new[] { "Miqo'te" }, rsp!.Races);
        Assert.True(rsp.Complete);
        Assert.True(rsp.Pure);
    }

    [Fact]
    public void TwoRaces_AreBothReported()
    {
        var json = "{\"Files\":{},\"Manipulations\":[" + Rsp("Midlander") + "," + Rsp("Rava") + "]}";
        var rsp = Read(json)!.Rsp!;
        Assert.Equal(new[] { "Midlander", "Viera" }, rsp.Races.OrderBy(r => r, StringComparer.Ordinal));
        Assert.True(rsp.Pure);
    }

    [Fact]
    public void ModWithoutRsp_HasNullEvidence() =>
        Assert.Null(Read("{\"Files\":{},\"Manipulations\":[]}")!.Rsp);

    [Fact]
    public void AnOtherManipulation_MakesItImpure()
    {
        var other = "{\"Type\":\"Atch\",\"Manipulation\":{}}";
        var rsp = Read("{\"Files\":{},\"Manipulations\":[" + Rsp("Midlander") + "," + other + "]}")!.Rsp!;
        Assert.False(rsp.Pure);
    }

    [Fact]
    public void AGameFileRedirect_MakesItImpure()
    {
        var json = "{\"Files\":{\"chara/equipment/e0001/model/c0101e0001_top.mdl\":\"a.mdl\"},"
            + "\"Manipulations\":[" + Rsp("Midlander") + "]}";
        Assert.False(Read(json)!.Rsp!.Pure);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("7")]
    [InlineData("Seeker of the Sun")]
    public void AnUnrecognizedSubRace_MakesTheEvidenceIncomplete(string subRace)
    {
        var rsp = Read("{\"Files\":{},\"Manipulations\":[" + Rsp("Midlander") + "," + Rsp(subRace) + "]}")!.Rsp!;
        Assert.False(rsp.Complete);
    }

    [Fact]
    public void ASubRaceThatIsANumberInJson_MakesTheEvidenceIncomplete()
    {
        var numeric = "{\"Type\":\"Rsp\",\"Manipulation\":{\"SubRace\":7,\"Attribute\":\"MaleMinSize\",\"Entry\":1.0}}";
        Assert.False(Read("{\"Files\":{},\"Manipulations\":[" + numeric + "]}")!.Rsp!.Complete);
    }

    [Fact]
    public void AnUnreadableConfig_MakesTheWholeReadNull() =>
        Assert.Null(Read("{ this is not json"));

    // The real pack, captured from the author's library: Penumbra 1.7 "meta.json" shape (a DefaultData
    // object holding only Manipulations, no Files key at all), 28 Rsp entries for SeekerOfTheSun and
    // KeeperOfTheMoon. It must read as pure, complete and one race.
    [Fact]
    public void TheRealSamplePack_IsPureCompleteAndOneRace()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RaceSplit", "variant-miqotes-v2.meta.json");
        File.Copy(path, Path.Combine(_dir, "meta.json"));

        var rsp = ModContentReader.Read(new DirectoryInfo(_dir))!.Rsp!;

        Assert.Equal(new[] { "Miqo'te" }, rsp.Races);
        Assert.True(rsp.Complete);
        Assert.True(rsp.Pure);
    }

    [Fact]
    public void ADefaultDataWithNoFilesKey_ReadsLikeTheRealPack()
    {
        File.WriteAllText(Path.Combine(_dir, "meta.json"),
            "{\"FileVersion\":4,\"DefaultData\":{\"Manipulations\":[" + Rsp("SeekerOfTheSun") + "]}}");

        var rsp = ModContentReader.Read(new DirectoryInfo(_dir))!.Rsp!;

        Assert.Equal(new[] { "Miqo'te" }, rsp.Races);
        Assert.True(rsp.Pure);
    }

    // Shape seen in a real library mod (a physics mod with a "Viera Breast Fix" option group): the Rsp
    // entries sit inside an option of a group, not in DefaultData, and the mod also carries file entries.
    // The race is still read from them, but the mod is not pure racial scaling.
    [Fact]
    public void RspInsideAnOptionGroupBesideFiles_ReportsTheRaceButIsNotPure()
    {
        File.WriteAllText(Path.Combine(_dir, "meta.json"),
            "{\"FileVersion\":4,\"DefaultData\":{\"Files\":{\"chara/human/c1801/obj/body/b0001/model/c1801b0001_top.mdl\":\"a.mdl\"}},"
            + "\"Groups\":[{\"Type\":\"Multi\",\"Name\":\"Viera Breast Fix\",\"Options\":[{\"Name\":\"Enabled\",\"Manipulations\":["
            + Rsp("Rava") + "," + Rsp("Veena", "BustMinX") + "]}]}]}");

        var rsp = ModContentReader.Read(new DirectoryInfo(_dir))!.Rsp!;

        Assert.Equal(new[] { "Viera" }, rsp.Races);
        Assert.True(rsp.Complete);
        Assert.False(rsp.Pure);
    }

    // A real pack in the OLDER Penumbra folder layout (author's download, 2026-11-22): meta.json has no
    // DefaultData, default_mod.json is empty, and every Rsp entry sits inside an option of a
    // group_NNN_*.json file ("Taller Males" and "Shorter Females", options Veena and Rava). It must read as
    // pure, complete, and one race (Viera).
    [Fact]
    public void TheRealLegacyLayoutPack_IsPureCompleteAndOneRace()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RaceSplit", "viera-height-diversity");
        foreach (var file in Directory.GetFiles(fixtureDir, "*.json"))
            File.Copy(file, Path.Combine(_dir, Path.GetFileName(file)));

        var rsp = ModContentReader.Read(new DirectoryInfo(_dir))!.Rsp!;

        Assert.Equal(new[] { "Viera" }, rsp.Races);
        Assert.True(rsp.Complete);
        Assert.True(rsp.Pure);
    }
}
