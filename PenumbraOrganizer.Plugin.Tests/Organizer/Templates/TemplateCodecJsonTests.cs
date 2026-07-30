namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateCodecJsonTests
{
    private const string ValidJson = """
    {
      "formatVersion": 1,
      "name": "Detailed type sort",
      "author": "Akako",
      "description": "Character mods up front.",
      "fallbackStrategy": "TypeThenCreator",
      "folderLabels": { "Others": "_Unsorted" },
      "folders": ["Characters", "Gear/Top"],
      "entries": [ { "n": "bibo+ medieval", "f": "Gear/Top" } ]
    }
    """;

    [Fact]
    public void DecodeJson_ValidDocument_Succeeds()
    {
        var result = TemplateCodec.DecodeJson(ValidJson);

        Assert.True(result.Succeeded);
        Assert.Equal("Detailed type sort", result.Template!.Name);
        Assert.Equal(TemplateFallbackStrategy.TypeThenCreator, result.Template.FallbackStrategy);
        Assert.Equal("Gear/Top", result.Template.EntriesByNormalizedName["bibo+ medieval"]);
        Assert.Equal(["Characters", "Gear/Top"], result.Template.Folders);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var template = new OrganizationTemplate
        {
            FormatVersion = 1,
            Name = "Round trip",
            FallbackStrategy = "ModTypeDetailed",
            Folders = ["Gear/Head"],
            Entries = [new TemplateEntry("some mod", "Gear/Head")],
        };

        var result = TemplateCodec.DecodeJson(TemplateCodec.EncodeJson(template));

        Assert.True(result.Succeeded);
        Assert.Equal("Round trip", result.Template!.Name);
        Assert.Equal(TemplateFallbackStrategy.ModTypeDetailed, result.Template.FallbackStrategy);
        Assert.Equal("Gear/Head", result.Template.EntriesByNormalizedName["some mod"]);
    }

    [Fact]
    public void DecodeJson_MalformedJson_FailsWithMalformedJson()
    {
        var result = TemplateCodec.DecodeJson("{ not json");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.MalformedJson, result.Error);
    }

    // A future template must never be half-read by an older plugin.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void DecodeJson_UnsupportedFormatVersion_Fails(int version)
    {
        var result = TemplateCodec.DecodeJson(
            $$"""{"formatVersion":{{version}},"name":"x","fallbackStrategy":"ModType"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.UnsupportedFormatVersion, result.Error);
        Assert.Contains(version.ToString(), result.ErrorDetail);
    }

    [Fact]
    public void DecodeJson_UnknownFallbackStrategy_Fails()
    {
        var result = TemplateCodec.DecodeJson(
            """{"formatVersion":1,"name":"x","fallbackStrategy":"ByVibes"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.UnknownFallbackStrategy, result.Error);
        Assert.Contains("ByVibes", result.ErrorDetail);
    }

    [Fact]
    public void DecodeJson_MissingName_Fails()
    {
        var result = TemplateCodec.DecodeJson(
            """{"formatVersion":1,"name":"","fallbackStrategy":"ModType"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.MissingName, result.Error);
    }

    [Fact]
    public void DecodeJson_TooManyEntries_FailsWithLimitExceeded()
    {
        var entries = string.Join(',',
            Enumerable.Range(0, TemplateLimits.MaxEntries + 1).Select(i => $$"""{"n":"m{{i}}","f":"Gear"}"""));
        var result = TemplateCodec.DecodeJson(
            $$"""{"formatVersion":1,"name":"x","fallbackStrategy":"ModType","entries":[{{entries}}]}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.LimitExceeded, result.Error);
    }

    [Fact]
    public void DecodeJson_InvalidFolderInFoldersList_IsSkippedWithWarning()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType","folders":["Gear","Gear//Bad"]}
        """);

        Assert.True(result.Succeeded);
        Assert.Equal(["Gear"], result.Template!.Folders);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.InvalidEntryPath, "Gear//Bad"),
            result.Warnings);
    }

    [Fact]
    public void DecodeJson_InvalidFolderLabelKey_IsDroppedWithWarning()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType","folderLabels":{"Gear//Bad":"Equipment"}}
        """);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Template!.FolderLabels);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.UnknownFolderLabelKey, "Gear//Bad"),
            result.Warnings);
    }

    // A malformed replacement VALUE would inject a broken path into every fallback proposal,
    // so unlike a bad key it is fatal rather than skippable.
    [Fact]
    public void DecodeJson_InvalidFolderLabelValue_Fails()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType","folderLabels":{"Gear":"/Equipment"}}
        """);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.InvalidFolderLabelValue, result.Error);
    }

    [Fact]
    public void DecodeJson_ConflictingDuplicateEntries_DropsGroupAndWarns()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
         "entries":[{"n":"dup","f":"Gear"},{"n":"dup","f":"Hair"}]}
        """);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Template!.EntriesByNormalizedName);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, "dup"),
            result.Warnings);
    }

    [Fact]
    public void DecodeJson_UnnormalizedEntryKeys_AreRenormalized()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
         "entries":[{"n":"Bibo+ Medieval (Penumbra)_1_1_0","f":"Gear/Top"}]}
        """);

        Assert.True(result.Succeeded);
        Assert.Equal("Gear/Top", result.Template!.EntriesByNormalizedName["bibo+ medieval"]);
    }
}
