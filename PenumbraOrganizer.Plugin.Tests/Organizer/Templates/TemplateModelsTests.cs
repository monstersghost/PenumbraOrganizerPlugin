namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using System.Text.Json;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateModelsTests
{
    // The seven names are a wire contract: a template naming one of these must keep working.
    [Theory]
    [InlineData("Creator")]
    [InlineData("ModType")]
    [InlineData("ModTypeDetailed")]
    [InlineData("TypeThenCreator")]
    [InlineData("TypeThenCreatorFlat")]
    [InlineData("CreatorThenType")]
    [InlineData("CreatorThenTypeFlat")]
    public void FallbackStrategy_SpecNames_Parse(string name)
    {
        Assert.True(Enum.TryParse<TemplateFallbackStrategy>(name, ignoreCase: false, out _));
    }

    [Fact]
    public void FallbackStrategy_HasExactlySevenValues()
    {
        Assert.Equal(7, Enum.GetValues<TemplateFallbackStrategy>().Length);
    }

    // Entries dominate payload size, so they serialize as "n"/"f", not as property names.
    [Fact]
    public void TemplateEntry_SerializesWithShortFieldNames()
    {
        var json = JsonSerializer.Serialize(
            new TemplateEntry("bibo+ medieval", "Gear/Top"), TemplateJson.SerializerOptions);

        Assert.Equal("{\"n\":\"bibo+ medieval\",\"f\":\"Gear/Top\"}", json);
    }

    // The default encoder escapes '+' as + and every non-ASCII char as \uXXXX -- six bytes
    // where one belongs. Mod names are full of both ("Bibo+", "Café"), and payload size decides
    // whether a share code fits in a chat message, so the relaxed encoder is load-bearing rather
    // than cosmetic.
    [Fact]
    public void SerializerOptions_DoNotEscapePlusOrNonAscii()
    {
        var json = JsonSerializer.Serialize(
            new TemplateEntry("café+", "Gear"), TemplateJson.SerializerOptions);

        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void OrganizationTemplate_RoundTripsThroughJson()
    {
        var template = new OrganizationTemplate
        {
            FormatVersion = 1,
            Name = "Detailed type sort",
            Author = "Akako",
            Description = "Character mods up front.",
            FallbackStrategy = "TypeThenCreator",
            FolderLabels = new Dictionary<string, string> { ["Others"] = "_Unsorted" },
            Folders = ["Characters", "Gear/Top"],
            Entries = [new TemplateEntry("bibo+ medieval", "Gear/Top")],
        };

        var round = JsonSerializer.Deserialize<OrganizationTemplate>(JsonSerializer.Serialize(template))!;

        Assert.Equal(1, round.FormatVersion);
        Assert.Equal("Detailed type sort", round.Name);
        Assert.Equal("TypeThenCreator", round.FallbackStrategy);
        Assert.Equal("_Unsorted", round.FolderLabels["Others"]);
        Assert.Equal(["Characters", "Gear/Top"], round.Folders);
        Assert.Equal("bibo+ medieval", round.Entries[0].N);
    }

    // Provenance is informational only and must never be required to import.
    [Fact]
    public void OrganizationTemplate_MissingProvenance_DeserializesWithNulls()
    {
        var json = """{"formatVersion":1,"name":"x","fallbackStrategy":"ModType"}""";

        var template = JsonSerializer.Deserialize<OrganizationTemplate>(json)!;

        Assert.Null(template.CreatedWithVersion);
        Assert.Null(template.CreatedAtUtc);
        Assert.Empty(template.Entries);
        Assert.Empty(template.Folders);
        Assert.Empty(template.FolderLabels);
    }

    [Fact]
    public void Limits_MatchSpec()
    {
        Assert.Equal(1_048_576, TemplateLimits.MaxCompressedBytes);
        Assert.Equal(8_388_608, TemplateLimits.MaxDecompressedBytes);
        Assert.Equal(20_000, TemplateLimits.MaxEntries);
        Assert.Equal(5_000, TemplateLimits.MaxFolders);
        Assert.Equal(500, TemplateLimits.MaxFolderLabels);
        Assert.Equal(512, TemplateLimits.MaxStringLength);
        Assert.Equal(16, TemplateLimits.MaxPathDepth);
        Assert.Equal(128, TemplateLimits.MaxSegmentLength);
    }
}
