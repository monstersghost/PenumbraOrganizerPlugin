namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using System.Text.Json;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateModelsTests
{
    // These four names are a wire contract: a template naming one of them must keep working, so
    // renaming a SortStrategy member is a breaking format change and has to be treated as one.
    [Theory]
    [InlineData("CreatorOnly")]
    [InlineData("TypeOnly")]
    [InlineData("TypeThenCreator")]
    [InlineData("CreatorThenType")]
    public void FallbackStrategy_SpecNames_Parse(string name)
    {
        Assert.True(Enum.TryParse<SortStrategy>(name, ignoreCase: false, out _));
    }

    // Guards the wire contract in the other direction: a new member is a new value a template can
    // name, so adding one must be a deliberate act rather than a silent side effect of UI work.
    [Fact]
    public void FallbackStrategy_HasExactlyFourValues()
    {
        Assert.Equal(4, Enum.GetValues<SortStrategy>().Length);
    }

    // A hand-written template that omits the split flags gets the Sort tab's own defaults, which
    // SortPanel sets to gear off / NPC on. Pinned because the NPC default is the non-obvious one:
    // it is true to match the unconditional subdivision that predates the split checkboxes.
    [Fact]
    public void OrganizationTemplate_AbsentSplitFlags_UseSortTabDefaults()
    {
        var document = JsonSerializer.Deserialize<OrganizationTemplate>(
            """{"formatVersion":1,"name":"x","fallbackStrategy":"TypeOnly"}""")!;

        Assert.False(document.FallbackSplitGear);
        Assert.True(document.FallbackSplitNpc);
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
            FallbackSplitGear = true,
            FallbackSplitNpc = false,
            FolderLabels = new Dictionary<string, string> { ["Others"] = "_Unsorted" },
            Folders = ["Characters", "Gear/Top"],
            Entries = [new TemplateEntry("bibo+ medieval", "Gear/Top")],
        };

        var round = JsonSerializer.Deserialize<OrganizationTemplate>(JsonSerializer.Serialize(template))!;

        Assert.Equal(1, round.FormatVersion);
        Assert.Equal("Detailed type sort", round.Name);
        Assert.Equal("TypeThenCreator", round.FallbackStrategy);
        // Explicitly non-default on both flags, so a dropped field cannot pass by luck.
        Assert.True(round.FallbackSplitGear);
        Assert.False(round.FallbackSplitNpc);
        Assert.Equal("_Unsorted", round.FolderLabels["Others"]);
        Assert.Equal(["Characters", "Gear/Top"], round.Folders);
        Assert.Equal("bibo+ medieval", round.Entries[0].N);
    }

    // Provenance is informational only and must never be required to import.
    [Fact]
    public void OrganizationTemplate_MissingProvenance_DeserializesWithNulls()
    {
        var json = """{"formatVersion":1,"name":"x","fallbackStrategy":"TypeOnly"}""";

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
