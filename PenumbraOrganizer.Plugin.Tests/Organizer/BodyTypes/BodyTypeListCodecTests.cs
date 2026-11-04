namespace PenumbraOrganizer.Plugin.Tests.Organizer.BodyTypes;

using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

public class BodyTypeListCodecTests
{
    [Fact]
    public void Parse_Null_IsMalformedNotAThrow() =>
        Assert.Equal(BodyTypeListParseStatus.MalformedJson, BodyTypeListCodec.Parse(null!).Status);

    [Fact]
    public void Parse_Garbage_IsMalformed() =>
        Assert.Equal(BodyTypeListParseStatus.MalformedJson, BodyTypeListCodec.Parse("{ nope").Status);

    [Fact]
    public void Parse_WrongVersion_IsUnsupported()
    {
        var result = BodyTypeListCodec.Parse("""{"Version":99,"Bodies":[]}""");

        Assert.Equal(BodyTypeListParseStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_LowercasesAndTrimsAliases()
    {
        var result = BodyTypeListCodec.Parse("""
            {"Version":1,"Bodies":[{"Label":"Bibo+","Aliases":["  BIBO+  ","Bibo"]}]}
            """);

        Assert.Equal(BodyTypeListParseStatus.Ok, result.Status);
        Assert.Equal(["bibo+", "bibo"], result.Data!.Bodies.Single().Aliases);
    }

    [Fact]
    public void Parse_DropsEntryWithNoUsableAlias()
    {
        var result = BodyTypeListCodec.Parse("""
            {"Version":1,"Bodies":[{"Label":"Ghost","Aliases":["   ",""]}]}
            """);

        Assert.Empty(result.Data!.Bodies);
    }

    [Fact]
    public void Parse_DropsEntryWithNoLabel()
    {
        var result = BodyTypeListCodec.Parse("""
            {"Version":1,"Bodies":[{"Label":"","Aliases":["x"]}]}
            """);

        Assert.Empty(result.Data!.Bodies);
    }

    [Theory]
    [InlineData("Body/Nested")]
    [InlineData("Body\\Nested")]
    public void Parse_DropsLabelCarryingAPathSeparator(string label)
    {
        // The flat-folder guarantee. Such a label would otherwise reach GetFolder and nest a
        // folder under Body; IsBodyTypeLabel is the second guard behind this one.
        var result = BodyTypeListCodec.Parse($$"""
            {"Version":1,"Bodies":[{"Label":"{{label}}","Aliases":["x"]}]}
            """);

        Assert.Empty(result.Data!.Bodies);
    }

    [Fact]
    public void Parse_DropsOverLongLabel()
    {
        var result = BodyTypeListCodec.Parse($$"""
            {"Version":1,"Bodies":[{"Label":"{{new string('x', 41)}}","Aliases":["x"]}]}
            """);

        Assert.Empty(result.Data!.Bodies);
    }

    [Fact]
    public void Parse_DeDuplicatesAliasesWithinAnEntry()
    {
        var result = BodyTypeListCodec.Parse("""
            {"Version":1,"Bodies":[{"Label":"Rue+","Aliases":["rue","RUE","Rue"]}]}
            """);

        Assert.Equal(["rue"], result.Data!.Bodies.Single().Aliases);
    }

    [Fact]
    public void Parse_KeepsDuplicateAliasAcrossEntries()
    {
        // Deliberately NOT repaired: silently dropping one would hide an authoring mistake that
        // BodyTypeListStaticContentTests is there to fail on.
        var result = BodyTypeListCodec.Parse("""
            {"Version":1,"Bodies":[
              {"Label":"Alpha","Aliases":["shared"]},
              {"Label":"Beta","Aliases":["shared"]}]}
            """);

        Assert.Equal(2, result.Data!.Bodies.Count);
    }

    [Fact]
    public void Parse_MissingBodiesArray_IsEmptyNotAThrow()
    {
        var result = BodyTypeListCodec.Parse("""{"Version":1}""");

        Assert.Equal(BodyTypeListParseStatus.Ok, result.Status);
        Assert.Empty(result.Data!.Bodies);
    }

    [Fact]
    public void Parse_UnknownFields_AreIgnored()
    {
        // The shipped file carries commentary fields; a reader must not choke on them.
        var result = BodyTypeListCodec.Parse("""
            {"Version":1,"_comment":"hi","Bodies":[
              {"Label":"Eve","Sex":"Female","Source":"verified","Aliases":["eve"],"_note":"x"}]}
            """);

        Assert.Equal(BodyTypeListParseStatus.Ok, result.Status);
        Assert.Equal("Eve", result.Data!.Bodies.Single().Label);
    }

    [Fact]
    public void SerializeThenParse_RoundTrips()
    {
        var original = BodyTypeListCodec.Parse(BodyTypeListStore.ReadEmbeddedStaticList()).Data!;

        var again = BodyTypeListCodec.Parse(BodyTypeListCodec.Serialize(original));

        Assert.Equal(BodyTypeListParseStatus.Ok, again.Status);
        Assert.Equal(original.Bodies.Select(b => b.Label), again.Data!.Bodies.Select(b => b.Label));
        Assert.Equal(original.Bodies.SelectMany(b => b.Aliases), again.Data!.Bodies.SelectMany(b => b.Aliases));
    }
}
