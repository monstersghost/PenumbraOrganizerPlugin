using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizationJsonCodecTests
{
    private const string WellFormed = """
        {
          "Version": 1,
          "Folders": {
            "Plain/Empty": {},
            "Colored": { "ExpandedColor": 4294901760, "SortMode": "FoldersFirst" }
          },
          "Separators": {
            "MySep": { "Folder": false, "Color": null, "CreationDate": 638123456789 }
          }
        }
        """;

    [Fact]
    public void Parse_WellFormed_ReturnsOkWithData()
    {
        var result = OrganizationJsonCodec.Parse(WellFormed);

        Assert.Equal(OrganizationJsonParseStatus.Ok, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Folders.Count);
        Assert.Single(result.Data.Separators);
        Assert.Equal(4294901760u, result.Data.Folders["Colored"].ExpandedColor);
        Assert.Equal("FoldersFirst", result.Data.Folders["Colored"].SortMode);
        Assert.Null(result.Data.Folders["Plain/Empty"].ExpandedColor);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsMalformedWithNullData()
    {
        var result = OrganizationJsonCodec.Parse("{ not valid json !");

        Assert.Equal(OrganizationJsonParseStatus.MalformedJson, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_VersionTwo_ReturnsUnsupportedVersionWithNullData()
    {
        var result = OrganizationJsonCodec.Parse("""{ "Version": 2, "Folders": {}, "Separators": {} }""");

        Assert.Equal(OrganizationJsonParseStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_MissingVersion_ReturnsUnsupportedVersion()
    {
        // Version defaults to 0 when absent — fail closed, same as any non-1 value.
        var result = OrganizationJsonCodec.Parse("""{ "Folders": {}, "Separators": {} }""");

        Assert.Equal(OrganizationJsonParseStatus.UnsupportedVersion, result.Status);
    }

    [Fact]
    public void Parse_NullJson_ReturnsMalformedWithNullData()
    {
        // The production assembly doesn't have <Nullable>enable</Nullable>, so a caller can
        // pass null at runtime despite the string (non-nullable) signature. Parse must not throw.
        var result = OrganizationJsonCodec.Parse(null!);

        Assert.Equal(OrganizationJsonParseStatus.MalformedJson, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void UnknownFields_SurviveParseSerializeRoundTrip()
    {
        const string withUnknowns = """
            {
              "Version": 1,
              "Folders": { "F": { "FutureFlag": true } },
              "Separators": {},
              "FutureTopLevel": "kept"
            }
            """;

        var parsed = OrganizationJsonCodec.Parse(withUnknowns);
        Assert.Equal(OrganizationJsonParseStatus.Ok, parsed.Status);

        var reserialized = OrganizationJsonCodec.Serialize(parsed.Data!);

        Assert.Contains("FutureFlag", reserialized);
        Assert.Contains("FutureTopLevel", reserialized);
    }

    [Fact]
    public void Serialize_OmitsNullProperties()
    {
        var parsed = OrganizationJsonCodec.Parse(WellFormed);

        var reserialized = OrganizationJsonCodec.Serialize(parsed.Data!);

        // "Plain/Empty" has every field null — none of the known field names may appear for it.
        // Cheap proxy: CollapsedColor is null on every entry in the fixture, so it must not
        // appear anywhere in the output.
        Assert.DoesNotContain("CollapsedColor", reserialized);
    }

    [Fact]
    public void Serialize_ThenParse_RoundTripsFolderData()
    {
        var parsed = OrganizationJsonCodec.Parse(WellFormed);

        var roundTripped = OrganizationJsonCodec.Parse(OrganizationJsonCodec.Serialize(parsed.Data!));

        Assert.Equal(OrganizationJsonParseStatus.Ok, roundTripped.Status);
        Assert.Equal(4294901760u, roundTripped.Data!.Folders["Colored"].ExpandedColor);
        Assert.Equal(638123456789L, roundTripped.Data.Separators["MySep"].CreationDate);
    }
}
