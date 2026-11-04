namespace PenumbraOrganizer.Plugin.Tests.Organizer.BodyTypes;

using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

public class BodyTypeListCodecTests
{
    /// <summary>
    /// Parses and asserts the parse SUCCEEDED before handing back the document.
    /// </summary>
    /// <remarks>
    /// Every test below that reads <c>Data</c> goes through this. Dereferencing <c>Data!</c>
    /// directly is what let a test with malformed JSON in it look like a test of something else:
    /// "Body\Nested" written as a C# single backslash produced the invalid JSON escape \N, Parse
    /// correctly answered MalformedJson with a null Data, and the assertion threw a
    /// NullReferenceException instead of reporting what was actually wrong. Asserting the status
    /// first turns that whole class of mistake into a legible failure.
    /// </remarks>
    private static BodyTypeListDocument ParseOk(string json)
    {
        var result = BodyTypeListCodec.Parse(json);

        Assert.Equal(BodyTypeListParseStatus.Ok, result.Status);
        return result.Data!;
    }

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
        var document = ParseOk("""
            {"Version":1,"Bodies":[{"Label":"Ghost","Aliases":["   ",""]}]}
            """);

        Assert.Empty(document.Bodies);
    }

    [Fact]
    public void Parse_DropsEntryWithNoLabel()
    {
        var document = ParseOk("""
            {"Version":1,"Bodies":[{"Label":"","Aliases":["x"]}]}
            """);

        Assert.Empty(document.Bodies);
    }

    [Theory]
    [InlineData("Body/Nested")]          // '/' needs no JSON escaping
    [InlineData(@"Body\\Nested")]        // JSON for ONE backslash; a lone \N is not a valid escape
    public void Parse_DropsLabelCarryingAPathSeparator(string jsonEscapedLabel)
    {
        // A label with a separator would otherwise reach GetFolder and add a level below the body
        // folder; IsBodyTypeLabel is the second guard behind this one.
        //
        // The parameter is the label AS WRITTEN IN JSON, not the label itself - the backslash case
        // has to arrive here already escaped or the document is malformed rather than merely
        // carrying a bad label, which is a different test entirely (Parse_Garbage covers that).
        var document = ParseOk($$"""
            {"Version":1,"Bodies":[{"Label":"{{jsonEscapedLabel}}","Aliases":["x"]}]}
            """);

        Assert.Empty(document.Bodies);
    }

    [Fact]
    public void Parse_DropsOverLongLabel()
    {
        var document = ParseOk($$"""
            {"Version":1,"Bodies":[{"Label":"{{new string('x', 41)}}","Aliases":["x"]}]}
            """);

        Assert.Empty(document.Bodies);
    }

    [Fact]
    public void Parse_DeDuplicatesAliasesWithinAnEntry()
    {
        var document = ParseOk("""
            {"Version":1,"Bodies":[{"Label":"Rue+","Aliases":["rue","RUE","Rue"]}]}
            """);

        Assert.Equal(["rue"], document.Bodies.Single().Aliases);
    }

    [Fact]
    public void Parse_KeepsDuplicateAliasAcrossEntries()
    {
        // Deliberately NOT repaired: silently dropping one would hide an authoring mistake that
        // BodyTypeListStaticContentTests is there to fail on.
        var document = ParseOk("""
            {"Version":1,"Bodies":[
              {"Label":"Alpha","Aliases":["shared"]},
              {"Label":"Beta","Aliases":["shared"]}]}
            """);

        Assert.Equal(2, document.Bodies.Count);
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
