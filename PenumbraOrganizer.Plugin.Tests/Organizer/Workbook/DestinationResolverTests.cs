using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class DestinationResolverTests
{
    private static DestinationRules Rules(bool tokens = true, Dictionary<int, string>? custom = null) =>
        new(
            tokens,
            code => code switch
            {
                1 => "Gear",
                4 => "Hair",
                15 => "Animation and VFX",
                _ => custom is not null && custom.TryGetValue(code, out var folder) ? folder : null,
            },
            name => name.ToLowerInvariant() switch
            {
                "gear" => "Gear",
                "animation" => "Animation and VFX",
                _ => null,
            });

    private static readonly DestinationTokens Thor = new("Thor", "Hair", "Viera");

    private static DestinationResult R(string raw, DestinationRules? rules = null, DestinationTokens? tokens = null) =>
        DestinationResolver.Resolve(raw, rules ?? Rules(), tokens ?? Thor);

    [Fact]
    public void Blank_LeavesTheModAlone()
    {
        Assert.Equal(DestinationResult.Leave, R(""));
        Assert.Equal(DestinationResult.Leave, R("   "));
    }

    [Fact]
    public void ACode_ExpandsToItsFolder_AndReportsTheCodeUsed()
    {
        var result = R("1/Thor");
        Assert.Equal("Gear/Thor", result.Destination);
        Assert.Equal(1, result.CodeUsed);
    }

    [Fact]
    public void ALiteralFolder_ReportsNoCode()
    {
        Assert.Null(R("Mine/17").CodeUsed);
        Assert.Null(R("gear/x").CodeUsed);
    }

    [Fact]
    public void ACustomCode_ExpandsToItsFolderPath()
    {
        var rules = Rules(custom: new() { [17] = "Face/Sculpts" });
        Assert.Equal("Face/Sculpts/Thor", R("17/{creator}", rules).Destination);
    }

    [Fact]
    public void ACodeAlone_IsJustItsFolder() =>
        Assert.Equal("Animation and VFX", R("15").Destination);

    [Fact]
    public void AnUnknownCode_FailsInPlainWords()
    {
        var result = R("99/Thor");
        Assert.Null(result.Destination);
        Assert.Equal("code 99 is not in the Category Mapping sheet", result.Error);
    }

    [Theory]
    [InlineData("0/Thor", "0/Thor")]
    [InlineData("-16/Creator", "-16/Creator")]
    public void ZeroAndNegativeFirstSegments_StayLiteral(string raw, string expected) =>
        Assert.Equal(expected, R(raw).Destination);

    [Fact]
    public void ANumberInALaterSegment_IsAFolderName() =>
        Assert.Equal("Mine/17", R("Mine/17").Destination);

    [Theory]
    [InlineData("gear/Thor", "Gear/Thor")]
    [InlineData("GEAR/Thor", "Gear/Thor")]
    [InlineData("Animation/Emotes", "Animation and VFX/Emotes")]
    public void ABuiltInCategoryName_ResolvesToItsFolder(string raw, string expected) =>
        Assert.Equal(expected, R(raw).Destination);

    [Fact]
    public void AnythingElseInTheFirstSegment_IsALiteralFolder() =>
        Assert.Equal("Mine/Stuff", R("Mine/Stuff").Destination);

    [Fact]
    public void Tokens_ExpandAnywhereInThePath() =>
        Assert.Equal("Mine/Hair/Thor/test", R("Mine/{type}/{creator}/test").Destination);

    [Fact]
    public void ARaceToken_UsesTheSuppliedRaceFolder() =>
        Assert.Equal("Mine/Viera", R("Mine/{race}").Destination);

    [Fact]
    public void TokenNames_AreCaseInsensitive() =>
        Assert.Equal("Mine/Thor", R("Mine/{CREATOR}").Destination);

    [Fact]
    public void AnEmptyToken_CollapsesItsWholeSegment()
    {
        var noAuthor = new DestinationTokens("", "Hair", "");
        Assert.Equal("Mine/Hair/test", R("Mine/{type}/{creator}/test", tokens: noAuthor).Destination);
        Assert.Equal("Mine/test", R("Mine/{creator}{race}/test", tokens: noAuthor).Destination);
    }

    [Fact]
    public void APartlyEmptySegment_KeepsItsLiteralText()
    {
        var noAuthor = new DestinationTokens("", "Hair", "");
        Assert.Equal("Mine/By-", R("Mine/By-{creator}", tokens: noAuthor).Destination);
    }

    [Fact]
    public void WhenEveryLevelCollapses_TheModIsLeftAlone() =>
        Assert.Equal(DestinationResult.Leave, R("{creator}", tokens: DestinationTokens.None));

    [Fact]
    public void AnUnknownToken_FailsNamingIt() =>
        Assert.Equal("unknown token {foo}", R("Mine/{foo}").Error);

    [Fact]
    public void ALongUnknownToken_IsStillAnError() =>
        Assert.StartsWith("unknown token", R("Mine/{" + new string('a', 60) + "}").Error);

    [Fact]
    public void UnknownTokens_ListsOnlyTheBadNames()
    {
        Assert.Equal(["typo"], DestinationResolver.UnknownTokens("Mine/{type}/{typo}/{CREATOR}"));
        Assert.Empty(DestinationResolver.UnknownTokens("Mine/{race}"));
    }

    [Fact]
    public void TokenValues_AreNeverExpandedAgain()
    {
        var tricky = new DestinationTokens("{type}", "Hair", "");
        Assert.Equal("Mine/{type}", R("Mine/{creator}", tokens: tricky).Destination);
    }

    [Fact]
    public void WithTokensOff_BracesAreOrdinaryText() =>
        Assert.Equal("Mine/{creator}", R("Mine/{creator}", Rules(tokens: false)).Destination);

    [Theory]
    [InlineData("Mine//x")]
    [InlineData("../x")]
    [InlineData("Mine/./x")]
    [InlineData("Mine/..")]
    public void EmptyDotAndDotDotSegments_Fail(string raw)
    {
        var result = R(raw);
        Assert.Null(result.Destination);
        Assert.Equal("Destination cannot contain empty segments, . or ..", result.Error);
    }

    [Theory]
    [InlineData("C:/x")]
    [InlineData("C:\\Mods\\x")]
    [InlineData("Mine/a:b")]
    public void RootedAndColonPaths_Fail(string raw) =>
        Assert.Equal("Use a relative Penumbra folder path.", R(raw).Error);

    [Fact]
    public void ABackslashPath_IsNormalizedToForwardSlashes() =>
        Assert.Equal("Mine/Sub/x", R("Mine\\Sub\\x").Destination);

    [Fact]
    public void ALongFinalPath_Fails()
    {
        var long1 = string.Join('/', Enumerable.Repeat(new string('a', 50), 6)); // 6 * 50 + 5 = 305
        Assert.Equal($"Destination is longer than {DestinationResolver.MaxPathLength} characters.", R(long1).Error);
    }

    [Fact]
    public void FirstSegmentCode_ReadsOnlyAPositiveWholeNumber()
    {
        Assert.Equal(17, DestinationResolver.FirstSegmentCode("17/{creator}"));
        Assert.Equal(17, DestinationResolver.FirstSegmentCode("  17  "));
        Assert.Null(DestinationResolver.FirstSegmentCode("0/x"));
        Assert.Null(DestinationResolver.FirstSegmentCode("-16/x"));
        Assert.Null(DestinationResolver.FirstSegmentCode("Mine/17"));
        Assert.Null(DestinationResolver.FirstSegmentCode(""));
    }

    // The export writes a creator "A/B" as the single folder segment "A\B"; an unchanged cell must come back as is.
    [Fact]
    public void Literal_KeepsABackslashSegmentAndReadsNoGrammar()
    {
        Assert.Equal("Hair/A\\B", DestinationResolver.Literal("Hair/A\\B").Destination);
        Assert.Equal("VFX/Foo", DestinationResolver.Literal("VFX/Foo").Destination);
        Assert.Equal("17/{creator}", DestinationResolver.Literal("17/{creator}").Destination);
        Assert.Null(DestinationResolver.Literal("17").CodeUsed);
    }

    [Fact]
    public void Literal_BlankLeavesTheModAlone_AndBadStructureFails()
    {
        Assert.Equal(DestinationResult.Leave, DestinationResolver.Literal(""));
        Assert.NotNull(DestinationResolver.Literal("a//b").Error);
        Assert.NotNull(DestinationResolver.Literal("C:/x").Error);
    }
}
