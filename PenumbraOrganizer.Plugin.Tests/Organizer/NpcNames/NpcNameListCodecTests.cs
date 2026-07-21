using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcNameListCodecTests
{
    private const string ValidJson = """
        {"Version":1,"NPCs":["Y'shtola","Thancred"],"Enemies":["Titania"],"Bosses":["Zenos"],"Excluded":[]}
        """;

    [Fact]
    public void Parse_ValidDocument_ReturnsOk()
    {
        var result = NpcNameListCodec.Parse(ValidJson);

        Assert.Equal(NpcNameListParseStatus.Ok, result.Status);
        Assert.NotNull(result.Data);
        Assert.Contains("Y'shtola", result.Data!.NPCs);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsMalformedJson()
    {
        var result = NpcNameListCodec.Parse("{ not json");

        Assert.Equal(NpcNameListParseStatus.MalformedJson, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_UnsupportedVersion_ReturnsUnsupportedVersion()
    {
        var result = NpcNameListCodec.Parse("""{"Version":99,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":[]}""");

        Assert.Equal(NpcNameListParseStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_TrimsBlankAndOverLongEntries()
    {
        var overLong = new string('a', 200);
        var json = $$"""{"Version":1,"NPCs":["  Y'shtola  ","","{{overLong}}"],"Enemies":[],"Bosses":[],"Excluded":[]}""";

        var result = NpcNameListCodec.Parse(json);

        Assert.Equal(["Y'shtola"], result.Data!.NPCs);
    }

    [Fact]
    public void Parse_DeduplicatesCaseInsensitivelyWithinAnArray()
    {
        var json = """{"Version":1,"NPCs":["Zenos","ZENOS","zenos"],"Enemies":[],"Bosses":[],"Excluded":[]}""";

        var result = NpcNameListCodec.Parse(json);

        Assert.Single(result.Data!.NPCs);
    }

    [Fact]
    public void Parse_AllowsSameNameAcrossDifferentArrays()
    {
        var json = """{"Version":1,"NPCs":[],"Enemies":["Titania"],"Bosses":["Titania"],"Excluded":[]}""";

        var result = NpcNameListCodec.Parse(json);

        Assert.Contains("Titania", result.Data!.Enemies);
        Assert.Contains("Titania", result.Data!.Bosses);
    }

    [Fact]
    public void Serialize_IsDeterministic_RepeatedCallsProduceIdenticalOutput()
    {
        var doc = NpcNameListCodec.Parse(ValidJson).Data!;

        var first = NpcNameListCodec.Serialize(doc);
        var second = NpcNameListCodec.Serialize(doc);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Serialize_SortsEntriesDeterministically()
    {
        var doc = NpcNameListCodec.Parse(
            """{"Version":1,"NPCs":["Thancred","Alphinaud","Y'shtola"],"Enemies":[],"Bosses":[],"Excluded":[]}""").Data!;

        var serialized = NpcNameListCodec.Serialize(doc);
        var reparsed = NpcNameListCodec.Parse(serialized).Data!;

        Assert.Equal(["Alphinaud", "Thancred", "Y'shtola"], reparsed.NPCs);
    }

    [Fact]
    public void MergeAdditive_UnionsNewNamesIntoEachCategory()
    {
        var existing = NpcNameListCodec.Parse(ValidJson).Data!;

        var merged = NpcNameListCodec.MergeAdditive(
            existing, newNpcs: ["Alphinaud"], newEnemies: ["Garuda"], newBosses: []);

        Assert.Contains("Alphinaud", merged.NPCs);
        Assert.Contains("Y'shtola", merged.NPCs); // nothing already present is removed
        Assert.Contains("Garuda", merged.Enemies);
    }

    [Fact]
    public void MergeAdditive_NeverRemovesExistingNames()
    {
        var existing = NpcNameListCodec.Parse(ValidJson).Data!;

        var merged = NpcNameListCodec.MergeAdditive(existing, newNpcs: [], newEnemies: [], newBosses: []);

        Assert.Contains("Y'shtola", merged.NPCs);
        Assert.Contains("Thancred", merged.NPCs);
        Assert.Contains("Titania", merged.Enemies);
        Assert.Contains("Zenos", merged.Bosses);
    }

    [Fact]
    public void MergeAdditive_PreservesExcludedList()
    {
        var existing = NpcNameListCodec.Parse(
            """{"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":["Bad Entry"]}""").Data!;

        var merged = NpcNameListCodec.MergeAdditive(existing, newNpcs: [], newEnemies: [], newBosses: []);

        Assert.Contains("Bad Entry", merged.Excluded);
    }
}
