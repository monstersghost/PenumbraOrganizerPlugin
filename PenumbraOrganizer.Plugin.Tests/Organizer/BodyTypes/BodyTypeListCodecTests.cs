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

    [Theory]
    // Everything IsValidLabel must reject, and why each matters. These all once passed the codec
    // and then threw out of ModTypeFolders.GetFolder partway through a sort.
    [InlineData("Yo*Body", "asterisk")]
    [InlineData("Body:X", "colon")]
    [InlineData("Bibo?", "question mark")]
    [InlineData("My|Body", "pipe")]
    [InlineData("✨Sparkle", "emoji - outside the BMP, so not a letter or digit")]
    [InlineData("Bibo Plus", "non-breaking space, the classic paste artifact")]
    [InlineData(".", "punctuation only")]
    [InlineData("..", "punctuation only, and would become its own path segment")]
    [InlineData("+", "punctuation only")]
    public void IsValidLabel_RejectsWhatGetFolderWouldReject(string label, string why) =>
        Assert.False(BodyTypeListCodec.IsValidLabel(label), why);

    [Theory]
    [InlineData("Bibo+")]
    [InlineData("Tight&Firm")]
    [InlineData("Re[de]fined Lalafell")]
    [InlineData("TB2.0 Type M")]
    [InlineData("NIRAE")]
    [InlineData("Kräuter")]
    [InlineData("YAB Buff")]
    public void IsValidLabel_AcceptsRealBodyNames(string label) =>
        Assert.True(BodyTypeListCodec.IsValidLabel(label));

    [Fact]
    public void Parse_UnusableLabel_IsDroppedAndReported()
    {
        // Dropping silently is what let the whole class of bug hide: the entry vanished and the
        // user had no way to know. DroppedLabels is what the store turns into a warning.
        var result = ParseRaw("{\"Version\":1,\"Bodies\":[{\"Label\":\"Yo*Body\",\"Aliases\":[\"yo\"]}]}");

        Assert.Equal(BodyTypeListParseStatus.Ok, result.Status);
        Assert.Empty(result.Data!.Bodies);
        Assert.Equal(["Yo*Body"], result.DroppedLabels);
    }

    [Fact]
    public void Parse_EntryWithNoLabelAtAll_IsReportedWithAPlaceholder()
    {
        var result = ParseRaw("{\"Version\":1,\"Bodies\":[{\"Label\":\"\",\"Aliases\":[\"x\"]}]}");

        Assert.Equal(["(entry with no label)"], result.DroppedLabels);
    }

    [Fact]
    public void Parse_EntryDroppedForHavingNoAlias_IsAlsoReported()
    {
        var result = ParseRaw("{\"Version\":1,\"Bodies\":[{\"Label\":\"Ghost\",\"Aliases\":[]}]}");

        Assert.Equal(["Ghost"], result.DroppedLabels);
    }

    [Fact]
    public void Parse_AllEntriesUsable_ReportsNothingDropped()
    {
        var result = ParseRaw("{\"Version\":1,\"Bodies\":[{\"Label\":\"Bibo+\",\"Aliases\":[\"bibo+\"]}]}");

        Assert.Empty(result.DroppedLabels);
    }

    private static BodyTypeListParseResult ParseRaw(string json) => BodyTypeListCodec.Parse(json);

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
